using System.Runtime.Versioning; // DpapiProtector 仅 Windows（CA1416），与 SaleVerificationTests 同标注
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Domain.Top;
using TraceFlow.Infrastructure.Jobs;
using TraceFlow.Infrastructure.Sales;
using TraceFlow.Infrastructure.Security;
using TraceFlow.Infrastructure.Settings;
using TraceFlow.Infrastructure.Top;
using TraceFlow.Infrastructure.Uploads;
using TraceFlow.IntegrationTests.Sources;
using Xunit;

namespace TraceFlow.IntegrationTests.Uploads;

/// <summary>
/// billstatus 回查（TOP-02/03/04、JOB-04）：两次查询推进 Processing→Succeeded、部分成功保留逐码证据、
/// 无平台结论的码一律 UNKNOWN、查询超时不改变受理状态、20 次升级人工、采购成功唤醒依赖销售核验、
/// 终态分片零写入、扫描兜底只挑到期且无待执行回查作业的分片。平台一侧只用 <see cref="FakeTopGateway"/>（env=MOCK）。
/// </summary>
[SupportedOSPlatform("windows")]
public class BillQueryTests : IClassFixture<Sql2008Fixture>
{
    private const string Worker = "w1";

    private readonly Sql2008Fixture _fx;
    public BillQueryTests(Sql2008Fixture fx) => _fx = fx;

    private sealed record Seed(long OrgId, long TaskId, long PartId, long JobId, long FencingToken, string[] Codes);

    private BillQueryService NewService(Func<TopCall, TopRawResponse> handler)
    {
        var settings = new SettingsService(_fx.ConnectionString, new DpapiProtector());
        var audit = new AuditLog();
        var queue = new JobQueue(_fx.ConnectionString, audit);
        var top = new TopClientService(_fx.ConnectionString, new FakeTopGateway(handler), new RateBudgetService(),
            new ChannelPauseService(), audit);
        var sales = new SaleVerificationService(_fx.ConnectionString, "MOCK", settings, top,
            new RetailTaskFactory(settings, queue), queue, audit);
        return new BillQueryService(_fx.ConnectionString, settings, top, queue, sales, audit);
    }

    /// <summary>20 位 8 前缀码（同库内按 n 唯一）。</summary>
    private static string Code(int n) => "8" + n.ToString("D19");

    private static string PartPayload(string tag, string taskType, IReadOnlyList<string> codes)
        => JsonSerializer.Serialize(new
        {
            eventKey = $"KSOA:{tag}:{taskType}",
            taskType,
            partIndex = 0,
            businessDateTimeUtc = new DateTime(2026, 11, 20, 6, 0, 0, DateTimeKind.Utc),
            ruleVersion = "r1",
            lines = new[]
            {
                new
                {
                    sourceLineKey = "1", productSourceId = "SP1", platformDrugId = "PD1",
                    quantity = "1.000000", unit = "盒", batchNo = "B1", codes,
                },
            },
        });

    private static TopRawResponse BillStatus(string resultType, string? flag,
        params (string Code, string? Status, string? Reason)[] codes)
    {
        var model = new Dictionary<string, object?> { ["result_type"] = resultType };
        if (flag is not null) model["sub_process_flag"] = flag;
        model["code_list"] = codes.Select(c =>
        {
            var entry = new Dictionary<string, object?> { ["code"] = c.Code };
            if (c.Status is not null) entry["status"] = c.Status;
            if (c.Reason is not null) entry["reason"] = c.Reason;
            return entry;
        }).ToArray();
        var envelope = new
        {
            response_success = true,
            request_id = "mock-bq-" + Guid.NewGuid().ToString("N")[..8],
            model,
        };
        return new TopRawResponse(true, 200, null, JsonSerializer.Serialize(envelope), null);
    }

