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
using TraceFlow.Application.Security;
using TraceFlow.IntegrationTests.Sources;

namespace TraceFlow.IntegrationTests.Codes;

/// <summary>
/// M4 Task 2 码台账只读 HTTP 层（UI-08）：前缀搜索 + ROW_NUMBER 分页、事件类型/时间窗/占用状态筛选、
/// env 隔离、单码事件链（升序 + 前序依赖）、占用与激活单号、逐码平台结果（不回显 platform_evidence）、
/// 退货确认与父子关系、未知/跨环境 404、匿名 401。种子全部直写 SQL（trace_code/code_event/code_claim/
/// code_result/upload_task/upload_part/upload_attempt/return_confirmation）；测试宿主移除后台 HostedService。
/// 同一个类共用一份测试库，故列表断言一律用本用例专属码前缀 / 时间窗定界，绝不跨用例计数。
/// </summary>
[SupportedOSPlatform("windows")]
public class CodeLedgerApiTests : IAsyncLifetime
{
    private static readonly DateTime T1 = new(2026, 11, 20, 2, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T2 = new(2026, 11, 20, 5, 0, 0, DateTimeKind.Utc);

    private const string AdminPassword = "Codes-Admin-123";
    private const string ReaderPassword = "Codes-Reader-123";
    private const string LeakMarker = "SHOULD-NOT-LEAK";

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
        await SeedUserAsync(c, "codes-admin", "码台账管理员", AdminPassword, PermissionCatalog.All);
        await SeedUserAsync(c, "codes-reader", "码台账只读", ReaderPassword, new[] { "trace:read" });
    }

    public async Task DisposeAsync()
    {
        _factory.Dispose();
        await _fx.DisposeAsync();
    }

    // ---------------------------------------------------------------- 种子

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

    private static async Task<long> EnsureCodeAsync(SqlConnection c, string code, DateTime? firstSeenAt = null)
    {
        return await c.ExecuteScalarAsync<long>(@"
IF NOT EXISTS (SELECT 1 FROM dbo.trace_code WHERE code_normalized=@code)
    INSERT INTO dbo.trace_code(code_normalized,code_raw,format,first_seen_at)
    VALUES(@code,@code,N'S8P20',@seen);
SELECT id FROM dbo.trace_code WHERE code_normalized=@code",
            new { code, seen = firstSeenAt ?? T1.AddHours(-1) });
    }

