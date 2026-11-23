using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Domain.Top;
using TraceFlow.Infrastructure.Jobs;
using TraceFlow.Infrastructure.Security;
using TraceFlow.Infrastructure.Top;
using TraceFlow.Infrastructure.Uploads;
using Xunit;

namespace TraceFlow.IntegrationTests.Uploads;

/// <summary>
/// 发送协议 upload-dispatch（IDEM-04 / §3.2）：短事务先落尝试再发 HTTP、T3 fencing 守卫、
/// 回执→状态映射、任务级聚合、限流/暂停的受理未知与退避、崩溃后"先回查"。
/// 平台一侧只用 <see cref="FakeTopGateway"/>（env=MOCK）；LIVE 必须 fail-closed（TopNotCertifiedException）。
/// </summary>
public class UploadDispatchTests : IClassFixture<Sql2008Fixture>
{
    private const string Worker = "w1";

    private readonly Sql2008Fixture _fx;
    public UploadDispatchTests(Sql2008Fixture fx) => _fx = fx;

    private sealed record Seed(long OrgId, long TaskId, long PartId, long JobId, long FencingToken);

    private UploadDispatchService NewService(Func<TopCall, TopRawResponse> handler, IJobQueue? queue = null)
    {
        var audit = new AuditLog();
        var q = queue ?? new JobQueue(_fx.ConnectionString, audit);
        var top = new TopClientService(_fx.ConnectionString, new FakeTopGateway(handler), new RateBudgetService(),
            new ChannelPauseService(), audit);
        return new UploadDispatchService(_fx.ConnectionString, top, q, new SendRecoveryService(_fx.ConnectionString, q));
    }

    private static TopRawResponse Accepted() => new(true, 200, null,
        """{"response_success":true,"msg_code":"SUCCESS","request_id":"mock-req-1","result":{"bill_code":"TF-TEST","task_id":"MOCK-TASK-1"}}""", null);

