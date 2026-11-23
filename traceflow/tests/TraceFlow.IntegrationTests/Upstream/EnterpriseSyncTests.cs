using System.Text.Json;
using Dapper;
using TraceFlow.Application.Abstractions;
using TraceFlow.Infrastructure.Jobs;
using TraceFlow.Infrastructure.Security;
using TraceFlow.Infrastructure.Top;
using TraceFlow.Infrastructure.Upstream;
using TraceFlow.IntegrationTests.Sources; // brief 缺失此 using（TestSeed 所在命名空间）；编译必需（T5 偏差 1 同型）
using Xunit;

namespace TraceFlow.IntegrationTests.Upstream;

public class EnterpriseSyncTests : IClassFixture<Sql2008Fixture>
{
    private readonly Sql2008Fixture _fx;
    public EnterpriseSyncTests(Sql2008Fixture fx) => _fx = fx;

    private static TopRawResponse Parts(string partsJson, int totalPages = 1)
        // brief 原为 $$ 原始字符串，尾部 "}}" 连续右大括号触发 CS9007；改 $$$（输出 JSON 逐字一致）
        => new(true, 200, null, $$$"""{"response_success":true,"result":{"total_pages":{{{totalPages}}},"parts":{{{partsJson}}}}}""", null);

    private async Task<(EnterpriseSyncService svc, long orgId)> BuildAsync(Func<TopCall, TopRawResponse> handler)
    {
        await using var c = await _fx.OpenAsync();
        var (orgId, _) = await TestSeed.EnsureAsync(c);
        var top = new TopClientService(_fx.ConnectionString, new FakeTopGateway(handler),
            new RateBudgetService(), new ChannelPauseService(), new AuditLog());
        return (new EnterpriseSyncService(_fx.ConnectionString, top, new AuditLog()), orgId);
    }