    private async Task<long> SeedEventAsync(string code, string eventType, string direction, DateTime occurredAt,
        string env = "MOCK", long? uploadTaskId = null, long? predecessorEventId = null,
        DateTime? businessDatetime = null, long sourceLineId = 0)
    {
        await using var c = await _fx.OpenAsync();
        var (orgId, _) = await TestSeed.EnsureAsync(c);
        var codeId = await EnsureCodeAsync(c, code);
        return await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.code_event(org_id,env,code_id,event_type,direction,upload_task_id,predecessor_event_id,
    occurred_at,business_datetime,source_line_id)
OUTPUT inserted.id
VALUES(@orgId,@env,@codeId,@type,@dir,@taskId,@predId,@at,@biz,@line)",
            new { orgId, env, codeId, type = eventType, dir = direction, taskId = uploadTaskId,
                  predId = predecessorEventId, at = occurredAt, biz = businessDatetime, line = sourceLineId });
    }

    private async Task<long> SeedClaimAsync(string code, string claimState, string eventScope,
        DateTime claimedAt, long? uploadTaskId = null, DateTime? releasedAt = null, string? releasedBy = null,
        string env = "MOCK")
    {
        await using var c = await _fx.OpenAsync();
        var (orgId, _) = await TestSeed.EnsureAsync(c);
        var codeId = await EnsureCodeAsync(c, code);
        return await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.code_claim(org_id,env,code_id,event_scope,upload_task_id,claim_state,claimed_at,
    released_at,released_by)
OUTPUT inserted.id
VALUES(@orgId,@env,@codeId,@scope,@taskId,@state,@at,@releasedAt,@releasedBy)",
            new { orgId, env, codeId, scope = eventScope, taskId = uploadTaskId, state = claimState,
                  at = claimedAt, releasedAt, releasedBy });
    }

    private async Task<long> SeedTaskAsync(string tag, string env = "MOCK", string taskType = "RETAIL_321",
        string taskState = "Succeeded")
    {
        await using var c = await _fx.OpenAsync();
        var (orgId, _) = await TestSeed.EnsureAsync(c);
        var payload = "{\"lines\":[],\"note\":\"" + LeakMarker + "\"}";
        return await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upload_task(org_id,env,event_key,task_type,source_hash,payload_hash,payload_json,state,
    source_integrity,match_state,verification_state,dependency_state)
OUTPUT inserted.id
VALUES(@orgId,@env,@key,@taskType,@hash,@hash,@payload,@state,
    N'COMPLETE',N'COMPLETE',N'PASSED',N'NONE')",
            new { orgId, env, key = "KSOA:" + tag + ":" + Guid.NewGuid().ToString("N")[..8],
                  taskType, hash = Sha(tag + Guid.NewGuid().ToString("N")), payload, state = taskState });
    }

    private async Task<(long TaskId, long PartId, long AttemptId, string BillCode)> SeedTaskWithPartAsync(
        string tag, string env = "MOCK", string taskState = "Succeeded", string partState = "Succeeded",
        string httpStatusRequestId = "req-1")
    {
        var taskId = await SeedTaskAsync(tag, env, taskState: taskState);
        await using var c = await _fx.OpenAsync();
        var (orgId, _) = await TestSeed.EnsureAsync(c);
        var billCode = "TFU" + Guid.NewGuid().ToString("N")[..10];
        var partId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upload_part(task_id,org_id,env,part_index,bill_code,payload_json,payload_hash,code_set_hash,state)
OUTPUT inserted.id
VALUES(@taskId,@orgId,@env,0,@bill,N'{}',@hash,@hash,@state)",
            new { taskId, orgId, env, bill = billCode, hash = Sha(tag + ":part"), state = partState });
        var attemptId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upload_attempt(task_id,part_id,attempt_no,attempt_kind,env,fencing_token,started_at,
    finished_at,http_status,request_id,platform_task_id,raw_response)
