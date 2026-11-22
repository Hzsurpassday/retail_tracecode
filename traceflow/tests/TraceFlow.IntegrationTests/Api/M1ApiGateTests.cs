using System.Runtime.Versioning; // brief 缺失此 using（SupportedOSPlatform 所需）；与 ApiGateTests/InputValidationTests 标注模式一致（T6 偏差 1 同型）
using System.Net;
using System.Net.Http.Json;
using Dapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;

namespace TraceFlow.IntegrationTests.Api;

[SupportedOSPlatform("windows")]
public class M1ApiGateTests : IClassFixture<Sql2008Fixture>, IAsyncLifetime
{
    private readonly Sql2008Fixture _fx;
    private WebApplicationFactory<Program> _factory = null!;
    public M1ApiGateTests(Sql2008Fixture fx) => _fx = fx;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:TraceFlow", _fx.ConnectionString);
            b.UseSetting("TraceFlow:Env", "Mock");
        });
        await SeedUserAsync("m1viewer", "只读", "Test-Password-123", new[] { "trace:read" });
        await SeedUserAsync("m1admin", "管理员", "Admin-Password-123",
            new[] { "system:manage", "job:rematch", "audit:read", "trace:read", "master:match", "master:classify" });
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

    private async Task<(HttpClient client, string csrf)> LoginAsync(string user, string pass)
    {
        var client = _factory.CreateClient();
        var session = await client.GetFromJsonAsync<SessionResp>("/api/v1/auth/session");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session!.Data.CsrfToken);
        var resp = await client.PostAsJsonAsync("/api/v1/auth/login", new { username = user, password = pass });
        resp.EnsureSuccessStatusCode();
        var session2 = await client.GetFromJsonAsync<SessionResp>("/api/v1/auth/session");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session2!.Data.CsrfToken);
        return (client, session2.Data.CsrfToken);
    }

    private sealed record SessionResp(SessionData Data);
    private sealed record SessionData(bool Authenticated, string CsrfToken);

    [Fact]
    public async Task Anonymous_gets_401_on_all_m1_endpoints()
    {
        var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/jobs/1")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/audit-logs")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/sync-jobs",
            new { entity = "sale", start = DateTime.UtcNow.AddDays(-1), end = DateTime.UtcNow })).StatusCode);
    }

    [Fact]
    public async Task Viewer_lacks_settings_and_sync_and_audit_permissions()
    {
        var (client, _) = await LoginAsync("m1viewer", "Test-Password-123");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/audit-logs")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/v1/sync-jobs",
            new { entity = "sale", start = DateTime.UtcNow.AddDays(-1), end = DateTime.UtcNow })).StatusCode);
        // trace:read 允许查作业（不存在→404 而非 403）
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/jobs/999999")).StatusCode);
    }

    [Fact]
    public async Task Admin_mutations_require_csrf()
    {
        var (client, csrf) = await LoginAsync("m1admin", "Admin-Password-123");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        var put = await client.PutAsJsonAsync("/api/v1/settings/sync.pollSeconds",
            new { valueJson = "30", expectedVersion = "0", reason = "无CSRF" });
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Contains("CSRF_INVALID", await put.Content.ReadAsStringAsync());
        var post = await client.PostAsJsonAsync("/api/v1/sync-jobs",
            new { entity = "sale", start = DateTime.UtcNow.AddDays(-1), end = DateTime.UtcNow });
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
    }

    [Fact]
    public async Task Settings_update_missing_expected_version_422()
    {
        var (client, _) = await LoginAsync("m1admin", "Admin-Password-123");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var resp = await client.PutAsJsonAsync("/api/v1/settings/sync.pollSeconds", new { valueJson = "30", reason = "缺版本" });
        Assert.Equal((HttpStatusCode)422, resp.StatusCode);
    }

    [Fact]
    public async Task Manual_sync_job_accepted_and_validated()
    {
        var (client, _) = await LoginAsync("m1admin", "Admin-Password-123");
        var now = DateTime.UtcNow;
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var ok = await client.PostAsJsonAsync("/api/v1/sync-jobs",
            new { entity = "sale", start = now.AddDays(-1), end = now });
        Assert.Equal(HttpStatusCode.Accepted, ok.StatusCode);
        var body = await ok.Content.ReadFromJsonAsync<JobAccepted>();
        Assert.False(string.IsNullOrEmpty(body!.Data.JobId));
        // 非法 entity
        client.DefaultRequestHeaders.Remove("Idempotency-Key");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var bad = await client.PostAsJsonAsync("/api/v1/sync-jobs",
            new { entity = "inventory", start = now.AddDays(-1), end = now });
        Assert.Equal((HttpStatusCode)422, bad.StatusCode);
        // end 超过 now+5min
        client.DefaultRequestHeaders.Remove("Idempotency-Key");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var future = await client.PostAsJsonAsync("/api/v1/sync-jobs",
            new { entity = "sale", start = now.AddDays(-1), end = now.AddHours(1) });
        Assert.Equal((HttpStatusCode)422, future.StatusCode);
        // 窗口 >90 天
        client.DefaultRequestHeaders.Remove("Idempotency-Key");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var wide = await client.PostAsJsonAsync("/api/v1/sync-jobs",
            new { entity = "sale", start = now.AddDays(-120), end = now });
        Assert.Equal((HttpStatusCode)422, wide.StatusCode);
        // 作业可查（trace:read 语义，admin 亦有）；后台 Worker 可能已认领，只断言字段存在不锁定状态
        var jobResp = await client.GetAsync($"/api/v1/jobs/{body.Data.JobId}");
        Assert.Equal(HttpStatusCode.OK, jobResp.StatusCode);
        Assert.Contains("jobState", await jobResp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Audit_logs_paginated_and_filterable()
    {
        var (client, _) = await LoginAsync("m1admin", "Admin-Password-123");
        var resp = await client.GetAsync("/api/v1/audit-logs?page=1&pageSize=5&action=auth.login");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("records", body);
        Assert.Contains("total", body);
    }

    private sealed record JobAccepted(JobData Data);
    private sealed record JobData(string JobId);
}
