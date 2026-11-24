using System.Net;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using TraceFlow.Application.Security;
using TraceFlow.IntegrationTests.Sources;

namespace TraceFlow.IntegrationTests.Exceptions;

/// <summary>
/// 人工补配与异常统一队列（spec §4.2）：四类聚合与 OPEN 全量计数、类别/状态/环境过滤与
/// ROW_NUMBER 分页、接入异常/对账差异的留证关闭（422/409/LIVE 审计）、TASK/MAPPING 不可在队列解决
/// （422 + 深链）、详情跨环境/未知 ID 不暴露，以及匿名 401。种子直写 SQL；任何原始平台响应不入响应。
/// </summary>
[SupportedOSPlatform("windows")]
public class ExceptionQueueTests : IAsyncLifetime
{
    private static readonly DateTime T0 = new(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc);

    private const string AdminPassword = "Exc-Admin-123";

    private Sql2008Fixture _fx = null!;
    private WebApplicationFactory<Program> _factory = null!;
    private long _orgId;
    private long _sysId;

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
        (_orgId, _sysId) = await TestSeed.EnsureAsync(c);
        await SeedUserAsync(c, "exc-admin", "异常队列管理员", AdminPassword,
            new[] { PermissionCatalog.TraceRead, PermissionCatalog.SystemManage });
    }

    public async Task DisposeAsync()
    {
        _factory.Dispose();
        await _fx.DisposeAsync();
    }

    // ---------------------------------------------------------------- 1. 四类聚合

    [Fact]
    public async Task Queue_aggregates_four_categories_with_open_counts()
    {
        await using var c = await _fx.OpenAsync();
        var longDetail = new string('X', 250);
        var ingestId = await SeedIngestAsync(c, "DOC-AGG-1", "MISSING_LINE_KEY", longDetail, T0.AddHours(1));
        var reconId = await SeedDifferenceAsync(c, "MOCK", "DOC-AGG-2", T0.AddHours(3));
        var taskId = await SeedTaskAsync(c, "MOCK", "KSOA:DOC-AGG-3:RETAIL_321", "ManualAction",
            "需人工选择映射后重试", T0.AddHours(2));
        var productMappingId = await SeedProductConflictAsync(c, "SPID-AGG-1", "聚合测试药品", "PD-AGG-1", T0.AddHours(4));
        var supplierMappingId = await SeedSupplierConflictAsync(c, "DWBH-AGG-1", "聚合测试供应商", "REF-AGG-1");

        var client = await LoginAsync();
        var listResp = await client.GetAsync("/api/v1/exceptions");
        var listRaw = await listResp.Content.ReadAsStringAsync();
        Assert.True(listResp.IsSuccessStatusCode, listRaw);
        var json = JsonDocument.Parse(listRaw).RootElement;
        var data = json.GetProperty("data");

        Assert.Equal(5, data.GetProperty("total").GetInt32());
        Assert.Equal(50, data.GetProperty("pageSize").GetInt32());
        Assert.Equal("MOCK", data.GetProperty("env").GetString());
        var counts = data.GetProperty("counts");
        Assert.Equal(1, counts.GetProperty("ingestOpen").GetInt32());
        Assert.Equal(1, counts.GetProperty("reconOpen").GetInt32());
        Assert.Equal(1, counts.GetProperty("taskOpen").GetInt32());
        Assert.Equal(2, counts.GetProperty("mappingOpen").GetInt32());

        var items = data.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(5, items.Count);
        // createdAt DESC，NULL 置后：商品映射(4h) → 对账(3h) → 任务(2h) → 接入(1h) → 供应商映射(NULL)。
        Assert.Equal(new[] { "MAPPING", "RECON", "TASK", "INGEST", "MAPPING" },
            items.Select(i => i.GetProperty("category").GetString()).ToArray());

        var ingest = items.Single(i => i.GetProperty("category").GetString() == "INGEST");
        Assert.Equal(ingestId.ToString(), ingest.GetProperty("id").GetString());
        Assert.Equal("OPEN", ingest.GetProperty("status").GetString());
        Assert.Equal("MISSING_LINE_KEY", ingest.GetProperty("title").GetString());
        Assert.Equal(200, ingest.GetProperty("detailSnippet").GetString()!.Length);
        Assert.Equal(longDetail[..200], ingest.GetProperty("detailSnippet").GetString());
        Assert.Equal("DOC-AGG-1", ingest.GetProperty("objectRef").GetString());
        Assert.Equal("/imports", ingest.GetProperty("deepLink").GetString());
        Assert.Equal(JsonValueKind.Null, ingest.GetProperty("env").ValueKind);

        var recon = items.Single(i => i.GetProperty("category").GetString() == "RECON");
        Assert.Equal(reconId.ToString(), recon.GetProperty("id").GetString());
        Assert.Equal("SOURCE_MISSING", recon.GetProperty("title").GetString());
        Assert.Equal("DOC-AGG-2", recon.GetProperty("objectRef").GetString());
        Assert.Equal("MOCK", recon.GetProperty("env").GetString());
        Assert.Equal("复核平台侧证据", recon.GetProperty("detailSnippet").GetString());
        Assert.Equal(JsonValueKind.Null, recon.GetProperty("deepLink").ValueKind);

        var task = items.Single(i => i.GetProperty("category").GetString() == "TASK");
        Assert.Equal(taskId.ToString(), task.GetProperty("id").GetString());
        Assert.Equal("ManualAction", task.GetProperty("title").GetString());
        Assert.Equal("需人工选择映射后重试", task.GetProperty("detailSnippet").GetString());
        Assert.Equal(
            $"/uploads?taskId={taskId}&env=MOCK",
            task.GetProperty("deepLink").GetString());
        Assert.Equal("KSOA:DOC-AGG-3:RETAIL_321", task.GetProperty("objectRef").GetString());

        var productMapping = items.Single(i => i.GetProperty("id").GetString() == $"product:{productMappingId}");
        Assert.Equal("商品映射冲突", productMapping.GetProperty("title").GetString());
        Assert.Equal("聚合测试药品", productMapping.GetProperty("detailSnippet").GetString());
        Assert.Equal("SPID-AGG-1", productMapping.GetProperty("objectRef").GetString());
        Assert.Equal("/products", productMapping.GetProperty("deepLink").GetString());
        Assert.Equal(JsonValueKind.Null, productMapping.GetProperty("env").ValueKind);

        var supplierMapping = items.Single(i => i.GetProperty("id").GetString() == $"supplier:{supplierMappingId}");
        Assert.Equal("供应商映射冲突", supplierMapping.GetProperty("title").GetString());
        Assert.Equal("DWBH-AGG-1", supplierMapping.GetProperty("objectRef").GetString());
        Assert.Equal("/suppliers", supplierMapping.GetProperty("deepLink").GetString());
        // 映射表无时间列：supplier 映射 createdAt 为 null（落到末尾，不臆造时间）。
        Assert.Equal(JsonValueKind.Null, supplierMapping.GetProperty("createdAt").ValueKind);
    }

    // ---------------------------------------------------------------- 2. 过滤与分页

    [Fact]
    public async Task Queue_filters_by_category_status_and_paginates()
    {
        await using var c = await _fx.OpenAsync();
        var oldest = await SeedIngestAsync(c, "DOC-F-1", "EMPTY_DETAIL", "最早", T0.AddHours(1));
        var middle = await SeedIngestAsync(c, "DOC-F-2", "ORPHAN_LINE", "居中", T0.AddHours(2));
        var newest = await SeedIngestAsync(c, "DOC-F-3", "QTY_NONPOSITIVE", "最新", T0.AddHours(3));
        var resolved = await SeedIngestAsync(c, "DOC-F-4", "EMPTY_DETAIL", "已处理", T0.AddHours(4), "RESOLVED");
        await SeedDifferenceAsync(c, "MOCK", "DOC-F-RECON", T0.AddHours(5));
        await SeedTaskAsync(c, "MOCK", "KSOA:DOC-F-TASK:RETAIL_321", "ResultUnresolved", "结果未确认", T0.AddHours(6));

        var client = await LoginAsync();

        var page1 = await client.GetFromJsonAsync<JsonElement>("/api/v1/exceptions?category=INGEST&pageSize=2");
        var d1 = page1.GetProperty("data");
        Assert.Equal(3, d1.GetProperty("total").GetInt32());
        Assert.Equal(new[] { newest, middle },
            d1.GetProperty("items").EnumerateArray().Select(i => long.Parse(i.GetProperty("id").GetString()!)).ToArray());

        var page2 = await client.GetFromJsonAsync<JsonElement>("/api/v1/exceptions?category=INGEST&page=2&pageSize=2");
        var d2 = page2.GetProperty("data");
        Assert.Equal(new[] { oldest },
            d2.GetProperty("items").EnumerateArray().Select(i => long.Parse(i.GetProperty("id").GetString()!)).ToArray());

        // status=RESOLVED：只有接入异常可已解决；RECON 已解决的种子不存在，TASK/MAPPING 自然为空。
        var resolvedOnly = await client.GetFromJsonAsync<JsonElement>("/api/v1/exceptions?status=RESOLVED");
        var dr = resolvedOnly.GetProperty("data");
        Assert.Equal(1, dr.GetProperty("total").GetInt32());
        Assert.Equal(resolved.ToString(), dr.GetProperty("items")[0].GetProperty("id").GetString());

        var taskResolved = await client.GetFromJsonAsync<JsonElement>("/api/v1/exceptions?category=TASK&status=RESOLVED");
        Assert.Equal(0, taskResolved.GetProperty("data").GetProperty("total").GetInt32());
        Assert.Equal(0, taskResolved.GetProperty("data").GetProperty("items").GetArrayLength());

        // pageSize 上限 100；非法参数 400。
        var clamped = await client.GetFromJsonAsync<JsonElement>("/api/v1/exceptions?pageSize=500");
        Assert.Equal(100, clamped.GetProperty("data").GetProperty("pageSize").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync("/api/v1/exceptions?category=BOGUS")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync("/api/v1/exceptions?status=BOGUS")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync("/api/v1/exceptions?env=PROD")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync("/api/v1/exceptions?page=0")).StatusCode);
    }

    // ---------------------------------------------------------------- 3. 环境定界

    [Fact]
    public async Task Queue_recon_and_task_rows_are_env_scoped()
    {
        await using var c = await _fx.OpenAsync();
        await SeedIngestAsync(c, "DOC-ENV-I", "EMPTY_DETAIL", "无环境接入异常", T0.AddHours(1));
        var mockDiff = await SeedDifferenceAsync(c, "MOCK", "DOC-ENV-M", T0.AddHours(2));
        var liveDiff = await SeedDifferenceAsync(c, "LIVE", "DOC-ENV-L", T0.AddHours(3));
        var mockTask = await SeedTaskAsync(c, "MOCK", "KSOA:DOC-ENV-MT:RETAIL_321", "ManualAction", "MOCK 待处理", T0.AddHours(4));
        var liveTask = await SeedTaskAsync(c, "LIVE", "KSOA:DOC-ENV-LT:RETAIL_321", "AcceptanceUnknown", "LIVE 待处理", T0.AddHours(5));

        var client = await LoginAsync();
        var mock = (await client.GetFromJsonAsync<JsonElement>("/api/v1/exceptions")).GetProperty("data");
        Assert.Equal(3, mock.GetProperty("total").GetInt32()); // 接入 + MOCK 差异 + MOCK 任务
        Assert.Equal(1, mock.GetProperty("counts").GetProperty("ingestOpen").GetInt32());
        Assert.Equal(1, mock.GetProperty("counts").GetProperty("reconOpen").GetInt32());
        Assert.Equal(1, mock.GetProperty("counts").GetProperty("taskOpen").GetInt32());
        var mockItems = mock.GetProperty("items").EnumerateArray().ToList();
        static bool IsRow(JsonElement i, string category, long id)
            => i.GetProperty("category").GetString() == category
               && i.GetProperty("id").GetString() == id.ToString();
        Assert.Contains(mockItems, i => IsRow(i, "RECON", mockDiff));
        Assert.Contains(mockItems, i => IsRow(i, "TASK", mockTask));
        Assert.DoesNotContain(mockItems, i => IsRow(i, "RECON", liveDiff));
        Assert.DoesNotContain(mockItems, i => IsRow(i, "TASK", liveTask));

        var live = (await client.GetFromJsonAsync<JsonElement>("/api/v1/exceptions?env=LIVE")).GetProperty("data");
        Assert.Equal("LIVE", live.GetProperty("env").GetString());
        Assert.Equal(3, live.GetProperty("total").GetInt32());
        Assert.Equal(1, live.GetProperty("counts").GetProperty("ingestOpen").GetInt32());
        Assert.Equal(1, live.GetProperty("counts").GetProperty("reconOpen").GetInt32());
        Assert.Equal(1, live.GetProperty("counts").GetProperty("taskOpen").GetInt32());
        var liveItems = live.GetProperty("items").EnumerateArray().ToList();
        Assert.Contains(liveItems, i => IsRow(i, "RECON", liveDiff));
        Assert.Contains(liveItems, i => IsRow(i, "TASK", liveTask));
        Assert.DoesNotContain(liveItems, i => IsRow(i, "RECON", mockDiff));
        Assert.DoesNotContain(liveItems, i => IsRow(i, "TASK", mockTask));
        Assert.All(liveItems.Where(i => i.GetProperty("category").GetString() is "RECON" or "TASK"),
            i => Assert.Equal("LIVE", i.GetProperty("env").GetString()));
    }

    // ---------------------------------------------------------------- 4. counts 全量口径

    [Fact]
    public async Task Queue_counts_reflect_full_open_set_not_page()
    {
        await using var c = await _fx.OpenAsync();
        for (var i = 0; i < 7; i++)
            await SeedIngestAsync(c, $"DOC-CNT-{i}", "EMPTY_DETAIL", "计数", T0.AddHours(i + 1));
        await SeedDifferenceAsync(c, "MOCK", "DOC-CNT-R1", T0.AddHours(9));
        await SeedDifferenceAsync(c, "MOCK", "DOC-CNT-R2", T0.AddHours(10));
        await SeedTaskAsync(c, "MOCK", "KSOA:DOC-CNT-T1:RETAIL_321", "ManualAction", "a", T0.AddHours(11));
        await SeedTaskAsync(c, "MOCK", "KSOA:DOC-CNT-T2:RETAIL_321", "ResultUnresolved", "b", T0.AddHours(12));
        await SeedTaskAsync(c, "MOCK", "KSOA:DOC-CNT-T3:RETAIL_321", "AcceptanceUnknown", "c", T0.AddHours(13));
        await SeedProductConflictAsync(c, "SPID-CNT", "计数药品", "PD-CNT", T0.AddHours(14));

        var client = await LoginAsync();
        var page = (await client.GetFromJsonAsync<JsonElement>("/api/v1/exceptions?pageSize=2")).GetProperty("data");
        Assert.Equal(2, page.GetProperty("items").GetArrayLength());
        Assert.Equal(13, page.GetProperty("total").GetInt32());
        Assert.Equal(7, page.GetProperty("counts").GetProperty("ingestOpen").GetInt32());
        Assert.Equal(2, page.GetProperty("counts").GetProperty("reconOpen").GetInt32());
        Assert.Equal(3, page.GetProperty("counts").GetProperty("taskOpen").GetInt32());
        Assert.Equal(1, page.GetProperty("counts").GetProperty("mappingOpen").GetInt32());

        // status 过滤不影响 OPEN 计数。
        var resolved = (await client.GetFromJsonAsync<JsonElement>("/api/v1/exceptions?status=RESOLVED&pageSize=2"))
            .GetProperty("data");
        Assert.Equal(0, resolved.GetProperty("total").GetInt32());
        Assert.Equal(7, resolved.GetProperty("counts").GetProperty("ingestOpen").GetInt32());
        Assert.Equal(2, resolved.GetProperty("counts").GetProperty("reconOpen").GetInt32());
        Assert.Equal(3, resolved.GetProperty("counts").GetProperty("taskOpen").GetInt32());
        Assert.Equal(1, resolved.GetProperty("counts").GetProperty("mappingOpen").GetInt32());
    }

    // ---------------------------------------------------------------- 5. 接入异常留证关闭

    [Fact]
    public async Task Ingest_resolve_requires_evidence_audits_live_and_conflicts_on_repeat()
    {
        await using var c = await _fx.OpenAsync();
        var ingestId = await SeedIngestAsync(c, "DOC-RSV-1", "MISSING_DOC_KEY", "单头单据键为空", T0.AddHours(1));

        var client = await LoginAsync();
        var path = $"/api/v1/exceptions/INGEST/{ingestId}/resolve";

        var empty = await PostJsonAsync(client, path, new { resolutionEvidence = "   " }, "exc-ingest-empty-1");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, empty.StatusCode);
        var emptyBody = await ReadJsonAsync(empty);
        Assert.Equal("VALIDATION", emptyBody.GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Array, emptyBody.GetProperty("fieldErrors").ValueKind);

        var ok = await PostJsonAsync(client, path,
            new { resolutionEvidence = "已补录单据键并重导，确认无重复行" }, "exc-ingest-ok-1");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var okBody = (await ReadJsonAsync(ok)).GetProperty("data");
        Assert.Equal("INGEST", okBody.GetProperty("category").GetString());
        Assert.Equal("RESOLVED", okBody.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.String, okBody.GetProperty("resolvedAt").ValueKind);

        var row = await c.QuerySingleAsync<(string Status, string ResolvedBy, string Evidence)>(
            "SELECT status, resolved_by, resolution_evidence FROM dbo.ingest_exception WHERE id=@id",
            new { id = ingestId });
        Assert.Equal("RESOLVED", row.Status);
        Assert.Equal("exc-admin", row.ResolvedBy);
        Assert.Contains("已补录单据键", row.Evidence, StringComparison.Ordinal);
        // 控制面人工动作：审计 env 恒 LIVE，reason 为证据摘要。
        Assert.Equal(1, await c.ExecuteScalarAsync<int>(@"
SELECT COUNT(*) FROM dbo.audit_log
WHERE action=N'exception.resolved' AND env='LIVE' AND actor=N'exc-admin'
  AND object_type=N'ingest_exception' AND object_id=@oid",
            new { oid = ingestId.ToString() }));

        // 同幂等键重放：返回同一成功响应；新键再解：409 ALREADY_RESOLVED。
        var replay = await PostJsonAsync(client, path,
            new { resolutionEvidence = "已补录单据键并重导，确认无重复行" }, "exc-ingest-ok-1");
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var again = await PostJsonAsync(client, path, new { resolutionEvidence = "重复处理" }, "exc-ingest-again-1");
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("ALREADY_RESOLVED", (await ReadJsonAsync(again)).GetProperty("code").GetString());

        var missing = await PostJsonAsync(client, "/api/v1/exceptions/INGEST/999999/resolve",
            new { resolutionEvidence = "不存在" }, "exc-ingest-missing-1");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    // ---------------------------------------------------------------- 6. 对账差异经队列关闭

    [Fact]
    public async Task Recon_difference_resolve_via_queue_updates_status_and_audit()
    {
        await using var c = await _fx.OpenAsync();
        var diffId = await SeedDifferenceAsync(c, "MOCK", "DOC-RSV-R", T0.AddHours(1));
        var liveDiffId = await SeedDifferenceAsync(c, "LIVE", "DOC-RSV-L", T0.AddHours(2));

        var client = await LoginAsync();
        // 跨 env：宿主 MOCK 看不到 LIVE 差异 → 404（不暴露存在性）。
        var crossEnv = await PostJsonAsync(client, $"/api/v1/exceptions/RECON/{liveDiffId}/resolve",
            new { resolutionEvidence = "跨环境" }, "exc-recon-cross-1");
        Assert.Equal(HttpStatusCode.NotFound, crossEnv.StatusCode);

        var path = $"/api/v1/exceptions/RECON/{diffId}/resolve";
        var ok = await PostJsonAsync(client, path, new { resolutionEvidence = "已按平台回查证据关闭" }, "exc-recon-ok-1");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var okBody = (await ReadJsonAsync(ok)).GetProperty("data");
        Assert.Equal("RECON", okBody.GetProperty("category").GetString());
        Assert.Equal("RESOLVED", okBody.GetProperty("status").GetString());

        Assert.Equal("RESOLVED", await c.ExecuteScalarAsync<string>(
            "SELECT status FROM dbo.reconciliation_difference WHERE id=@id", new { id = diffId }));
        Assert.Equal(1, await c.ExecuteScalarAsync<int>(@"
SELECT COUNT(*) FROM dbo.audit_log
WHERE action=N'exception.resolved' AND env='LIVE' AND actor=N'exc-admin'
  AND object_type=N'reconciliation_difference' AND object_id=@oid",
            new { oid = diffId.ToString() }));

        var again = await PostJsonAsync(client, path, new { resolutionEvidence = "重复" }, "exc-recon-again-1");
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("DIFFERENCE_ALREADY_RESOLVED", (await ReadJsonAsync(again)).GetProperty("code").GetString());
    }

    // ---------------------------------------------------------------- 7. TASK/MAPPING 不可队列关闭

    [Fact]
    public async Task Task_and_mapping_categories_are_not_resolvable_via_queue()
    {
        await using var c = await _fx.OpenAsync();
        var taskId = await SeedTaskAsync(c, "MOCK", "KSOA:DOC-NR-1:RETAIL_321", "ManualAction", "待人工核对", T0.AddHours(1));
        var pmId = await SeedProductConflictAsync(c, "SPID-NR", "不可关闭药品", "PD-NR", T0.AddHours(2));
        var smId = await SeedSupplierConflictAsync(c, "DWBH-NR", "不可关闭供应商", "REF-NR");

        var client = await LoginAsync();

        var taskResp = await PostJsonAsync(client, $"/api/v1/exceptions/TASK/{taskId}/resolve",
            new { resolutionEvidence = "试图关闭" }, "exc-nr-task-1");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, taskResp.StatusCode);
        var taskBody = await ReadJsonAsync(taskResp);
        Assert.Equal("NOT_RESOLVABLE_VIA_QUEUE", taskBody.GetProperty("code").GetString());
        Assert.Contains($"/uploads?taskId={taskId}&env=MOCK", taskBody.GetProperty("message").GetString()!,
            StringComparison.Ordinal);
        var taskField = taskBody.GetProperty("fieldErrors")[0];
        Assert.Equal($"/uploads?taskId={taskId}&env=MOCK", taskField.GetProperty("value").GetString());

        var productResp = await PostJsonAsync(client, $"/api/v1/exceptions/MAPPING/product:{pmId}/resolve",
            new { resolutionEvidence = "试图关闭" }, "exc-nr-pm-1");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, productResp.StatusCode);
        var productBody = await ReadJsonAsync(productResp);
        Assert.Equal("NOT_RESOLVABLE_VIA_QUEUE", productBody.GetProperty("code").GetString());
        Assert.Equal("/products", productBody.GetProperty("fieldErrors")[0].GetProperty("value").GetString());

        var supplierResp = await PostJsonAsync(client, $"/api/v1/exceptions/MAPPING/supplier:{smId}/resolve",
            new { resolutionEvidence = "试图关闭" }, "exc-nr-sm-1");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, supplierResp.StatusCode);
        Assert.Equal("/suppliers",
            (await ReadJsonAsync(supplierResp)).GetProperty("fieldErrors")[0].GetProperty("value").GetString());

        // 队列未改动任何状态：处置仍归既有页面。
        Assert.Equal("ManualAction", await c.ExecuteScalarAsync<string>(
            "SELECT state FROM dbo.upload_task WHERE id=@id", new { id = taskId }));
        Assert.Equal("CONFLICT", await c.ExecuteScalarAsync<string>(
            "SELECT state FROM dbo.product_mapping WHERE id=@id", new { id = pmId }));
    }

    // ---------------------------------------------------------------- 8. 详情 404 与去敏

    [Fact]
    public async Task Exception_detail_404_for_cross_env_and_unknown_ids()
    {
        await using var c = await _fx.OpenAsync();
        var mockDiff = await SeedDifferenceAsync(c, "MOCK", "DOC-D-M", T0.AddHours(1));
        var liveDiff = await SeedDifferenceAsync(c, "LIVE", "DOC-D-L", T0.AddHours(2));
        var liveTask = await SeedTaskAsync(c, "LIVE", "KSOA:DOC-D-T:RETAIL_321", "AcceptanceUnknown", "LIVE 受理未知",
            T0.AddHours(3));
        await SeedAttemptsAsync(c, liveTask, 6, "RAW-SECRET-PAYLOAD");
        var pmId = await SeedProductConflictAsync(c, "SPID-D", "详情药品", "PD-D", T0.AddHours(4));

        var client = await LoginAsync();

        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/v1/exceptions/RECON/{liveDiff}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/v1/exceptions/TASK/{liveTask}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await client.GetAsync($"/api/v1/exceptions/TASK/{liveTask}?env=LIVE")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync("/api/v1/exceptions/INGEST/999999")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync("/api/v1/exceptions/MAPPING/product:999999")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync("/api/v1/exceptions/MAPPING/bogus:1")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync("/api/v1/exceptions/BOGUS/1")).StatusCode);

        // RECON 详情：evidence 解析为 JSON + run 窗口/类型/状态；同一行形状。
        var reconDetail = (await client.GetFromJsonAsync<JsonElement>($"/api/v1/exceptions/RECON/{mockDiff}"))
            .GetProperty("data");
        var reconItem = reconDetail.GetProperty("item");
        Assert.Equal("RECON", reconItem.GetProperty("category").GetString());
        Assert.Equal("MOCK", reconItem.GetProperty("env").GetString());
        var reconBody = reconDetail.GetProperty("detail");
        Assert.Equal(JsonValueKind.Object, reconBody.GetProperty("evidence").ValueKind);
        Assert.Equal(0, reconBody.GetProperty("evidence").GetProperty("matchedCodes").GetInt32());
        Assert.Equal("MOCK", reconBody.GetProperty("run").GetProperty("env").GetString());
        Assert.Equal("SALE", reconBody.GetProperty("run").GetProperty("businessType").GetString());
        Assert.Equal("COMPLETE", reconBody.GetProperty("run").GetProperty("status").GetString());

        // TASK 详情：最近 5 次尝试；raw_response 原文（含密钥样内容）绝不出网。
        var taskResp = await client.GetAsync($"/api/v1/exceptions/TASK/{liveTask}?env=LIVE");
        var taskRaw = await taskResp.Content.ReadAsStringAsync();
        Assert.DoesNotContain("RAW-SECRET-PAYLOAD", taskRaw, StringComparison.Ordinal);
        Assert.DoesNotContain("raw_response", taskRaw, StringComparison.OrdinalIgnoreCase);
        var taskDetail = JsonDocument.Parse(taskRaw).RootElement.GetProperty("data").GetProperty("detail");
        Assert.Equal("AcceptanceUnknown", taskDetail.GetProperty("state").GetString());
        Assert.Equal("LIVE 受理未知", taskDetail.GetProperty("stateReason").GetString());
        Assert.Equal(5, taskDetail.GetProperty("attempts").GetArrayLength());
        var attempt = taskDetail.GetProperty("attempts")[0];
        Assert.Equal("QUERY", attempt.GetProperty("attemptKind").GetString());
        Assert.Equal(200, attempt.GetProperty("httpStatus").GetInt32());

        // MAPPING 详情：本地与平台身份并排 + evidence；非冲突后即 404。
        var mappingDetail = (await client.GetFromJsonAsync<JsonElement>($"/api/v1/exceptions/MAPPING/product:{pmId}"))
            .GetProperty("data");
        Assert.Equal("SPID-D", mappingDetail.GetProperty("detail").GetProperty("local").GetProperty("spid").GetString());
        Assert.Equal("详情药品", mappingDetail.GetProperty("detail").GetProperty("local").GetProperty("name").GetString());
        Assert.Equal("PD-D",
            mappingDetail.GetProperty("detail").GetProperty("platform").GetProperty("platformDrugId").GetString());
        Assert.Equal("NAME_MISMATCH",
            mappingDetail.GetProperty("detail").GetProperty("evidence").GetProperty("reason").GetString());
        await c.ExecuteAsync("UPDATE dbo.product_mapping SET state=N'MANUAL_CONFIRMED' WHERE id=@id", new { id = pmId });
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/v1/exceptions/MAPPING/product:{pmId}")).StatusCode);
    }

    // ---------------------------------------------------------------- 9. 匿名 401

    [Fact]
    public async Task Exception_endpoints_reject_anonymous_with_401()
    {
        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/v1/exceptions")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/v1/exceptions/INGEST/1")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.GetAsync("/api/v1/exceptions/MAPPING/product:1")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.PostAsJsonAsync("/api/v1/exceptions/INGEST/1/resolve",
                new { resolutionEvidence = "匿名" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.PostAsJsonAsync("/api/v1/exceptions/RECON/1/resolve",
                new { resolutionEvidence = "匿名" })).StatusCode);
    }

    // ---------------------------------------------------------------- 种子

    private static string Sha(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private async Task SeedUserAsync(SqlConnection c, string username, string display, string password,
        IEnumerable<string> permissions)
    {
        var hasher = new Microsoft.AspNetCore.Identity.PasswordHasher<string>();
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

    private async Task<long> SeedIngestAsync(SqlConnection c, string documentKey, string exceptionType,
        string? detail, DateTime createdAt, string status = "OPEN")
        => await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.ingest_exception(org_id,source_system_id,source_document_key,source_line_key,exception_type,
    detail,status,created_at)
OUTPUT inserted.id
VALUES(@orgId,@sysId,@documentKey,N'L1',@exceptionType,@detail,@status,@createdAt)",
            new { orgId = _orgId, sysId = _sysId, documentKey, exceptionType, detail, status, createdAt });

    private async Task<long> SeedDifferenceAsync(SqlConnection c, string env, string objectRef, DateTime foundAt,
        string status = "OPEN")
    {
        var runId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.reconciliation_run(org_id,env,window_start,window_end,business_type,status,started_at,finished_at)
VALUES(@orgId,@env,@start,@end,N'SALE',N'COMPLETE',@foundAt,@foundAt);
SELECT CAST(SCOPE_IDENTITY() AS bigint);",
            new { orgId = _orgId, env, start = T0, end = T0.AddDays(1), foundAt });
        return await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.reconciliation_difference(run_id,diff_type,severity,object_ref,evidence_json,status,
    owner_role,next_action,found_at)
OUTPUT inserted.id
VALUES(@runId,N'SOURCE_MISSING',N'HIGH',@objectRef,N'{""matchedCodes"":0,""platformQueryRequestId"":""q-1""}',
    @status,N'operator',N'复核平台侧证据',@foundAt)",
            new { runId, objectRef, status, foundAt });
    }

    private async Task<long> SeedTaskAsync(SqlConnection c, string env, string eventKey, string state,
        string? stateReason, DateTime createdAt)
        => await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upload_task(org_id,env,event_key,task_type,source_hash,payload_hash,state,state_reason,created_at)
