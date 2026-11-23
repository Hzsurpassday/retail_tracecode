using System.Runtime.Versioning;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using TraceFlow.Infrastructure.Codes;
using TraceFlow.IntegrationTests.Sources;

namespace TraceFlow.IntegrationTests.Api;

// 每测试独立库：种子插入 platform_enterprise_version(REF-SUP,ENT-SUP) 与 active supplier_mapping，
// 二者是全局唯一约束（UQ_ent_version / UX_smap_active_platform），类共享库从第 2 个测试起必撞约束。
[SupportedOSPlatform("windows")]
public class InboundApiTests : IAsyncLifetime
{
    private Sql2008Fixture _fx = null!;
    private WebApplicationFactory<Program> _factory = null!;
    public InboundApiTests() { }

    private sealed record Seed(long OrgId, long SnapId, long LineId, long BillId, long UpLineId, string Code1, string Code2);

    private static string Code(int i) => "812345678901234567" + i.ToString("D2");

    public async Task InitializeAsync()
    {
        _fx = new Sql2008Fixture();
        await _fx.InitializeAsync();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:TraceFlow", _fx.ConnectionString);
            b.UseSetting("TraceFlow:Env", "Mock");
        });
        await using var c = await _fx.OpenAsync();
        var hasher = new PasswordHasher<string>();
        var id = await c.ExecuteScalarAsync<long>(@"
IF NOT EXISTS (SELECT 1 FROM dbo.app_user WHERE username=N'm2ops')
    INSERT INTO dbo.app_user(username,display_name,password_hash) VALUES(N'm2ops',N'入库操作员',@h);
SELECT id FROM dbo.app_user WHERE username=N'm2ops'", new { h = hasher.HashPassword("m2ops", "Ops-Password-123") });
        foreach (var p in new[] { "inbound:allocate", "job:rematch", "trace:read" })
            await c.ExecuteAsync(@"
IF NOT EXISTS (SELECT 1 FROM dbo.user_permission WHERE user_id=@id AND permission=@p)
    INSERT INTO dbo.user_permission(user_id,permission,granted_by) VALUES(@id,@p,N'test')", new { id, p });
    }

    public async Task DisposeAsync()
    {
        _factory.Dispose();
        await _fx.DisposeAsync();
    }

    private async Task<Seed> SeedAsync(string tag, int codeBase)
    {
        await using var c = await _fx.OpenAsync();
        var (orgId, sysId) = await TestSeed.EnsureAsync(c);
        await c.ExecuteAsync("UPDATE dbo.organization SET ent_id=N'ENT-STORE', ref_ent_id=N'REF-STORE' WHERE id=@orgId", new { orgId });
        var dwbh = "DW" + tag; var spid = "SP" + tag; var drug = "DRUG-" + tag;
        await c.ExecuteAsync("INSERT INTO dbo.supplier(org_id,source_system_id,dwbh,name) VALUES(@orgId,@sysId,@dwbh,N'供应商')", new { orgId, sysId, dwbh });
        var supId = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.supplier WHERE org_id=@orgId AND dwbh=@dwbh", new { orgId, dwbh });
        var entVerId = await c.ExecuteScalarAsync<long>("INSERT INTO dbo.platform_enterprise_version(ref_ent_id,ent_id,name) OUTPUT INSERTED.id VALUES(N'REF-SUP',N'ENT-SUP',N'上游企业')");
        await c.ExecuteAsync("INSERT INTO dbo.supplier_mapping(org_id,supplier_id,enterprise_version_id,state) VALUES(@orgId,@supId,@ev,N'MANUAL_CONFIRMED')", new { orgId, supId, ev = entVerId });
        await c.ExecuteAsync("INSERT INTO dbo.product(org_id,source_system_id,spid,name,trace_classification) VALUES(@orgId,@sysId,@spid,N'药品',N'REQUIRED')", new { orgId, sysId, spid });
        var prodId = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.product WHERE org_id=@orgId AND spid=@spid", new { orgId, spid });
        var drugId = await c.ExecuteScalarAsync<long>("INSERT INTO dbo.platform_drug(platform_drug_id,source_kind) OUTPUT INSERTED.id VALUES(@drug,N'fixture')", new { drug });
        await c.ExecuteAsync("INSERT INTO dbo.product_mapping(org_id,product_id,platform_drug_id,state) VALUES(@orgId,@prodId,@drugId,N'MANUAL_CONFIRMED')", new { orgId, prodId, drugId });
        var snapId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.source_snapshot(org_id,source_system_id,source_document_key,document_type,version,business_datetime,completeness_status,content_hash,raw_header)
OUTPUT INSERTED.id VALUES(@orgId,@sysId,@key,N'PURCHASE_IN',1,@biz,N'COMPLETE',@hash,@header)",
            new { orgId, sysId, key = "PI-API-" + tag, biz = new DateTime(2026, 11, 20, 2, 0, 0, DateTimeKind.Utc),
                  hash = new string('0', 64), header = "{\"dwbh\":\"" + dwbh + "\"}" });
        var lineId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.source_line(snapshot_id,source_line_key,product_source_id,quantity_original,unit_original,batch_raw,line_hash,event_kind)
OUTPUT INSERTED.id VALUES(@snapId,N'1',@spid,2,N'盒',N'B2026',@hash,N'PURCHASE_IN')", new { snapId, spid, hash = new string('0', 64) });
        var billId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upstream_bill(org_id,env,upstream_ref_ent_id,platform_bill_id,bill_no,bill_date,to_ent_id,completeness)
OUTPUT INSERTED.id VALUES(@orgId,N'MOCK',N'REF-SUP',@pb,@no,@date,N'ENT-STORE',N'COMPLETE')",
            new { orgId, pb = "PB-API-" + tag, no = "UP-API-" + tag, date = new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc) });
        var upLineId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upstream_bill_line(bill_id,platform_line_id,platform_drug_id,batch_no,quantity,unit)
