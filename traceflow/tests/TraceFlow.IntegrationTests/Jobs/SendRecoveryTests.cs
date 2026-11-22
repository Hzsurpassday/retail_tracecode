using Dapper;
using TraceFlow.Infrastructure.Jobs;
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

    private async Task<(long taskId, long partId)> SeedDispatchingPartAsync(string suffix, bool leaseExpired)
    {
        await using var c = await _fx.OpenAsync();
        var orgId = await SeedOrgAsync(c);
        var taskId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upload_task(org_id,env,event_key,task_type,source_hash,payload_hash,state)
OUTPUT inserted.id VALUES(@orgId,N'LIVE',N'O1|KSOA|REC-'+@suffix+N'|SALE',N'RETAIL_321',REPLICATE('a',64),REPLICATE('b',64),N'Dispatching')", new { orgId, suffix });
        var partId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upload_part(task_id,org_id,env,part_index,bill_code,payload_json,payload_hash,code_set_hash,state)
OUTPUT inserted.id VALUES(@taskId,@orgId,N'LIVE',0,N'TFREC'+@suffix,N'{}',REPLICATE('b',64),REPLICATE('c',64),N'Dispatching')", new { taskId, orgId, suffix });
        var q = new JobQueue(_fx.ConnectionString);
        await using var tx = (Microsoft.Data.SqlClient.SqlTransaction)await c.BeginTransactionAsync();
        var jobId = await q.EnqueueAsync(c, tx, orgId, "LIVE", "upload-dispatch", $"part:{partId}", null, null, default);
        await tx.CommitAsync();
        var claim = await q.ClaimAsync("dead-worker", "LIVE", new[] { "upload-dispatch" }, TimeSpan.FromMinutes(5), default);
        if (leaseExpired)
            await c.ExecuteAsync("UPDATE dbo.outbox_job SET lease_until=DATEADD(minute,-1,SYSUTCDATETIME()) WHERE id=@jobId", new { jobId });
        return (taskId, partId);
    }

    [Fact]
    public async Task Expired_dispatch_becomes_acceptance_unknown_and_query_only()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var (taskId, partId) = await SeedDispatchingPartAsync(suffix, leaseExpired: true);
        var svc = new SendRecoveryService(_fx.ConnectionString, new JobQueue(_fx.ConnectionString));
        var n = await svc.RecoverAsync(default);
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
        var svc = new SendRecoveryService(_fx.ConnectionString, new JobQueue(_fx.ConnectionString));
        await svc.RecoverAsync(default);
        await using var c = await _fx.OpenAsync();
        Assert.Equal("Dispatching", await c.ExecuteScalarAsync<string>("SELECT state FROM dbo.upload_part WHERE id=@partId", new { partId }));
    }

    [Fact]
    public async Task Recovery_idempotent_second_run_no_duplicate_query_job()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var (_, partId) = await SeedDispatchingPartAsync(suffix, leaseExpired: true);
        var svc = new SendRecoveryService(_fx.ConnectionString, new JobQueue(_fx.ConnectionString));
        await svc.RecoverAsync(default);
        await svc.RecoverAsync(default);
        await using var c = await _fx.OpenAsync();
        Assert.Equal(1, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=N'bill-query' AND intent_key=@k", new { k = $"query:part:{partId}" }));
    }

    [Fact]
    public async Task Stale_receipt_preserved_as_evidence_without_state_change()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var (taskId, partId) = await SeedDispatchingPartAsync(suffix, leaseExpired: false);
        var svc = new SendRecoveryService(_fx.ConnectionString, new JobQueue(_fx.ConnectionString));
        await svc.AppendStaleEvidenceAsync(taskId, partId, 99, "UPLOAD", 1, "{\"response_success\":true}", default);
        await using var c = await _fx.OpenAsync();
        Assert.Equal("Dispatching", await c.ExecuteScalarAsync<string>("SELECT state FROM dbo.upload_part WHERE id=@partId", new { partId }));
        Assert.Equal(1, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.upload_attempt WHERE task_id=@taskId AND is_stale_evidence=1", new { taskId }));
    }
}