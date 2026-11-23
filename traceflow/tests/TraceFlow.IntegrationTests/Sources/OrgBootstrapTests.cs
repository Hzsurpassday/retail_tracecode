using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Infrastructure.Bootstrap;

namespace TraceFlow.IntegrationTests.Sources;

public class OrgBootstrapTests : IClassFixture<Sql2008Fixture>
{
    private readonly Sql2008Fixture _fx;
    public OrgBootstrapTests(Sql2008Fixture fx) => _fx = fx;

    [Fact]
    public async Task Ensure_creates_default_org_and_ksoa_then_idempotent()
    {
        await OrgBootstrap.EnsureAsync(_fx.ConnectionString);
        await OrgBootstrap.EnsureAsync(_fx.ConnectionString);
        await using var c = new SqlConnection(_fx.ConnectionString);
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.organization WHERE is_active=1"));
        Assert.Equal("DEFAULT", await c.ExecuteScalarAsync<string>("SELECT org_code FROM dbo.organization"));
        var cfg = await c.ExecuteScalarAsync<string>("SELECT config_json FROM dbo.source_system WHERE system_code=N'KSOA'");
        Assert.Contains("fixture", cfg);
        Assert.Equal("ksoa-fixture-1", await c.ExecuteScalarAsync<string>(
            "SELECT adapter_contract_version FROM dbo.source_system WHERE system_code=N'KSOA'"));
    }
}

// 与 brief 的最小偏差：brief 将两个测试放同一类，但 IClassFixture 每类共享一个测试库，
// 而 UX_organization_single_active（0001）禁止两个 active 组织——DEFAULT 先种子则 EXISTING
// 插入违反唯一索引；EXISTING 先插入则 DEFAULT 断言失败（两种顺序均实测/推演不可共存）。
// 按 Sql2008Fixture“每类独立测试库”语义拆为第二个类，测试体与断言保持 brief 原样。
public class OrgBootstrapExistingOrgTests : IClassFixture<Sql2008Fixture>
{
    private readonly Sql2008Fixture _fx;
    public OrgBootstrapExistingOrgTests(Sql2008Fixture fx) => _fx = fx;

    [Fact]
    public async Task Ensure_never_touches_existing_active_org()
    {
        await using (var c = new SqlConnection(_fx.ConnectionString))
        {
            await c.OpenAsync();
            await c.ExecuteAsync("INSERT INTO dbo.organization(org_code,name) VALUES(N'EXISTING',N'已配置门店')");
        }
        await OrgBootstrap.EnsureAsync(_fx.ConnectionString);
        await using var c2 = new SqlConnection(_fx.ConnectionString);
        Assert.Equal(1, await c2.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.organization"));
        Assert.Equal("EXISTING", await c2.ExecuteScalarAsync<string>("SELECT org_code FROM dbo.organization"));
    }
}