    [Fact]
    public async Task Rename_creates_new_ent_version_and_old_row_untouched()
    {
        var refId = "REF-R." + Guid.NewGuid().ToString("N")[..8];
        var phase = 0;
        var (svc, orgId) = await BuildAsync(_ => Parts(phase == 0
            ? $$"""[{"ref_ent_id":"{{refId}}","ent_id":"E-OLD","name":"旧名称","credit_code":"CC1","audit_status":"PASS","role":"owner"}]"""
            : $$"""[{"ref_ent_id":"{{refId}}","ent_id":"E-NEW","name":"新名称","credit_code":"CC1","audit_status":"PASS","role":"owner"}]"""));
        var s1 = await svc.RunAsync(orgId, "MOCK", default);
        Assert.Equal(1, s1.VersionsAdded);
        await using var c0 = await _fx.OpenAsync();
        var oldBefore = await c0.QuerySingleAsync<VersionRow>(
            "SELECT name AS Name, audit_status AS AuditStatus, observed_at AS ObservedAt FROM dbo.platform_enterprise_version WHERE ref_ent_id=@r AND ent_id=N'E-OLD'", new { r = refId });
        phase = 1;
        var s2 = await svc.RunAsync(orgId, "MOCK", default);
        Assert.Equal(1, s2.VersionsAdded); // AT-020：改名→新 ent_id 版本行
        await using var c = await _fx.OpenAsync();
        Assert.Equal(2, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.platform_enterprise_version WHERE ref_ent_id=@r", new { r = refId }));
        var oldAfter = await c.QuerySingleAsync<VersionRow>(
            "SELECT name AS Name, audit_status AS AuditStatus, observed_at AS ObservedAt FROM dbo.platform_enterprise_version WHERE ref_ent_id=@r AND ent_id=N'E-OLD'", new { r = refId });
        Assert.Equal(oldBefore.Name, oldAfter.Name); // ENT-02：历史版本字节不动
        Assert.Equal(oldBefore.ObservedAt, oldAfter.ObservedAt);
        Assert.Equal("新名称", await c.ExecuteScalarAsync<string>("SELECT name FROM dbo.platform_enterprise_version WHERE ref_ent_id=@r AND ent_id=N'E-NEW'", new { r = refId }));
    }

    private sealed record VersionRow(string Name, string? AuditStatus, DateTime ObservedAt);

    [Fact]
    public async Task Roles_stored_separately_and_no_supplier_mapping_auto_created()
    {
        var refId = "REF-ROLE." + Guid.NewGuid().ToString("N")[..8];
        var (svc, orgId) = await BuildAsync(_ => Parts($$"""
            [{"ref_ent_id":"{{refId}}","ent_id":"E-D","name":"配送商","credit_code":"CC-D","audit_status":"PASS","role":"deliver"},
             {"ref_ent_id":"{{refId}}","ent_id":"E-O","name":"所有者","credit_code":"CC-O","audit_status":"PASS","role":"owner"}]
            """));
        await svc.RunAsync(orgId, "MOCK", default);
        await using var c = await _fx.OpenAsync();
        // AT-021：deliver 与 owner 角色分别保留，不混同
        Assert.Equal("deliver", await c.ExecuteScalarAsync<string>("SELECT role FROM dbo.platform_enterprise_version WHERE ref_ent_id=@r AND ent_id=N'E-D'", new { r = refId }));
        Assert.Equal("owner", await c.ExecuteScalarAsync<string>("SELECT role FROM dbo.platform_enterprise_version WHERE ref_ent_id=@r AND ent_id=N'E-O'", new { r = refId }));
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.supplier_mapping")); // 不自动建映射
    }

    [Fact]
    public async Task Same_identity_twice_does_not_duplicate_or_touch_observed_at()
    {
        var refId = "REF-DUP." + Guid.NewGuid().ToString("N")[..8];
        var (svc, orgId) = await BuildAsync(_ => Parts($$"""[{"ref_ent_id":"{{refId}}","ent_id":"E-1","name":"单位","credit_code":"CC","audit_status":"PASS","role":"owner"}]"""));
        await svc.RunAsync(orgId, "MOCK", default);
        await using var c0 = await _fx.OpenAsync();
        var first = await c0.ExecuteScalarAsync<DateTime>("SELECT observed_at FROM dbo.platform_enterprise_version WHERE ref_ent_id=@r", new { r = refId });
        var s2 = await svc.RunAsync(orgId, "MOCK", default);
        Assert.Equal(0, s2.VersionsAdded);
        await using var c = await _fx.OpenAsync();
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.platform_enterprise_version WHERE ref_ent_id=@r", new { r = refId }));
        Assert.Equal(first, await c.ExecuteScalarAsync<DateTime>("SELECT observed_at FROM dbo.platform_enterprise_version WHERE ref_ent_id=@r", new { r = refId }));
    }

    [Fact]
    public async Task Live_env_rejected_and_handler_kind_correct()
    {
        var (svc, orgId) = await BuildAsync(_ => Parts("[]"));
        await Assert.ThrowsAsync<TopNotCertifiedException>(() => svc.RunAsync(orgId, "LIVE", default));
        var handler = new EnterpriseSyncJobHandler(svc);
        Assert.Equal("enterprise-sync", handler.Kind);
        var payload = JsonSerializer.Serialize(new { orgId, env = "MOCK", trigger = "daily" });
        await handler.HandleAsync(new ClaimedJob(1, "enterprise-sync", "daily:test", payload, 1, DateTime.UtcNow.AddMinutes(5), 0), default);
    }

    [Fact]
    public async Task Page_failure_throws_for_job_backoff_no_partial_fake()
    {
        var calls = 0;
        var (svc, orgId) = await BuildAsync(_ =>
        {
            calls++;
            return calls == 1
                ? Parts("""[{"ref_ent_id":"REF-PF","ent_id":"E-1","name":"n","credit_code":null,"audit_status":null,"role":null}]""", 2)
                : new TopRawResponse(true, 200, """{"code":2,"sub_code":"isp.top-remote-connection-timeout"}""", null, null);
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RunAsync(orgId, "MOCK", default));
        await using var c = await _fx.OpenAsync();
        // 第 1 页已同事务落库（查询幂等，下轮重拉不重复）
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.platform_enterprise_version WHERE ref_ent_id=N'REF-PF'"));
    }
}