    /// <summary>播种任务+分片（可选回查作业并定向认领），返回句柄。</summary>
    private async Task<Seed> SeedAsync(string[] codes, string partState = "Accepted", string taskState = "Accepted",
        string taskType = "RETAIL_321", int queryCount = 0, DateTime? firstSentAt = null, DateTime? nextQueryAt = null,
        bool withQueryJob = true)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        await using var c = await _fx.OpenAsync();
        var (orgId, _) = await TestSeed.EnsureAsync(c);
        var payload = PartPayload(tag, taskType, codes);
        var taskId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upload_task(org_id,env,event_key,task_type,source_hash,payload_hash,payload_json,state)
OUTPUT inserted.id
VALUES(@orgId,N'MOCK',@key,@type,REPLICATE('a',64),REPLICATE('b',64),@payload,@state)",
            new { orgId, key = $"KSOA:{tag}:{taskType}", type = taskType, payload, state = taskState });
        var partId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upload_part(task_id,org_id,env,part_index,bill_code,payload_json,payload_hash,code_set_hash,state,
    query_count,first_sent_at,next_query_at)
OUTPUT inserted.id
VALUES(@taskId,@orgId,N'MOCK',0,@bill,@payload,REPLICATE('b',64),REPLICATE('c',64),@state,@queryCount,@firstSentAt,@nextQueryAt)",
            new { taskId, orgId, bill = "TFQ" + tag, payload, state = partState, queryCount, firstSentAt, nextQueryAt });

        if (!withQueryJob) return new Seed(orgId, taskId, partId, 0, 0, codes);

        var q = new JobQueue(_fx.ConnectionString, new AuditLog());
        long jobId;
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            jobId = await q.EnqueueAsync(c, tx, orgId, "MOCK", "bill-query", $"query:part:{partId}",
                JsonSerializer.Serialize(new { orgId, env = "MOCK", taskId, partId, trigger = "test" }), null, default);
            await tx.CommitAsync();
        }
        var token = await ClaimAsync(c, $"query:part:{partId}");
        return new Seed(orgId, taskId, partId, jobId, token, codes);
    }

    /// <summary>定向认领本测试的 bill-query 作业（避免与同库其他 PENDING 作业竞争），返回递增后的 fencing_token。</summary>
    private static Task<long> ClaimAsync(SqlConnection c, string intent)
        => c.ExecuteScalarAsync<long>(@"
UPDATE dbo.outbox_job
SET job_state=N'RUNNING', lease_owner=@w, lease_until=DATEADD(minute,5,SYSUTCDATETIME()),
    fencing_token=fencing_token+1, attempt_count=attempt_count+1
OUTPUT inserted.fencing_token
WHERE job_kind=N'bill-query' AND intent_key=@intent AND job_state=N'PENDING'",
            new { w = Worker, intent });

    private async Task<T> ScalarAsync<T>(string sql, object? args = null)
    {
        await using var c = await _fx.OpenAsync();
        return (await c.ExecuteScalarAsync<T>(sql, args))!;
    }

    private async Task<T> RowAsync<T>(string sql, object? args = null)
    {
        await using var c = await _fx.OpenAsync();
        return await c.QuerySingleAsync<T>(sql, args);
    }

    private Task<string?> PartStateAsync(long partId)
        => ScalarAsync<string?>("SELECT state FROM dbo.upload_part WHERE id=@id", new { id = partId });

    private Task<string?> TaskStateAsync(long taskId)
        => ScalarAsync<string?>("SELECT state FROM dbo.upload_task WHERE id=@id", new { id = taskId });

    private Task<int> QueryAttemptCountAsync(long taskId)
        => ScalarAsync<int>("SELECT COUNT(*) FROM dbo.upload_attempt WHERE task_id=@id AND attempt_kind=N'QUERY'",
            new { id = taskId });

    private Task<int> PendingPartJobsAsync(long partId)
        => ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=N'bill-query' AND intent_key=@k AND job_state=N'PENDING'",
            new { k = $"query:part:{partId}" });

    [Fact]
    public async Task Processing_then_success_walks_processing_to_succeeded()
    {
        var codes = new[] { Code(1), Code(2) };
        var seed = await SeedAsync(codes);
        var responses = new Queue<TopRawResponse>(new[]
        {
            BillStatus("1", null),
            BillStatus("2", null, (codes[0], "1", null), (codes[1], "1", null)),
        });
        var service = NewService(_ => responses.Dequeue());
        var handler = new BillQueryJobHandler(service, _fx.ConnectionString);

        var first = new ClaimedJob(seed.JobId, "bill-query", $"query:part:{seed.PartId}", null,
            seed.FencingToken, DateTime.UtcNow.AddMinutes(5), 1);
        await handler.HandleAsync(first, default);

        Assert.Equal("Processing", await PartStateAsync(seed.PartId));
        Assert.Equal("Processing", await TaskStateAsync(seed.TaskId));
        Assert.Equal(1, await ScalarAsync<int>("SELECT query_count FROM dbo.upload_part WHERE id=@id", new { id = seed.PartId }));
        Assert.True(await ScalarAsync<DateTime?>(
            "SELECT next_query_at FROM dbo.upload_part WHERE id=@id", new { id = seed.PartId }) > DateTime.UtcNow.AddSeconds(20));
        Assert.Equal(1, await PendingPartJobsAsync(seed.PartId)); // 下一次回查已退避重排
        Assert.Equal(2, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.code_result WHERE part_id=@id AND result=N'UNKNOWN'", new { id = seed.PartId }));

        // 重新认领已改期的回查作业，第二次查询返回整单成功。
        await using (var c = await _fx.OpenAsync())
        {
            var token = await ClaimAsync(c, $"query:part:{seed.PartId}");
            var second = new ClaimedJob(seed.JobId, "bill-query", $"query:part:{seed.PartId}", null, token,
                DateTime.UtcNow.AddMinutes(5), 2);
            await handler.HandleAsync(second, default);
        }

        Assert.Equal("Succeeded", await PartStateAsync(seed.PartId));
        Assert.Equal("Succeeded", await TaskStateAsync(seed.TaskId));
        Assert.Equal(2, await QueryAttemptCountAsync(seed.TaskId));
        Assert.Equal(2, await ScalarAsync<int>("SELECT query_count FROM dbo.upload_part WHERE id=@id", new { id = seed.PartId }));
        Assert.Null(await ScalarAsync<DateTime?>(
            "SELECT next_query_at FROM dbo.upload_part WHERE id=@id", new { id = seed.PartId }));
        Assert.Equal(0, await PendingPartJobsAsync(seed.PartId));
        Assert.Equal(2, await ScalarAsync<int>(@"
SELECT COUNT(*) FROM dbo.code_result r
JOIN dbo.upload_attempt a ON a.id=r.attempt_id AND a.attempt_kind=N'QUERY'
WHERE r.part_id=@id AND r.result=N'SUCCESS' AND a.attempt_no=2", new { id = seed.PartId }));
    }

    [Fact]
    public async Task Partial_saves_success_and_failed_codes_without_requeue()
    {
        var codes = new[] { Code(3), Code(4) };
        var seed = await SeedAsync(codes);
        var service = NewService(_ => BillStatus("3", "52",
            (codes[0], "1", null), (codes[1], "2", "码已被其他企业绑定")));

        var outcome = await service.QueryPartAsync(seed.PartId, Worker, seed.FencingToken, default);

        Assert.Equal(QueryOutcome.Partial, outcome);
        Assert.Equal("PartialSuccess", await PartStateAsync(seed.PartId));
        Assert.Equal("PartialSuccess", await TaskStateAsync(seed.TaskId));
        Assert.Equal(1, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.code_result WHERE part_id=@id AND result=N'SUCCESS'", new { id = seed.PartId }));
        Assert.Equal("码已被其他企业绑定", await ScalarAsync<string>(@"
SELECT TOP 1 reason FROM dbo.code_result WHERE part_id=@id AND result=N'FAILED'", new { id = seed.PartId }));
        // 部分成功是已结算结论：不重排整单、不再回查。
        Assert.Null(await ScalarAsync<DateTime?>(
            "SELECT next_query_at FROM dbo.upload_part WHERE id=@id", new { id = seed.PartId }));
        Assert.Equal(0, await PendingPartJobsAsync(seed.PartId));
    }

    [Fact]
    public async Task Unknown_codes_are_persisted_as_UNKNOWN()
    {
        // 平台只对两个码给结论：载荷内第三个码必须落 UNKNOWN，且整单不得升级为成功。
        var codes = new[] { Code(5), Code(6), Code(7) };
        var seed = await SeedAsync(codes);
        var service = NewService(_ => BillStatus("2", null, (codes[0], "1", null), (codes[1], "1", null)));

        var outcome = await service.QueryPartAsync(seed.PartId, Worker, seed.FencingToken, default);

        Assert.Equal(QueryOutcome.Partial, outcome);
        Assert.Equal("PartialSuccess", await PartStateAsync(seed.PartId));
        Assert.NotEqual("Succeeded", await TaskStateAsync(seed.TaskId));
        Assert.Equal("UNKNOWN", await ScalarAsync<string>(@"
SELECT r.result FROM dbo.code_result r
JOIN dbo.trace_code c ON c.id=r.code_id
WHERE r.part_id=@id AND c.code_normalized=@code", new { id = seed.PartId, code = codes[2] }));
        Assert.Equal("平台未返回该码结论", await ScalarAsync<string>(@"
SELECT TOP 1 r.reason FROM dbo.code_result r
JOIN dbo.trace_code c ON c.id=r.code_id
WHERE r.part_id=@id AND c.code_normalized=@code", new { id = seed.PartId, code = codes[2] }));
    }

    [Fact]
    public async Task Query_timeout_does_not_change_acceptance_state()
    {
        var seed = await SeedAsync(new[] { Code(8) });
        var service = NewService(_ => new TopRawResponse(false, null, null, null, null));

        var outcome = await service.QueryPartAsync(seed.PartId, Worker, seed.FencingToken, default);

        Assert.Equal(QueryOutcome.Unresolved, outcome);
        Assert.Equal("Accepted", await PartStateAsync(seed.PartId)); // 查询超时只影响查询，不改变受理结论
        Assert.Equal("Accepted", await TaskStateAsync(seed.TaskId));
        Assert.Equal("TRANSPORT_UNCERTAIN", await ScalarAsync<string>(
            "SELECT error_kind FROM dbo.upload_attempt WHERE task_id=@id AND attempt_kind=N'QUERY'", new { id = seed.TaskId }));
        Assert.Null(await ScalarAsync<int?>(
            "SELECT http_status FROM dbo.upload_attempt WHERE task_id=@id AND attempt_kind=N'QUERY'", new { id = seed.TaskId }));
        Assert.NotNull(await ScalarAsync<DateTime?>(
            "SELECT next_query_at FROM dbo.upload_part WHERE id=@id", new { id = seed.PartId }));
        Assert.Equal(1, await PendingPartJobsAsync(seed.PartId));
        Assert.Equal("UNKNOWN", await ScalarAsync<string>(
            "SELECT TOP 1 result FROM dbo.code_result WHERE part_id=@id", new { id = seed.PartId }));
    }

    [Fact]
    public async Task Twenty_attempts_escalates_to_manual_action()
    {
        var seed = await SeedAsync(new[] { Code(9) }, partState: "Processing", taskState: "Processing", queryCount: 19);
        var service = NewService(_ => BillStatus("1", null));

        var outcome = await service.QueryPartAsync(seed.PartId, Worker, seed.FencingToken, default);

        Assert.Equal(QueryOutcome.Escalated, outcome);
        Assert.Equal("ManualAction", await PartStateAsync(seed.PartId));
        Assert.Equal("ManualAction", await TaskStateAsync(seed.TaskId));
        Assert.Equal(20, await ScalarAsync<int>("SELECT query_count FROM dbo.upload_part WHERE id=@id", new { id = seed.PartId }));
        Assert.Null(await ScalarAsync<DateTime?>(
            "SELECT next_query_at FROM dbo.upload_part WHERE id=@id", new { id = seed.PartId }));
        Assert.Equal(0, await PendingPartJobsAsync(seed.PartId)); // 到人工后不再无限回查

        var run = await RowAsync<(long Id, string BusinessType, string Status, string? Summary)>(@"
SELECT id AS Id, business_type AS BusinessType, status AS Status, local_summary AS Summary
FROM dbo.reconciliation_run WHERE org_id=@orgId", new { seed.OrgId });
        Assert.Equal("SALE", run.BusinessType.Trim());
        Assert.Equal("COMPLETE", run.Status.Trim());
        Assert.NotNull(run.Summary);

        var diff = await RowAsync<(long RunId, string DiffType, string Severity, string ObjectRef, string Status, string OwnerRole, string NextAction)>(@"
SELECT run_id AS RunId, diff_type AS DiffType, severity AS Severity, object_ref AS ObjectRef,
       status AS Status, owner_role AS OwnerRole, next_action AS NextAction
FROM dbo.reconciliation_difference WHERE run_id=@runId", new { runId = run.Id });
        Assert.Equal(run.Id, diff.RunId);
        Assert.Equal("ACCEPTANCE_UNKNOWN", diff.DiffType.Trim());
        Assert.Equal("HIGH", diff.Severity.Trim());
        Assert.Equal($"part:{seed.PartId}", diff.ObjectRef);
        Assert.Equal("OPEN", diff.Status.Trim());
        Assert.Equal("OPERATOR", diff.OwnerRole.Trim());
        Assert.Equal("MANUAL_VERIFY", diff.NextAction.Trim());
    }

    [Fact]
    public async Task Purchase_success_triggers_dependent_sale_verify()
    {
        var code = Code(10);
        var seed = await SeedAsync(new[] { code }, taskType: "PURCHASE_IN_102");

        long snapshotId;
        await using (var c = await _fx.OpenAsync())
        {
            var (orgId, sysId) = await TestSeed.EnsureAsync(c);
            snapshotId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.source_snapshot(org_id,source_system_id,source_document_key,document_type,version,completeness_status,content_hash)
OUTPUT inserted.id VALUES(@orgId,@sysId,@key,N'SALE',1,N'COMPLETE',REPLICATE('d',64))",
                new { orgId, sysId, key = "SALE-BQ-" + Guid.NewGuid().ToString("N")[..8] });
            await c.ExecuteAsync(@"
INSERT INTO dbo.source_line(snapshot_id,source_line_key,product_source_id,quantity_original,code_format,line_hash,
    event_kind,trace_code_normalized)
VALUES(@snapshotId,N'1',N'SP1',1,N'Supported8Prefix20',REPLICATE('e',64),N'SALE',@code)",
                new { snapshotId, code });
            await c.ExecuteAsync(@"
INSERT INTO dbo.upload_task(org_id,env,event_key,task_type,source_snapshot_id,source_hash,payload_hash,state,
    state_reason,dependency_state)
VALUES(@orgId,N'MOCK',@key,N'RETAIL_321',@snapshotId,REPLICATE('f',64),REPLICATE('f',64),N'Blocked',
       N'WAIT_DEPENDENCY',N'WAIT')",
                new { orgId, key = "KSOA:SALE-BQ-DEP:RETAIL_321", snapshotId });
        }

        var service = NewService(_ => BillStatus("2", null, (code, "1", null)));

        var outcome = await service.QueryPartAsync(seed.PartId, Worker, seed.FencingToken, default);

        Assert.Equal(QueryOutcome.Succeeded, outcome);
        Assert.Equal("Succeeded", await PartStateAsync(seed.PartId));
        Assert.Equal("Succeeded", await TaskStateAsync(seed.TaskId));
        Assert.Equal(1, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=N'sale-verify' AND intent_key LIKE @p",
            new { p = $"snapshot:{snapshotId}:dep:%" }));
    }

    [Fact]
    public async Task Non_terminal_part_only_queried()
    {
        var seed = await SeedAsync(new[] { Code(11) }, partState: "Succeeded", taskState: "Succeeded");
        var calls = 0;
        var service = NewService(_ => { calls++; return BillStatus("2", null); });

        var outcome = await service.QueryPartAsync(seed.PartId, Worker, seed.FencingToken, default);

        Assert.Equal(QueryOutcome.Skipped, outcome);
        Assert.Equal(0, calls); // 终态分片不触达平台
        Assert.Equal(0, await QueryAttemptCountAsync(seed.TaskId));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.code_result WHERE part_id=@id", new { id = seed.PartId }));
        Assert.Equal("Succeeded", await PartStateAsync(seed.PartId));
        Assert.Equal(0, await ScalarAsync<int>("SELECT query_count FROM dbo.upload_part WHERE id=@id", new { id = seed.PartId }));
        Assert.Equal("RUNNING", await ScalarAsync<string>(
            "SELECT job_state FROM dbo.outbox_job WHERE id=@id", new { id = seed.JobId }));
    }

    [Fact]
    public async Task Scan_job_queries_due_parts_without_pending_part_jobs()
    {
        var codes = new[] { Code(12) };
        var due1 = await SeedAsync(codes, withQueryJob: false, nextQueryAt: DateTime.UtcNow.AddMinutes(-2));
        var due2 = await SeedAsync(codes, withQueryJob: false, nextQueryAt: DateTime.UtcNow.AddMinutes(-1));
        var waiting = await SeedAsync(codes, withQueryJob: true, nextQueryAt: DateTime.UtcNow.AddMinutes(-1));
        var future = await SeedAsync(codes, withQueryJob: false, nextQueryAt: DateTime.UtcNow.AddHours(1));

        var intent = "query:scan:999999";
        var payload = JsonSerializer.Serialize(new { orgId = due1.OrgId, env = "MOCK", trigger = "scan" });
        long jobId;
        await using (var c = await _fx.OpenAsync())
        {
            var q = new JobQueue(_fx.ConnectionString, new AuditLog());
            await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
            jobId = await q.EnqueueAsync(c, tx, due1.OrgId, "MOCK", "bill-query", intent, payload, null, default);
            await tx.CommitAsync();
        }

        var service = NewService(_ => BillStatus("1", null));
        var handler = new BillQueryJobHandler(service, _fx.ConnectionString);
        long token;
        await using (var c = await _fx.OpenAsync())
            token = await ClaimAsync(c, intent);
        await handler.HandleAsync(new ClaimedJob(jobId, "bill-query", intent, payload, token,
            DateTime.UtcNow.AddMinutes(5), 1), default);

        Assert.Equal("Processing", await PartStateAsync(due1.PartId));
        Assert.Equal("Processing", await PartStateAsync(due2.PartId));
        Assert.Equal(1, await ScalarAsync<int>("SELECT query_count FROM dbo.upload_part WHERE id=@id", new { id = due1.PartId }));
        // 已有待执行 per-part 回查作业的分片由该作业负责，扫描不得双查。
        Assert.Equal("Accepted", await PartStateAsync(waiting.PartId));
        Assert.Equal(0, await QueryAttemptCountAsync(waiting.TaskId));
        // 未到期的分片不扫。
        Assert.Equal("Accepted", await PartStateAsync(future.PartId));
        Assert.Equal(0, await QueryAttemptCountAsync(future.TaskId));
        // 扫描只兜底：为到期分片建 per-part 回查作业，扫描作业自身保持租约（不改期）。
        Assert.Equal(1, await PendingPartJobsAsync(due1.PartId));
        Assert.Equal(1, await PendingPartJobsAsync(due2.PartId));
        Assert.Equal("RUNNING", await ScalarAsync<string>(
            "SELECT job_state FROM dbo.outbox_job WHERE id=@id", new { id = jobId }));
    }
}