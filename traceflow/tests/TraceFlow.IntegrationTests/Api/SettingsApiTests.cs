using System.Runtime.Versioning;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TraceFlow.IntegrationTests.Api;

/// <summary>
/// T16 携带项 1：/settings* HTTP 级契约测试（宿主/认证/种子沿用 M1ApiGateTests 模式）。
/// 特征化既有行为：camelCase 记录响应（Results.Json Web）、未持久化键 id=0/version="0"、
/// 密钥/非密钥互斥 422、rowversion 乐观并发 409、连接测试 SSRF 守卫 422、system:manage 权限门禁。
/// </summary>
[SupportedOSPlatform("windows")]
public class SettingsApiTests : IClassFixture<Sql2008Fixture>, IAsyncLifetime
{
    private readonly Sql2008Fixture _fx;
    private WebApplicationFactory<Program> _factory = null!;
    public SettingsApiTests(Sql2008Fixture fx) => _fx = fx;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:TraceFlow", _fx.ConnectionString);
            b.UseSetting("TraceFlow:Env", "Mock");
        });
        await SeedUserAsync("setviewer", "只读", "Test-Password-123", new[] { "trace:read" });
        await SeedUserAsync("setadmin", "管理员", "Admin-Password-123",
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

    private async Task<HttpResponseMessage> PutSettingAsync(HttpClient client, string key, object body)
    {
        client.DefaultRequestHeaders.Remove("Idempotency-Key");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return await client.PutAsJsonAsync($"/api/v1/settings/{key}", body);
    }

    private async Task<SettingViewResp[]> ListAsync(HttpClient client)
    {
        var resp = await client.GetAsync("/api/v1/settings");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<SettingsListResp>())!.Data;
    }

    // 顺序无关：键可能已被本类其他测试持久化，取当前 version（未持久化→"0"）
    private async Task<string> CurrentVersionAsync(HttpClient client, string key)
    {
        var v = (await ListAsync(client)).Single(x => x.Key == key);
        return v.Id == 0 ? "0" : v.Version;
    }

    [Fact]
    public async Task Get_settings_returns_camel_case_views_with_unpersisted_defaults()
    {
        var client = await LoginAsync("setadmin", "Admin-Password-123");
        var resp = await client.GetAsync("/api/v1/settings");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal(JsonValueKind.String, root.GetProperty("requestId").ValueKind);
        var data = root.GetProperty("data");
        Assert.Equal(JsonValueKind.Array, data.ValueKind);
        Assert.Equal(13, data.GetArrayLength()); // SettingsService.Catalog 全目录（含未持久化默认值）；T5 +upstream.pollSeconds（brief 遗漏的目录计数锁，同 Step 6b 型同步）
        foreach (var el in data.EnumerateArray())
        {
            // camelCase 记录形状（Results.Json Web）；id 为 JSON number（内部 int64 代理，裁决 #9）
            Assert.Equal(JsonValueKind.Number, el.GetProperty("id").ValueKind);
            Assert.Equal(JsonValueKind.String, el.GetProperty("key").ValueKind);
            Assert.Equal(JsonValueKind.String, el.GetProperty("version").ValueKind);
            Assert.True(el.TryGetProperty("valueJson", out _));
            Assert.True(el.TryGetProperty("isSecret", out _));
            Assert.True(el.TryGetProperty("hasSecretValue", out _));
            if (el.GetProperty("isSecret").GetBoolean())
                Assert.Equal(JsonValueKind.Null, el.GetProperty("valueJson").ValueKind); // 密钥永不回显
        }
        // import.maxRows 不被本类任何测试写入→必为未持久化默认值
        var untouched = data.EnumerateArray().Single(e => e.GetProperty("key").GetString() == "import.maxRows");
        Assert.Equal(0, untouched.GetProperty("id").GetInt64());
        Assert.Equal("0", untouched.GetProperty("version").GetString());
        Assert.Equal("10000", untouched.GetProperty("valueJson").GetString());
        Assert.False(untouched.GetProperty("hasSecretValue").GetBoolean());
    }

    [Fact]
    public async Task Put_non_secret_key_with_secret_value_422()
    {
        var client = await LoginAsync("setadmin", "Admin-Password-123");
        var resp = await PutSettingAsync(client, "sync.pollSeconds",
            new { valueJson = "30", secretValue = "不应接受", expectedVersion = "0", reason = "互斥校验" });
        Assert.Equal((HttpStatusCode)422, resp.StatusCode);
        Assert.Contains("VALIDATION", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Put_secret_key_with_value_json_422()
    {
        var client = await LoginAsync("setadmin", "Admin-Password-123");
        var resp = await PutSettingAsync(client, "top.appsecret",
            new { valueJson = "\"plain\"", secretValue = "s3cret", expectedVersion = "0", reason = "互斥校验" });
        Assert.Equal((HttpStatusCode)422, resp.StatusCode);
        Assert.Contains("VALIDATION", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Put_stale_expected_version_409_version_conflict()
    {
        var client = await LoginAsync("setadmin", "Admin-Password-123");
        var v0 = await CurrentVersionAsync(client, "sync.pollSeconds");
        var first = await PutSettingAsync(client, "sync.pollSeconds",
            new { valueJson = "30", expectedVersion = v0, reason = "首次写入" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var v1 = (await first.Content.ReadFromJsonAsync<SettingUpdateResp>())!.Data.Version;
        Assert.NotEqual("0", v1);
        var second = await PutSettingAsync(client, "sync.pollSeconds",
            new { valueJson = "45", expectedVersion = v1, reason = "正常更新" });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        // 旧 rowversion 再写→409 VERSION_CONFLICT（事务回滚，不落库）
        var stale = await PutSettingAsync(client, "sync.pollSeconds",
            new { valueJson = "60", expectedVersion = v1, reason = "过期版本" });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("VERSION_CONFLICT", await stale.Content.ReadAsStringAsync());
        Assert.Equal("45", (await ListAsync(client)).Single(v => v.Key == "sync.pollSeconds").ValueJson);
    }

    [Fact]
    public async Task Secret_write_returns_has_secret_value_and_null_value_json()
    {
        var client = await LoginAsync("setadmin", "Admin-Password-123");
        var ver = await CurrentVersionAsync(client, "erp.connection");
        var resp = await PutSettingAsync(client, "erp.connection",
            new { secretValue = _fx.ConnectionString, expectedVersion = ver, reason = "保存 ERP 凭据" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Integrated Security", body); // 明文连接串绝不回显（NFR-05）
        var view = (await resp.Content.ReadFromJsonAsync<SettingUpdateResp>())!.Data;
        Assert.True(view.IsSecret);
        Assert.True(view.HasSecretValue);
        Assert.Null(view.ValueJson);
        Assert.True(view.Id > 0);
        Assert.NotEqual("0", view.Version);
        // GET 列表同样只给 hasSecretValue，不给明文
        var listed = (await ListAsync(client)).Single(v => v.Key == "erp.connection");
        Assert.True(listed.HasSecretValue);
        Assert.Null(listed.ValueJson);
    }

    [Fact]
    public async Task Connection_test_guards_and_stored_local_connection()
    {
        var client = await LoginAsync("setadmin", "Admin-Password-123");
        // 未持久化/未知 configId：SettingsService.TestErpConnectionAsync 守卫→SettingValidationException→422
        var unknown = await client.PostAsJsonAsync("/api/v1/settings/connections/test", new { configId = 999999 });
        Assert.Equal((HttpStatusCode)422, unknown.StatusCode);
        Assert.Contains("VALIDATION", await unknown.Content.ReadAsStringAsync());
        var zero = await client.PostAsJsonAsync("/api/v1/settings/connections/test", new { configId = 0 });
        Assert.Equal((HttpStatusCode)422, zero.StatusCode);
        // 已持久化的非 erp.connection 键同样拒绝（防 SSRF：只接受已保存 ERP 凭据的 id）
        var dv = await CurrentVersionAsync(client, "sync.dailyRescanDays");
        var putNonErp = await PutSettingAsync(client, "sync.dailyRescanDays",
            new { valueJson = "30", expectedVersion = dv, reason = "非密钥配置" });
        Assert.Equal(HttpStatusCode.OK, putNonErp.StatusCode);
        var nonErpId = (await putNonErp.Content.ReadFromJsonAsync<SettingUpdateResp>())!.Data.Id;
        var nonErp = await client.PostAsJsonAsync("/api/v1/settings/connections/test", new { configId = nonErpId });
        Assert.Equal((HttpStatusCode)422, nonErp.StatusCode);
        // 已保存 erp.connection（本机测试库连接串充当 ERP，仅验证 SELECT 1 通路）→ ok=true，不回显凭据
        var ev = await CurrentVersionAsync(client, "erp.connection");
        var putErp = await PutSettingAsync(client, "erp.connection",
            new { secretValue = _fx.ConnectionString, expectedVersion = ev, reason = "连接测试" });
        Assert.Equal(HttpStatusCode.OK, putErp.StatusCode);
        var erpId = (await putErp.Content.ReadFromJsonAsync<SettingUpdateResp>())!.Data.Id;
        var ok = await client.PostAsJsonAsync("/api/v1/settings/connections/test", new { configId = erpId });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var okBody = await ok.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Integrated Security", okBody);
        var result = (await ok.Content.ReadFromJsonAsync<ConnTestResp>())!.Data;
        Assert.True(result.Ok);
        Assert.False(string.IsNullOrEmpty(result.Message));
    }

    [Fact]
    public async Task Settings_endpoints_require_system_manage_permission()
    {
        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/v1/settings")).StatusCode);
        var viewer = await LoginAsync("setviewer", "Test-Password-123");
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/v1/settings")).StatusCode);
        viewer.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PutAsJsonAsync("/api/v1/settings/sync.pollSeconds",
            new { valueJson = "30", expectedVersion = "0", reason = "越权" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/v1/settings/connections/test",
            new { configId = 1 })).StatusCode);
    }

    private sealed record SessionResp(SessionData Data);
    private sealed record SessionData(bool Authenticated, string CsrfToken);
    private sealed record SettingsListResp(SettingViewResp[] Data);
    private sealed record SettingUpdateResp(SettingViewResp Data);
    private sealed record SettingViewResp(long Id, string Key, string? ValueJson, bool IsSecret, bool HasSecretValue, string Version);
    private sealed record ConnTestResp(ConnTestData Data);
    private sealed record ConnTestData(bool Ok, string Message);
}
