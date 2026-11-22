using System.Net;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace TraceFlow.IntegrationTests.Api;

[SupportedOSPlatform("windows")]
public class InputValidationTests : IClassFixture<Sql2008Fixture>, IAsyncLifetime
{
    private readonly Sql2008Fixture _fx;
    private WebApplicationFactory<Program> _factory = null!;
    public InputValidationTests(Sql2008Fixture fx) => _fx = fx;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:TraceFlow", _fx.ConnectionString);
            b.UseSetting("TraceFlow:Env", "Mock");
        });
        await using var c = await _fx.OpenAsync();
        var hasher = new Microsoft.AspNetCore.Identity.PasswordHasher<string>();
        await c.ExecuteAsync(@"
IF NOT EXISTS (SELECT 1 FROM dbo.app_user WHERE username=N'ivadmin')
    INSERT INTO dbo.app_user(username,display_name,password_hash) VALUES(N'ivadmin',N'验证管理员',@h)",
            new { h = hasher.HashPassword("ivadmin", "Admin-Password-123") });
        var id = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.app_user WHERE username=N'ivadmin'");
        await c.ExecuteAsync(@"
IF NOT EXISTS (SELECT 1 FROM dbo.user_permission WHERE user_id=@id AND permission=N'system:manage')
    INSERT INTO dbo.user_permission(user_id,permission,granted_by) VALUES(@id,N'system:manage',N'test')", new { id });
    }

    public Task DisposeAsync() { _factory.Dispose(); return Task.CompletedTask; }

    private sealed record SessionResp(SessionData Data);
    private sealed record SessionData(bool Authenticated, string CsrfToken);

    private async Task<HttpClient> AdminClientAsync()
    {
        var client = _factory.CreateClient();
        var session = await client.GetFromJsonAsync<SessionResp>("/api/v1/auth/session");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session!.Data.CsrfToken);
        var resp = await client.PostAsJsonAsync("/api/v1/auth/login", new { username = "ivadmin", password = "Admin-Password-123" });
        resp.EnsureSuccessStatusCode();
        var authed = await client.GetFromJsonAsync<SessionResp>("/api/v1/auth/session");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", authed!.Data.CsrfToken);
        return client;
    }

    private async Task<HttpClient> AnonClientWithCsrfAsync()
    {
        var client = _factory.CreateClient();
        var session = await client.GetFromJsonAsync<SessionResp>("/api/v1/auth/session");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session!.Data.CsrfToken);
        return client;
    }

    [Fact]
    public async Task Login_empty_username_returns_401_and_audits_empty_actor()
    {
        var client = await AnonClientWithCsrfAsync();
        var resp = await client.PostAsJsonAsync("/api/v1/auth/login", new { username = "", password = "whatever123" });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        await using var c = await _fx.OpenAsync();
        Assert.True(await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.audit_log WHERE action=N'auth.login.failed' AND actor=N'(empty)'") >= 1);
    }

    [Fact]
    public async Task Login_null_username_returns_401_not_500()
    {
        var client = await AnonClientWithCsrfAsync();
        var resp = await client.PostAsJsonAsync("/api/v1/auth/login", new { password = "whatever123" });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Theory]
    [InlineData(65, 0, 0)]   // username 超 64
    [InlineData(0, 101, 0)]  // displayName 超 100
    [InlineData(0, 0, 1001)] // reason 超 1000
    public async Task Admin_create_overlength_columns_return_422(int usernameLen, int displayLen, int reasonLen)
    {
        var client = await AdminClientAsync();
        var body = new
        {
            username = usernameLen > 0 ? new string('u', usernameLen) : "iv-ok-" + Guid.NewGuid().ToString("N")[..8],
            password = "1234567890",
            displayName = displayLen > 0 ? new string('d', displayLen) : null,
            reason = reasonLen > 0 ? new string('r', reasonLen) : null,
        };
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/users")
        { Content = JsonContent.Create(body) };
        req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }

    [Fact]
    public async Task Admin_create_null_password_returns_422_not_500()
    {
        var client = await AdminClientAsync();
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/users")
        { Content = JsonContent.Create(new { username = "iv-np-" + Guid.NewGuid().ToString("N")[..8] }) };
        req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }
}