OUTPUT INSERTED.id VALUES(@billId,N'L1',@drug,N'B2026',2,N'盒')", new { billId, drug });
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
        foreach (var i in new[] { codeBase, codeBase + 1 })
        {
            var stored = await TraceCodeStore.EnsureAsync(c, tx, Code(i), default);
            await c.ExecuteAsync("INSERT INTO dbo.upstream_bill_code(bill_id,line_id,code_normalized,code_raw,package_level) VALUES(@billId,@lineId,@n,@raw,N'MIN')",
                new { billId, lineId = upLineId, n = stored.Normalized, raw = stored.Raw }, tx);
        }
        await c.ExecuteAsync("INSERT INTO dbo.inbound_match(org_id,env,source_snapshot_id,supplier_dwbh,match_state) VALUES(@orgId,N'MOCK',@snapId,@dwbh,N'UNMATCHED')",
            new { orgId, snapId, dwbh }, tx);
        await tx.CommitAsync();
        return new Seed(orgId, snapId, lineId, billId, upLineId, Code(codeBase), Code(codeBase + 1));
    }

    private async Task<HttpClient> LoginAsync()
    {
        var client = _factory.CreateClient();
        var session = await client.GetFromJsonAsync<SessionResp>("/api/v1/auth/session");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session!.Data.CsrfToken);
        (await client.PostAsJsonAsync("/api/v1/auth/login", new { username = "m2ops", password = "Ops-Password-123" })).EnsureSuccessStatusCode();
        var session2 = await client.GetFromJsonAsync<SessionResp>("/api/v1/auth/session");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session2!.Data.CsrfToken);
        return client;
    }

    private sealed record SessionResp(SessionData Data);
    private sealed record SessionData(bool Authenticated, string CsrfToken);

    private static void NewKey(HttpClient client)
    {
        client.DefaultRequestHeaders.Remove("Idempotency-Key");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
    }

    private static object SaveBody(Seed s, string version) => new
    {
        expectedMatchVersion = version,
        lines = new[] { new { sourceLineId = s.LineId, upstreamLineId = s.UpLineId, quantity = 2m, unitRatio = "1/1", conversionEvidence = (string?)null, codes = new[] { s.Code1, s.Code2 } } },
    };

    // 保存 → 确认 → 列表/详情：确认后任务 Ready，匹配 COMPLETE，分配 COMMITTED 且码已绑定任务。
    [Fact]
    public async Task Save_confirm_and_list_flow()
    {
        var s = await SeedAsync("F1", 10);
        var client = await LoginAsync();
        NewKey(client);
        var save = await client.PostAsJsonAsync($"/api/v1/inbounds/{s.SnapId}/allocations", SaveBody(s, "1"));
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        var saveBody = await save.Content.ReadFromJsonAsync<SaveResp>();
        Assert.Equal(2, saveBody!.Data.MatchVersion);
        Assert.True(saveBody.Data.AllRequiredCovered);
        NewKey(client);
        var confirm = await client.PostAsJsonAsync($"/api/v1/inbounds/{s.SnapId}/confirm-allocation", new { expectedMatchVersion = "2" });
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        var confirmBody = await confirm.Content.ReadFromJsonAsync<ConfirmResp>();
        Assert.StartsWith("TF", confirmBody!.Data.BillCode);
        Assert.Equal("符合资格后将自动上传", confirmBody.Data.Message);
        var list = await client.GetFromJsonAsync<InboundList>($"/api/v1/inbounds?docKey=PI-API-F1");
        var rec = Assert.Single(list!.Data.Records);
        Assert.Equal("COMPLETE", rec.MatchState);
        Assert.Equal("Ready", rec.TaskState);
        var detail = await client.GetAsync($"/api/v1/inbounds/{s.SnapId}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        var detailText = await detail.Content.ReadAsStringAsync();
        Assert.Contains("COMMITTED", detailText);
        Assert.Contains(s.Code1, detailText);
        // API-01：外部 ID 与数量必须是 JSON 字符串（JS 精度），与 confirm 的 taskId 一致
        using var detailDoc = JsonDocument.Parse(detailText);
        var data = detailDoc.RootElement.GetProperty("data");
        Assert.Equal(JsonValueKind.String, data.GetProperty("snapshot").GetProperty("snapshotId").ValueKind);
        var line = data.GetProperty("lines")[0];
        Assert.Equal(JsonValueKind.String, line.GetProperty("lineId").ValueKind);
        Assert.Equal("2", line.GetProperty("quantity").GetString());
        var alloc = data.GetProperty("allocations")[0];
        Assert.Equal(JsonValueKind.String, alloc.GetProperty("allocationId").ValueKind);
        Assert.Equal(JsonValueKind.String, alloc.GetProperty("sourceLineId").ValueKind);
        Assert.Equal(JsonValueKind.String, alloc.GetProperty("upstreamLineId").ValueKind);
        Assert.Equal("2", alloc.GetProperty("quantity").GetString());
        Assert.Equal(JsonValueKind.String, alloc.GetProperty("codes")[0].GetProperty("codeId").ValueKind);
        Assert.Equal(JsonValueKind.String, data.GetProperty("task").GetProperty("taskId").ValueKind);
    }

    [Fact]
    public async Task Rematch_enqueues_job()
    {
        var s = await SeedAsync("F2", 20);
        var client = await LoginAsync();
        NewKey(client);
        var resp = await client.PostAsJsonAsync($"/api/v1/inbounds/{s.SnapId}/rematch", new { });
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JobResp>();
        Assert.False(string.IsNullOrEmpty(body!.Data.JobId));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/v1/jobs/{body.Data.JobId}")).StatusCode);
    }

    [Fact]
    public async Task Cancel_allocation_flow()
    {
        var s = await SeedAsync("F3", 30);
        var client = await LoginAsync();
        NewKey(client);
        (await client.PostAsJsonAsync($"/api/v1/inbounds/{s.SnapId}/allocations", SaveBody(s, "1"))).EnsureSuccessStatusCode();
        await using var c = await _fx.OpenAsync();
        var allocId = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.inbound_allocation WHERE source_line_id=@id", new { id = s.LineId });
        NewKey(client);
        var cancel = await client.PostAsJsonAsync($"/api/v1/inbounds/{s.SnapId}/cancel-allocation", new { allocationId = allocId, reason = "录错上游单" });
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        var body = await cancel.Content.ReadFromJsonAsync<CancelResp>();
        Assert.Equal(1, body!.Data.Cancelled);
        Assert.Equal("UNMATCHED", await c.ExecuteScalarAsync<string>("SELECT match_state FROM dbo.inbound_match WHERE source_snapshot_id=@id", new { id = s.SnapId }));
    }

    [Fact]
    public async Task Upstream_bills_list_detail_and_redownload()
    {
        var s = await SeedAsync("F4", 40);
        var client = await LoginAsync();
        var list = await client.GetFromJsonAsync<UpstreamList>($"/api/v1/upstream-bills?billNo=UP-API-F4");
        var rec = Assert.Single(list!.Data.Records);
        Assert.Equal("COMPLETE", rec.Completeness);
        var detail = await client.GetAsync($"/api/v1/upstream-bills/{rec.BillId}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        var detailText = await detail.Content.ReadAsStringAsync();
        Assert.Contains(s.Code1, detailText);
        // API-01：billId/lineId/quantity 同为 JSON 字符串
        using var detailDoc = JsonDocument.Parse(detailText);
        var data = detailDoc.RootElement.GetProperty("data");
        Assert.Equal(JsonValueKind.String, data.GetProperty("bill").GetProperty("billId").ValueKind);
        var line = data.GetProperty("lines")[0];
        Assert.Equal(JsonValueKind.String, line.GetProperty("lineId").ValueKind);
        Assert.Equal("2", line.GetProperty("quantity").GetString());
        NewKey(client);
        var re = await client.PostAsJsonAsync($"/api/v1/upstream-bills/{rec.BillId}/redownload", new { });
        Assert.Equal(HttpStatusCode.Accepted, re.StatusCode);
    }

    // T9 携带项：已发送任务上保存必须 409 FROZEN（此前仅服务层覆盖，无 HTTP 证据）。
    [Fact]
    public async Task Save_after_send_is_frozen()
    {
        var s = await SeedAsync("F5", 50);
        var client = await LoginAsync();
        NewKey(client);
        (await client.PostAsJsonAsync($"/api/v1/inbounds/{s.SnapId}/allocations", SaveBody(s, "1"))).EnsureSuccessStatusCode();
        NewKey(client);
        (await client.PostAsJsonAsync($"/api/v1/inbounds/{s.SnapId}/confirm-allocation", new { expectedMatchVersion = "2" })).EnsureSuccessStatusCode();
        var version = await MarkSentAsync(s.SnapId);
        NewKey(client);
        var save = await client.PostAsJsonAsync($"/api/v1/inbounds/{s.SnapId}/allocations", SaveBody(s, version.ToString()));
        Assert.Equal(HttpStatusCode.Conflict, save.StatusCode);
        Assert.Contains("FROZEN", await save.Content.ReadAsStringAsync());
    }

    // T9 携带项：已发送任务上取消分配必须 409 FROZEN。
    [Fact]
    public async Task Cancel_after_send_is_frozen()
    {
        var s = await SeedAsync("F6", 60);
        var client = await LoginAsync();
        NewKey(client);
        (await client.PostAsJsonAsync($"/api/v1/inbounds/{s.SnapId}/allocations", SaveBody(s, "1"))).EnsureSuccessStatusCode();
        NewKey(client);
        (await client.PostAsJsonAsync($"/api/v1/inbounds/{s.SnapId}/confirm-allocation", new { expectedMatchVersion = "2" })).EnsureSuccessStatusCode();
        await MarkSentAsync(s.SnapId);
        await using var c = await _fx.OpenAsync();
        var allocId = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.inbound_allocation WHERE source_line_id=@id AND state=N'COMMITTED'", new { id = s.LineId });
        NewKey(client);
        var cancel = await client.PostAsJsonAsync($"/api/v1/inbounds/{s.SnapId}/cancel-allocation", new { allocationId = allocId, reason = "发送后取消" });
        Assert.Equal(HttpStatusCode.Conflict, cancel.StatusCode);
        Assert.Contains("FROZEN", await cancel.Content.ReadAsStringAsync());
    }

    // 模拟已发送：0006 的 fencing_token 无默认值；upload-dispatch 在测试宿主内无 handler，任务不会被后台推进。
    private async Task<int> MarkSentAsync(long snapId)
    {
        await using var c = await _fx.OpenAsync();
        var taskId = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.upload_task WHERE source_snapshot_id=@id AND task_type=N'PURCHASE_IN_102'", new { id = snapId });
        await c.ExecuteAsync(@"
INSERT INTO dbo.upload_attempt(task_id,attempt_no,attempt_kind,env,fencing_token,started_at)
VALUES(@taskId,1,N'UPLOAD',N'MOCK',0,SYSUTCDATETIME())", new { taskId });
        return await c.ExecuteScalarAsync<int>("SELECT version FROM dbo.inbound_match WHERE source_snapshot_id=@id AND env=N'MOCK'", new { id = snapId });
    }

    // UI-04/UI-05（R13a）：已配/剩余量由后端 SQL 聚合，前端不参与资格判定。
    [Fact]
    public async Task Upstream_detail_shows_allocated_and_remaining()
    {
        var s = await SeedAsync("F7", 70);
        var client = await LoginAsync();
        var detail = await client.GetAsync($"/api/v1/upstream-bills/{s.BillId}");
        using (var doc = System.Text.Json.JsonDocument.Parse(await detail.Content.ReadAsStringAsync()))
        {
            var line = doc.RootElement.GetProperty("data").GetProperty("lines")[0];
            Assert.Equal("0", line.GetProperty("allocatedQuantity").GetString());
            Assert.Equal("2", line.GetProperty("remainingQuantity").GetString());
        }
        NewKey(client);
        (await client.PostAsJsonAsync($"/api/v1/inbounds/{s.SnapId}/allocations", new
        {
            expectedMatchVersion = "1",
            lines = new[] { new { sourceLineId = s.LineId, upstreamLineId = s.UpLineId, quantity = 1m, unitRatio = "1/1", conversionEvidence = (string?)null, codes = new[] { s.Code1 } } },
        })).EnsureSuccessStatusCode();
        detail = await client.GetAsync($"/api/v1/upstream-bills/{s.BillId}");
        using (var doc = System.Text.Json.JsonDocument.Parse(await detail.Content.ReadAsStringAsync()))
        {
            var line = doc.RootElement.GetProperty("data").GetProperty("lines")[0];
            Assert.Equal("1", line.GetProperty("allocatedQuantity").GetString());
            Assert.Equal("1", line.GetProperty("remainingQuantity").GetString());
        }
    }

    [Fact]
    public async Task Inbound_detail_line_shows_allocated_quantity()
    {
        var s = await SeedAsync("F8", 80);
        var client = await LoginAsync();
        NewKey(client);
        (await client.PostAsJsonAsync($"/api/v1/inbounds/{s.SnapId}/allocations", SaveBody(s, "1"))).EnsureSuccessStatusCode();
        var detail = await client.GetAsync($"/api/v1/inbounds/{s.SnapId}");
        using var doc = System.Text.Json.JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
        var line = doc.RootElement.GetProperty("data").GetProperty("lines")[0];
        Assert.Equal("2", line.GetProperty("allocatedQuantity").GetString());
    }

    private sealed record SaveResp(SaveData Data);
    private sealed record SaveData(int MatchVersion, int AllocationsSaved, bool AllRequiredCovered);
    private sealed record ConfirmResp(ConfirmData Data);
    private sealed record ConfirmData(string TaskId, string BillCode, int MatchVersion, bool Reused, string Message);
    private sealed record CancelResp(CancelData Data);
    private sealed record CancelData(int Cancelled);
    private sealed record JobResp(JobData Data);
    private sealed record JobData(string JobId);
    private sealed record InboundList(InboundListData Data);
    private sealed record InboundListData(InboundRecord[] Records, int Total);
    // API-01：外部 ID 与数量走 JSON 字符串
    private sealed record InboundRecord(string SnapshotId, string DocKey, string? MatchState, string? TaskState);
    private sealed record UpstreamList(UpstreamListData Data);
    private sealed record UpstreamListData(UpstreamRecord[] Records, int Total);
    private sealed record UpstreamRecord(string BillId, string? BillNo, string Completeness);
}
