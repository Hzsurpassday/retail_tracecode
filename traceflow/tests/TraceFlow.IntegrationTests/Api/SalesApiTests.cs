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
using TraceFlow.Domain.Codes;
using TraceFlow.Infrastructure.Jobs;
using TraceFlow.Infrastructure.Sales;
using TraceFlow.Infrastructure.Security;
using TraceFlow.Infrastructure.Settings;
using TraceFlow.Infrastructure.Top;
using TraceFlow.IntegrationTests.Sources;

namespace TraceFlow.IntegrationTests.Api;

/// <summary>
/// Task 7 销售/退货 HTTP 层（UI-06/07）：日期/环境过滤与脱敏、钻取含核验/任务、退货确认权限与幂等、分页口径。
/// 种子全部直写 SQL（销售列表/钻取）或复用真实核验服务（退货确认），平台一侧绝不触网。
/// 测试宿主移除后台 HostedService：避免 Worker/SyncScheduler 异步认领作业干扰「无写入」断言。
/// </summary>
[SupportedOSPlatform("windows")]
public class SalesApiTests : IAsyncLifetime
{
    private Sql2008Fixture _fx = null!;
    private WebApplicationFactory<Program> _factory = null!;

    private const string AdminPassword = "Admin-Password-123";
    private const string OperatorPassword = "Oper-Password-123";

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
        await SeedUserAsync(c, "sales-admin", "销售管理员", AdminPassword, PermissionCatalog.All);
        await SeedUserAsync(c, "sales-operator", "销售操作员", OperatorPassword, new[] { "trace:read" });
    }

    public async Task DisposeAsync()
    {
        _factory.Dispose();
        await _fx.DisposeAsync();
    }

    private static async Task SeedUserAsync(SqlConnection c, string username,
        string display, string password, IEnumerable<string> permissions)
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

    // ---------------------------------------------------------------- 种子

    private static string Code(int n) => "8" + n.ToString("D19");
    private static string Sha(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    private sealed record SaleSeed(long OrgId, long SysId, long SnapshotId, long LineId, long CodeId, string Code,
        string DocKey);

    /// <summary>销售快照 + 行 + 码；raw_header/payload_json 埋入唯一标记，用于断言脱敏。</summary>
    private async Task<SaleSeed> SeedSaleAsync(string tag, int code, DateTime biz)
    {
        await using var c = await _fx.OpenAsync();
        var (orgId, sysId) = await TestSeed.EnsureAsync(c);
        var docKey = "SALE-API-" + tag;
        var normalized = Code(code);
        var codeId = await c.ExecuteScalarAsync<long>(@"
IF NOT EXISTS (SELECT 1 FROM dbo.trace_code WHERE code_normalized=@code)
    INSERT INTO dbo.trace_code(code_normalized,code_raw,format) VALUES(@code,@code,N'S8P20');
SELECT id FROM dbo.trace_code WHERE code_normalized=@code", new { code = normalized });
        var snapshotId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.source_snapshot(org_id,source_system_id,source_document_key,document_type,version,business_datetime,
    completeness_status,content_hash,raw_header,observed_at)
OUTPUT INSERTED.id
VALUES(@orgId,@sysId,@key,N'SALE',1,@biz,N'COMPLETE',@hash,@header,@biz)",
            new { orgId, sysId, key = docKey, biz, hash = Sha(docKey + "#1"),
                  header = "{\"refEntId\":\"REF\",\"appSecret\":\"SHOULD-NOT-LEAK\"}" });
        var lineId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.source_line(snapshot_id,source_line_key,product_source_id,quantity_original,unit_original,batch_raw,
    trace_code_raw,trace_code_normalized,code_format,line_hash,event_kind)
OUTPUT INSERTED.id
VALUES(@snapshotId,N'1',N'SP0001',2,N'盒',N'B2026',@code,@code,N'S8P20',@hash,N'SALE')",
            new { snapshotId, code = normalized, hash = Sha(docKey + "#1|1") });
        return new SaleSeed(orgId, sysId, snapshotId, lineId, codeId, normalized, docKey);
    }

    /// <summary>核验结论 + 逐码结论 + 321 任务/分片（按需给 env）。</summary>
    private async Task<long> SeedVerificationAsync(SaleSeed seed, string env, bool withTask)
    {
        await using var c = await _fx.OpenAsync();
        var resultJson = JsonSerializer.Serialize(new
        {
            state = "PASSED",
            reason = (string?)null,
            saleLineKeys = new[] { "1" },
            codes = new[] { new { code = seed.Code, lineKey = "1", result = "PASS", reason = (string?)null } },
            returnCandidates = Array.Empty<object>(),
        });
        var verificationId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.verification(org_id,env,event_key,source_snapshot_id,source_hash,code_set_hash,rule_version,state,
    result_json,verified_at,is_current)
OUTPUT INSERTED.id
VALUES(@orgId,@env,@eventKey,@snapshotId,@hash,@hash,N'r1',N'PASSED',@resultJson,SYSUTCDATETIME(),1)",
            new { orgId = seed.OrgId, env, eventKey = $"KSOA:{seed.DocKey}:SALE", snapshotId = seed.SnapshotId,
                  hash = Sha(seed.DocKey), resultJson });
        await c.ExecuteAsync(@"
INSERT INTO dbo.verification_code(verification_id,org_id,env,code_id,source_line_id,result,reason)
VALUES(@verificationId,@orgId,@env,@codeId,@lineId,N'PASS',NULL)",
            new { verificationId, seed.OrgId, env, seed.CodeId, seed.LineId });
        if (!withTask) return verificationId;
        var taskId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upload_task(org_id,env,event_key,task_type,source_snapshot_id,source_hash,payload_hash,payload_json,
    state,source_integrity,match_state,verification_state,dependency_state)
