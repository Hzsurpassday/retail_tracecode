using Dapper;
using TraceFlow.Infrastructure.Jobs;
using TraceFlow.Infrastructure.Security;
using Xunit;

namespace TraceFlow.IntegrationTests.Jobs;

public class SendRecoveryTests : IClassFixture<Sql2008Fixture>
{
    private readonly Sql2008Fixture _fx;
    public SendRecoveryTests(Sql2008Fixture fx) => _fx = fx;

    // 与 brief 种子代码的最小偏差（沿用 Task 6–9 模式）：
    // brief 硬编码 org_id=1，但每类独立测试库的 organization 为空（IDENTITY 未种子），
    // upload_task/outbox_job 的 FK 会触发 547。改为幂等共享门店种子（JobQueueTests.SeedOrgAsync 同款），
    // org_id 参数化；其余种子列已逐一对照 0006 DDL（NOT NULL/CHECK/唯一约束）均满足，保持 verbatim。
    private static async Task<long> SeedOrgAsync(Microsoft.Data.SqlClient.SqlConnection c)
        => await c.ExecuteScalarAsync<long>(@"IF NOT EXISTS (SELECT 1 FROM dbo.organization WHERE org_code = N'O1')
                                              INSERT INTO dbo.organization(org_code,name) VALUES(N'O1',N'店');
                                            SELECT id FROM dbo.organization WHERE org_code = N'O1'");

    // claimJob=false：入队后不认领，作业保持 PENDING（模拟 RescheduleAsync 退回的预发送重试）；
    // 默认 true 时行为与原种子完全一致，既有 4 个测试不受影响。
    // env 默认 LIVE（既有语义）；MOCK 用于跨环境恢复测试。
    private async Task<(long taskId, long partId)> SeedDispatchingPartAsync(string suffix, bool leaseExpired,
        bool claimJob = true, string env = "LIVE")
    {
        await using var c = await _fx.OpenAsync();
        var orgId = await SeedOrgAsync(c);
        var taskId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upload_task(org_id,env,event_key,task_type,source_hash,payload_hash,state)
OUTPUT inserted.id VALUES(@orgId,@env,N'O1|KSOA|REC-'+@suffix+N'|SALE',N'RETAIL_321',REPLICATE('a',64),REPLICATE('b',64),N'Dispatching')", new { orgId, suffix, env });
        var partId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upload_part(task_id,org_id,env,part_index,bill_code,payload_json,payload_hash,code_set_hash,state)
OUTPUT inserted.id VALUES(@taskId,@orgId,@env,0,N'TFREC'+@suffix,N'{}',REPLICATE('b',64),REPLICATE('c',64),N'Dispatching')", new { taskId, orgId, suffix, env });
        var q = new JobQueue(_fx.ConnectionString, new AuditLog());
        await using var tx = (Microsoft.Data.SqlClient.SqlTransaction)await c.BeginTransactionAsync();
        var jobId = await q.EnqueueAsync(c, tx, orgId, env, "upload-dispatch", $"part:{partId}", null, null, default);
        await tx.CommitAsync();
        if (claimJob)
        {
            var claim = await q.ClaimAsync("dead-worker", env, new[] { "upload-dispatch" }, TimeSpan.FromMinutes(5), default);
            if (leaseExpired)
                await c.ExecuteAsync("UPDATE dbo.outbox_job SET lease_until=DATEADD(minute,-1,SYSUTCDATETIME()) WHERE id=@jobId", new { jobId });
        }
        return (taskId, partId);
    }

    [Fact]
    public async Task Expired_dispatch_becomes_acceptance_unknown_and_query_only()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var (taskId, partId) = await SeedDispatchingPartAsync(suffix, leaseExpired: true);
        var svc = new SendRecoveryService(_fx.ConnectionString, new JobQueue(_fx.ConnectionString, new AuditLog()));
        var n = await svc.RecoverAsync("LIVE", default);
        Assert.True(n >= 1);
        await using var c = await _fx.OpenAsync();
        Assert.Equal("AcceptanceUnknown", await c.ExecuteScalarAsync<string>("SELECT state FROM dbo.upload_part WHERE id=@partId", new { partId }));
        Assert.Equal(1, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=N'bill-query' AND intent_key=@k AND job_state=N'PENDING'", new { k = $"query:part:{partId}" }));
        Assert.Equal(0, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=N'upload-dispatch' AND intent_key=@k AND job_state=N'PENDING'", new { k = $"part:{partId}" }));
    }

    [Fact]
    public async Task Valid_lease_dispatch_untouched()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var (_, partId) = await SeedDispatchingPartAsync(suffix, leaseExpired: false);
        var svc = new SendRecoveryService(_fx.ConnectionString, new JobQueue(_fx.ConnectionString, new AuditLog()));
        await svc.RecoverAsync("LIVE", default);
        await using var c = await _fx.OpenAsync();
        Assert.Equal("Dispatching", await c.ExecuteScalarAsync<string>("SELECT state FROM dbo.upload_part WHERE id=@partId", new { partId }));
    }

    [Fact]
    public async Task Recovery_idempotent_second_run_no_duplicate_query_job()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var (_, partId) = await SeedDispatchingPartAsync(suffix, leaseExpired: true);
        var svc = new SendRecoveryService(_fx.ConnectionString, new JobQueue(_fx.ConnectionString, new AuditLog()));
        await svc.RecoverAsync("LIVE", default);
        await svc.RecoverAsync("LIVE", default);
        await using var c = await _fx.OpenAsync();
        Assert.Equal(1, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=N'bill-query' AND intent_key=@k", new { k = $"query:part:{partId}" }));
    }

    [Fact]
    public async Task Stale_receipt_preserved_as_evidence_without_state_change()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var (taskId, partId) = await SeedDispatchingPartAsync(suffix, leaseExpired: false);
        var svc = new SendRecoveryService(_fx.ConnectionString, new JobQueue(_fx.ConnectionString, new AuditLog()));
        await svc.AppendStaleEvidenceAsync(taskId, partId, 99, "UPLOAD", 1, "{\"response_success\":true}", default);
        await using var c = await _fx.OpenAsync();
        Assert.Equal("Dispatching", await c.ExecuteScalarAsync<string>("SELECT state FROM dbo.upload_part WHERE id=@partId", new { partId }));
        Assert.Equal(1, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.upload_attempt WHERE task_id=@taskId AND is_stale_evidence=1", new { taskId }));
    }

    // 回归（JOB-02 复审）：RescheduleAsync 可把 dispatch 作业退回 PENDING，而分片仍是 Dispatching。
    // 恢复扫掉该分片后，PENDING 的 dispatch 作业必须一并置 FAILED，否则留下可认领的重上传向量，击穿 query-first。
    [Fact]
    public async Task Pending_dispatch_job_failed_on_recovery_closing_reupload_vector()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var (_, partId) = await SeedDispatchingPartAsync(suffix, leaseExpired: false, claimJob: false);
        var svc = new SendRecoveryService(_fx.ConnectionString, new JobQueue(_fx.ConnectionString, new AuditLog()));
        var n = await svc.RecoverAsync("LIVE", default);
        Assert.True(n >= 1);
        await using var c = await _fx.OpenAsync();
        Assert.Equal("AcceptanceUnknown", await c.ExecuteScalarAsync<string>("SELECT state FROM dbo.upload_part WHERE id=@partId", new { partId }));
        Assert.Equal(0, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=N'upload-dispatch' AND intent_key=@k AND job_state<>N'FAILED'", new { k = $"part:{partId}" }));
        Assert.Equal(1, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=N'upload-dispatch' AND intent_key=@k AND job_state=N'FAILED'", new { k = $"part:{partId}" }));
        Assert.Equal(1, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=N'bill-query' AND intent_key=@k", new { k = $"query:part:{partId}" }));
    }

    /// <summary>
    /// 终审修复 2：恢复按 worker 环境定界。MOCK worker 的恢复扫描绝不触碰 LIVE 的 Dispatching 分片
    /// （不改状态、不失败 LIVE 派发作业、不入队 LIVE 回查、不写该分片审计）。
    /// </summary>
    [Fact]
    public async Task Mock_recovery_leaves_live_dispatching_part_untouched()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var (taskId, partId) = await SeedDispatchingPartAsync(suffix, leaseExpired: true);
        var svc = new SendRecoveryService(_fx.ConnectionString, new JobQueue(_fx.ConnectionString, new AuditLog()));
        var n = await svc.RecoverAsync("MOCK", default);
        Assert.Equal(0, n);
        await using var c = await _fx.OpenAsync();
        Assert.Equal("Dispatching", await c.ExecuteScalarAsync<string>(
            "SELECT state FROM dbo.upload_part WHERE id=@partId", new { partId }));
        Assert.Equal(0, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=N'bill-query' AND intent_key=@k", new { k = $"query:part:{partId}" }));
        Assert.Equal(0, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=N'upload-dispatch' AND intent_key=@k AND job_state=N'FAILED'",
            new { k = $"part:{partId}" }));
        Assert.Equal(0, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.audit_log WHERE action=N'send-recovery' AND object_id=@obj",
            new { obj = partId.ToString(System.Globalization.CultureInfo.InvariantCulture) }));
        Assert.Equal("Dispatching", await c.ExecuteScalarAsync<string>(
            "SELECT state FROM dbo.upload_task WHERE id=@id", new { id = taskId }));
    }

    /// <summary>
    /// 终审修复 2 的另一半：同环境仍按原语义恢复（MOCK 分片由 MOCK worker 收尾），
    /// 审计/回查入队的 env 与被处理分片一致（MOCK），不因审计口径改写而变。
    /// </summary>
    [Fact]
    public async Task Mock_recovery_recovers_mock_part_with_mock_env_evidence()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var (_, partId) = await SeedDispatchingPartAsync(suffix, leaseExpired: true, env: "MOCK");
        var svc = new SendRecoveryService(_fx.ConnectionString, new JobQueue(_fx.ConnectionString, new AuditLog()));
        var n = await svc.RecoverAsync("MOCK", default);
        Assert.Equal(1, n);
        await using var c = await _fx.OpenAsync();
        Assert.Equal("AcceptanceUnknown", await c.ExecuteScalarAsync<string>(
            "SELECT state FROM dbo.upload_part WHERE id=@partId", new { partId }));
        Assert.Equal(1, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.outbox_job WHERE env=N'MOCK' AND job_kind=N'bill-query' AND intent_key=@k",
            new { k = $"query:part:{partId}" }));
        Assert.Equal(1, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.audit_log WHERE action=N'send-recovery' AND env=N'MOCK' AND object_id=@obj",
            new { obj = partId.ToString(System.Globalization.CultureInfo.InvariantCulture) }));
    }
}
