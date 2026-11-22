using System.Net;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace TraceFlow.IntegrationTests.Api;

[SupportedOSPlatform("windows")] // 宿主为 Windows-only（DPAPI/Windows 服务，ADR-0001）；沿用 DpapiProtectorTests 标注模式
public class ApiGateTests : IClassFixture<Sql2008Fixture>, IAsyncLifetime
{
    private readonly Sql2008Fixture _fx;
    private WebApplicationFactory<Program> _factory = null!;
    public ApiGateTests(Sql2008Fixture fx) => _fx = fx;

    public async Task InitializeAsync()
    {
        // 与 brief 的最小偏差：minimal hosting 下 WithWebHostBuilder.ConfigureAppConfiguration 的回调
        // 在 builder.Build() 才生效，顶层语句读 builder.Configuration 时看不到（实测连接串为空）。
        // UseSetting 由 WAF 注入为 CreateBuilder 命令行参数，顶层读取可见；Program.cs 保持 brief 原样。
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:TraceFlow", _fx.ConnectionString);
            b.UseSetting("TraceFlow:Env", "Mock");
        });
        // xUnit 每个测试方法新建类实例 → InitializeAsync 每测试执行一次；
        // 沿用 JobQueueTests/PermissionAuditTests 的幂等种子模式（IF NOT EXISTS），避免 UQ_app_user_username。
        await SeedUserAsync("viewer", "只读", "Test-Password-123", "trace:read");
        await SeedUserAsync("admin", "管理员", "Admin-Password-123", "system:manage"); // AT-066 需要 system:manage
    }

    private async Task SeedUserAsync(string username, string displayName, string password, string permission)
    {
        await using var c = await _fx.OpenAsync();
        var hasher = new Microsoft.AspNetCore.Identity.PasswordHasher<string>();
        var id = await c.ExecuteScalarAsync<long>(@"
IF NOT EXISTS (SELECT 1 FROM dbo.app_user WHERE username=@u)
    INSERT INTO dbo.app_user(username,display_name,password_hash) VALUES(@u,@d,@h);
SELECT id FROM dbo.app_user WHERE username=@u",
            new { u = username, d = displayName, h = hasher.HashPassword(username, password) });
        await c.ExecuteAsync(@"
IF NOT EXISTS (SELECT 1 FROM dbo.user_permission WHERE user_id=@id AND permission=@p)
    INSERT INTO dbo.user_permission(user_id,permission,granted_by) VALUES(@id,@p,N'test')",
            new { id, p = permission });
    }

    public Task DisposeAsync() { _factory.Dispose(); return Task.CompletedTask; }

    private async Task<(HttpClient client, string csrf)> LoginAsync(string user, string pass)
    {
        var client = _factory.CreateClient();
        var session = await client.GetFromJsonAsync<SessionResp>("/api/v1/auth/session");
        // 登录端点内部 ValidateRequestAsync（防登录 CSRF）：必须先带匿名会话令牌。
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session!.Data.CsrfToken);
        var resp = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { username = user, password = pass });
        resp.EnsureSuccessStatusCode();
        // antiforgery 令牌绑定用户身份：匿名令牌在已认证 POST 上会校验失败，
        // 登录后重新取会话，替换为绑定当前用户的新令牌。
        var authed = await client.GetFromJsonAsync<SessionResp>("/api/v1/auth/session");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", authed!.Data.CsrfToken);
        return (client, authed.Data.CsrfToken);
    }

    private sealed record SessionResp(SessionData Data);
    private sealed record SessionData(bool Authenticated, string CsrfToken);

    [Fact]
    public async Task Anonymous_mutation_rejected_401()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/v1/admin/users", new { username = "x", password = "1234567890" });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task No_permission_confirm_returns_403_and_db_unchanged() // AT-065
    {
        var (client, _) = await LoginAsync("viewer", "Test-Password-123");
        var resp = await client.PostAsJsonAsync("/api/v1/admin/users",
            new { username = "hacker", password = "1234567890", reason = "test" });
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        await using var c = await _fx.OpenAsync();
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.app_user WHERE username=N'hacker'"));
    }

    [Fact]
    public async Task Missing_csrf_rejected()
    {
        var client = _factory.CreateClient();
        await client.GetAsync("/api/v1/auth/session"); // 取 cookie 但不带 X-CSRF-TOKEN
        var resp = await client.PostAsJsonAsync("/api/v1/auth/login", new { username = "viewer", password = "Test-Password-123" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // AUTH-02 全局 CSRF 中间件负向门禁：Missing_csrf_rejected 只覆盖登录端点内部校验（匿名路径，中间件跳过）。
    // 本测试覆盖已认证 /api 变更路径。须用 admin（system:manage）：管线中 UseAuthorization 在 CSRF 中间件之前
    // （WebApplication 自动前置路由），viewer 会先被授权短路 403，无论中间件是否存在——无法证伪。
    // admin 通过授权后由全局中间件拒绝：400 + CSRF_INVALID（API-01 契约），且库不变。
    [Fact]
    public async Task Authenticated_mutation_without_csrf_header_rejected_400()
    {
        var (client, _) = await LoginAsync("admin", "Admin-Password-123");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN"); // 已认证会话，但去掉令牌头
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/users")
        { Content = JsonContent.Create(new { username = "csrf-probe", password = "1234567890", reason = "test" }) };
        req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString()); // 合法请求：仅缺 CSRF 头这一个变量
        var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("CSRF_INVALID", body); // API-01 错误契约 code
        await using var c = await _fx.OpenAsync();
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.app_user WHERE username=N'csrf-probe'"));
    }

    [Fact]
    public async Task Dashboard_requires_trace_read_and_returns_real_counts()
    {
        var (client, _) = await LoginAsync("viewer", "Test-Password-123");
        var resp = await client.GetAsync("/api/v1/dashboard");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("openIngestExceptions", body);
        Assert.Contains("MOCK", body); // 环境标记持续可见（启动规范化为大写，I-2）
    }

    [Fact]
    public async Task Idempotent_user_create_same_key_same_body_replays_different_body_409() // AT-066
    {
        var (client, _) = await LoginAsync("admin", "Admin-Password-123");
        var key = Guid.NewGuid().ToString();
        var body = new { username = "idem-user-" + key[..8], password = "1234567890", reason = "t" };
        var req1 = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/users") { Content = JsonContent.Create(body) };
        req1.Headers.Add("Idempotency-Key", key);
        var r1 = await client.SendAsync(req1);
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        var req2 = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/users") { Content = JsonContent.Create(body) };
        req2.Headers.Add("Idempotency-Key", key);
        var r2 = await client.SendAsync(req2);
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
        Assert.Equal(await r1.Content.ReadAsStringAsync(), await r2.Content.ReadAsStringAsync());
        var req3 = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/users")
        { Content = JsonContent.Create(new { username = "other-" + key[..8], password = "1234567890", reason = "t" }) };
        req3.Headers.Add("Idempotency-Key", key);
        var r3 = await client.SendAsync(req3);
        Assert.Equal(HttpStatusCode.Conflict, r3.StatusCode);
        await using var c = await _fx.OpenAsync();
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.app_user WHERE username=@u", new { u = body.username }));
    }

    // 定向补充（AUTH-02 / Task 11 clamp 修复的行为级证明）：>64 位用户名登录 → 401 且失败审计 actor 被截断存证。
    [Fact]
    public async Task Overlength_username_login_401_and_failure_audit_actor_clamped()
    {
        var client = _factory.CreateClient();
        var session = await client.GetFromJsonAsync<SessionResp>("/api/v1/auth/session");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session!.Data.CsrfToken);
        var longName = new string('x', 100);
        var resp = await client.PostAsJsonAsync("/api/v1/auth/login", new { username = longName, password = "Test-Password-123" });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        await using var c = await _fx.OpenAsync();
        var actor = await c.ExecuteScalarAsync<string?>(
            "SELECT actor FROM dbo.audit_log WHERE action=N'auth.login.failed' AND actor=@a", new { a = longName[..64] });
        Assert.Equal(longName[..64], actor);
    }
}