OUTPUT inserted.id
VALUES(@taskId,@partId,1,N'UPLOAD',@env,1,DATEADD(minute,-30,SYSUTCDATETIME()),SYSUTCDATETIME(),200,
    @requestId,N'PT-1',@raw)",
            new { taskId, partId, env, requestId = httpStatusRequestId,
                  raw = "{\"msgCode\":\"SUCCESS\",\"note\":\"" + LeakMarker + "\"}" });
        return (taskId, partId, attemptId, billCode);
    }

    private async Task SeedCodeResultAsync(string code, long taskId, long partId, long attemptId, string result,
        string? reason, DateTime recordedAt)
    {
        await using var c = await _fx.OpenAsync();
        var codeId = await EnsureCodeAsync(c, code);
        await c.ExecuteAsync(@"
INSERT INTO dbo.code_result(task_id,part_id,code_id,result,reason,platform_evidence,attempt_id,recorded_at)
VALUES(@taskId,@partId,@codeId,@result,@reason,@evidence,@attemptId,@at)",
            new { taskId, partId, codeId, result, reason,
                  evidence = "{\"evidence\":\"" + LeakMarker + "-EVIDENCE\"}", attemptId, at = recordedAt });
    }

    private async Task SeedReturnConfirmationAsync(long taskId, string decision, string relationKind,
        long? predecessorTaskId, DateTime confirmedAt, string confirmedBy = "confirm-user")
    {
        await using var c = await _fx.OpenAsync();
        var (orgId, _) = await TestSeed.EnsureAsync(c);
        await c.ExecuteAsync(@"
INSERT INTO dbo.return_confirmation(org_id,env,task_id,decision,relation_kind,predecessor_task_id,
    evidence_json,confirmed_by,confirmed_at)
VALUES(@orgId,N'MOCK',@taskId,@decision,@kind,@predTask,@evidence,@by,@at)",
            new { orgId, taskId, decision, kind = relationKind, predTask = predecessorTaskId,
                  evidence = "{\"note\":\"ret\"}", by = confirmedBy, at = confirmedAt });
    }

    // ---------------------------------------------------------------- 登录 / 取数

    private async Task<HttpClient> LoginAsync(string username, string password)
    {
        var client = _factory.CreateClient();
        var session = await client.GetFromJsonAsync<SessionResp>("/api/v1/auth/session");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session!.Data.CsrfToken);
        (await client.PostAsJsonAsync("/api/v1/auth/login", new { username, password })).EnsureSuccessStatusCode();
        // 登录后轮换 CSRF 令牌：后续变更方法（用于验证「无写路由」）必须带新令牌。
        var session2 = await client.GetFromJsonAsync<SessionResp>("/api/v1/auth/session");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session2!.Data.CsrfToken);
        return client;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Data, string Text)> GetAsync(HttpClient client,
        string url)
    {
        var resp = await client.GetAsync(url);
        var text = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(text);
        return (resp.StatusCode, doc.RootElement.GetProperty("data").Clone(), text);
    }

    private static List<string> Codes(JsonElement data)
        => data.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("code").GetString()!).ToList();

    // ---------------------------------------------------------------- 1. 前缀搜索 + 分页

    [Fact]
    public async Task Code_list_prefix_search_and_pagination()
    {
        const string hit1 = "690000000000000111";
        const string hit2 = "690000000000000112";
        const string miss = "690000000000000903";
        await SeedEventAsync(hit1, "PURCHASE_IN", "IN", T1);
        await SeedEventAsync(hit2, "SALE", "OUT", T1);
        await SeedEventAsync(miss, "PURCHASE_IN", "IN", T1);

        var client = await LoginAsync("codes-admin", AdminPassword);
        var (status, page1, _) = await GetAsync(client,
            "/api/v1/codes?q=69000000000000011&page=1&pageSize=1");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(2, page1.GetProperty("total").GetInt32());
        Assert.Equal(1, page1.GetProperty("pageSize").GetInt32());
        Assert.Equal("MOCK", page1.GetProperty("env").GetString());
        var firstPage = Codes(page1);
        Assert.Single(firstPage);

        var (_, page2, _) = await GetAsync(client, "/api/v1/codes?q=69000000000000011&page=2&pageSize=1");
        var secondPage = Codes(page2);
        Assert.Single(secondPage);
        Assert.Equal(new[] { hit1, hit2 }.OrderBy(x => x, StringComparer.Ordinal),
            firstPage.Concat(secondPage).OrderBy(x => x, StringComparer.Ordinal));
        Assert.DoesNotContain(miss, firstPage.Concat(secondPage));

        // 行字段完整性（列表首行）。
        var row = page1.GetProperty("items")[0];
        Assert.Equal(1, row.GetProperty("eventCount").GetInt32());
        Assert.False(string.IsNullOrEmpty(row.GetProperty("codeRaw").GetString()));
        Assert.Equal("S8P20", row.GetProperty("format").GetString());
        Assert.Equal(JsonValueKind.Number, row.GetProperty("eventCount").ValueKind);

        // 前缀过短 → 400 VALIDATION（绝不静默全表模糊）。
        var shortQ = await client.GetAsync("/api/v1/codes?q=690");
        Assert.Equal(HttpStatusCode.BadRequest, shortQ.StatusCode);
        Assert.Contains("VALIDATION", await shortQ.Content.ReadAsStringAsync());
        // 分页越界 → 400；pageSize>100 收敛到 100。
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/codes?page=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/codes?pageSize=0")).StatusCode);
        var clamped = await client.GetAsync("/api/v1/codes?q=69000000000000011&pageSize=1000");
        using var clampedDoc = JsonDocument.Parse(await clamped.Content.ReadAsStringAsync());
        Assert.Equal(100, clampedDoc.RootElement.GetProperty("data").GetProperty("pageSize").GetInt32());
    }

    // ---------------------------------------------------------------- 2. 事件类型 / 时间窗

    [Fact]
    public async Task Code_list_filters_by_event_type_and_time_window()
    {
        const string both = "690000000000002201";   // 入库 + 销售
        const string onlyIn = "690000000000002202"; // 仅入库
        await SeedEventAsync(both, "PURCHASE_IN", "IN", T1);
        await SeedEventAsync(both, "SALE", "OUT", T2, sourceLineId: 7);
        await SeedEventAsync(onlyIn, "PURCHASE_IN", "IN", T1);

        var client = await LoginAsync("codes-admin", AdminPassword);
        var (_, all, _) = await GetAsync(client, "/api/v1/codes?q=6900000000000022");
        Assert.Equal(2, all.GetProperty("total").GetInt32());

        var (_, sale, _) = await GetAsync(client, "/api/v1/codes?q=6900000000000022&eventType=SALE");
        Assert.Equal(1, sale.GetProperty("total").GetInt32());
        var saleRow = sale.GetProperty("items")[0];
        Assert.Equal(both, saleRow.GetProperty("code").GetString());
        Assert.Equal(1, saleRow.GetProperty("eventCount").GetInt32());
        Assert.Equal("SALE", saleRow.GetProperty("latestEventType").GetString());
        Assert.Equal(T2, saleRow.GetProperty("lastEventAt").GetDateTime());

        // 窗口只覆盖 T1 → 两个码的入库事件（各 1 条）。
        var (_, window1, _) = await GetAsync(client,
            "/api/v1/codes?q=6900000000000022&from=2026-11-20T00:00:00Z&to=2026-11-20T03:00:00Z");
        Assert.Equal(2, window1.GetProperty("total").GetInt32());
        Assert.All(window1.GetProperty("items").EnumerateArray().ToList(),
            i => Assert.Equal(1, i.GetProperty("eventCount").GetInt32()));

        // 窗口只覆盖 T2 → 仅销售码。
        var (_, window2, _) = await GetAsync(client,
            "/api/v1/codes?q=6900000000000022&from=2026-11-20T04:00:00Z&to=2026-11-20T06:00:00Z");
        Assert.Equal(1, window2.GetProperty("total").GetInt32());
        Assert.Equal(both, window2.GetProperty("items")[0].GetProperty("code").GetString());

        // 非法 eventType → 400。
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync("/api/v1/codes?eventType=RETURN")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync("/api/v1/codes?from=not-a-date")).StatusCode);
    }

    // ---------------------------------------------------------------- 3. env 隔离

    [Fact]
    public async Task Code_list_is_env_scoped()
    {
        const string mockCode = "690000000000003301";
        const string liveCode = "690000000000003302";
        await SeedEventAsync(mockCode, "PURCHASE_IN", "IN", T1);
        await SeedEventAsync(liveCode, "PURCHASE_IN", "IN", T1, env: "LIVE");

        var client = await LoginAsync("codes-admin", AdminPassword);
        // 缺省 = 宿主 env（MOCK）：LIVE 事件不可见。
        var (_, mock, mockText) = await GetAsync(client, "/api/v1/codes?q=6900000000000033");
        Assert.Equal(1, mock.GetProperty("total").GetInt32());
        Assert.Equal(mockCode, mock.GetProperty("items")[0].GetProperty("code").GetString());
        Assert.DoesNotContain(liveCode, mockText);

        var (_, mockExplicit, _) = await GetAsync(client, "/api/v1/codes?q=6900000000000033&env=MOCK");
        Assert.Equal(1, mockExplicit.GetProperty("total").GetInt32());
        Assert.Equal("MOCK", mockExplicit.GetProperty("env").GetString());

        // env=LIVE：MOCK 事件不可见。
        var (_, live, liveText) = await GetAsync(client, "/api/v1/codes?q=6900000000000033&env=LIVE");
        Assert.Equal(1, live.GetProperty("total").GetInt32());
        Assert.Equal(liveCode, live.GetProperty("items")[0].GetProperty("code").GetString());
        Assert.DoesNotContain(mockCode, liveText);
        Assert.Equal("LIVE", live.GetProperty("env").GetString());

        // 非法 env → 400 VALIDATION。
        var bad = await client.GetAsync("/api/v1/codes?env=PROD");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("VALIDATION", await bad.Content.ReadAsStringAsync());
    }

    // ---------------------------------------------------------------- 4. 事件链 + 前序

    [Fact]
    public async Task Code_detail_returns_ordered_event_chain_with_predecessor()
    {
        const string code = "690000000000004401";
        var first = await SeedEventAsync(code, "PURCHASE_IN", "IN", T1,
            businessDatetime: T1.AddMinutes(-5));
        var second = await SeedEventAsync(code, "SALE", "OUT", T2, predecessorEventId: first, sourceLineId: 7);

        var client = await LoginAsync("codes-admin", AdminPassword);
        var (status, data, _) = await GetAsync(client, "/api/v1/codes/" + code);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(code, data.GetProperty("code").GetProperty("code").GetString());
        Assert.Equal("MOCK", data.GetProperty("code").GetProperty("env").GetString());
        Assert.Equal("S8P20", data.GetProperty("code").GetProperty("format").GetString());

        var events = data.GetProperty("events").EnumerateArray().ToList();
        Assert.Equal(2, events.Count);
        // 升序（occurred_at, id）：第一条入库、第二条销售。
        Assert.Equal("PURCHASE_IN", events[0].GetProperty("eventType").GetString());
        Assert.Equal("SALE", events[1].GetProperty("eventType").GetString());
        Assert.Equal("IN", events[0].GetProperty("direction").GetString());
        Assert.Equal("OUT", events[1].GetProperty("direction").GetString());
        Assert.Equal(first.ToString(), events[0].GetProperty("eventId").GetString());
        Assert.Equal(second.ToString(), events[1].GetProperty("eventId").GetString());
        // 对象标识一律 JSON 字符串；sourceSnapshotId/uploadTaskId 与 M2/M3 一致为数字/null。
        Assert.Equal(JsonValueKind.String, events[1].GetProperty("eventId").ValueKind);
        Assert.Equal(first.ToString(), events[1].GetProperty("predecessorEventId").GetString());
        Assert.Equal(JsonValueKind.Null, events[0].GetProperty("predecessorEventId").ValueKind);
        // 0 = 外部证据事件（前端显示 "—"）。
        Assert.Equal(0, events[0].GetProperty("sourceLineId").GetInt64());
        Assert.Equal(7, events[1].GetProperty("sourceLineId").GetInt64());
        Assert.Equal(JsonValueKind.Null, events[0].GetProperty("uploadTaskId").ValueKind);
        Assert.Equal(T1.AddMinutes(-5), events[0].GetProperty("businessDatetime").GetDateTime());
    }

    // ---------------------------------------------------------------- 5. 占用 + 激活单号

    [Fact]
    public async Task Code_detail_includes_claims_and_active_bill_code()
    {
        const string code = "690000000000005501";
        await SeedEventAsync(code, "PURCHASE_IN", "IN", T1);
        var (taskId, _, _, billCode) = await SeedTaskWithPartAsync("CLAIM");
        // 先释放一条历史占用，再建一条激活占用：激活优先展示，单号取激活任务分片。
        await SeedClaimAsync(code, "RELEASED", "SALE", T1.AddHours(-2), releasedAt: T1.AddHours(-1),
            releasedBy: "operator");
        await SeedClaimAsync(code, "ACTIVE", "PURCHASE_IN", T1, uploadTaskId: taskId);

        var client = await LoginAsync("codes-admin", AdminPassword);
        var (status, data, _) = await GetAsync(client, "/api/v1/codes/" + code);
        Assert.Equal(HttpStatusCode.OK, status);
        var claims = data.GetProperty("claims").EnumerateArray().ToList();
        Assert.Equal(2, claims.Count);
        // 按 claimed_at 升序：先历史释放、后激活。
        Assert.Equal("RELEASED", claims[0].GetProperty("claimState").GetString());
        Assert.Equal("operator", claims[0].GetProperty("releasedBy").GetString());
        Assert.Equal("ACTIVE", claims[1].GetProperty("claimState").GetString());
        Assert.Equal("PURCHASE_IN", claims[1].GetProperty("eventScope").GetString());
        Assert.Equal(taskId, claims[1].GetProperty("uploadTaskId").GetInt64());

        // 列表口径：占用状态 ACTIVE + 范围 + 激活任务分片单号。
        var (_, list, _) = await GetAsync(client, "/api/v1/codes?q=6900000000000055");
        var row = list.GetProperty("items")[0];
        Assert.Equal("ACTIVE", row.GetProperty("claimState").GetString());
        Assert.Equal("PURCHASE_IN", row.GetProperty("claimScope").GetString());
        Assert.Equal(billCode, row.GetProperty("billCode").GetString());
    }

    // ---------------------------------------------------------------- 6. 逐码结果 + 退货确认

    [Fact]
    public async Task Code_detail_includes_code_results_and_return_confirmation()
    {
        const string code = "690000000000006601";
        var (taskId, partId, attemptId, billCode) = await SeedTaskWithPartAsync("RESULT",
            taskState: "PartialSuccess", partState: "PartialSuccess");
        await SeedEventAsync(code, "SALE", "OUT", T2, uploadTaskId: taskId);
        await SeedCodeResultAsync(code, taskId, partId, attemptId, "FAILED", "平台拒绝", T2.AddMinutes(1));
        var predecessorTask = await SeedTaskAsync("ORIGIN-SALE");
        await SeedReturnConfirmationAsync(taskId, "CONFIRMED", "SALE", predecessorTask, T2.AddMinutes(5));

        var client = await LoginAsync("codes-admin", AdminPassword);
        var (status, data, text) = await GetAsync(client, "/api/v1/codes/" + code);
        Assert.Equal(HttpStatusCode.OK, status);

        var results = data.GetProperty("results").EnumerateArray().ToList();
        var result = Assert.Single(results);
        Assert.Equal("FAILED", result.GetProperty("result").GetString());
        Assert.Equal("平台拒绝", result.GetProperty("reason").GetString());
        Assert.Equal(billCode, result.GetProperty("billCode").GetString());
        Assert.Equal("PartialSuccess", result.GetProperty("partState").GetString());
        Assert.Equal("PartialSuccess", result.GetProperty("taskState").GetString());
        Assert.Equal("req-1", result.GetProperty("requestId").GetString());
        Assert.Equal(200, result.GetProperty("httpStatus").GetInt32());
        Assert.Equal(taskId.ToString(), result.GetProperty("taskId").GetString());

        var confirmations = data.GetProperty("confirmations").EnumerateArray().ToList();
        var confirmation = Assert.Single(confirmations);
        Assert.Equal(taskId.ToString(), confirmation.GetProperty("taskId").GetString());
        Assert.Equal("CONFIRMED", confirmation.GetProperty("decision").GetString());
        Assert.Equal("SALE", confirmation.GetProperty("relationKind").GetString());
        Assert.Equal(predecessorTask.ToString(), confirmation.GetProperty("predecessorTaskId").GetString());
        Assert.Equal("confirm-user", confirmation.GetProperty("confirmedBy").GetString());

        // 脱敏：platform_evidence / raw_response / payload 一律不外泄。
        Assert.DoesNotContain(LeakMarker, text);
        Assert.DoesNotContain("platform_evidence", text);
        Assert.DoesNotContain("raw_response", text);
    }

    // ---------------------------------------------------------------- 7. 404 / 跨环境

    [Fact]
    public async Task Code_detail_404_for_unknown_or_cross_env_code()
    {
        const string mockOnly = "690000000000007701";
        await SeedEventAsync(mockOnly, "PURCHASE_IN", "IN", T1);

        var client = await LoginAsync("codes-admin", AdminPassword);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/codes/" + mockOnly)).StatusCode);
        // 只在 MOCK 有事件的码在 LIVE 视图 → 404（不暴露另一环境事实）。
        var cross = await client.GetAsync("/api/v1/codes/" + mockOnly + "?env=LIVE");
        Assert.Equal(HttpStatusCode.NotFound, cross.StatusCode);
        Assert.Contains("NOT_FOUND", await cross.Content.ReadAsStringAsync());
        // 完全未知的码 → 404。
        var unknown = await client.GetAsync("/api/v1/codes/699999999999999999");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Contains("NOT_FOUND", await unknown.Content.ReadAsStringAsync());
        // 非法 env → 400 VALIDATION。
        var badEnv = await client.GetAsync("/api/v1/codes/" + mockOnly + "?env=PROD");
        Assert.Equal(HttpStatusCode.BadRequest, badEnv.StatusCode);
        Assert.Contains("VALIDATION", await badEnv.Content.ReadAsStringAsync());
    }

    // ---------------------------------------------------------------- 8. 匿名 401 / 权限

    [Fact]
    public async Task Codes_endpoints_reject_anonymous_with_401()
    {
        const string code = "690000000000008801";
        await SeedEventAsync(code, "PURCHASE_IN", "IN", T1);
        var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/api/v1/codes")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/api/v1/codes/" + code)).StatusCode);

        // trace:read 只读账号可读（逐请求权限门禁不阻塞合法读者）。
        var reader = await LoginAsync("codes-reader", ReaderPassword);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/api/v1/codes?q=6900000000000088")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/api/v1/codes/" + code)).StatusCode);

        // 只读：除 GET 外无码台账写路由（POST/DELETE 一律无匹配 → 405）。
        Assert.Equal(HttpStatusCode.MethodNotAllowed,
            (await reader.PostAsync("/api/v1/codes", new StringContent("{}"))).StatusCode);
    }

    // ---------------------------------------------------------------- 9. 占用状态筛选

    [Fact]
    public async Task Code_list_state_filter_active_vs_none()
    {
        const string active = "690000000000009901";
        const string released = "690000000000009902";
        const string none = "690000000000009903";
        await SeedEventAsync(active, "PURCHASE_IN", "IN", T1);
        await SeedEventAsync(released, "PURCHASE_IN", "IN", T1);
        await SeedEventAsync(none, "PURCHASE_IN", "IN", T1);
        await SeedClaimAsync(active, "ACTIVE", "PURCHASE_IN", T1);
        await SeedClaimAsync(released, "RELEASED", "PURCHASE_IN", T1.AddHours(-2),
            releasedAt: T1.AddHours(-1), releasedBy: "operator");

        var client = await LoginAsync("codes-admin", AdminPassword);
        var (_, activeRows, _) = await GetAsync(client, "/api/v1/codes?q=6900000000000099&state=ACTIVE");
        Assert.Equal(active, Assert.Single(Codes(activeRows)));

        var (_, releasedRows, _) = await GetAsync(client, "/api/v1/codes?q=6900000000000099&state=RELEASED");
        Assert.Equal(released, Assert.Single(Codes(releasedRows)));
        Assert.Equal("RELEASED", releasedRows.GetProperty("items")[0].GetProperty("claimState").GetString());

        var (_, noneRows, _) = await GetAsync(client, "/api/v1/codes?q=6900000000000099&state=NONE");
        Assert.Equal(none, Assert.Single(Codes(noneRows)));
        Assert.Equal("NONE", noneRows.GetProperty("items")[0].GetProperty("claimState").GetString());
        Assert.Equal(JsonValueKind.Null, noneRows.GetProperty("items")[0].GetProperty("billCode").ValueKind);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync("/api/v1/codes?state=LOST")).StatusCode);
    }

    private sealed record SessionResp(SessionData Data);
    private sealed record SessionData(bool Authenticated, string CsrfToken);
}