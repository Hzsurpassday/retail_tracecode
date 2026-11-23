using System.Runtime.Versioning;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TraceFlow.IntegrationTests.Api;

/// <summary>
/// T16 携带项 3：GET /products 与 /suppliers 分页边界（MasterEndpoints ROW_NUMBER 分页）。
/// 特征化既有行为：pageSize Math.Clamp(…,1,200)、page Math.Max(1,…)、越界页 records 空且 total 不变。
/// </summary>
[SupportedOSPlatform("windows")]
public class MasterPaginationApiTests : IClassFixture<Sql2008Fixture>, IAsyncLifetime
{
    private readonly Sql2008Fixture _fx;
    private WebApplicationFactory<Program> _factory = null!;
    public MasterPaginationApiTests(Sql2008Fixture fx) => _fx = fx;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:TraceFlow", _fx.ConnectionString);
            b.UseSetting("TraceFlow:Env", "Mock");
        });
        await SeedUserAsync("pageadmin", "管理员", "Admin-Password-123", new[] { "trace:read", "master:match" });
        // 宿主 OrgBootstrap 延迟到宿主实际启动才执行；此处先以 TestSeed 幂等种子 O1 组织（is_active 默认 1）
        // + KSOA 来源系统（OrgBootstrap 见已有 active 组织即不动），再在其下种子 3 商品/3 供应商
        await using var c = await _fx.OpenAsync();
        var (orgId, sysId) = await Sources.TestSeed.EnsureAsync(c);
        for (var i = 1; i <= 3; i++)
        {
            await c.ExecuteAsync(@"
IF NOT EXISTS (SELECT 1 FROM dbo.product WHERE org_id=@org AND source_system_id=@sys AND spid=@spid)
    INSERT INTO dbo.product(org_id,source_system_id,spid,name) VALUES(@org,@sys,@spid,@name)",
                new { org = orgId, sys = sysId, spid = $"T16P{i}", name = $"分页药{i}" });
            await c.ExecuteAsync(@"
IF NOT EXISTS (SELECT 1 FROM dbo.supplier WHERE org_id=@org AND source_system_id=@sys AND dwbh=@dwbh)
    INSERT INTO dbo.supplier(org_id,source_system_id,dwbh,name) VALUES(@org,@sys,@dwbh,@name)",
                new { org = orgId, sys = sysId, dwbh = $"T16D{i}", name = $"分页供应商{i}" });
        }
    }

    public Task DisposeAsync() { _factory.Dispose(); return Task.CompletedTask; }

    private async Task SeedUserAsync(string username, string displayName, string password, string[] perms)
    {
        await using var c = await _fx.OpenAsync();
        var hasher = new PasswordHasher<string>();
        var id = await c.ExecuteScalarAsync<long>(@"
IF NOT EXISTS (SELECT 1 FROM dbo.app_user WHERE username=@u)
    INSERT INTO dbo.app_user(username,display_name,password_hash) VALUES(@u,@d,@h);
SELECT id FROM dbo.app_user WHERE username=@u",
            new { u = username, d = displayName, h = hasher.HashPassword(username, password) });
        foreach (var p in perms)
            await c.ExecuteAsync(@"
IF NOT EXISTS (SELECT 1 FROM dbo.user_permission WHERE user_id=@id AND permission=@p)
    INSERT INTO dbo.user_permission(user_id,permission,granted_by) VALUES(@id,@p,N'test')",
                new { id, p });
    }

    private async Task<HttpClient> LoginAsync(string user, string pass)
    {
        var client = _factory.CreateClient();
        var session = await client.GetFromJsonAsync<SessionResp>("/api/v1/auth/session");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session!.Data.CsrfToken);
        var resp = await client.PostAsJsonAsync("/api/v1/auth/login", new { username = user, password = pass });
        resp.EnsureSuccessStatusCode();
        var session2 = await client.GetFromJsonAsync<SessionResp>("/api/v1/auth/session");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session2!.Data.CsrfToken);
        return client;
    }

    private static async Task<(int Total, int Page, int PageSize, int Count)> PageAsync(HttpClient client, string url)
    {
        var resp = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var d = doc.RootElement.GetProperty("data");
        return (d.GetProperty("total").GetInt32(), d.GetProperty("page").GetInt32(),
            d.GetProperty("pageSize").GetInt32(), d.GetProperty("records").GetArrayLength());
    }

    [Fact]
    public async Task Products_page_size_clamped_and_out_of_range_page_empty()
    {
        var client = await LoginAsync("pageadmin", "Admin-Password-123");
        var baseline = await PageAsync(client, "/api/v1/products?page=1&pageSize=200");
        Assert.True(baseline.Total >= 3);
        Assert.Equal(baseline.Total, baseline.Count); // 种子量远小于 200，首页全量
        var big = await PageAsync(client, "/api/v1/products?pageSize=1000");
        Assert.Equal(200, big.PageSize); // Math.Clamp 上界
        Assert.Equal(baseline.Total, big.Total);
        var zero = await PageAsync(client, "/api/v1/products?pageSize=0");
        Assert.Equal(1, zero.PageSize); // Math.Clamp 下界
        Assert.Equal(1, zero.Count);
        var negative = await PageAsync(client, "/api/v1/products?pageSize=-5&page=0");
        Assert.Equal(1, negative.PageSize);
        Assert.Equal(1, negative.Page); // Math.Max(1, page)
        var far = await PageAsync(client, "/api/v1/products?page=100000&pageSize=200");
        Assert.Equal(0, far.Count); // ROW_NUMBER 越界→空 records
        Assert.Equal(baseline.Total, far.Total); // total 不受分页影响
    }

    [Fact]
    public async Task Suppliers_page_size_clamped_and_out_of_range_page_empty()
    {
        var client = await LoginAsync("pageadmin", "Admin-Password-123");
        var baseline = await PageAsync(client, "/api/v1/suppliers?page=1&pageSize=200");
        Assert.True(baseline.Total >= 3);
        Assert.Equal(baseline.Total, baseline.Count);
        var big = await PageAsync(client, "/api/v1/suppliers?pageSize=1000");
        Assert.Equal(200, big.PageSize);
        Assert.Equal(baseline.Total, big.Total);
        var zero = await PageAsync(client, "/api/v1/suppliers?pageSize=0");
        Assert.Equal(1, zero.PageSize);
        Assert.Equal(1, zero.Count);
        var negative = await PageAsync(client, "/api/v1/suppliers?pageSize=-5&page=0");
        Assert.Equal(1, negative.PageSize);
        Assert.Equal(1, negative.Page);
        var far = await PageAsync(client, "/api/v1/suppliers?page=100000&pageSize=200");
        Assert.Equal(0, far.Count);
        Assert.Equal(baseline.Total, far.Total);
    }

    private sealed record SessionResp(SessionData Data);
    private sealed record SessionData(bool Authenticated, string CsrfToken);
}