OUTPUT INSERTED.id
VALUES(@orgId,@env,@eventKey,N'RETAIL_321',@snapshotId,@hash,@hash,@payload,
    N'Succeeded',N'COMPLETE',N'COMPLETE',N'PASSED',N'NONE')",
            new { seed.OrgId, env, eventKey = $"KSOA:{seed.DocKey}:RETAIL_321", seed.SnapshotId,
                  hash = Sha(seed.DocKey + ":task"), payload = "{\"lines\":[],\"appSecret\":\"SHOULD-NOT-LEAK\"}" });
        await c.ExecuteAsync(@"
INSERT INTO dbo.upload_part(task_id,org_id,env,part_index,bill_code,payload_json,payload_hash,code_set_hash,state)
VALUES(@taskId,@orgId,@env,0,@bill,N'{}',@hash,@hash,N'Succeeded')",
            new { taskId, seed.OrgId, env, bill = "TFS" + Guid.NewGuid().ToString("N")[..8], hash = Sha(seed.DocKey + ":part") });
        await c.ExecuteAsync("UPDATE dbo.verification SET upload_task_id=@taskId WHERE id=@verificationId",
            new { taskId, verificationId });
        return verificationId;
    }

    // ---------------------------------------------------------------- 登录

    private async Task<HttpClient> LoginAsync(string username, string password)
    {
        var client = _factory.CreateClient();
        var session = await client.GetFromJsonAsync<SessionResp>("/api/v1/auth/session");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session!.Data.CsrfToken);
        (await client.PostAsJsonAsync("/api/v1/auth/login", new { username, password })).EnsureSuccessStatusCode();
        var session2 = await client.GetFromJsonAsync<SessionResp>("/api/v1/auth/session");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session2!.Data.CsrfToken);
        return client;
    }

    private static void NewIdemKey(HttpClient client)
    {
        client.DefaultRequestHeaders.Remove("Idempotency-Key");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
    }

    // ---------------------------------------------------------------- 用例

    [Fact]
    public async Task Sales_list_filters_by_date_and_env_and_masks_secrets()
    {
        var today = await SeedSaleAsync("A1", 1, new DateTime(2026, 11, 20, 6, 0, 0, DateTimeKind.Utc));
        await SeedVerificationAsync(today, "MOCK", withTask: true);
        var outside = await SeedSaleAsync("A2", 2, new DateTime(2026, 10, 1, 6, 0, 0, DateTimeKind.Utc));
        await SeedVerificationAsync(outside, "MOCK", withTask: true);
        var liveOnly = await SeedSaleAsync("A3", 3, new DateTime(2026, 11, 21, 6, 0, 0, DateTimeKind.Utc));
        await SeedVerificationAsync(liveOnly, "LIVE", withTask: false);

        var client = await LoginAsync("sales-admin", AdminPassword);
        // 列表/钻取/退货列表仅「登录」可读：匿名请求一律 401（brief 权限列）。
        var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/pos-sales")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync($"/api/v1/pos-sales/{today.SnapshotId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/returns")).StatusCode);
        var mockResp = await client.GetAsync("/api/v1/pos-sales?dateFrom=2026-11-20&dateTo=2026-11-20");
        Assert.Equal(HttpStatusCode.OK, mockResp.StatusCode);
        var mockText = await mockResp.Content.ReadAsStringAsync();
        using (var doc = JsonDocument.Parse(mockText))
        {
            var data = doc.RootElement.GetProperty("data");
            Assert.Equal(1, data.GetProperty("total").GetInt32());
            var item = Assert.Single(data.GetProperty("items").EnumerateArray().ToList());
            Assert.Equal(today.SnapshotId.ToString(), item.GetProperty("snapshotId").GetString());
            Assert.Equal("MOCK", item.GetProperty("env").GetString());
            Assert.Equal("PASSED", item.GetProperty("verificationState").GetString());
            Assert.Equal("Succeeded", item.GetProperty("taskState").GetString());
            Assert.Equal(1, item.GetProperty("lineCount").GetInt32());
            Assert.Equal(JsonValueKind.String, item.GetProperty("snapshotId").ValueKind);
            Assert.Equal(JsonValueKind.String, item.GetProperty("taskId").ValueKind);
        }
        // 脱敏：raw_header/payload_json/内埋标记一律不得出现在响应中。
        Assert.DoesNotContain("raw_header", mockText);
        Assert.DoesNotContain("payload_json", mockText);
        Assert.DoesNotContain("SHOULD-NOT-LEAK", mockText);
        Assert.DoesNotContain("appSecret", mockText);

        // env 过滤把 LIVE 与 MOCK 分开计数（默认当前环境 MOCK）。
        var liveResp = await client.GetAsync("/api/v1/pos-sales?env=LIVE");
        using (var doc = JsonDocument.Parse(await liveResp.Content.ReadAsStringAsync()))
        {
            var data = doc.RootElement.GetProperty("data");
            Assert.Equal(1, data.GetProperty("total").GetInt32());
            var item = Assert.Single(data.GetProperty("items").EnumerateArray().ToList());
            Assert.Equal(liveOnly.SnapshotId.ToString(), item.GetProperty("snapshotId").GetString());
            Assert.Equal("LIVE", item.GetProperty("env").GetString());
        }

        // 分页口径：page<1 / pageSize<1 → 400；pageSize>100 收敛到 100。
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/pos-sales?page=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/pos-sales?pageSize=0")).StatusCode);
        var clamped = await client.GetAsync("/api/v1/pos-sales?pageSize=500");
        using (var doc = JsonDocument.Parse(await clamped.Content.ReadAsStringAsync()))
            Assert.Equal(100, doc.RootElement.GetProperty("data").GetProperty("pageSize").GetInt32());
    }

    [Fact]
    public async Task Sale_detail_includes_verification_and_task_state()
    {
        var seed = await SeedSaleAsync("B1", 11, new DateTime(2026, 11, 20, 6, 0, 0, DateTimeKind.Utc));
        await SeedVerificationAsync(seed, "MOCK", withTask: true);
        var client = await LoginAsync("sales-admin", AdminPassword);
        var resp = await client.GetAsync($"/api/v1/pos-sales/{seed.SnapshotId}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var text = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(text);
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal(seed.SnapshotId.ToString(), data.GetProperty("snapshot").GetProperty("snapshotId").GetString());
        Assert.Equal(seed.DocKey, data.GetProperty("snapshot").GetProperty("docKey").GetString());
        var line = data.GetProperty("lines")[0];
        Assert.Equal(seed.Code, line.GetProperty("traceCodeNormalized").GetString());
        Assert.Equal("2", line.GetProperty("quantity").GetString());
        var code = data.GetProperty("codes")[0];
        Assert.Equal(seed.Code, code.GetProperty("codeNormalized").GetString());
        Assert.Equal("PASS", code.GetProperty("result").GetString());
        Assert.Equal("PASSED", data.GetProperty("verification").GetProperty("state").GetString());
        var task = data.GetProperty("task");
        Assert.Equal("RETAIL_321", task.GetProperty("taskType").GetString());
        Assert.Equal("Succeeded", task.GetProperty("state").GetString());
        Assert.Single(task.GetProperty("parts").EnumerateArray().ToList());
        Assert.DoesNotContain("SHOULD-NOT-LEAK", text);
        // 未知快照 404（未知任务 404 在 UploadApiTests 覆盖同一错误契约）。
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/pos-sales/999999")).StatusCode);
    }

    /// <summary>详情与列表同一 env 口径：非法值 400；快照不属于该环境视图 → 404；未核验快照两视图都可见。</summary>
    [Fact]
    public async Task Sale_detail_is_env_scoped_like_the_list()
    {
        var mock = await SeedSaleAsync("E1", 51, new DateTime(2026, 11, 20, 6, 0, 0, DateTimeKind.Utc));
        await SeedVerificationAsync(mock, "MOCK", withTask: true);
        var live = await SeedSaleAsync("E2", 52, new DateTime(2026, 11, 21, 6, 0, 0, DateTimeKind.Utc));
        await SeedVerificationAsync(live, "LIVE", withTask: false);
        var unverified = await SeedSaleAsync("E3", 53, new DateTime(2026, 11, 22, 6, 0, 0, DateTimeKind.Utc));

        var client = await LoginAsync("sales-admin", AdminPassword);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/v1/pos-sales/{mock.SnapshotId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await client.GetAsync($"/api/v1/pos-sales/{mock.SnapshotId}?env=MOCK")).StatusCode);
        var cross = await client.GetAsync($"/api/v1/pos-sales/{mock.SnapshotId}?env=LIVE");
        Assert.Equal(HttpStatusCode.NotFound, cross.StatusCode);
        Assert.Contains("NOT_FOUND", await cross.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK,
            (await client.GetAsync($"/api/v1/pos-sales/{live.SnapshotId}?env=LIVE")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/v1/pos-sales/{live.SnapshotId}")).StatusCode);
        // R-M3-17：未核验快照无 env 归属，两个环境视图都能钻取。
        Assert.Equal(HttpStatusCode.OK,
            (await client.GetAsync($"/api/v1/pos-sales/{unverified.SnapshotId}?env=LIVE")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync($"/api/v1/pos-sales/{mock.SnapshotId}?env=PROD")).StatusCode);
    }

    // ---------------------------------------------------------------- 退货确认

    private sealed record ReturnScenario(long SaleTaskId, long SaleEventId, string SaleBillCode, long ReturnTaskId,
        int ReturnVersion);

    private SettingsService NewSettings() => new(_fx.ConnectionString, new DpapiProtector());

    private SaleVerificationService NewVerifyService()
    {
        var settings = NewSettings();
        var audit = new AuditLog();
        var queue = new JobQueue(_fx.ConnectionString, audit);
        var top = new TopClientService(_fx.ConnectionString, new FakeTopGateway(_ => throw new InvalidOperationException("依赖已满足/退货行不应查平台")),
            new RateBudgetService(), new ChannelPauseService(), audit);
        return new SaleVerificationService(_fx.ConnectionString, "MOCK", settings, top,
            new RetailTaskFactory(settings, queue), queue, audit);
    }

    private async Task<ReturnScenario> SeedConfirmableReturnAsync(string tag, int codeNumber)
    {
        var saleCode = Code(codeNumber);
        await using var c = await _fx.OpenAsync();
        var (orgId, sysId) = await TestSeed.EnsureAsync(c);
        // 采购入库证据：让 321 核验无需平台调用即 PASSED（前序事件 + Succeeded 任务）。
        var purchaseTaskId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upload_task(org_id,env,event_key,task_type,source_hash,payload_hash,state,source_integrity,match_state,
    verification_state,dependency_state)
OUTPUT INSERTED.id
VALUES(@orgId,N'MOCK',@key,N'PURCHASE_IN_102',@hash,@hash,N'Succeeded',N'COMPLETE',N'COMPLETE',N'PASSED',N'NONE')",
            new { orgId, key = $"KSOA:{saleCode}:PURCHASE_IN", hash = Sha(saleCode) });
        // 商品映射（核验需要 product + mapping 才能生成行）。
        await c.ExecuteAsync(@"
IF NOT EXISTS (SELECT 1 FROM dbo.product WHERE org_id=@orgId AND source_system_id=@sysId AND spid=N'SP0001')
INSERT INTO dbo.product(org_id,source_system_id,spid,name,trace_classification)
VALUES(@orgId,@sysId,N'SP0001',N'药品',N'REQUIRED')", new { orgId, sysId });
        var productId = await c.ExecuteScalarAsync<long>(
            "SELECT id FROM dbo.product WHERE org_id=@orgId AND source_system_id=@sysId AND spid=N'SP0001'",
            new { orgId, sysId });
        var drugId = await c.ExecuteScalarAsync<long>(@"
IF NOT EXISTS (SELECT 1 FROM dbo.platform_drug WHERE platform_drug_id=N'DRUG-SP0001')
    INSERT INTO dbo.platform_drug(platform_drug_id,source_kind) VALUES(N'DRUG-SP0001',N'FIXTURE');
SELECT id FROM dbo.platform_drug WHERE platform_drug_id=N'DRUG-SP0001'");
        await c.ExecuteAsync(@"
IF NOT EXISTS (SELECT 1 FROM dbo.product_mapping
               WHERE product_id=@productId AND state IN (N'AUTO_CONFIRMED',N'MANUAL_CONFIRMED'))
INSERT INTO dbo.product_mapping(org_id,product_id,platform_drug_id,state)
VALUES(@orgId,@productId,@drugId,N'AUTO_CONFIRMED')", new { orgId, productId, drugId });

        var saleSnap = await SeedSaleAsync("RT" + tag + "S", codeNumber, new DateTime(2026, 11, 20, 6, 0, 0, DateTimeKind.Utc));
        // 前序 PURCHASE_IN 事件（按码）。
        await c.ExecuteAsync(@"
INSERT INTO dbo.code_event(org_id,env,code_id,event_type,direction,upload_task_id)
VALUES(@orgId,N'MOCK',@codeId,N'PURCHASE_IN',N'IN',@purchaseTaskId)",
            new { orgId, codeId = saleSnap.CodeId, purchaseTaskId });
        // 销售核验（PASSED → 321）。
        await NewVerifyService().VerifySnapshotAsync(saleSnap.SnapshotId, default);
        var saleTaskId = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.upload_task WHERE event_key=@k",
            new { k = $"KSOA:{saleSnap.DocKey}:RETAIL_321" });
        // 前序任务必须有平台受理结论（Succeeded 或受理证据）才允许确认退货。
        await c.ExecuteAsync("UPDATE dbo.upload_task SET state=N'Succeeded' WHERE id=@id", new { id = saleTaskId });
        var saleEventId = await c.ExecuteScalarAsync<long>(
            "SELECT id FROM dbo.code_event WHERE upload_task_id=@id AND event_type=N'SALE'", new { id = saleTaskId });
        var saleBillCode = await c.ExecuteScalarAsync<string>(
            "SELECT bill_code FROM dbo.upload_part WHERE task_id=@id", new { id = saleTaskId });
        // 退货单（CONSUMER_RETURN 负行）→ 116 占位（Blocked + RETURN_CONFIRM_REQUIRED）。
        var returnSnap = await SeedSaleAsync("RT" + tag + "R", codeNumber + 5000, new DateTime(2026, 11, 20, 7, 0, 0, DateTimeKind.Utc));
        // 覆盖退货行的 event_kind/数量与码（SeedSaleAsync 固定 SALE/2，这里直接更新）。
        await c.ExecuteAsync(@"
UPDATE dbo.source_line SET event_kind=N'CONSUMER_RETURN', quantity_original=-1, trace_code_raw=@code,
    trace_code_normalized=@code WHERE id=@lineId", new { code = saleCode, lineId = returnSnap.LineId });
        await NewVerifyService().VerifySnapshotAsync(returnSnap.SnapshotId, default);
        var returnTaskId = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.upload_task WHERE event_key=@k",
            new { k = $"KSOA:{returnSnap.DocKey}:CONSUMER_RETURN_116" });
        var version = await c.ExecuteScalarAsync<int>("SELECT version FROM dbo.upload_task WHERE id=@id", new { id = returnTaskId });
        return new ReturnScenario(saleTaskId, saleEventId, saleBillCode!, returnTaskId, version);
    }

    [Fact]
    public async Task Return_confirm_requires_return_permission()
    {
        var scenario = await SeedConfirmableReturnAsync("P1", 21);
        var client = await LoginAsync("sales-operator", OperatorPassword);
        NewIdemKey(client);
        var resp = await client.PostAsJsonAsync($"/api/v1/returns/{scenario.ReturnTaskId}/confirm", new
        {
            expectedVersion = scenario.ReturnVersion,
            decision = "CONFIRMED",
            predecessorEventIds = new[] { scenario.SaleEventId },
            evidence = new { reason = "原码退回" },
            idemKey = Guid.NewGuid().ToString(),
        });
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        await using var c = await _fx.OpenAsync();
        Assert.Equal("Blocked", await c.ExecuteScalarAsync<string>(
            "SELECT state FROM dbo.upload_task WHERE id=@id", new { id = scenario.ReturnTaskId }));
        Assert.Equal(0, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.return_confirmation WHERE task_id=@id", new { id = scenario.ReturnTaskId }));
    }

    [Fact]
    public async Task Return_confirm_is_idempotent_by_idem_key()
    {
        var scenario = await SeedConfirmableReturnAsync("I1", 31);
        var client = await LoginAsync("sales-admin", AdminPassword);
        NewIdemKey(client);
        var idemKey = Guid.NewGuid().ToString();
        object Body() => new
        {
            expectedVersion = scenario.ReturnVersion,
            decision = "CONFIRMED",
            predecessorEventIds = new[] { scenario.SaleEventId },
            evidence = new { reason = "原码退回", actor = "tester" },
            idemKey,
        };
        var first = await client.PostAsJsonAsync($"/api/v1/returns/{scenario.ReturnTaskId}/confirm", Body());
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstText = await first.Content.ReadAsStringAsync();
        using (var doc = JsonDocument.Parse(firstText))
        {
            Assert.Equal("Ready", doc.RootElement.GetProperty("data").GetProperty("state").GetString());
            Assert.False(doc.RootElement.GetProperty("data").GetProperty("idempotent").GetBoolean());
        }
        NewIdemKey(client);
        var second = await client.PostAsJsonAsync($"/api/v1/returns/{scenario.ReturnTaskId}/confirm", Body());
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        using (var doc = JsonDocument.Parse(await second.Content.ReadAsStringAsync()))
        {
            Assert.Equal("Ready", doc.RootElement.GetProperty("data").GetProperty("state").GetString());
            Assert.True(doc.RootElement.GetProperty("data").GetProperty("idempotent").GetBoolean());
        }
        await using var c = await _fx.OpenAsync();
        Assert.Equal(1, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.return_confirmation WHERE task_id=@id", new { id = scenario.ReturnTaskId }));
        Assert.Equal(1, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.audit_log WHERE object_type=N'upload_task' AND object_id=@id AND action=N'task.return.confirmed'",
            new { id = scenario.ReturnTaskId.ToString() }));
    }

    /// <summary>
    /// 未知退货任务 404（不是 422）：任务不存在/跨环境 → NOT_FOUND；
    /// 任务存在但载荷缺 lines → 仍为 422 NOT_RETURNABLE（既有语义保持）。
    /// </summary>
    [Fact]
    public async Task Unknown_return_task_is_404_not_422()
    {
        var client = await LoginAsync("sales-admin", AdminPassword);
        NewIdemKey(client);
        var resp = await client.PostAsJsonAsync("/api/v1/returns/999999/confirm", new
        {
            expectedVersion = 1,
            decision = "CONFIRMED",
            predecessorEventIds = Array.Empty<long>(),
            evidence = new { reason = "原码退回" },
            idemKey = Guid.NewGuid().ToString(),
        });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Contains("NOT_FOUND", await resp.Content.ReadAsStringAsync());
        // 拒绝即零写入：无审计、无幂等记录。
        await using var c = await _fx.OpenAsync();
        Assert.Equal(0, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.audit_log WHERE action IN (N'task.return.confirmed',N'task.return.rejected')"));
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.idempotency_record"));
    }

    [Fact]
    public async Task Return_task_without_payload_lines_is_422_NOT_RETURNABLE()
    {
        const string key = "KSOA:RET-NOLINES:CONSUMER_RETURN_116";
        long taskId;
        await using (var c = await _fx.OpenAsync())
        {
            var (orgId, _) = await TestSeed.EnsureAsync(c);
            taskId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upload_task(org_id,env,event_key,task_type,source_hash,payload_json,payload_hash,state,state_reason,
    source_integrity,match_state,verification_state,dependency_state)
OUTPUT INSERTED.id
VALUES(@orgId,N'MOCK',@key,N'CONSUMER_RETURN_116',@hash,@payload,@hash,N'Blocked',
    N'RETURN_CONFIRM_REQUIRED',N'COMPLETE',N'COMPLETE',N'PASSED',N'NONE')",
                new { orgId, key, hash = Sha("return-no-lines"), payload = "{\"ruleVersion\":\"M3-1\"}" });
        }
        var client = await LoginAsync("sales-admin", AdminPassword);
        NewIdemKey(client);
        var resp = await client.PostAsJsonAsync($"/api/v1/returns/{taskId}/confirm", new
        {
            expectedVersion = 1,
            decision = "CONFIRMED",
            predecessorEventIds = Array.Empty<long>(),
            evidence = new { reason = "原码退回" },
            idemKey = Guid.NewGuid().ToString(),
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Contains("NOT_RETURNABLE", await resp.Content.ReadAsStringAsync());
        await using var c2 = await _fx.OpenAsync();
        Assert.Equal("Blocked", await c2.ExecuteScalarAsync<string>(
            "SELECT state FROM dbo.upload_task WHERE id=@id", new { id = taskId }));
    }

    /// <summary>
    /// 退货候选展示字段（单号/时间/码）：由前序事件码、发生时间与来源单据键联查补齐；
    /// 候选数量与核验结论一致（绝不因联查缺行丢候选）。
    /// </summary>
    [Fact]
    public async Task Returns_list_candidates_carry_doc_key_time_and_code()
    {
        var scenario = await SeedConfirmableReturnAsync("C1", 41);
        var client = await LoginAsync("sales-admin", AdminPassword);
        var resp = await client.GetAsync("/api/v1/returns");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var items = doc.RootElement.GetProperty("data").GetProperty("items").EnumerateArray().ToList();
        var item = Assert.Single(items, i => i.GetProperty("id").GetString() == scenario.ReturnTaskId.ToString());
        var candidates = item.GetProperty("predecessorCandidates").EnumerateArray().ToList();
        var candidate = Assert.Single(candidates);
        Assert.Equal(Code(41), candidate.GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.String, candidate.GetProperty("occurredAt").ValueKind);
        var docKey = candidate.GetProperty("docKey").GetString();
        Assert.False(string.IsNullOrWhiteSpace(docKey));
        Assert.Equal(JsonValueKind.String, candidate.GetProperty("predecessorEventId").ValueKind);
        Assert.Equal(JsonValueKind.Null, candidate.GetProperty("error").ValueKind);
    }

    /// <summary>
    /// 退货页签按稳定的确认分组过滤（UI-06/07）：confirmStatus ∈ PENDING|CONFIRMED|REJECTED（大小写敏感），
    /// 三组互斥；已确认走排除法，任务随后续上传推进改写 state_reason（PLATFORM_ACCEPTED）也不从页签消失；
    /// 非法值 → 422 VALIDATION（不是静默空列表）。
    /// </summary>
    [Fact]
    public async Task Returns_list_filters_by_stable_confirm_status_server_side()
    {
        var pending = await SeedReturnTaskAsync("SR1", "Blocked", "RETURN_CONFIRM_REQUIRED");
        var rejected = await SeedReturnTaskAsync("SR2", "Blocked", "RETURN_CONFIRM_REJECTED");
        var confirmed = await SeedReturnTaskAsync("SR4", "Ready", "RETURN_CONFIRMED");
        var client = await LoginAsync("sales-admin", AdminPassword);

        async Task<List<string>> IdsAsync(string query)
        {
            var resp = await client.GetAsync("/api/v1/returns?" + query);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var data = doc.RootElement.GetProperty("data");
            var items = data.GetProperty("items").EnumerateArray().ToList();
            // total 必须与过滤后的行一致：过滤发生在服务端 SQL（不是返回全量后前端裁剪）。
            Assert.Equal(items.Count, data.GetProperty("total").GetInt32());
            return items.Select(i => i.GetProperty("id").GetString()!).ToList();
        }

        Assert.Equal(new[] { pending.ToString() }, await IdsAsync("confirmStatus=PENDING"));
        Assert.Equal(new[] { rejected.ToString() }, await IdsAsync("confirmStatus=REJECTED"));
        Assert.Equal(new[] { confirmed.ToString() }, await IdsAsync("confirmStatus=CONFIRMED"));

        // 上传推进改写 state/state_reason 后，已确认页签必须仍返回该任务（不依赖 RETURN_CONFIRMED 留存）。
        await using (var c = await _fx.OpenAsync())
            await c.ExecuteAsync(
                "UPDATE dbo.upload_task SET state=N'Processing', state_reason=N'PLATFORM_ACCEPTED' WHERE id=@id",
                new { id = confirmed });
        Assert.Equal(new[] { confirmed.ToString() }, await IdsAsync("confirmStatus=CONFIRMED"));
        Assert.Equal(new[] { pending.ToString() }, await IdsAsync("confirmStatus=PENDING"));
        Assert.Equal(new[] { rejected.ToString() }, await IdsAsync("confirmStatus=REJECTED"));

        // 三组互斥：同一任务不得同时出现在两个页签。
        var pendingIds = await IdsAsync("confirmStatus=PENDING");
        var confirmedIds = await IdsAsync("confirmStatus=CONFIRMED");
        var rejectedIds = await IdsAsync("confirmStatus=REJECTED");
        Assert.Empty(pendingIds.Intersect(confirmedIds));
        Assert.Empty(pendingIds.Intersect(rejectedIds));
        Assert.Empty(confirmedIds.Intersect(rejectedIds));

        // 不带 confirmStatus 时不过滤（保持旧行为：全部退货任务）。
        Assert.Equal(3, (await IdsAsync("page=1&pageSize=20")).Count);

        // 非法值（大小写不符 / 未知分组 / 多值 / 前后空白）一律 422，绝不静默返回空列表。
        foreach (var bad in new[] { "pending", "Pending", "BOGUS", "PENDING,CONFIRMED", " PENDING" })
        {
            var resp = await client.GetAsync($"/api/v1/returns?confirmStatus={Uri.EscapeDataString(bad)}");
            Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
            Assert.Contains("VALIDATION", await resp.Content.ReadAsStringAsync());
        }
    }

    /// <summary>直写退货占位任务（不走核验服务）：用于确认分组过滤断言。</summary>
    private async Task<long> SeedReturnTaskAsync(string tag, string state, string stateReason)
    {
        await using var c = await _fx.OpenAsync();
        var (orgId, _) = await TestSeed.EnsureAsync(c);
        var key = $"KSOA:RET-{tag}:CONSUMER_RETURN_116";
        return await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upload_task(org_id,env,event_key,task_type,source_hash,payload_hash,state,state_reason,
    source_integrity,match_state,verification_state,dependency_state)
OUTPUT INSERTED.id
VALUES(@orgId,N'MOCK',@key,N'CONSUMER_RETURN_116',@hash,@hash,@state,@stateReason,
    N'COMPLETE',N'COMPLETE',N'PASSED',N'NONE')",
            new { orgId, key, hash = Sha("return-" + tag), state, stateReason });
    }

    private sealed record SessionResp(SessionData Data);
    private sealed record SessionData(bool Authenticated, string CsrfToken);
}