    private static async Task<long> SeedOrgAsync(SqlConnection c)
        => await c.ExecuteScalarAsync<long>(@"
IF NOT EXISTS (SELECT 1 FROM dbo.organization WHERE org_code=N'O1')
    INSERT INTO dbo.organization(org_code,name,ref_ent_id,ent_id) VALUES(N'O1',N'店',N'REF-STORE',N'ENT-STORE');
SELECT id FROM dbo.organization WHERE org_code=N'O1'");

    private static string PartPayloadJson(string tag)
        => JsonSerializer.Serialize(new
        {
            eventKey = $"KSOA:SALE-{tag}:RETAIL_321",
            taskType = "RETAIL_321",
            partIndex = 0,
            businessDateTimeUtc = new DateTime(2026, 11, 20, 6, 0, 0, DateTimeKind.Utc),
            ruleVersion = "r1",
            lines = Array.Empty<object>(),
        });

    /// <summary>播种任务+分片+发送作业，并原子认领（fencing_token+1，租约持有者=w1）。</summary>
    private async Task<Seed> SeedAsync(string tag, string partState = "Ready", string taskState = "Ready",
        string taskType = "RETAIL_321", string env = "MOCK")
    {
        await using var c = await _fx.OpenAsync();
        var orgId = await SeedOrgAsync(c);
        var taskId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upload_task(org_id,env,event_key,task_type,source_hash,payload_hash,payload_json,state)
OUTPUT inserted.id
VALUES(@orgId,@env,@key,@type,REPLICATE('a',64),REPLICATE('b',64),@payload,@state)",
            new { orgId, env, key = $"KSOA:SALE-{tag}:{taskType}", type = taskType, payload = PartPayloadJson(tag), state = taskState });
        var partId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upload_part(task_id,org_id,env,part_index,bill_code,payload_json,payload_hash,code_set_hash,state)
OUTPUT inserted.id
VALUES(@taskId,@orgId,@env,0,@bill,@payload,REPLICATE('b',64),REPLICATE('c',64),@state)",
            new { taskId, orgId, env, bill = "TFD" + tag, payload = PartPayloadJson(tag), state = partState });
        var q = new JobQueue(_fx.ConnectionString, new AuditLog());
        long jobId;
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            jobId = await q.EnqueueAsync(c, tx, orgId, env, "upload-dispatch", $"part:{partId}",
                JsonSerializer.Serialize(new { orgId, env, taskId, partId, trigger = "test" }), null, default);
            await tx.CommitAsync();
        }
        var token = await ClaimAsync(c, partId);
        return new Seed(orgId, taskId, partId, jobId, token);
    }

    /// <summary>定向认领本测试的作业（避免与同库其他 PENDING 作业竞争），返回递增后的 fencing_token。</summary>
    private static Task<long> ClaimAsync(SqlConnection c, long partId)
        => c.ExecuteScalarAsync<long>(@"
UPDATE dbo.outbox_job
SET job_state=N'RUNNING', lease_owner=@w, lease_until=DATEADD(minute,5,SYSUTCDATETIME()),
    fencing_token=fencing_token+1, attempt_count=attempt_count+1
OUTPUT inserted.fencing_token
WHERE job_kind=N'upload-dispatch' AND intent_key=@intent AND job_state=N'PENDING'",
            new { w = Worker, intent = $"part:{partId}" });

    private async Task<string?> PartStateAsync(long partId)
    {
        await using var c = await _fx.OpenAsync();
        return await c.ExecuteScalarAsync<string>("SELECT state FROM dbo.upload_part WHERE id=@id", new { id = partId });
    }

    private async Task<string?> TaskStateAsync(long taskId)
    {
        await using var c = await _fx.OpenAsync();
        return await c.ExecuteScalarAsync<string>("SELECT state FROM dbo.upload_task WHERE id=@id", new { id = taskId });
    }

    private async Task<int> QueryJobCountAsync(long partId, string state = "PENDING")
    {
        await using var c = await _fx.OpenAsync();
        return await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=N'bill-query' AND intent_key=@k AND job_state=@state",
            new { k = $"query:part:{partId}", state });
    }

    private async Task<int> UploadAttemptCountAsync(long taskId)
    {
        await using var c = await _fx.OpenAsync();
        return await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.upload_attempt WHERE task_id=@id AND attempt_kind=N'UPLOAD'", new { id = taskId });
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public async Task Successful_upload_sets_accepted_and_schedules_query()
    {
        var tag = Tag();
        var seed = await SeedAsync(tag);
        var service = NewService(_ => Accepted());

        var outcome = await service.DispatchAsync(seed.PartId, Worker, seed.FencingToken, default);

        Assert.Equal(DispatchOutcome.Accepted, outcome);
        Assert.Equal("Accepted", await PartStateAsync(seed.PartId));
        Assert.Equal("Accepted", await TaskStateAsync(seed.TaskId));
        Assert.Equal(1, await UploadAttemptCountAsync(seed.TaskId));
        Assert.Equal(1, await QueryJobCountAsync(seed.PartId));
        await using var c = await _fx.OpenAsync();
        var attempt = await c.QuerySingleAsync<(string? requestId, string? platformTaskId, string? errorKind, DateTime? finishedAt)>(
            @"SELECT request_id AS requestId, platform_task_id AS platformTaskId, error_kind AS errorKind, finished_at AS finishedAt
              FROM dbo.upload_attempt WHERE task_id=@id AND attempt_kind=N'UPLOAD'", new { id = seed.TaskId });
        Assert.Equal("mock-req-1", attempt.requestId);
        Assert.Equal("MOCK-TASK-1", attempt.platformTaskId);
        Assert.Null(attempt.errorKind);
        Assert.NotNull(attempt.finishedAt);
        Assert.NotNull(await c.ExecuteScalarAsync<DateTime?>("SELECT first_sent_at FROM dbo.upload_part WHERE id=@id", new { id = seed.PartId }));
    }

    [Fact]
    public async Task Timeout_sets_acceptance_unknown_and_schedules_query()
    {
        var seed = await SeedAsync(Tag());
        var service = NewService(_ => new TopRawResponse(false, null, null, null, null));

        var outcome = await service.DispatchAsync(seed.PartId, Worker, seed.FencingToken, default);

        Assert.Equal(DispatchOutcome.AcceptanceUnknown, outcome);
        Assert.Equal("AcceptanceUnknown", await PartStateAsync(seed.PartId));
        Assert.Equal(1, await QueryJobCountAsync(seed.PartId));
        await using var c = await _fx.OpenAsync();
        Assert.Equal("TRANSPORT_UNCERTAIN", await c.ExecuteScalarAsync<string>(
            "SELECT error_kind FROM dbo.upload_attempt WHERE task_id=@id AND attempt_kind=N'UPLOAD'", new { id = seed.TaskId }));
    }

    [Fact]
    public async Task Duplicate_bill_code_moves_to_acceptance_unknown_only()
    {
        var seed = await SeedAsync(Tag());
        var calls = 0;
        var service = NewService(_ =>
        {
            calls++;
            return new TopRawResponse(true, 200, null,
                """{"response_success":false,"sub_msg_code":"FAIL_BIZ_BILL_CODE_DUPLICATE","sub_msg":"单据号重复","request_id":"r-dup"}""", null);
        });

        var outcome = await service.DispatchAsync(seed.PartId, Worker, seed.FencingToken, default);

        Assert.Equal(DispatchOutcome.AcceptanceUnknown, outcome);
        Assert.Equal(1, calls); // 重复号不得触发第二次上传
        Assert.Equal(1, await UploadAttemptCountAsync(seed.TaskId));
        Assert.Equal("AcceptanceUnknown", await PartStateAsync(seed.PartId));
        Assert.Equal(1, await QueryJobCountAsync(seed.PartId));
        await using var c = await _fx.OpenAsync();
        Assert.Equal("DUPLICATE_BILL_CODE", await c.ExecuteScalarAsync<string>(
            "SELECT error_kind FROM dbo.upload_attempt WHERE task_id=@id AND attempt_kind=N'UPLOAD'", new { id = seed.TaskId }));
    }

    [Fact]
    public async Task Business_rejection_sets_failed_without_query()
    {
        var seed = await SeedAsync(Tag());
        var service = NewService(_ => new TopRawResponse(true, 200, null,
            """{"response_success":false,"msg_code":"FAIL_BIZ_CHECK","sub_msg":"批号不符","request_id":"r-rej"}""", null));

        var outcome = await service.DispatchAsync(seed.PartId, Worker, seed.FencingToken, default);

        Assert.Equal(DispatchOutcome.Failed, outcome);
        Assert.Equal("Failed", await PartStateAsync(seed.PartId));
        Assert.Equal("Failed", await TaskStateAsync(seed.TaskId));
        Assert.Equal(0, await QueryJobCountAsync(seed.PartId));
        await using var c = await _fx.OpenAsync();
        Assert.Equal("BUSINESS_REJECTED", await c.ExecuteScalarAsync<string>(
            "SELECT error_kind FROM dbo.upload_attempt WHERE task_id=@id AND attempt_kind=N'UPLOAD'", new { id = seed.TaskId }));
    }

    [Fact]
    public async Task Partial_flag_sets_partial_and_records_no_success()
    {
        var seed = await SeedAsync(Tag());
        var service = NewService(_ => new TopRawResponse(true, 200, null,
            """{"response_success":false,"msg_code":"WARN_BIZ_PARTSUCCESS","request_id":"r-part","result":{"success_count":3,"failed_codes":["81234567890123456789"]}}""", null));

        var outcome = await service.DispatchAsync(seed.PartId, Worker, seed.FencingToken, default);

        Assert.Equal(DispatchOutcome.Accepted, outcome);
        Assert.Equal("PartialSuccess", await PartStateAsync(seed.PartId));
        Assert.Equal(1, await QueryJobCountAsync(seed.PartId)); // 部分成功仍需回查逐码结论
        await using var c = await _fx.OpenAsync();
        Assert.Equal(1, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.upload_attempt WHERE task_id=@id AND raw_response LIKE N'%success_count%'", new { id = seed.TaskId }));
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.code_result WHERE task_id=@id", new { id = seed.TaskId }));
        Assert.Equal(0, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.code_result WHERE task_id=@id AND result=N'SUCCESS'", new { id = seed.TaskId }));
    }

    [Fact]
    public async Task Attempt_row_written_before_http()
    {
        var seed = await SeedAsync(Tag());
        var observed = new List<(int Attempts, string? PartState, DateTime? FinishedAt, string? LeaseOwner)>();
        var service = NewService(_ =>
        {
            // 网关被调用时（事务外 HTTP 之前的一刻）尝试行必须已提交、分片已在发送中。
            using var conn = new SqlConnection(_fx.ConnectionString);
            var attempts = conn.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM dbo.upload_attempt WHERE part_id=@id AND attempt_kind=N'UPLOAD'", new { id = seed.PartId });
            var part = conn.QuerySingle<(string state, DateTime? finishedAt, string? leaseOwner)>(@"
SELECT p.state AS state, a.finished_at AS finishedAt, a.lease_owner AS leaseOwner
FROM dbo.upload_part p JOIN dbo.upload_attempt a ON a.part_id=p.id AND a.attempt_kind=N'UPLOAD'
WHERE p.id=@id", new { id = seed.PartId });
            observed.Add((attempts, part.state, part.finishedAt, part.leaseOwner));
            return Accepted();
        });

        await service.DispatchAsync(seed.PartId, Worker, seed.FencingToken, default);

        var snapshot = Assert.Single(observed);
        Assert.Equal(1, snapshot.Attempts);
        Assert.Equal("Dispatching", snapshot.PartState);
        Assert.Null(snapshot.FinishedAt); // 回执尚未写回
        Assert.Equal(Worker, snapshot.LeaseOwner);
    }

    [Fact]
    public async Task Stale_worker_cannot_write_back()
    {
        var seed = await SeedAsync(Tag(), partState: "Dispatching");
        long attemptId;
        await using (var c = await _fx.OpenAsync())
        {
            attemptId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upload_attempt(task_id,part_id,attempt_no,attempt_kind,env,lease_owner,fencing_token)
OUTPUT inserted.id VALUES(@taskId,@partId,1,N'UPLOAD',N'MOCK',@w,@token)",
                new { seed.TaskId, seed.PartId, w = Worker, token = seed.FencingToken });
            // 并发重认领：作业 token 递增，旧 token 失效（§2.7）
            await c.ExecuteAsync("UPDATE dbo.outbox_job SET fencing_token=fencing_token+1 WHERE id=@id", new { id = seed.JobId });
        }

        var service = NewService(_ => Accepted());
        var outcome = await service.ApplyReceiptAsync(new AttemptReceipt(attemptId, seed.PartId, seed.TaskId, "MOCK",
            Worker, seed.FencingToken, new TopOutcome(TopOutcomeKind.Success, null, null, null, null, "r-late", null), 200), default);

        Assert.Equal(DispatchOutcome.StaleRejected, outcome);
        Assert.Equal("Dispatching", await PartStateAsync(seed.PartId)); // 状态不推进
        await using var c2 = await _fx.OpenAsync();
        Assert.Equal(1, await c2.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.upload_attempt WHERE task_id=@id AND is_stale_evidence=1", new { id = seed.TaskId }));
        Assert.Null(await c2.ExecuteScalarAsync<DateTime?>(
            "SELECT finished_at FROM dbo.upload_attempt WHERE id=@id", new { id = attemptId })); // 原尝试保持未结
        Assert.Equal(0, await c2.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.code_result WHERE task_id=@id", new { id = seed.TaskId }));
    }

    [Fact]
    public async Task Rate_limit_keeps_acceptance_unknown()
    {
        var seed = await SeedAsync(Tag());
        var service = NewService(_ => throw new TopRateLimitedException(TimeSpan.FromSeconds(30)));

        var outcome = await service.DispatchAsync(seed.PartId, Worker, seed.FencingToken, default);

        Assert.Equal(DispatchOutcome.RateLimited, outcome);
        Assert.Equal("AcceptanceUnknown", await PartStateAsync(seed.PartId)); // 请求可能已发出，不得判坏
        Assert.Equal(1, await QueryJobCountAsync(seed.PartId));
        await using var c = await _fx.OpenAsync();
        Assert.Equal("RATE_LIMITED", await c.ExecuteScalarAsync<string>(
            "SELECT error_kind FROM dbo.upload_attempt WHERE task_id=@id AND attempt_kind=N'UPLOAD'", new { id = seed.TaskId }));
        Assert.Equal("PENDING", await c.ExecuteScalarAsync<string>("SELECT job_state FROM dbo.outbox_job WHERE id=@id", new { id = seed.JobId }));
        Assert.True(await c.ExecuteScalarAsync<DateTime>("SELECT next_run_at FROM dbo.outbox_job WHERE id=@id", new { id = seed.JobId })
            > DateTime.UtcNow.AddSeconds(20)); // 遵守平台等待/退避
    }

    [Fact]
    public async Task Crash_before_http_leaves_dispatching_then_recovery_queries_first()
    {
        var seed = await SeedAsync(Tag());
        var service = NewService(_ => throw new InvalidOperationException("模拟崩溃：HTTP 前进程退出"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DispatchAsync(seed.PartId, Worker, seed.FencingToken, default));

        Assert.Equal("Dispatching", await PartStateAsync(seed.PartId));
        await using (var c = await _fx.OpenAsync())
        {
            Assert.Null(await c.ExecuteScalarAsync<DateTime?>(
                "SELECT finished_at FROM dbo.upload_attempt WHERE task_id=@id AND attempt_kind=N'UPLOAD'", new { id = seed.TaskId }));
            // 崩溃后租约自然过期（无 Worker 续租）
            await c.ExecuteAsync("UPDATE dbo.outbox_job SET lease_until=DATEADD(minute,-1,SYSUTCDATETIME()) WHERE id=@id",
                new { id = seed.JobId });
        }

        // 租约过期后由发送恢复接管：受理未知 + 先回查，绝不再次上传（JOB-02）
        var recovery = new SendRecoveryService(_fx.ConnectionString, new JobQueue(_fx.ConnectionString, new AuditLog()));
        var recovered = await recovery.RecoverAsync(default);

        Assert.True(recovered >= 1);
        Assert.Equal("AcceptanceUnknown", await PartStateAsync(seed.PartId));
        Assert.Equal(1, await QueryJobCountAsync(seed.PartId));
        Assert.Equal(1, await UploadAttemptCountAsync(seed.TaskId)); // 没有第二次 UPLOAD
    }

    [Fact]
    public async Task Non_sendable_part_is_stale_without_http_or_attempt()
    {
        // 已成功分片：即便仍有待认领的作业也不得重发（precondition: part ∈ Ready/RetryWait）。
        var seed = await SeedAsync(Tag(), partState: "Succeeded", taskState: "Succeeded");
        var calls = 0;
        var service = NewService(_ => { calls++; return Accepted(); });

        var outcome = await service.DispatchAsync(seed.PartId, Worker, seed.FencingToken, default);

        Assert.Equal(DispatchOutcome.StaleRejected, outcome);
        Assert.Equal(0, calls);
        Assert.Equal(0, await UploadAttemptCountAsync(seed.TaskId));
        Assert.Equal("Succeeded", await PartStateAsync(seed.PartId));
    }

    [Fact]
    public async Task Live_dispatch_fails_closed_before_any_state_change()
    {
        // G-TOP 未取证：LIVE 经 Fake 网关必须抛 TopNotCertifiedException，分片保持发送中等待恢复，不得判受理。
        var seed = await SeedAsync(Tag(), env: "LIVE");
        var service = NewService(_ => Accepted());

        await Assert.ThrowsAsync<TopNotCertifiedException>(() =>
            service.DispatchAsync(seed.PartId, Worker, seed.FencingToken, default));

        Assert.Equal("Dispatching", await PartStateAsync(seed.PartId));
        await using var c = await _fx.OpenAsync();
        Assert.Null(await c.ExecuteScalarAsync<DateTime?>(
            "SELECT finished_at FROM dbo.upload_attempt WHERE task_id=@id AND attempt_kind=N'UPLOAD'", new { id = seed.TaskId }));
        Assert.Equal(0, await QueryJobCountAsync(seed.PartId));
    }
}