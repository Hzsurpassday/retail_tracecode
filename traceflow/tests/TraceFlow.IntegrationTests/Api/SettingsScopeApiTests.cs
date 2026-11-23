using System.Runtime.Versioning;
using System.Net;
using System.Net.Http.Json;
using Dapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Domain.Billing;
using TraceFlow.Infrastructure.Settings;

namespace TraceFlow.IntegrationTests.Api;

[SupportedOSPlatform("windows")]
public class SettingsScopeApiTests : IClassFixture<Sql2008Fixture>, IAsyncLifetime
{
    private readonly Sql2008Fixture _fx;
    private WebApplicationFactory<Program> _factory = null!;
    public SettingsScopeApiTests(Sql2008Fixture fx) => _fx = fx;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:TraceFlow", _fx.ConnectionString);
            b.UseSetting("TraceFlow:Env", "Mock");
        });
        await using var c = await _fx.OpenAsync();
        var hasher = new PasswordHasher<string>();
        var id = await c.ExecuteScalarAsync<long>(@"
IF NOT EXISTS (SELECT 1 FROM dbo.app_user WHERE username=N'm2scope')
    INSERT INTO dbo.app_user(username,display_name,password_hash) VALUES(N'm2scope',N'管理员',@h);
SELECT id FROM dbo.app_user WHERE username=N'm2scope'", new { h = hasher.HashPassword("m2scope", "Scope-Password-123") });
        foreach (var p in new[] { "system:manage", "trace:read" })
            await c.ExecuteAsync(@"
IF NOT EXISTS (SELECT 1 FROM dbo.user_permission WHERE user_id=@id AND permission=@p)
    INSERT INTO dbo.user_permission(user_id,permission,granted_by) VALUES(@id,@p,N'test')", new { id, p });
    }

    public Task DisposeAsync() { _factory.Dispose(); return Task.CompletedTask; }

    private async Task<HttpClient> LoginAsync()
    {
        var client = _factory.CreateClient();
        var session = await client.GetFromJsonAsync<SessionResp>("/api/v1/auth/session");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session!.Data.CsrfToken);
        (await client.PostAsJsonAsync("/api/v1/auth/login", new { username = "m2scope", password = "Scope-Password-123" })).EnsureSuccessStatusCode();
        var session2 = await client.GetFromJsonAsync<SessionResp>("/api/v1/auth/session");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session2!.Data.CsrfToken);
        return client;
    }

    private sealed record SessionResp(SessionData Data);
    private sealed record SessionData(bool Authenticated, string CsrfToken);

    [Fact]
    public async Task Secret_setting_exposes_protection_scope()
    {
        var client = await LoginAsync();
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var put = await client.PutAsJsonAsync("/api/v1/settings/top.appsecret",
            new { valueJson = (string?)null, secretValue = "test-secret-value", expectedVersion = "0", reason = "录入测试凭证" });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Contains("\"protectionScope\":\"CurrentUser\"", await put.Content.ReadAsStringAsync());
        var list = await client.GetAsync("/api/v1/settings");
        var text = await list.Content.ReadAsStringAsync();
        Assert.Contains("\"protectionScope\":\"CurrentUser\"", text);
        Assert.DoesNotContain("test-secret-value", text); // 密文不回显
    }

    [Fact]
    public async Task Service_account_endpoint_returns_process_identity()
    {
        var client = await LoginAsync();
        var resp = await client.GetAsync("/api/v1/settings/service-account");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<AccountResp>();
        Assert.False(string.IsNullOrEmpty(body!.Data.ServiceAccount));
        Assert.Equal(Environment.MachineName, body.Data.MachineName);
    }

    [Fact]
    public async Task Top_budgets_endpoint_lists_usage_and_pauses()
    {
        await using var c = await _fx.OpenAsync();
        await c.ExecuteAsync(@"
INSERT INTO dbo.top_rate_budget(env,budget_key,day_count,minute_count,second_count)
VALUES(N'MOCK',N'appkey:total',3,1,0)");
        await c.ExecuteAsync(@"
INSERT INTO dbo.top_channel_pause(env,scope_key,reason,paused_by)
VALUES(N'MOCK',N'appkey:total',N'限流暂停',N'test')");
        var client = await LoginAsync();
        var resp = await client.GetAsync("/api/v1/top-budgets");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<BudgetResp>();
        Assert.Contains(body!.Data.Budgets, b => b.BudgetKey == "appkey:total" && b.DayCount == 3);
        Assert.Null(body.Data.Budgets.Single(b => b.BudgetKey == "appkey:total").DayLimit); // NULL=不设帽
        Assert.NotNull(body.Data.Pauses);
        // 持久化状态断言：刚插入的暂停行以字符串 id 回读（M2 端点 id 序列化为字符串）
        var pause = body.Data.Pauses.Single(p => p.ScopeKey == "appkey:total");
        Assert.True(pause.IsActive);
        Assert.True(long.TryParse(pause.Id, out var pauseId) && pauseId > 0);
    }

    private async Task<string> VersionAsync(HttpClient client, string key)
    {
        var list = await client.GetFromJsonAsync<SettingsListResp>("/api/v1/settings");
        var row = list!.Data.Single(x => x.Key == key);
        return row.Id == 0 ? "0" : row.Version;
    }

    private async Task<HttpResponseMessage> PutSettingAsync(HttpClient client, string key, string valueJson)
    {
        client.DefaultRequestHeaders.Remove("Idempotency-Key");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return await client.PutAsJsonAsync($"/api/v1/settings/{key}",
            new { valueJson, expectedVersion = await VersionAsync(client, key), reason = "账单码合同耦合校验" });
    }

    [Fact]
    public async Task Bill_prefix_and_max_length_coupling_blocks_impossible_pairs()
    {
        var client = await LoginAsync();
        // 类共享库：先复位默认合同，避免测试顺序依赖
        Assert.Equal(HttpStatusCode.OK, (await PutSettingAsync(client, "top.billPrefix", "\"TF\"")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PutSettingAsync(client, "top.billMaxLength", "32")).StatusCode);

        // 上限 8 装不下默认前缀 TF（2 位）+ 预留后缀 7 位 → 422，且消息给出原因
        var tooSmall = await PutSettingAsync(client, "top.billMaxLength", "8");
        Assert.Equal((HttpStatusCode)422, tooSmall.StatusCode);
        var msg = await tooSmall.Content.ReadAsStringAsync();
        Assert.Contains("VALIDATION", msg);
        Assert.Contains("top.billMaxLength", msg);
        Assert.Contains("7", msg); // 预留后缀位数对操作者可见

        // 上限 12 可容纳 2 位前缀（9 ≤ 12），但装不下 16 位前缀（23 > 12）
        Assert.Equal(HttpStatusCode.OK, (await PutSettingAsync(client, "top.billMaxLength", "12")).StatusCode);
        var longPrefix = await PutSettingAsync(client, "top.billPrefix", "\"ABCDEFGHIJKLMNOP\"");
        Assert.Equal((HttpStatusCode)422, longPrefix.StatusCode);
        Assert.Contains("top.billPrefix", await longPrefix.Content.ReadAsStringAsync());

        // 上限 23 恰为 16 位前缀 + 预留 7 位的边界 → 两键均可写，且真实落库（非仅响应形状）
        Assert.Equal(HttpStatusCode.OK, (await PutSettingAsync(client, "top.billMaxLength", "23")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PutSettingAsync(client, "top.billPrefix", "\"ABCDEFGHIJKLMNOP\"")).StatusCode);
        var list = await client.GetFromJsonAsync<SettingsListResp>("/api/v1/settings");
        Assert.Equal("\"ABCDEFGHIJKLMNOP\"", list!.Data.Single(x => x.Key == "top.billPrefix").ValueJson); // valueJson 为原始 JSON 文本
        Assert.Equal("23", list.Data.Single(x => x.Key == "top.billMaxLength").ValueJson);

        // 复原默认合同
        Assert.Equal(HttpStatusCode.OK, (await PutSettingAsync(client, "top.billPrefix", "\"TF\"")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PutSettingAsync(client, "top.billMaxLength", "32")).StatusCode);
    }

    [Fact]
    public void Coupling_guard_boundary_accepts_defaults_and_rejects_short_caps()
    {
        SettingsService.EnsureBillContract("TF", 32);                       // 默认合同
        SettingsService.EnsureBillContract("T", 8);                         // 最小上限 + 1 位前缀
        SettingsService.EnsureBillContract("ABCDEFGHIJKLMNOP", 23);         // 最大前缀 + 边界上限
        Assert.Throws<SettingValidationException>(() => SettingsService.EnsureBillContract("TF", 8));
        Assert.Throws<SettingValidationException>(() => SettingsService.EnsureBillContract("ABCDEFGHIJKLMNOP", 22));
    }

    [Fact]
    public void Reserved_suffix_covers_base35_identity_and_part_suffix()
    {
        // 预留 7 位 = Base35 身份 4 位（35^4-1 = 1_500_624 个任务）+ "P99" 分片后缀 3 位
        var contract = new BillCodeContract("ABCDEFGHIJKLMNOP", 23);
        var boundaryTask = 1_500_624L;
        var wide = BillCodeGenerator.Generate(contract, boundaryTask, 99);
        Assert.Equal(23, wide.Length);
        Assert.StartsWith("ABCDEFGHIJKLMNOP", wide, StringComparison.Ordinal);
        // 合同少一位即抛错（不截断）——耦合校验正是防住这类必然超长的组合
        Assert.Throws<InvalidOperationException>(() =>
            BillCodeGenerator.Generate(new BillCodeContract("ABCDEFGHIJKLMNOP", 22), boundaryTask, 99));
    }

    private sealed record AccountResp(AccountData Data);
    private sealed record AccountData(string ServiceAccount, string MachineName);
    private sealed record BudgetResp(BudgetData Data);
    private sealed record BudgetData(BudgetRow[] Budgets, PauseRow[] Pauses);
    private sealed record BudgetRow(string BudgetKey, int DayCount, int? DayLimit, int MinuteCount, int? MinuteLimit, int SecondCount, int? SecondLimit, DateTime? NextAllowedAtUtc);
    private sealed record PauseRow(string Id, string ScopeKey, bool IsActive);
    private sealed record SettingsListResp(SettingRow[] Data);
    private sealed record SettingRow(long Id, string Key, string? ValueJson, string Version);
}