OUTPUT inserted.id
VALUES(@orgId,@env,@eventKey,N'RETAIL_321',@hash,@hash,@state,@stateReason,@createdAt)",
            new { orgId = _orgId, env, eventKey, hash = Sha(eventKey), state, stateReason, createdAt });

    private async Task SeedAttemptsAsync(SqlConnection c, long taskId, int count, string rawResponse)
    {
        for (var i = 1; i <= count; i++)
            await c.ExecuteAsync(@"
INSERT INTO dbo.upload_attempt(task_id,attempt_no,attempt_kind,env,fencing_token,started_at,finished_at,
    http_status,request_id,raw_response)
VALUES(@taskId,@attemptNo,N'QUERY',N'LIVE',1,DATEADD(minute,-@attemptNo,SYSUTCDATETIME()),SYSUTCDATETIME(),
    200,@requestId,@raw)",
                new { taskId, attemptNo = i, requestId = "q-req-" + i, raw = rawResponse });
    }

    private async Task<long> SeedProductConflictAsync(SqlConnection c, string spid, string name, string drugKey,
        DateTime updatedAt)
    {
        var productId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.product(org_id,source_system_id,spid,name,updated_at)
OUTPUT inserted.id
VALUES(@orgId,@sysId,@spid,@name,@updatedAt)",
            new { orgId = _orgId, sysId = _sysId, spid, name, updatedAt });
        var drugId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.platform_drug(platform_drug_id,source_kind)
