using System.Net;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using TraceFlow.Application.Export;
using TraceFlow.Application.Security;
using TraceFlow.Host.Endpoints;
using TraceFlow.IntegrationTests.Sources;

namespace TraceFlow.IntegrationTests.Export;

/// <summary>
/// M4 Task 4 CSV 导出 HTTP 层（DEF-07 / AT-074）：BOM + 表头、公式注入防护（= 开头的单号/范围加 '）、
/// 与台账列表同口径筛选与 env 隔离、对账差异导出按宿主 env 定界（跨 env 404）、
/// `data.exported` 审计（env 恒 LIVE）、data:export / audit:export 权限 403、匿名 401、GET 无需 CSRF。
/// 行数上限不做 5 万行 seed：<see cref="ExportPolicy.ExceedsLimit"/> 为应用层公共纯函数（brief 允许的偏差）。
/// 同一个类共用一份测试库，故所有断言用本用例专属码前缀 / run / action 定界，绝不跨用例计数。
/// </summary>
[SupportedOSPlatform("windows")]
public class ExportApiTests : IAsyncLifetime
{
    private static readonly DateTime T1 = new(2026, 11, 20, 2, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T2 = new(2026, 11, 20, 5, 0, 0, DateTimeKind.Utc);

    private const string AdminPassword = "Export-Admin-123";
    private const string DataPassword = "Export-Data-123";
    private const string TracePassword = "Export-Trace-123";
    private const string Header = "\"码\",\"格式\",\"最近事件类型\",\"事件数\",\"最近事件时间\",\"占用状态\",\"占用范围\",\"单号\"";

    private Sql2008Fixture _fx = null!;
    private WebApplicationFactory<Program> _factory = null!;

    public async Task InitializeAsync()
    {
        _fx = new Sql2008Fixture();
        await _fx.InitializeAsync();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:TraceFlow", _fx.ConnectionString);
            b.UseSetting("TraceFlow:Env", "Mock");
            b.ConfigureServices(s => s.RemoveAll<IHostedService>());
        });
        await using var c = await _fx.OpenAsync();
        await SeedUserAsync(c, "export-admin", "导出管理员", AdminPassword, PermissionCatalog.All);
        await SeedUserAsync(c, "export-data", "仅数据导出", DataPassword, new[] { "data:export" });
        await SeedUserAsync(c, "export-trace", "仅码台账只读", TracePassword, new[] { "trace:read" });
    }

    public async Task DisposeAsync()
    {
        _factory.Dispose();
        await _fx.DisposeAsync();
    }

    // ---------------------------------------------------------------- 1. BOM + 注入防护 + 无 CSRF + 审计

    [Fact]
    public async Task Codes_export_streams_csv_with_bom_and_injected_formula_prefixed()
    {
        const string code = "690000000000100001";
        const string prefix = "6900000000001000";
        await SeedEventAsync(code, "PURCHASE_IN", "IN", T1);
        var (taskId, _, _, _) = await SeedTaskWithPartAsync("EXPORT-INJECT",
            billCode: "=HYPERLINK(\"http://evil\")");
        await SeedClaimAsync(code, "ACTIVE", "=EVIL", T1, uploadTaskId: taskId);

        var client = await LoginAsync("export-admin", AdminPassword);
        // GET 无需 CSRF：移除令牌后仍须 200（全局防伪中间件只拦 POST/PUT/DELETE/PATCH）。
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        var resp = await client.GetAsync("/api/v1/exports/codes.csv?q=" + prefix);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/csv", resp.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", resp.Content.Headers.ContentType?.CharSet);
        Assert.Equal("attachment", resp.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Matches("^codes-\\d{14}\\.csv$",
            (resp.Content.Headers.ContentDisposition?.FileName ?? "").Trim('"'));

        // UTF-8 BOM（EF BB BF）是正文首 3 字节。
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 3);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());

        var text = Encoding.UTF8.GetString(bytes);
        var lines = CsvLines(text);
        Assert.Equal(Header, lines[0]);
        var row = Assert.Single(lines.Skip(1));
        // 公式注入防护：= 开头的占用范围/单号在引号内加 ' 前缀；单号内部引号双写。
        Assert.Contains("\"'=EVIL\"", row);
        Assert.Contains("\"'=HYPERLINK(\"\"http://evil\"\")\"", row);
        Assert.StartsWith("\"690000000000100001\",\"S8P20\",\"PURCHASE_IN\",1,\"2026-11-20T02:00:00.000Z\"", row);
        Assert.Contains("http://evil", row);

        // 成功导出写 data.exported：env 恒 LIVE、object_type=export、reason 含筛选与行数。
        var audit = await WaitAuditAsync("data.exported", "codes", "q=" + prefix);
        Assert.NotNull(audit);
        Assert.Equal("export-admin", audit!.Actor);
        Assert.Equal("LIVE", audit.Env);
        Assert.Contains("rows=1", audit.Reason);
        Assert.Contains("\"rows\":1", audit.AfterDigest);
    }

    // ---------------------------------------------------------------- 2. 与列表同口径 + env 隔离

    [Fact]
    public async Task Codes_export_matches_list_filter_and_scope()
    {
        const string prefix = "6900000000001100";
        await SeedEventAsync("690000000000110001", "PURCHASE_IN", "IN", T1);
        await SeedEventAsync("690000000000110002", "SALE", "OUT", T2);
        await SeedEventAsync("690000000000110003", "PURCHASE_IN", "IN", T1, env: "LIVE");

        var client = await LoginAsync("export-admin", AdminPassword);

        var list = await GetJsonAsync(client, $"/api/v1/codes?q={prefix}&pageSize=100");
        var listCodes = list.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("code").GetString()!).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(2, listCodes.Count);

        var mockCsv = await GetCsvAsync(client, $"/api/v1/exports/codes.csv?q={prefix}");
        var mockCodes = mockCsv.Skip(1).Select(FirstCell).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(listCodes, mockCodes);

        // env=LIVE：MOCK 码不出现（导出与列表同样按 env 定界）。
        var liveCsv = await GetCsvAsync(client, $"/api/v1/exports/codes.csv?q={prefix}&env=LIVE");
        Assert.Equal(new[] { "690000000000110003" }, liveCsv.Skip(1).Select(FirstCell));

        // eventType 筛选口径与列表一致（重新聚合事件集合，最近事件类型取筛选后集合）。
        var listSale = await GetJsonAsync(client, $"/api/v1/codes?q={prefix}&eventType=SALE&pageSize=100");
        var listSaleCodes = listSale.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("code").GetString()!).ToList();
        var saleCsv = await GetCsvAsync(client, $"/api/v1/exports/codes.csv?q={prefix}&eventType=SALE");
        Assert.Equal(listSaleCodes, saleCsv.Skip(1).Select(FirstCell).ToList());

        // 参数校验与列表一致：前缀过短 / 非法 eventType / 非法 env → 400 VALIDATION。
        await AssertErrorAsync(client, "/api/v1/exports/codes.csv?q=690", HttpStatusCode.BadRequest, "VALIDATION");
        await AssertErrorAsync(client, "/api/v1/exports/codes.csv?eventType=RETURN",
            HttpStatusCode.BadRequest, "VALIDATION");
        await AssertErrorAsync(client, "/api/v1/exports/codes.csv?env=PROD", HttpStatusCode.BadRequest, "VALIDATION");
        await AssertErrorAsync(client, "/api/v1/exports/codes.csv?from=not-a-date",
            HttpStatusCode.BadRequest, "VALIDATION");
    }

    // ---------------------------------------------------------------- 3. 对账差异导出：宿主 env + 审计

    [Fact]
    public async Task Recon_differences_export_scoped_to_host_env_and_audits()
    {
        var mockRun = await SeedRunAsync("MOCK");
        await SeedDifferenceAsync(mockRun, "MISSING_CODE", "=1+1", T1);            // 公式注入的 object_ref
        await SeedDifferenceAsync(mockRun, "WAIT_VERIFY", "bill-2", T2, resolved: true);
        var liveRun = await SeedRunAsync("LIVE");
        await SeedDifferenceAsync(liveRun, "MISSING_CODE", "live-only", T1);

        var client = await LoginAsync("export-admin", AdminPassword);
        var resp = await client.GetAsync($"/api/v1/exports/reconciliation-runs/{mockRun}/differences.csv");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/csv", resp.Content.Headers.ContentType?.MediaType);
        Assert.Matches($"^reconciliation-run-{mockRun}-differences-\\d{{14}}\\.csv$",
            (resp.Content.Headers.ContentDisposition?.FileName ?? "").Trim('"'));

        var lines = CsvLines(await resp.Content.ReadAsStringAsync());
        Assert.Equal("\"差异类型\",\"严重级\",\"对象\",\"状态\",\"责任角色\",\"下一步\",\"发现时间\",\"解决证据\",\"解决人\",\"解决时间\"",
            lines[0]);
        var rows = lines.Skip(1).ToList();
        Assert.Equal(2, rows.Count);
        // 排序与 run 详情一致：found_at, id 升序；object_ref 首字符 = 被加 ' 前缀。
        Assert.Contains("\"'=1+1\"", rows[0]);
        Assert.StartsWith("\"MISSING_CODE\",\"HIGH\",\"'=1+1\",\"OPEN\"", rows[0]);
        Assert.Contains("2026-11-20T02:00:00.000Z", rows[0]);
        Assert.StartsWith("\"WAIT_VERIFY\"", rows[1]);
        Assert.Contains("\"RESOLVED\"", rows[1]);
        Assert.Contains("evidence-ok", rows[1]);
        Assert.Contains("2026-11-20T05:00:00.000Z", rows[1]);

        // 审计：env 恒 LIVE，object_id = 导出名（含 run id），after_digest 记录行数。
        var audit = await WaitAuditAsync("data.exported", $"reconciliation-run-{mockRun}-differences",
            $"runId={mockRun}");
        Assert.NotNull(audit);
        Assert.Equal("LIVE", audit!.Env);
        Assert.Contains("rows=2", audit.Reason);
        Assert.Contains("\"rows\":2", audit.AfterDigest);

        // 跨 env / 不存在 → 404（不暴露另一环境的 run 是否存在）。
        await AssertErrorAsync(client, $"/api/v1/exports/reconciliation-runs/{liveRun}/differences.csv",
            HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertErrorAsync(client, "/api/v1/exports/reconciliation-runs/999999999/differences.csv",
            HttpStatusCode.NotFound, "NOT_FOUND");
    }

    // ---------------------------------------------------------------- 4. 审计导出：audit:export 权限 + 防注入

    [Fact]
    public async Task Audit_logs_export_requires_audit_export_permission()
    {
        const string action = "export.test.row";
        await SeedAuditAsync(action, "demo", "x1", T1, before: "=cmd", after: "@SUM(1)");
        await SeedAuditAsync(action, "demo", "x2", T2, before: "plain", after: "plain");

        // 仅 data:export 无 audit:export → 403。
        var dataClient = await LoginAsync("export-data", DataPassword);
        await AssertErrorAsync(dataClient, "/api/v1/exports/audit-logs.csv", HttpStatusCode.Forbidden, null);
        // 无权限的普通读者 → 403。
        var traceClient = await LoginAsync("export-trace", TracePassword);
        await AssertErrorAsync(traceClient, "/api/v1/exports/audit-logs.csv", HttpStatusCode.Forbidden, null);

        var admin = await LoginAsync("export-admin", AdminPassword);
        var resp = await admin.GetAsync($"/api/v1/exports/audit-logs.csv?action={action}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var lines = CsvLines(await resp.Content.ReadAsStringAsync());
        Assert.Equal("\"时间\",\"环境\",\"操作人\",\"动作\",\"对象类型\",\"对象ID\",\"原因\",\"变更前\",\"变更后\"",
            lines[0]);
        var rows = lines.Skip(1).ToList();
        Assert.Equal(2, rows.Count);
        // occurred_at DESC：x2（T2）在前，x1（T1）在后；before/after 原样文本经 Cell 防注入。
        Assert.StartsWith("\"2026-11-20T05:00:00.000Z\",\"LIVE\",\"seed-audit\",\"export.test.row\"", rows[0]);
        Assert.Contains("\"x2\"", rows[0]);
        Assert.Contains("\"'=cmd\"", rows[1]);
        Assert.Contains("\"'@SUM(1)\"", rows[1]);
        Assert.Contains("\"2026-11-20T02:00:00.000Z\"", rows[1]);
    }

    // ---------------------------------------------------------------- 5. data:export 权限

    [Fact]
    public async Task Export_requires_data_export_permission()
    {
        // 先落种子（O1 活跃组织），再启动宿主：OrgBootstrap 只在无活跃组织时插入 DEFAULT，避免单活唯一索引冲突。
        const string prefix = "6900000000001200";
        await SeedEventAsync("690000000000120001", "PURCHASE_IN", "IN", T1);

        var traceClient = await LoginAsync("export-trace", TracePassword);
        await AssertErrorAsync(traceClient, "/api/v1/exports/codes.csv", HttpStatusCode.Forbidden, null);
        await AssertErrorAsync(traceClient, "/api/v1/exports/reconciliation-runs/1/differences.csv",
            HttpStatusCode.Forbidden, null);

        // data:export 可导出码台账（但不可导出审计日志，见上一条用例）。
        var dataClient = await LoginAsync("export-data", DataPassword);
        var resp = await dataClient.GetAsync($"/api/v1/exports/codes.csv?q={prefix}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Single(CsvLines(await resp.Content.ReadAsStringAsync()).Skip(1));
    }

    // ---------------------------------------------------------------- 6. 行数上限（helper 覆盖，不 seed 5 万行）

    [Fact]
    public void Export_row_limit_helper_guards_at_fifty_thousand()
    {
        // brief 允许的偏差：不 seed 5 万行，改为公共常量 + 公共纯函数；端点写响应前调用同一方法。
        Assert.Equal(50_000, ExportPolicy.MaxExportRows);
        Assert.False(ExportPolicy.ExceedsLimit(0));
        Assert.False(ExportPolicy.ExceedsLimit(50_000));
        Assert.True(ExportPolicy.ExceedsLimit(50_001));
        Assert.True(ExportPolicy.ExceedsLimit(long.MaxValue));
        // 时间列 UTC ISO 格式（yyyy-MM-ddTHH:mm:ss.fffZ）。
        Assert.Equal("2026-11-20T02:00:00.000Z", ExportPolicy.UtcIso(T1));
    }

    // ---------------------------------------------------------------- 7. 匿名 401

    [Fact]
    public async Task Export_endpoints_reject_anonymous_with_401()
    {
        var anonymous = _factory.CreateClient();
        foreach (var url in new[]
                 {
                     "/api/v1/exports/codes.csv",
                     "/api/v1/exports/reconciliation-runs/1/differences.csv",
                     "/api/v1/exports/audit-logs.csv",
                 })
        {
            var resp = await anonymous.GetAsync(url);
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        }
    }

    // ---------------------------------------------------------------- 种子 / 辅助

    private static async Task SeedUserAsync(SqlConnection c, string username, string display, string password,
        IEnumerable<string> permissions)
    {
        var hasher = new PasswordHasher<string>();
        var id = await c.ExecuteScalarAsync<long>(@"
IF NOT EXISTS (SELECT 1 FROM dbo.app_user WHERE username=@username)
    INSERT INTO dbo.app_user(username,display_name,password_hash) VALUES(@username,@display,@h);
SELECT id FROM dbo.app_user WHERE username=@username",
            new { username, display, h = hasher.HashPassword(username, password) });
        foreach (var p in permissions)
            await c.ExecuteAsync(@"
IF NOT EXISTS (SELECT 1 FROM dbo.user_permission WHERE user_id=@id AND permission=@p)
    INSERT INTO dbo.user_permission(user_id,permission,granted_by) VALUES(@id,@p,N'test')", new { id, p });
    }

    private static string Sha(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    private static async Task<long> EnsureCodeAsync(SqlConnection c, string code)
    {
        return await c.ExecuteScalarAsync<long>(@"
IF NOT EXISTS (SELECT 1 FROM dbo.trace_code WHERE code_normalized=@code)
    INSERT INTO dbo.trace_code(code_normalized,code_raw,format,first_seen_at)
    VALUES(@code,@code,N'S8P20',@seen);
SELECT id FROM dbo.trace_code WHERE code_normalized=@code",
            new { code, seen = T1.AddHours(-1) });
    }

    private async Task<long> SeedEventAsync(string code, string eventType, string direction, DateTime occurredAt,
        string env = "MOCK")
    {
        await using var c = await _fx.OpenAsync();
        var (orgId, _) = await TestSeed.EnsureAsync(c);
        var codeId = await EnsureCodeAsync(c, code);
        return await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.code_event(org_id,env,code_id,event_type,direction,occurred_at,source_line_id)
OUTPUT inserted.id
VALUES(@orgId,@env,@codeId,@type,@dir,@at,0)",
            new { orgId, env, codeId, type = eventType, dir = direction, at = occurredAt });
    }

    private async Task<long> SeedClaimAsync(string code, string claimState, string eventScope, DateTime claimedAt,
        long? uploadTaskId = null)
    {
        await using var c = await _fx.OpenAsync();
        var (orgId, _) = await TestSeed.EnsureAsync(c);
        var codeId = await EnsureCodeAsync(c, code);
        return await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.code_claim(org_id,env,code_id,event_scope,upload_task_id,claim_state,claimed_at)
OUTPUT inserted.id
VALUES(@orgId,N'MOCK',@codeId,@scope,@taskId,@state,@at)",
            new { orgId, codeId, scope = eventScope, taskId = uploadTaskId, state = claimState, at = claimedAt });
    }

    private async Task<(long TaskId, long PartId, long AttemptId, string BillCode)> SeedTaskWithPartAsync(
        string tag, string billCode)
    {
        await using var c = await _fx.OpenAsync();
        var (orgId, _) = await TestSeed.EnsureAsync(c);
        var taskId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upload_task(org_id,env,event_key,task_type,source_hash,payload_hash,payload_json,state,
    source_integrity,match_state,verification_state,dependency_state)
OUTPUT inserted.id
VALUES(@orgId,N'MOCK',@key,N'RETAIL_321',@hash,@hash,N'{}',N'Succeeded',
    N'COMPLETE',N'COMPLETE',N'PASSED',N'NONE')",
            new { orgId, key = "KSOA:" + tag + ":" + Guid.NewGuid().ToString("N")[..8],
                  hash = Sha(tag + Guid.NewGuid().ToString("N")) });
        var partId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upload_part(task_id,org_id,env,part_index,bill_code,payload_json,payload_hash,code_set_hash,state)
OUTPUT inserted.id
VALUES(@taskId,@orgId,N'MOCK',0,@bill,N'{}',@hash,@hash,N'Succeeded')",
            new { taskId, orgId, bill = billCode, hash = Sha(tag + ":part") });
        var attemptId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upload_attempt(task_id,part_id,attempt_no,attempt_kind,env,fencing_token,started_at,
    finished_at,http_status)
OUTPUT inserted.id
VALUES(@taskId,@partId,1,N'UPLOAD',N'MOCK',1,DATEADD(minute,-30,SYSUTCDATETIME()),SYSUTCDATETIME(),200)",
            new { taskId, partId });
        return (taskId, partId, attemptId, billCode);
    }

    private async Task<long> SeedRunAsync(string env)
    {
        await using var c = await _fx.OpenAsync();
        var (orgId, _) = await TestSeed.EnsureAsync(c);
        return await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.reconciliation_run(org_id,env,window_start,window_end,business_type,status,started_at)
OUTPUT inserted.id
VALUES(@orgId,@env,@start,@end,N'DAILY',N'COMPLETE',SYSUTCDATETIME())",
            new { orgId, env, start = T1.Date, end = T1.Date.AddDays(1) });
    }

    private async Task<long> SeedDifferenceAsync(long runId, string diffType, string objectRef, DateTime foundAt,
        bool resolved = false)
    {
        await using var c = await _fx.OpenAsync();
        return await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.reconciliation_difference(run_id,diff_type,severity,object_ref,status,owner_role,next_action,
    found_at,resolution_evidence,resolved_by,resolved_at)
OUTPUT inserted.id
VALUES(@runId,@type,N'HIGH',@obj,@status,N'ops',N'recheck',@at,@evidence,@by,@resolvedAt)",
            new { runId, type = diffType, obj = objectRef,
                  status = resolved ? "RESOLVED" : "OPEN",
                  at = foundAt,
                  evidence = resolved ? "evidence-ok" : null,
                  by = resolved ? "ops-user" : null,
                  resolvedAt = resolved ? (DateTime?)foundAt.AddMinutes(10) : null });
    }

    private async Task SeedAuditAsync(string action, string objectType, string objectId, DateTime occurredAt,
        string before, string after)
    {
        await using var c = await _fx.OpenAsync();
        await c.ExecuteAsync(@"
INSERT INTO dbo.audit_log(occurred_at,actor,actor_kind,action,object_type,object_id,reason,
    before_digest,after_digest,request_id,env)
VALUES(@at,N'seed-audit',N'User',@action,@objectType,@objectId,N'seed',@before,@after,N'req-seed',N'LIVE')",
            new { at = occurredAt, action, objectType, objectId, before, after });
    }

    // ---------------------------------------------------------------- 登录 / 取数

    private async Task<HttpClient> LoginAsync(string username, string password)
    {
        var client = _factory.CreateClient();
        var session = await client.GetFromJsonAsync<SessionResp>("/api/v1/auth/session");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session!.Data.CsrfToken);
        (await client.PostAsJsonAsync("/api/v1/auth/login", new { username, password })).EnsureSuccessStatusCode();
        return client;
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string url)
    {
        var resp = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("data").Clone();
    }

    private static async Task<string[]> GetCsvAsync(HttpClient client, string url)
    {
        var resp = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return CsvLines(await resp.Content.ReadAsStringAsync());
    }

    /// <summary>按 CRLF 切行并去掉行首 BOM（单元格内部换行不在本类种子中出现）。</summary>
    private static string[] CsvLines(string text)
        => text.TrimStart('\uFEFF').Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

    private static string FirstCell(string line)
    {
        var close = line.IndexOf('"', 1);
        Assert.True(close > 0, "CSV 行首单元格缺少引号包裹：" + line);
        return line.Substring(1, close - 1).Replace("\"\"", "\"", StringComparison.Ordinal);
    }

    private static async Task AssertErrorAsync(HttpClient client, string url, HttpStatusCode status, string? code)
    {
        var resp = await client.GetAsync(url);
        Assert.Equal(status, resp.StatusCode);
        if (code is not null)
            Assert.Contains(code, await resp.Content.ReadAsStringAsync());
    }

    private async Task<AuditSnapshot?> WaitAuditAsync(string action, string objectId, string reasonLike,
        int attempts = 40)
    {
        for (var i = 0; i < attempts; i++)
        {
            await using var c = await _fx.OpenAsync();
            var row = await c.QuerySingleOrDefaultAsync<AuditSnapshot>(@"
SELECT TOP 1 actor, env, reason, after_digest AS afterDigest FROM dbo.audit_log
WHERE action=@action AND object_type=N'export' AND object_id=@objectId AND reason LIKE @reason
ORDER BY id DESC", new { action, objectId, reason = "%" + reasonLike + "%" });
            if (row is not null) return row;
            await Task.Delay(100);
        }
        return null;
    }

    private sealed record SessionResp(SessionData Data);
    private sealed record SessionData(bool Authenticated, string CsrfToken);

    private sealed class AuditSnapshot
    {
        public string Actor { get; set; } = "";
        public string Env { get; set; } = "";
        public string Reason { get; set; } = "";
        public string AfterDigest { get; set; } = "";
    }
}