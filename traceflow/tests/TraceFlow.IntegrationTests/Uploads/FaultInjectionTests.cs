using System.Globalization;
using System.Runtime.Versioning; // DpapiProtector 仅 Windows（CA1416），与 BillQueryTests 同标注
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Domain.Top;
using TraceFlow.Infrastructure.Codes;
using TraceFlow.Infrastructure.Jobs;
using TraceFlow.Infrastructure.Sales;
using TraceFlow.Infrastructure.Security;
using TraceFlow.Infrastructure.Settings;
using TraceFlow.Infrastructure.Top;
using TraceFlow.Infrastructure.Uploads;
using Xunit;

namespace TraceFlow.IntegrationTests.Uploads;

/// <summary>故障注入：模拟进程在被注入点直接消失（后续语句一行都不执行）。</summary>
public sealed class SimulatedCrashException : Exception
{
    public SimulatedCrashException(string message) : base(message) { }
}

/// <summary>
/// 故障注入套件（AT-068）：M3 发送管道的崩溃/围栏保障（IDEM-04 / §2.7 / §2.9 / JOB-02）。
/// <para>
/// 只断言"本地保障"：<b>不宣称平台恰好一次</b>。可断言的是本地可见事实——没有第二次 UPLOAD 调用、
/// 尝试行是否收尾、分片/任务是否推进、迟到写回是否只留证、回查作业是否先于任何重发。
/// 平台侧一律 <see cref="FakeTopGateway"/>（env=MOCK），env=LIVE 必须 fail-closed。
/// </para>
/// <para>
/// 崩溃点由 <see cref="UploadDispatchService"/> 的 internal 钩子（BeforeSend/AfterSend/BeforeCommit，
/// 仅测试程序集可见）注入：钩子抛异常等价于该边界上进程消失；生产代码从不赋值，行为与无钩子一致。
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public class FaultInjectionTests : IClassFixture<Sql2008Fixture>
{
    private const string Worker = "w1";

    private readonly Sql2008Fixture _fx;
    public FaultInjectionTests(Sql2008Fixture fx) => _fx = fx;

    private sealed record Seed(long OrgId, long TaskId, long PartId, long JobId, long FencingToken);

    // ---------------------------------------------------------------- 服务构造

    private UploadDispatchService NewDispatchService(Func<TopCall, TopRawResponse> handler)
    {
        var audit = new AuditLog();
        var queue = new JobQueue(_fx.ConnectionString, audit);
        var top = new TopClientService(_fx.ConnectionString, new FakeTopGateway(handler), new RateBudgetService(),
            new ChannelPauseService(), audit);
        return new UploadDispatchService(_fx.ConnectionString, top, queue, new SendRecoveryService(_fx.ConnectionString, queue));
    }

    private BillQueryService NewQueryService(Func<TopCall, TopRawResponse> handler)
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

    private Task<int> RecoverAsync()
        => new SendRecoveryService(_fx.ConnectionString, new JobQueue(_fx.ConnectionString, new AuditLog())).RecoverAsync(default);

    // ---------------------------------------------------------------- 网关应答（全 MOCK）

    private static TopRawResponse Accepted(string requestId) => new(true, 200, null, JsonSerializer.Serialize(new
    {
        response_success = true, msg_code = "SUCCESS", request_id = requestId,
        result = new { bill_code = "TF-FJ", task_id = "MOCK-FJ-1" },
    }), null);

    private static TopRawResponse DuplicateBillCode(string requestId) => new(true, 200, null, JsonSerializer.Serialize(new
    {
        response_success = false, sub_msg_code = "FAIL_BIZ_BILL_CODE_DUPLICATE", sub_msg = "单据号重复", request_id = requestId,
    }), null);

    private static TopRawResponse TransportUncertain() => new(false, null, null, null, null);

    private static TopRawResponse BillStatusAllSuccess(IReadOnlyList<string> codes)
    {
        var model = new Dictionary<string, object?>
        {
            ["result_type"] = "2",
            ["code_list"] = codes.Select(c => new Dictionary<string, object?> { ["code"] = c, ["status"] = "1" }).ToArray(),
        };
        var envelope = new
        {
            response_success = true,
            request_id = "mock-bq-" + Guid.NewGuid().ToString("N")[..8],
            model,
        };
        return new TopRawResponse(true, 200, null, JsonSerializer.Serialize(envelope), null);
    }

    // ---------------------------------------------------------------- 种子

    private static string NewTag() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>20 位 8 前缀码（同库内按 n 唯一）。</summary>
    private static string Code(int n) => "8" + n.ToString("D19", CultureInfo.InvariantCulture);

    private static async Task<long> SeedOrgAsync(SqlConnection c)
        => await c.ExecuteScalarAsync<long>(@"
IF NOT EXISTS (SELECT 1 FROM dbo.organization WHERE org_code=N'O1')
    INSERT INTO dbo.organization(org_code,name,ref_ent_id,ent_id) VALUES(N'O1',N'店',N'REF-STORE',N'ENT-STORE');
SELECT id FROM dbo.organization WHERE org_code=N'O1'");

    private static string PartPayload(string tag, IReadOnlyList<string> codes)
        => JsonSerializer.Serialize(new
        {
            eventKey = $"KSOA:FJ-{tag}:RETAIL_321",
            taskType = "RETAIL_321",
            partIndex = 0,
            businessDateTimeUtc = new DateTime(2026, 11, 22, 6, 0, 0, DateTimeKind.Utc),
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

    /// <summary>播种任务+分片+发送作业并原子认领（租约持有者=w1，token+1）。</summary>
    private async Task<Seed> SeedAsync(string tag, IReadOnlyList<string> codes, string env = "MOCK",
        string partState = "Ready", string taskState = "Ready")
    {
        await using var c = await _fx.OpenAsync();
        var orgId = await SeedOrgAsync(c);
        var payload = PartPayload(tag, codes);
        var taskId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upload_task(org_id,env,event_key,task_type,source_hash,payload_hash,payload_json,state)
OUTPUT inserted.id
VALUES(@orgId,@env,@key,N'RETAIL_321',REPLICATE('a',64),REPLICATE('b',64),@payload,@taskState)",
            new { orgId, env, key = $"KSOA:FJ-{tag}:RETAIL_321", payload, taskState });
        var partId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upload_part(task_id,org_id,env,part_index,bill_code,payload_json,payload_hash,code_set_hash,state)
OUTPUT inserted.id
VALUES(@taskId,@orgId,@env,0,@bill,@payload,REPLICATE('b',64),REPLICATE('c',64),@partState)",
            new { taskId, orgId, env, bill = "TFFJ" + tag, payload, partState });
        var q = new JobQueue(_fx.ConnectionString, new AuditLog());
        long jobId;
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            jobId = await q.EnqueueAsync(c, tx, orgId, env, "upload-dispatch", $"part:{partId}",
                JsonSerializer.Serialize(new { orgId, env, taskId, partId, trigger = "fault-injection" }), null, default);
            await tx.CommitAsync();
        }
        var token = await ClaimAsync(c, "upload-dispatch", $"part:{partId}");
        return new Seed(orgId, taskId, partId, jobId, token);
    }

    /// <summary>定向认领本用例的作业（避免与同库其他待认领作业竞争），返回递增后的 fencing_token。</summary>
    private static Task<long> ClaimAsync(SqlConnection c, string jobKind, string intent)
        => c.ExecuteScalarAsync<long>(@"
UPDATE dbo.outbox_job
SET job_state=N'RUNNING', lease_owner=@w, lease_until=DATEADD(minute,5,SYSUTCDATETIME()),
    fencing_token=fencing_token+1, attempt_count=attempt_count+1
OUTPUT inserted.fencing_token
WHERE job_kind=@jobKind AND intent_key=@intent AND job_state=N'PENDING'",
            new { w = Worker, jobKind, intent });

    /// <summary>
    /// 用真实任务工厂建 LIVE 任务（任务+分片+bill_code+逐码占用+码事件+发送作业），再认领发送作业。
    /// LIVE fail-closed 用例必须在派发前就存在可被推进的码台账，否则"台账未变化"不可证伪。
    /// </summary>
    private async Task<Seed> SeedLiveTaskAsync(string tag, IReadOnlyList<string> codes)
    {
        var stored = new List<(long CodeId, string CodeNormalized)>();
        long orgId;
        await using (var seedConn = await _fx.OpenAsync())
        {
            orgId = await SeedOrgAsync(seedConn);
            await using var codeTx = (SqlTransaction)await seedConn.BeginTransactionAsync();
            foreach (var code in codes)
            {
                var row = await TraceCodeStore.EnsureAsync(seedConn, codeTx, code, default);
                stored.Add((row.Id, row.Normalized));
            }
            await codeTx.CommitAsync();
        }

        var ctx = new RetailTaskContext(orgId, "LIVE", null, $"FJ-LIVE-{tag}",
            new DateTime(2026, 11, 22, 6, 0, 0, DateTimeKind.Utc), new string('a', 64), "map-1", "r1",
            new[] { new RetailLine(1, "1", "SP1", "PD1", codes.Count, "盒", "B1", stored) },
            RetailTaskFactory.Retail321);
        var factory = new RetailTaskFactory(new SettingsService(_fx.ConnectionString, new DpapiProtector()),
            new JobQueue(_fx.ConnectionString, new AuditLog()));

        long taskId;
        long partId;
        await using (var conn = await _fx.OpenAsync())
        {
            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
            var created = await factory.CreateAsync(conn, tx, ctx, default);
            await tx.CommitAsync();
            taskId = created.TaskId;
            partId = Assert.Single(created.PartIds);
        }

        var jobId = await ScalarAsync<long>(
            "SELECT id FROM dbo.outbox_job WHERE job_kind=N'upload-dispatch' AND intent_key=@k",
            new { k = $"part:{partId}" });
        long token;
        await using (var claimConn = await _fx.OpenAsync())
            token = await ClaimAsync(claimConn, "upload-dispatch", $"part:{partId}");
        return new Seed(orgId, taskId, partId, jobId, token);
    }

    /// <summary>模拟"崩溃后无人续租"：租约已过期，恢复扫描才可接管。</summary>
    private async Task ExpireLeaseAsync(long jobId)
    {
        await using var c = await _fx.OpenAsync();
        await c.ExecuteAsync("UPDATE dbo.outbox_job SET lease_until=DATEADD(minute,-1,SYSUTCDATETIME()) WHERE id=@id",
            new { id = jobId });
    }

    // ---------------------------------------------------------------- 断言用读取

    private async Task<T> ScalarAsync<T>(string sql, object? args = null)
    {
        await using var c = await _fx.OpenAsync();
        return (await c.ExecuteScalarAsync<T>(sql, args))!;
    }

    private Task<string?> PartStateAsync(long partId)
        => ScalarAsync<string?>("SELECT state FROM dbo.upload_part WHERE id=@id", new { id = partId });

    private Task<string?> TaskStateAsync(long taskId)
        => ScalarAsync<string?>("SELECT state FROM dbo.upload_task WHERE id=@id", new { id = taskId });

    private Task<int> UploadAttemptsAsync(long taskId)
        => ScalarAsync<int>("SELECT COUNT(*) FROM dbo.upload_attempt WHERE task_id=@id AND attempt_kind=N'UPLOAD'",
            new { id = taskId });

    private Task<int> QueryAttemptsAsync(long taskId)
        => ScalarAsync<int>("SELECT COUNT(*) FROM dbo.upload_attempt WHERE task_id=@id AND attempt_kind=N'QUERY'",
            new { id = taskId });

    private Task<int> OpenAttemptsAsync(long taskId, string kind)
        => ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.upload_attempt WHERE task_id=@id AND attempt_kind=@kind AND finished_at IS NULL",
            new { id = taskId, kind });

    private Task<string?> DispatchJobStateAsync(long jobId)
        => ScalarAsync<string?>("SELECT job_state FROM dbo.outbox_job WHERE id=@id", new { id = jobId });

    private Task<int> QueryJobsAsync(long partId, string state)
        => ScalarAsync<int>(@"
SELECT COUNT(*) FROM dbo.outbox_job
WHERE job_kind=N'bill-query' AND intent_key=@k AND job_state=@state",
            new { k = $"query:part:{partId}", state });

    /// <summary>
    /// 本任务已落库的核验流水行数：派发/恢复路径从不写逐码结论，本用例也未播种码台账，
    /// 故不再断言 code_result 计数；核验流水行数用于证明"未提交的受理成功/恢复均未凭空推进核验"。
    /// </summary>
    private Task<int> VerificationRowsAsync(long taskId)
        => ScalarAsync<int>("SELECT COUNT(*) FROM dbo.verification WHERE upload_task_id=@id", new { id = taskId });

    /// <summary>本任务的占用行快照（id、码、状态、归属任务），用于"派发前后完全相同"的整行比对。</summary>
    private async Task<List<(long Id, long CodeId, string ClaimState, long? UploadTaskId)>> ClaimRowsAsync(long taskId)
    {
        await using var c = await _fx.OpenAsync();
        return (await c.QueryAsync<(long Id, long CodeId, string ClaimState, long? UploadTaskId)>(@"
SELECT id AS Id, code_id AS CodeId, claim_state AS ClaimState, upload_task_id AS UploadTaskId
FROM dbo.code_claim WHERE upload_task_id=@id ORDER BY id", new { id = taskId })).ToList();
    }

    /// <summary>本任务的码事件行快照（id、码、事件类型、归属任务）。</summary>
    private async Task<List<(long Id, long CodeId, string EventType, long? UploadTaskId)>> EventRowsAsync(long taskId)
    {
        await using var c = await _fx.OpenAsync();
        return (await c.QueryAsync<(long Id, long CodeId, string EventType, long? UploadTaskId)>(@"
SELECT id AS Id, code_id AS CodeId, event_type AS EventType, upload_task_id AS UploadTaskId
FROM dbo.code_event WHERE upload_task_id=@id ORDER BY id", new { id = taskId })).ToList();
    }

    private async Task<(DateTime? FinishedAt, string? RequestId, string? ErrorKind, bool IsStale, string? RawResponse)>
        AttemptAsync(long taskId, string kind, int attemptNo)
    {
        await using var c = await _fx.OpenAsync();
        return await c.QuerySingleAsync<(DateTime? FinishedAt, string? RequestId, string? ErrorKind, bool IsStale, string? RawResponse)>(@"
SELECT finished_at, request_id, error_kind, is_stale_evidence, raw_response
FROM dbo.upload_attempt WHERE task_id=@id AND attempt_kind=@kind AND attempt_no=@no",
            new { id = taskId, kind, no = attemptNo });
    }

    private async Task<string?> BillCodeAsync(long partId)
    {
        await using var c = await _fx.OpenAsync();
        return await c.ExecuteScalarAsync<string>("SELECT bill_code FROM dbo.upload_part WHERE id=@id", new { id = partId });
    }

    // ---------------------------------------------------------------- 1

    /// <summary>
    /// 写尝试行之后、HTTP 之前崩溃：本地留下未收尾的尝试证据，恢复只做"受理未知 → 先回查"，
    /// 绝不再次 UPLOAD（第二次上传的本地证据为零、网关零调用）。
    /// </summary>
    [Fact]
    public async Task Kill_before_http_attempt_leaves_evidence_then_recovery_queries_first()
    {
        var codes = new[] { Code(101) };
        var seed = await SeedAsync(NewTag(), codes);
        var uploadCalls = 0;
        var service = NewDispatchService(_ => { uploadCalls++; return Accepted("r-fj-1"); });
        service.BeforeSend = () => throw new SimulatedCrashException("尝试行已写、HTTP 未发出时进程退出");

        await Assert.ThrowsAsync<SimulatedCrashException>(() =>
            service.DispatchAsync(seed.PartId, Worker, seed.FencingToken, default));

        // 崩溃现场：零 HTTP、尝试行已提交但未收尾、分片停在发送中。
        Assert.Equal(0, uploadCalls);
        Assert.Equal("Dispatching", await PartStateAsync(seed.PartId));
        Assert.Equal(1, await UploadAttemptsAsync(seed.TaskId));
        Assert.Null((await AttemptAsync(seed.TaskId, "UPLOAD", 1)).FinishedAt);

        await ExpireLeaseAsync(seed.JobId);
        Assert.True(await RecoverAsync() >= 1);

        // 恢复：受理未知 + 发送作业 FAILED + 回查作业待执行；没有第二次 UPLOAD（尝试行数不变是证据）。
        Assert.Equal("AcceptanceUnknown", await PartStateAsync(seed.PartId));
        Assert.Equal("AcceptanceUnknown", await TaskStateAsync(seed.TaskId)); // 恢复事务内重算聚合，任务即真实态
        Assert.Equal("FAILED", await DispatchJobStateAsync(seed.JobId));
        Assert.Equal(1, await QueryJobsAsync(seed.PartId, "PENDING"));
        Assert.Equal(1, await UploadAttemptsAsync(seed.TaskId));
    }

    // ---------------------------------------------------------------- 2

    /// <summary>
    /// 网关已记录"请求已发出"之后、T3 之前崩溃：恢复不得重发，后续只有 QUERY 尝试。
    /// </summary>
    [Fact]
    public async Task Kill_after_http_before_t3_leaves_dispatching_and_recovery_never_reuploads()
    {
        var codes = new[] { Code(102) };
        var seed = await SeedAsync(NewTag(), codes);
        var uploadCalls = 0;
        var queryCalls = 0;
        var service = NewDispatchService(call =>
        {
            if (call.Method == UploadDispatchService.UploadRetailMethod) { uploadCalls++; return Accepted("r-fj-2"); }
            queryCalls++;
            return BillStatusAllSuccess(codes);
        });
        service.AfterSend = () => throw new SimulatedCrashException("请求已发出、回执未落库时进程退出");

        await Assert.ThrowsAsync<SimulatedCrashException>(() =>
            service.DispatchAsync(seed.PartId, Worker, seed.FencingToken, default));

        Assert.Equal(1, uploadCalls); // 平台侧已发生一次发送（本地无法撤回，也不宣称恰好一次）
        Assert.Equal("Dispatching", await PartStateAsync(seed.PartId));
        Assert.Null((await AttemptAsync(seed.TaskId, "UPLOAD", 1)).FinishedAt);

        await ExpireLeaseAsync(seed.JobId);
        Assert.True(await RecoverAsync() >= 1);
        Assert.Equal("AcceptanceUnknown", await PartStateAsync(seed.PartId));
        Assert.Equal(1, await QueryJobsAsync(seed.PartId, "PENDING"));
        Assert.Equal(0, await QueryAttemptsAsync(seed.TaskId)); // 恢复本身不触达平台

        // 回查作业被认领执行后：只有 QUERY 尝试，UPLOAD 调用数仍是 1。
        await using (var c = await _fx.OpenAsync())
            await ClaimAsync(c, "bill-query", $"query:part:{seed.PartId}");
        var queryService = NewQueryService(call =>
        {
            if (call.Method == UploadDispatchService.UploadRetailMethod) { uploadCalls++; return Accepted("r-fj-2b"); }
            queryCalls++;
            return BillStatusAllSuccess(codes);
        });
        var fencingToken = await ScalarAsync<long>(
            "SELECT fencing_token FROM dbo.outbox_job WHERE job_kind=N'bill-query' AND intent_key=@k",
            new { k = $"query:part:{seed.PartId}" });
        var outcome = await queryService.QueryPartAsync(seed.PartId, Worker, fencingToken, default);

        Assert.Equal(QueryOutcome.Succeeded, outcome);
        Assert.Equal(1, queryCalls);
        Assert.Equal(1, uploadCalls);
        Assert.Equal(1, await UploadAttemptsAsync(seed.TaskId));
        Assert.Equal(1, await QueryAttemptsAsync(seed.TaskId));
        Assert.Equal("Succeeded", await PartStateAsync(seed.PartId));
    }

    // ---------------------------------------------------------------- 3

    /// <summary>
    /// T3 内部崩溃：回执/状态写入在事务内被回滚，尝试行保持未收尾（finished_at 为 NULL），
    /// 恢复路径同样只走"受理未知"。
    /// </summary>
    [Fact]
    public async Task Kill_during_t3_rolls_back_and_attempt_stays_open()
    {
        var codes = new[] { Code(103) };
        var seed = await SeedAsync(NewTag(), codes);
        var service = NewDispatchService(_ => TransportUncertain()); // 平台无结论
        service.BeforeCommit = () => throw new SimulatedCrashException("T3 提交前进程退出");

        await Assert.ThrowsAsync<SimulatedCrashException>(() =>
            service.DispatchAsync(seed.PartId, Worker, seed.FencingToken, default));

        // 回滚：分片仍是发送中（没有落在 AcceptanceUnknown），尝试行未收尾。
        Assert.Equal("Dispatching", await PartStateAsync(seed.PartId));
        Assert.Equal(1, await OpenAttemptsAsync(seed.TaskId, "UPLOAD"));
        Assert.Null((await AttemptAsync(seed.TaskId, "UPLOAD", 1)).FinishedAt);
        Assert.Equal(0, await QueryJobsAsync(seed.PartId, "PENDING")); // 事务内的回查入队一并回滚

        await ExpireLeaseAsync(seed.JobId);
        Assert.True(await RecoverAsync() >= 1);

        Assert.Equal("AcceptanceUnknown", await PartStateAsync(seed.PartId));
        Assert.Equal("AcceptanceUnknown", await TaskStateAsync(seed.TaskId)); // 恢复事务内重算聚合
        Assert.Equal("FAILED", await DispatchJobStateAsync(seed.JobId));
        Assert.Equal(1, await QueryJobsAsync(seed.PartId, "PENDING"));
        Assert.Equal(1, await UploadAttemptsAsync(seed.TaskId));
        Assert.Equal(0, await QueryAttemptsAsync(seed.TaskId));
    }

    // ---------------------------------------------------------------- 4

    /// <summary>
    /// 迟到写回：旧 token 的写回只追加只读证据（is_stale_evidence=1，raw_response 保留），
    /// 分片状态不变、原尝试保持未收尾。
    /// </summary>
    [Fact]
    public async Task Late_worker_write_rejected_but_response_preserved()
    {
        var seed = await SeedAsync(NewTag(), new[] { Code(104) }, partState: "Dispatching");
        long attemptId;
        await using (var c = await _fx.OpenAsync())
        {
            attemptId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upload_attempt(task_id,part_id,attempt_no,attempt_kind,env,lease_owner,fencing_token)
OUTPUT inserted.id VALUES(@taskId,@partId,1,N'UPLOAD',N'MOCK',@w,@token)",
                new { seed.TaskId, seed.PartId, w = Worker, token = seed.FencingToken });
            // 并发重认领：作业 token 递增，旧 token 立即失效（§2.7）。
            await c.ExecuteAsync("UPDATE dbo.outbox_job SET fencing_token=fencing_token+1 WHERE id=@id", new { id = seed.JobId });
        }

        var service = NewDispatchService(_ => Accepted("r-fj-4"));
        var outcome = await service.ApplyReceiptAsync(new AttemptReceipt(attemptId, seed.PartId, seed.TaskId, "MOCK",
            Worker, seed.FencingToken, new TopOutcome(TopOutcomeKind.Success, null, null, null, null, "r-fj-4-late", null), 200),
            default);

        Assert.Equal(DispatchOutcome.StaleRejected, outcome);
        Assert.Equal("Dispatching", await PartStateAsync(seed.PartId)); // 状态不推进
        var original = await AttemptAsync(seed.TaskId, "UPLOAD", 1);
        Assert.Null(original.FinishedAt); // 原尝试保持未收尾
        Assert.Null(original.RequestId); // 迟到回执绝不写回原尝试
        Assert.False(original.IsStale);
        Assert.Null(original.RawResponse);

        // 迟到结论不丢：写成只读证据行，raw_response 完整保留。
        var evidence = await AttemptAsync(seed.TaskId, "UPLOAD", 2);
        Assert.True(evidence.IsStale);
        Assert.NotNull(evidence.FinishedAt);
        Assert.Contains("r-fj-4-late", evidence.RawResponse ?? "", StringComparison.Ordinal);
        Assert.Equal(0, await QueryJobsAsync(seed.PartId, "PENDING"));
    }

    // ---------------------------------------------------------------- 5

    /// <summary>
    /// 重复号：本地判受理未知（绝不判成功），随后只有 QUERY 尝试被记录，没有第二次 UPLOAD。
    /// </summary>
    [Fact]
    public async Task Duplicate_bill_code_error_only_triggers_query()
    {
        var codes = new[] { Code(105), Code(106) };
        var seed = await SeedAsync(NewTag(), codes);
        var uploadCalls = 0;
        var queryCalls = 0;
        Func<TopCall, TopRawResponse> handler = call =>
        {
            if (call.Method == UploadDispatchService.UploadRetailMethod) { uploadCalls++; return DuplicateBillCode("r-fj-5"); }
            queryCalls++;
            return BillStatusAllSuccess(codes);
        };

        var outcome = await NewDispatchService(handler)
            .DispatchAsync(seed.PartId, Worker, seed.FencingToken, default);

        Assert.Equal(DispatchOutcome.AcceptanceUnknown, outcome);
        Assert.Equal("AcceptanceUnknown", await PartStateAsync(seed.PartId));
        Assert.Equal(1, uploadCalls);
        Assert.Equal(1, await UploadAttemptsAsync(seed.TaskId));
        Assert.Equal(1, await QueryJobsAsync(seed.PartId, "PENDING"));

        await using (var c = await _fx.OpenAsync())
            await ClaimAsync(c, "bill-query", $"query:part:{seed.PartId}");
        var fencingToken = await ScalarAsync<long>(
            "SELECT fencing_token FROM dbo.outbox_job WHERE job_kind=N'bill-query' AND intent_key=@k",
            new { k = $"query:part:{seed.PartId}" });
        var queryOutcome = await NewQueryService(handler).QueryPartAsync(seed.PartId, Worker, fencingToken, default);

        Assert.Equal(QueryOutcome.Succeeded, queryOutcome);
        Assert.Equal(1, queryCalls); // 收敛结论来自一次 QUERY
        Assert.Equal(1, uploadCalls); // 重复号从未触发第二次 UPLOAD
        Assert.Equal(1, await UploadAttemptsAsync(seed.TaskId));
        Assert.Equal(1, await QueryAttemptsAsync(seed.TaskId));
        Assert.Equal("Succeeded", await PartStateAsync(seed.PartId));
    }

    // ---------------------------------------------------------------- 6

    /// <summary>
    /// 回执提交前崩溃：平台已给出"受理成功"的应答，但 T3 未提交，本地不得据此落成功——
    /// 崩溃后任务/分片停在 T2 预发送标记且尝试行保持开放；恢复后分片与任务精确落在受理未知，
    /// 绝不出现受理成功/逐码成功/核验流水证据。
    /// </summary>
    [Fact]
    public async Task Crash_before_receipt_commit_keeps_accepted_unknown_and_no_task_success()
    {
        var seed = await SeedAsync(NewTag(), new[] { Code(107) });
        var service = NewDispatchService(_ => Accepted("r-fj-6")); // 平台应答是受理成功
        service.BeforeCommit = () => throw new SimulatedCrashException("回执已写入事务、提交前进程退出");

        await Assert.ThrowsAsync<SimulatedCrashException>(() =>
            service.DispatchAsync(seed.PartId, Worker, seed.FencingToken, default));

        // 提交前崩溃 ⇒ 回执与状态推进全部回滚：任务/分片只停在 T2 预发送标记，尝试行保持开放。
        Assert.Equal("Dispatching", await PartStateAsync(seed.PartId));
        Assert.Equal("Dispatching", await TaskStateAsync(seed.TaskId));
        Assert.Equal(1, await OpenAttemptsAsync(seed.TaskId, "UPLOAD"));
        var attempt = await AttemptAsync(seed.TaskId, "UPLOAD", 1);
        Assert.Null(attempt.FinishedAt);
        Assert.Null(attempt.RequestId);
        Assert.Null(attempt.ErrorKind);
        Assert.Null(attempt.RawResponse);
        Assert.False(attempt.IsStale);
        Assert.Equal(0, await VerificationRowsAsync(seed.TaskId)); // 未提交的受理成功不得产生核验流水

        await ExpireLeaseAsync(seed.JobId);
        Assert.True(await RecoverAsync() >= 1);

        Assert.Equal("AcceptanceUnknown", await PartStateAsync(seed.PartId));
        Assert.Equal("AcceptanceUnknown", await TaskStateAsync(seed.TaskId)); // 恢复事务内精确重算聚合
        Assert.Equal(1, await QueryJobsAsync(seed.PartId, "PENDING"));
        Assert.Equal(1, await UploadAttemptsAsync(seed.TaskId));
        Assert.Equal(0, await VerificationRowsAsync(seed.TaskId)); // 恢复也不得凭未提交回执推进核验
    }

    // ---------------------------------------------------------------- 7

    /// <summary>
    /// 同一业务意图（同 org/env/docKey/taskType）4 线程并发建任务：只产生 1 个任务、1 个分片、
    /// 1 个发送作业，bill_code 稳定（后续复用不换单号），事件与占用也只落一次。
    /// </summary>
    [Fact]
    public async Task Same_business_intent_creates_exactly_one_task_and_one_part_bill_code()
    {
        var tag = NewTag();
        var codes = new[] { Code(171), Code(172), Code(173) };
        await using var seedConn = await _fx.OpenAsync();
        var orgId = await SeedOrgAsync(seedConn);
        var stored = new List<(long CodeId, string CodeNormalized)>();
        await using (var tx = (SqlTransaction)await seedConn.BeginTransactionAsync())
        {
            foreach (var code in codes)
            {
                var row = await TraceCodeStore.EnsureAsync(seedConn, tx, code, default);
                stored.Add((row.Id, row.Normalized));
            }
            await tx.CommitAsync();
        }

        var ctx = new RetailTaskContext(orgId, "MOCK", null, $"FJ-{tag}",
            new DateTime(2026, 11, 22, 6, 0, 0, DateTimeKind.Utc), new string('a', 64), "map-1", "r1",
            new[] { new RetailLine(1, "1", "SP1", "PD1", 3m, "盒", "B1", stored) }, RetailTaskFactory.Retail321);
        var settings = new SettingsService(_fx.ConnectionString, new DpapiProtector());
        var factory = new RetailTaskFactory(settings, new JobQueue(_fx.ConnectionString, new AuditLog()));

        // 4 条独立连接/事务先各自就绪，再由同一放行信号同时进入工厂，确保调用真正重叠。
        var ready = Enumerable.Range(0, 4)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workers = Enumerable.Range(0, 4).Select(async i =>
        {
            await using var conn = await _fx.OpenAsync();
            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
            ready[i].SetResult();
            await release.Task;
            var result = await factory.CreateAsync(conn, tx, ctx, default);
            await tx.CommitAsync();
            return result;
        }).ToArray();
        await Task.WhenAll(ready.Select(t => t.Task));
        release.SetResult();
        var results = await Task.WhenAll(workers);

        var taskIds = results.Select(r => r.TaskId).Distinct().ToList();
        Assert.Single(taskIds);
        var taskId = taskIds[0];
        Assert.Equal(1, results.Count(r => !r.Reused)); // 恰好一次真正创建，其余复用

        var partIds = results.SelectMany(r => r.PartIds).Distinct().ToList();
        Assert.Single(partIds);
        var partId = partIds[0];
        var billCode = await BillCodeAsync(partId);

        Assert.Equal(1, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.upload_task WHERE org_id=@orgId AND env=N'MOCK' AND event_key=@key",
            new { orgId, key = $"KSOA:FJ-{tag}:RETAIL_321" }));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.upload_part WHERE task_id=@id", new { id = taskId }));
        Assert.Equal(1, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=N'upload-dispatch' AND intent_key=@k",
            new { k = $"part:{partId}" }));
        Assert.Equal(codes.Length, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.code_claim WHERE upload_task_id=@id AND claim_state=N'ACTIVE'", new { id = taskId }));
        Assert.Equal(codes.Length, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.code_event WHERE upload_task_id=@id AND event_type=N'SALE'", new { id = taskId }));

        // 单号稳定：同意图再次调用返回同一任务/分片，bill_code 不变。
        await using (var conn = await _fx.OpenAsync())
        {
            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
            var again = await factory.CreateAsync(conn, tx, ctx, default);
            await tx.CommitAsync();
            Assert.True(again.Reused);
            Assert.Equal(taskId, again.TaskId);
            Assert.Equal(partId, Assert.Single(again.PartIds));
        }
        Assert.Equal(billCode, await BillCodeAsync(partId));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.upload_part WHERE task_id=@id", new { id = taskId }));
    }

    // ---------------------------------------------------------------- 8

    /// <summary>
    /// MOCK 成功不得推进 LIVE 状态：env=LIVE 的任务经 Fake 网关必抛 <see cref="TopNotCertifiedException"/>，
    /// 网关处理函数零调用、无回执、无回查作业、无逐码结论；任务/分片只保留 HTTP 之前写入的发送中标记，
    /// 逐码占用与码事件整行不变，绝不出现任何"受理成功/逐码成功"的推进。
    /// </summary>
    [Fact]
    public async Task Mock_success_never_advances_live_state()
    {
        var tag = NewTag();
        var codes = new[] { Code(181), Code(182) };
        var seed = await SeedLiveTaskAsync(tag, codes);

        // 台账基线：逐码 ACTIVE 占用 + SALE 事件均归属本任务（基线为空时"未变化"断言不可证伪）。
        var claimsBefore = await ClaimRowsAsync(seed.TaskId);
        var eventsBefore = await EventRowsAsync(seed.TaskId);
        Assert.Equal(codes.Length, claimsBefore.Count);
        Assert.All(claimsBefore, row =>
        {
            Assert.Equal("ACTIVE", row.ClaimState);
            Assert.Equal(seed.TaskId, row.UploadTaskId);
        });
        Assert.Equal(codes.Length, eventsBefore.Count);
        Assert.All(eventsBefore, row =>
        {
            Assert.Equal("SALE", row.EventType);
            Assert.Equal(seed.TaskId, row.UploadTaskId);
        });

        var gatewayCalls = 0;
        var service = NewDispatchService(_ => { gatewayCalls++; return Accepted("r-fj-8"); });

        await Assert.ThrowsAsync<TopNotCertifiedException>(() =>
            service.DispatchAsync(seed.PartId, Worker, seed.FencingToken, default));

        Assert.Equal(0, gatewayCalls); // Fake 网关在进入处理函数之前就拒绝 LIVE
        // T2 的预发送标记（发送中 + 尝试行未收尾）是本地证据；结论层零推进。
        Assert.Equal("Dispatching", await PartStateAsync(seed.PartId));
        Assert.Equal("Dispatching", await TaskStateAsync(seed.TaskId));
        var attempt = await AttemptAsync(seed.TaskId, "UPLOAD", 1);
        Assert.Null(attempt.FinishedAt);
        Assert.Null(attempt.RequestId);
        Assert.Null(attempt.ErrorKind);
        Assert.False(attempt.IsStale);
        Assert.Equal(0, await QueryJobsAsync(seed.PartId, "PENDING"));
        // 码台账整行不变：占用仍 ACTIVE、事件仍归属本任务（LIVE 失败不得改写/释放任何码证据）。
        Assert.Equal(claimsBefore, await ClaimRowsAsync(seed.TaskId));
        Assert.Equal(eventsBefore, await EventRowsAsync(seed.TaskId));
    }
}