OUTPUT inserted.id
VALUES(@drugKey,N'API')", new { drugKey });
        return await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.product_mapping(org_id,product_id,platform_drug_id,state,evidence_json,version)
OUTPUT inserted.id
VALUES(@orgId,@productId,@drugId,N'CONFLICT',N'{""reason"":""NAME_MISMATCH""}',3)",
            new { orgId = _orgId, productId, drugId });
    }

    private async Task<long> SeedSupplierConflictAsync(SqlConnection c, string dwbh, string name, string refEntId)
    {
        var supplierId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.supplier(org_id,source_system_id,dwbh,name)
OUTPUT inserted.id
VALUES(@orgId,@sysId,@dwbh,@name)",
            new { orgId = _orgId, sysId = _sysId, dwbh, name });
        var versionId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.platform_enterprise_version(ref_ent_id,ent_id,name)
OUTPUT inserted.id
VALUES(@refEntId,@entId,@name)",
            new { refEntId, entId = refEntId + "-ENT", name = name + "（平台）" });
        return await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.supplier_mapping(org_id,supplier_id,enterprise_version_id,state,evidence_json,version)
OUTPUT inserted.id
VALUES(@orgId,@supplierId,@versionId,N'CONFLICT',N'{""reason"":""CREDIT_CODE_MISMATCH""}',2)",
            new { orgId = _orgId, supplierId, versionId });
    }

    // ---------------------------------------------------------------- HTTP 辅助

    private sealed record SessionResp(SessionData Data);
    private sealed record SessionData(bool Authenticated, string CsrfToken);

    private async Task<HttpClient> LoginAsync()
    {
        var client = _factory.CreateClient();
        var session = await client.GetFromJsonAsync<SessionResp>("/api/v1/auth/session");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session!.Data.CsrfToken);
        (await client.PostAsJsonAsync("/api/v1/auth/login",
            new { username = "exc-admin", password = AdminPassword })).EnsureSuccessStatusCode();
        var session2 = await client.GetFromJsonAsync<SessionResp>("/api/v1/auth/session");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session2!.Data.CsrfToken);
        return client;
    }

    private static async Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string path, object body,
        string idempotencyKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        return doc.RootElement.Clone();
    }
}