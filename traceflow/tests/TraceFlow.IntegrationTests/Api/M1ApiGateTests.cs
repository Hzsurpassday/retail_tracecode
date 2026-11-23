using System.Runtime.Versioning; // brief 缺失此 using（SupportedOSPlatform 所需）；与 ApiGateTests/InputValidationTests 标注模式一致（T6 偏差 1 同型）
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
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
        // 审计约定（T9 裁定）：控制面手动操作审计 env=LIVE（通道为 MOCK，作业自身审计仍随作业 env）
        await using var auditConn = await _fx.OpenAsync();
        Assert.Equal("LIVE", await auditConn.ExecuteScalarAsync<string>(
            "SELECT env FROM dbo.audit_log WHERE action='sync.manual' AND object_id=@oid",
            new { oid = body.Data.JobId }));
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

    [Fact]
    public async Task Mapping_endpoints_gated()
    {
        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/v1/product-mappings/1/confirm",
            new { targetId = "X", expectedVersion = "0", reason = "x" })).StatusCode);
        var (viewer, _) = await LoginAsync("m1viewer", "Test-Password-123");
        viewer.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/v1/product-mappings/1/confirm",
            new { targetId = "X", expectedVersion = "0", reason = "x" })).StatusCode);
        var (admin, _) = await LoginAsync("m1admin", "Admin-Password-123");
        admin.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        admin.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var noCsrf = await admin.PostAsJsonAsync("/api/v1/supplier-mappings/1/disable", new { expectedVersion = "0", reason = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        Assert.Contains("CSRF_INVALID", await noCsrf.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Import_lookup_by_lowercase_hash_finds_staged_batch()
    {
        // T10 Fix Round 1 回归：StageAsync 以 OrdinalIgnoreCase 接受任意大小写 fileHash 并存储大写 hex，
        // 而 import_batch.file_hash 为 BIN2 大小写敏感排序——GET 查询须先归一化为大写，否则小写 hash 写入被接受、读取 404。
        var (client, _) = await LoginAsync("m1admin", "Admin-Password-123");
        const string header = "spid,display_code,barcode,name,approval_no,manufacturer,dosage_form,strength,package_spec,unit,trace_classification,classification_reason,manufacturer_identity";
        var csv = header + "\nP900,,,小写哈希药,,,,,,盒,EXEMPT,豁免理由,\n";
        var bytes = Encoding.UTF8.GetBytes(csv);
        var lowerHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(); // 浏览器 SubtleCrypto/多数 hex 编码器的自然输出
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var stage = await client.PostAsJsonAsync("/api/v1/imports", new
        {
            fileKind = "PRODUCT",
            fileHash = lowerHash,
            content = Convert.ToBase64String(bytes),
            fileName = "lowercase-hash.csv",
        });
        Assert.Equal(HttpStatusCode.Accepted, stage.StatusCode);
        var staged = await stage.Content.ReadFromJsonAsync<ImportAccepted>();
        Assert.False(string.IsNullOrEmpty(staged!.Data.BatchId));
        // 用同一个（小写）hash 查批次 → 200 且 batchId 一致
        var lookup = await client.GetAsync($"/api/v1/imports?fileKind=PRODUCT&fileHash={lowerHash}");
        Assert.Equal(HttpStatusCode.OK, lookup.StatusCode);
        var view = await lookup.Content.ReadFromJsonAsync<ImportLookup>();
        Assert.Equal(staged.Data.BatchId, view!.Data.BatchId);
    }

    // ---- T16 携带项 2：审计查询加固（pageSize 钳制/越界页/排序/过滤精确性，特征化 OpsEndpoints 既有行为） ----

    private async Task<long> SeedAuditAsync(string action, DateTime occurredAtUtc, string objectType = "t16obj")
    {
        await using var c = await _fx.OpenAsync();
        return await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.audit_log(occurred_at,actor,actor_kind,action,object_type,env)
OUTPUT inserted.id
VALUES(@at,N't16seeder',N'User',@action,@ot,N'LIVE')",
            new { at = occurredAtUtc, action, ot = objectType });
    }

    [Fact]
    public async Task Audit_page_size_clamped_and_out_of_range_page_empty()
    {
        var (client, _) = await LoginAsync("m1admin", "Admin-Password-123");
        var action = "t16.audit.clamp." + Guid.NewGuid().ToString("N")[..8];
        for (var i = 0; i < 3; i++)
            await SeedAuditAsync(action, DateTime.UtcNow.AddMinutes(-i));
        // OpsEndpoints：size=Math.Clamp(pageSize??50,1,200)，p=Math.Max(1,page??1)
        var big = await client.GetFromJsonAsync<AuditPage>($"/api/v1/audit-logs?action={action}&pageSize=1000");
        Assert.Equal(200, big!.Data.PageSize);
        Assert.Equal(3, big.Data.Total);
        Assert.Equal(3, big.Data.Records.Length);
        var zero = await client.GetFromJsonAsync<AuditPage>($"/api/v1/audit-logs?action={action}&pageSize=0");
        Assert.Equal(1, zero!.Data.PageSize);
        Assert.Single(zero.Data.Records);
        var p0 = await client.GetFromJsonAsync<AuditPage>($"/api/v1/audit-logs?action={action}&page=0");
        Assert.Equal(1, p0!.Data.Page);
        // 越界页：ROW_NUMBER 无命中→records 空，total 不变
        var far = await client.GetFromJsonAsync<AuditPage>($"/api/v1/audit-logs?action={action}&page=100000&pageSize=200");
        Assert.Empty(far!.Data.Records);
        Assert.Equal(3, far.Data.Total);
    }

    [Fact]
    public async Task Audit_ordering_newest_first_with_id_tiebreak()
    {
        var (client, _) = await LoginAsync("m1admin", "Admin-Password-123");
        var action = "t16.audit.order." + Guid.NewGuid().ToString("N")[..8];
        var tie = DateTime.UtcNow.AddHours(-1);
        var older = await SeedAuditAsync(action, tie.AddHours(-1));
        var tieFirst = await SeedAuditAsync(action, tie);
        var tieSecond = await SeedAuditAsync(action, tie); // 同 occurred_at，后插入 id 更大
        // ORDER BY occurred_at DESC, id DESC
        var page = await client.GetFromJsonAsync<AuditPage>($"/api/v1/audit-logs?action={action}&pageSize=10");
        Assert.Equal(new[] { tieSecond, tieFirst, older }, page!.Data.Records.Select(r => r.Id).ToArray());
        // 内部代理 id 为 JSON number（int64；API-01 字符串约定仅适用外部平台 ID/rowversion，裁决 #9）
        var raw = await client.GetAsync($"/api/v1/audit-logs?action={action}&pageSize=10");
        using var doc = System.Text.Json.JsonDocument.Parse(await raw.Content.ReadAsStringAsync());
        var first = doc.RootElement.GetProperty("data").GetProperty("records")[0];
        Assert.Equal(System.Text.Json.JsonValueKind.Number, first.GetProperty("id").ValueKind);
        Assert.Equal(tieSecond, first.GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task Audit_filters_are_exact_not_substring()
    {
        var (client, _) = await LoginAsync("m1admin", "Admin-Password-123");
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var action = "t16.audit.filter." + suffix;
        var objectType = "t16obj." + suffix;
        await SeedAuditAsync(action, DateTime.UtcNow, objectType);
        await SeedAuditAsync(action + ".extra", DateTime.UtcNow, objectType + "X"); // 前缀扩展行不得命中
        var byAction = await client.GetFromJsonAsync<AuditPage>($"/api/v1/audit-logs?action={action}");
        Assert.Equal(1, byAction!.Data.Total); // action=@action 精确等值，非 LIKE 子串
        Assert.Equal(action, byAction.Data.Records.Single().Action);
        var byType = await client.GetFromJsonAsync<AuditPage>($"/api/v1/audit-logs?objectType={objectType}");
        Assert.Equal(1, byType!.Data.Total);
        Assert.Equal(objectType, byType.Data.Records.Single().ObjectType);
    }

    private sealed record AuditPage(AuditData Data);
    private sealed record AuditData(AuditRecord[] Records, int Total, int Page, int PageSize);
    private sealed record AuditRecord(long Id, string Action, string ObjectType, DateTime OccurredAtUtc);

    private sealed record JobAccepted(JobData Data);
    private sealed record JobData(string JobId);
    private sealed record ImportAccepted(ImportAcceptedData Data);
    private sealed record ImportAcceptedData(string JobId, string BatchId);
    private sealed record ImportLookup(ImportLookupData Data);
    private sealed record ImportLookupData(string BatchId, string FileHash);
}
