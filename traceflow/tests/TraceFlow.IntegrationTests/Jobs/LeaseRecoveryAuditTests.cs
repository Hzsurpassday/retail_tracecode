using Dapper;
using TraceFlow.Infrastructure.Jobs;
using TraceFlow.Infrastructure.Security;
using Xunit;

namespace TraceFlow.IntegrationTests.Jobs;

public class LeaseRecoveryAuditTests : IClassFixture<Sql2008Fixture>
{
    private readonly Sql2008Fixture _fx;
    public LeaseRecoveryAuditTests(Sql2008Fixture fx) => _fx = fx;

    private async Task<long> SeedOrgAsync()
    {
        await using var c = await _fx.OpenAsync();
        return await c.ExecuteScalarAsync<long>(@"
IF NOT EXISTS (SELECT 1 FROM dbo.organization WHERE org_code=N'LRAT')
    INSERT INTO dbo.organization(org_code,name) VALUES(N'LRAT',N'回收审计测试店');
SELECT id FROM dbo.organization WHERE org_code=N'LRAT'");
    }

    [Fact]
    public async Task Expired_lease_recovery_writes_audit_row()
    {
        var orgId = await SeedOrgAsync();
        var queue = new JobQueue(_fx.ConnectionString, new AuditLog());
        await using var c = await _fx.OpenAsync();
        await using var tx = (Microsoft.Data.SqlClient.SqlTransaction)await c.BeginTransactionAsync();
        var intent = "lr-" + Guid.NewGuid().ToString("N");
        var jobId = await queue.EnqueueAsync(c, tx, orgId, "MOCK", "source-sync", intent, null, null, default);
        await tx.CommitAsync();
        var claimed = await queue.ClaimAsync("w-test", "MOCK", new[] { "source-sync" }, TimeSpan.FromMinutes(5), default);
        Assert.NotNull(claimed);
        await c.ExecuteAsync("UPDATE dbo.outbox_job SET lease_until=DATEADD(minute,-1,SYSUTCDATETIME()) WHERE id=@id", new { id = jobId });

        var recovered = await queue.RecoverExpiredLeasesAsync(default);

        Assert.True(recovered >= 1);
        var audit = await c.ExecuteScalarAsync<string?>(
            "SELECT after_digest FROM dbo.audit_log WHERE action=N'job.lease.recovered' AND object_type=N'outbox_job' AND object_id=@id",
            new { id = jobId.ToString() });
        Assert.NotNull(audit);
        Assert.Contains("source-sync", audit);
        Assert.Contains("w-test", audit); // prevOwner 留痕
        var state = await c.ExecuteScalarAsync<string>("SELECT job_state FROM dbo.outbox_job WHERE id=@id", new { id = jobId });
        Assert.Equal("PENDING", state);
        var env = await c.ExecuteScalarAsync<string>("SELECT env FROM dbo.audit_log WHERE action=N'job.lease.recovered' AND object_id=@id", new { id = jobId.ToString() });
        // 与 brief 的最小偏差：Dapper 2.1.35 可空注解使 ExecuteScalarAsync<string> 返回 string?，
        // env.Trim() 触发 CS8602（TreatWarningsAsErrors）；改 env?.Trim()，env 为 null 时断言失败而非 NRE。
        Assert.Equal("MOCK", env?.Trim());
    }

    [Fact]
    public async Task Expired_upload_dispatch_is_not_recovered_and_not_audited()
    {
        var orgId = await SeedOrgAsync();
        var queue = new JobQueue(_fx.ConnectionString, new AuditLog());
        await using var c = await _fx.OpenAsync();
        await using var tx = (Microsoft.Data.SqlClient.SqlTransaction)await c.BeginTransactionAsync();
        var intent = "lr-d-" + Guid.NewGuid().ToString("N");
        var jobId = await queue.EnqueueAsync(c, tx, orgId, "MOCK", "upload-dispatch", intent, null, null, default);
        await tx.CommitAsync();
        await queue.ClaimAsync("w-test", "MOCK", new[] { "upload-dispatch" }, TimeSpan.FromMinutes(5), default);
        await c.ExecuteAsync("UPDATE dbo.outbox_job SET lease_until=DATEADD(minute,-1,SYSUTCDATETIME()) WHERE id=@id", new { id = jobId });

        await queue.RecoverExpiredLeasesAsync(default);

        var state = await c.ExecuteScalarAsync<string>("SELECT job_state FROM dbo.outbox_job WHERE id=@id", new { id = jobId });
        Assert.Equal("RUNNING", state); // JOB-02：失租发送必须走发送恢复→回查
        Assert.Equal(0, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.audit_log WHERE action=N'job.lease.recovered' AND object_id=@id", new { id = jobId.ToString() }));
    }
}
