using System.Runtime.Versioning;
using System.Net;
using System.Net.Http.Json;
using Dapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using TraceFlow.Domain.Versioning;
using TraceFlow.IntegrationTests.Sources;

namespace TraceFlow.IntegrationTests.Api;

[SupportedOSPlatform("windows")]
public class M2CarryforwardApiTests : IClassFixture<Sql2008Fixture>, IAsyncLifetime
{
    private readonly Sql2008Fixture _fx;
    private WebApplicationFactory<Program> _factory = null!;
    public M2CarryforwardApiTests(Sql2008Fixture fx) => _fx = fx;

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
IF NOT EXISTS (SELECT 1 FROM dbo.app_user WHERE username=N'm2master')
    INSERT INTO dbo.app_user(username,display_name,password_hash) VALUES(N'm2master',N'主数据员',@h);
SELECT id FROM dbo.app_user WHERE username=N'm2master'", new { h = hasher.HashPassword("m2master", "Master-Password-123") });
        foreach (var p in new[] { "master:match", "master:classify", "trace:read" })
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
        (await client.PostAsJsonAsync("/api/v1/auth/login", new { username = "m2master", password = "Master-Password-123" })).EnsureSuccessStatusCode();
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

    private async Task<(long OrgId, long SysId)> OrgAsync(SqlConnection c)
    {
        var (orgId, sysId) = await TestSeed.EnsureAsync(c);
        await c.ExecuteAsync("UPDATE dbo.organization SET ent_id=N'ENT-STORE', ref_ent_id=N'REF-STORE' WHERE id=@orgId", new { orgId });
        return (orgId, sysId);
    }

    [Fact]
    public async Task Resolve_product_rebinds_conflict_mapping()
    {
        await using var c = await _fx.OpenAsync();
        var (orgId, sysId) = await OrgAsync(c);
        await c.ExecuteAsync("INSERT INTO dbo.product(org_id,source_system_id,spid,name,trace_classification) VALUES(@orgId,@sysId,N'SPR1',N'药',N'REQUIRED')", new { orgId, sysId });
        var prodId = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.product WHERE org_id=@orgId AND spid=N'SPR1'", new { orgId });
        await c.ExecuteAsync("INSERT INTO dbo.platform_drug(platform_drug_id,source_kind) VALUES(N'DRUG-R1-A',N'fixture'),(N'DRUG-R1-B',N'fixture')");
        var drugA = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.platform_drug WHERE platform_drug_id=N'DRUG-R1-A'");
        var mapId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.product_mapping(org_id,product_id,platform_drug_id,state) OUTPUT INSERTED.id
VALUES(@orgId,@prodId,@drugA,N'CONFLICT')", new { orgId, prodId, drugA });
        // brief 原文 RowVer.ToHex(await ExecuteScalarAsync<byte[]>) → CS8604（Dapper 返回 T?）；映射刚插入必存在，null 包容
        var ver = RowVer.ToHex((await c.ExecuteScalarAsync<byte[]>("SELECT ver FROM dbo.product_mapping WHERE id=@id", new { id = mapId }))!);
        var client = await LoginAsync();
        NewKey(client);
        var resp = await client.PostAsJsonAsync($"/api/v1/product-mappings/{mapId}/resolve",
            new { targetId = "DRUG-R1-B", expectedVersion = ver, reason = "人工核对批准文号后改绑" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("MANUAL_CONFIRMED", await c.ExecuteScalarAsync<string>("SELECT state FROM dbo.product_mapping WHERE id=@id", new { id = mapId }));
        Assert.Equal("DRUG-R1-B", await c.ExecuteScalarAsync<string>(@"
SELECT pd.platform_drug_id FROM dbo.product_mapping pm JOIN dbo.platform_drug pd ON pd.id=pm.platform_drug_id WHERE pm.id=@id", new { id = mapId }));
        Assert.True(await c.ExecuteScalarAsync<bool>("SELECT is_locked FROM dbo.product_mapping WHERE id=@id", new { id = mapId }));
    }

    [Fact]
    public async Task Resolve_supplier_credit_mismatch_422()
    {
        await using var c = await _fx.OpenAsync();
        var (orgId, sysId) = await OrgAsync(c);
        await c.ExecuteAsync("INSERT INTO dbo.supplier(org_id,source_system_id,dwbh,name,credit_code) VALUES(@orgId,@sysId,N'DWR2',N'供应商',N'CREDIT-1')", new { orgId, sysId });
        var supId = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.supplier WHERE org_id=@orgId AND dwbh=N'DWR2'", new { orgId });
        var evId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.platform_enterprise_version(ref_ent_id,ent_id,name,credit_code) OUTPUT INSERTED.id
VALUES(N'REF-R2',N'ENT-R2',N'企业',N'CREDIT-2')");
        var mapId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.supplier_mapping(org_id,supplier_id,enterprise_version_id,state) OUTPUT INSERTED.id
VALUES(@orgId,@supId,@evId,N'REVIEW_REQUIRED')", new { orgId, supId, evId });
        var ver = RowVer.ToHex((await c.ExecuteScalarAsync<byte[]>("SELECT ver FROM dbo.supplier_mapping WHERE id=@id", new { id = mapId }))!);
        var client = await LoginAsync();
        NewKey(client);
        var bad = await client.PostAsJsonAsync($"/api/v1/supplier-mappings/{mapId}/resolve",
            new { targetId = "REF-R2", expectedVersion = ver, reason = "改绑" });
        Assert.Equal((HttpStatusCode)422, bad.StatusCode);
        Assert.Contains("CREDIT_MISMATCH", await bad.Content.ReadAsStringAsync());
        await c.ExecuteAsync("UPDATE dbo.platform_enterprise_version SET credit_code=N'CREDIT-1' WHERE id=@id", new { id = evId });
        NewKey(client);
        var ok = await client.PostAsJsonAsync($"/api/v1/supplier-mappings/{mapId}/resolve",
            new { targetId = "REF-R2", expectedVersion = ver, reason = "信用代码一致，改绑" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task Resolve_active_mapping_rejected()
    {
        await using var c = await _fx.OpenAsync();
        var (orgId, sysId) = await OrgAsync(c);
        await c.ExecuteAsync("INSERT INTO dbo.product(org_id,source_system_id,spid,name,trace_classification) VALUES(@orgId,@sysId,N'SPR3',N'药',N'REQUIRED')", new { orgId, sysId });
        var prodId = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.product WHERE org_id=@orgId AND spid=N'SPR3'", new { orgId });
        await c.ExecuteAsync("INSERT INTO dbo.platform_drug(platform_drug_id,source_kind) VALUES(N'DRUG-R3',N'fixture')");
        var drugId = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.platform_drug WHERE platform_drug_id=N'DRUG-R3'");
        var mapId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.product_mapping(org_id,product_id,platform_drug_id,state) OUTPUT INSERTED.id
VALUES(@orgId,@prodId,@drugId,N'MANUAL_CONFIRMED')", new { orgId, prodId, drugId });
        var ver = RowVer.ToHex((await c.ExecuteScalarAsync<byte[]>("SELECT ver FROM dbo.product_mapping WHERE id=@id", new { id = mapId }))!);
        var client = await LoginAsync();
        NewKey(client);
        var resp = await client.PostAsJsonAsync($"/api/v1/product-mappings/{mapId}/resolve",
            new { targetId = "DRUG-R3", expectedVersion = ver, reason = "x" });
        Assert.Equal((HttpStatusCode)422, resp.StatusCode);
        Assert.Contains("STATE_NOT_RESOLVABLE", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Resolve_onto_actively_mapped_drug_409()
    {
        // R11b 有效唯一竞态：改绑目标已被另一条 MANUAL_CONFIRMED 映射占用（UX_pmap_active_platform）→409；
        // 断言取库内真值——被改绑映射保持原状、目标上不出现第二条有效映射（不只看状态码）。
        await using var c = await _fx.OpenAsync();
        var (orgId, sysId) = await OrgAsync(c);
        await c.ExecuteAsync("INSERT INTO dbo.product(org_id,source_system_id,spid,name,trace_classification) VALUES(@orgId,@sysId,N'SPR4',N'药',N'REQUIRED'),(@orgId,@sysId,N'SPR4B',N'药',N'REQUIRED')", new { orgId, sysId });
        var prodId = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.product WHERE org_id=@orgId AND spid=N'SPR4'", new { orgId });
        var prodIdB = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.product WHERE org_id=@orgId AND spid=N'SPR4B'", new { orgId });
        await c.ExecuteAsync("INSERT INTO dbo.platform_drug(platform_drug_id,source_kind) VALUES(N'DRUG-R4-A',N'fixture'),(N'DRUG-R4-B',N'fixture')");
        var drugA = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.platform_drug WHERE platform_drug_id=N'DRUG-R4-A'");
        var drugB = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.platform_drug WHERE platform_drug_id=N'DRUG-R4-B'");
        var mapId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.product_mapping(org_id,product_id,platform_drug_id,state) OUTPUT INSERTED.id
VALUES(@orgId,@prodId,@drugA,N'CONFLICT')", new { orgId, prodId, drugA });
        var mapIdB = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.product_mapping(org_id,product_id,platform_drug_id,state) OUTPUT INSERTED.id
VALUES(@orgId,@prodIdB,@drugB,N'MANUAL_CONFIRMED')", new { orgId, prodIdB, drugB });
        var ver = RowVer.ToHex((await c.ExecuteScalarAsync<byte[]>("SELECT ver FROM dbo.product_mapping WHERE id=@id", new { id = mapId }))!);
        var client = await LoginAsync();
        NewKey(client);
        var resp = await client.PostAsJsonAsync($"/api/v1/product-mappings/{mapId}/resolve",
            new { targetId = "DRUG-R4-B", expectedVersion = ver, reason = "改绑到已被占用的平台药" });
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Contains("MAPPING_ACTIVE_CONFLICT", await resp.Content.ReadAsStringAsync());
        Assert.Equal("CONFLICT", await c.ExecuteScalarAsync<string>("SELECT state FROM dbo.product_mapping WHERE id=@id", new { id = mapId }));
        Assert.Equal(drugA, await c.ExecuteScalarAsync<long>("SELECT platform_drug_id FROM dbo.product_mapping WHERE id=@id", new { id = mapId }));
        Assert.Equal(1, await c.ExecuteScalarAsync<int>(@"
SELECT COUNT(*) FROM dbo.product_mapping
WHERE org_id=@orgId AND platform_drug_id=@drugB AND state IN (N'AUTO_CONFIRMED',N'MANUAL_CONFIRMED')", new { orgId, drugB }));
        Assert.Equal(mapIdB, await c.ExecuteScalarAsync<long>(@"
SELECT id FROM dbo.product_mapping
WHERE org_id=@orgId AND platform_drug_id=@drugB AND state IN (N'AUTO_CONFIRMED',N'MANUAL_CONFIRMED')", new { orgId, drugB }));
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.product_mapping_history WHERE mapping_id=@id", new { id = mapId }));
    }

    private async Task<(long ProdId, long SnapId, long TaskId, string ProdVer)> SeedTaskAsync(SqlConnection c, string tag, bool sent)
    {
        var (orgId, sysId) = await OrgAsync(c);
        await c.ExecuteAsync("INSERT INTO dbo.product(org_id,source_system_id,spid,name,trace_classification) VALUES(@orgId,@sysId,@spid,N'药',N'REQUIRED')", new { orgId, sysId, spid = "SPT-" + tag });
        var prodId = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.product WHERE org_id=@orgId AND spid=@spid", new { orgId, spid = "SPT-" + tag });
        var snapId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.source_snapshot(org_id,source_system_id,source_document_key,document_type,version,completeness_status,content_hash)
OUTPUT INSERTED.id VALUES(@orgId,@sysId,@key,N'PURCHASE_IN',1,N'COMPLETE',@hash)",
            new { orgId, sysId, key = "PI-T-" + tag, hash = new string('0', 64) });
        await c.ExecuteAsync(@"
INSERT INTO dbo.source_line(snapshot_id,source_line_key,product_source_id,quantity_original,line_hash,event_kind)
VALUES(@snapId,N'1',@spid,1,@hash,N'PURCHASE_IN')", new { snapId, spid = "SPT-" + tag, hash = new string('0', 64) });
        var taskId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upload_task(org_id,env,event_key,task_type,source_snapshot_id,source_hash,payload_hash,state)
OUTPUT INSERTED.id VALUES(@orgId,N'MOCK',@key,N'PURCHASE_IN_102',@snapId,@hash,@hash,N'Ready')",
            new { orgId, key = $"KSOA:PI-T-{tag}:PURCHASE_IN", snapId, hash = new string('0', 64) });
        await c.ExecuteAsync(@"
INSERT INTO dbo.upload_part(task_id,org_id,env,part_index,bill_code,payload_json,payload_hash,code_set_hash,state)
VALUES(@taskId,@orgId,N'MOCK',0,@bill,N'{}',@hash,@hash,N'Ready')",
            new { taskId, orgId, bill = "TF" + tag.PadRight(10, '0')[..10], hash = new string('0', 64) });
        if (sent)
            // brief 原文缺 fencing_token（0006 该列 NOT NULL 且无默认）→ SqlException 无法插 NULL；补 fencing_token=1
            await c.ExecuteAsync("INSERT INTO dbo.upload_attempt(task_id,attempt_no,attempt_kind,env,fencing_token,started_at) VALUES(@taskId,1,N'UPLOAD',N'MOCK',1,SYSUTCDATETIME())", new { taskId });
        var prodVer = RowVer.ToHex((await c.ExecuteScalarAsync<byte[]>("SELECT ver FROM dbo.product WHERE id=@id", new { id = prodId }))!);
        return (prodId, snapId, taskId, prodVer);
    }

    [Fact]
    public async Task Trace_policy_cancels_presend_task_and_enqueues_rematch()
    {
        await using var c = await _fx.OpenAsync();
        var (prodId, snapId, taskId, prodVer) = await SeedTaskAsync(c, "W1", sent: false);
        var client = await LoginAsync();
        NewKey(client);
        var resp = await client.PostAsJsonAsync($"/api/v1/products/{prodId}/trace-policy",
            new { classification = "EXEMPT", reason = "确认豁免", expectedVersion = prodVer });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("\"affectedNonTerminalTasks\":1", await resp.Content.ReadAsStringAsync());
        Assert.Equal("Cancelled", await c.ExecuteScalarAsync<string>("SELECT state FROM dbo.upload_task WHERE id=@id", new { id = taskId }));
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=N'inbound-match' AND intent_key LIKE N'map:%'"));
    }

    [Fact]
    public async Task Trace_policy_sent_task_untouched()
    {
        await using var c = await _fx.OpenAsync();
        var (prodId, snapId, taskId, prodVer) = await SeedTaskAsync(c, "W2", sent: true);
        var client = await LoginAsync();
        NewKey(client);
        var resp = await client.PostAsJsonAsync($"/api/v1/products/{prodId}/trace-policy",
            new { classification = "EXEMPT", reason = "确认豁免", expectedVersion = prodVer });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("Ready", await c.ExecuteScalarAsync<string>("SELECT state FROM dbo.upload_task WHERE id=@id", new { id = taskId }));
    }

    [Fact]
    public async Task Supplier_disable_guard_is_dwbh_scoped()
    {
        await using var c = await _fx.OpenAsync();
        var (orgId, sysId) = await OrgAsync(c);
        await c.ExecuteAsync("INSERT INTO dbo.supplier(org_id,source_system_id,dwbh,name) VALUES(@orgId,@sysId,N'DWG1',N'甲'),(@orgId,@sysId,N'DWG2',N'乙')", new { orgId, sysId });
        var sup1 = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.supplier WHERE org_id=@orgId AND dwbh=N'DWG1'", new { orgId });
        var sup2 = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.supplier WHERE org_id=@orgId AND dwbh=N'DWG2'", new { orgId });
        var evId = await c.ExecuteScalarAsync<long>("INSERT INTO dbo.platform_enterprise_version(ref_ent_id,ent_id,name) OUTPUT INSERTED.id VALUES(N'REF-G',N'ENT-G',N'企业')");
        var map1 = await c.ExecuteScalarAsync<long>("INSERT INTO dbo.supplier_mapping(org_id,supplier_id,enterprise_version_id,state) OUTPUT INSERTED.id VALUES(@orgId,@sup1,@evId,N'MANUAL_CONFIRMED')", new { orgId, sup1, evId });
        var evId2 = await c.ExecuteScalarAsync<long>("INSERT INTO dbo.platform_enterprise_version(ref_ent_id,ent_id,name) OUTPUT INSERTED.id VALUES(N'REF-G2',N'ENT-G2',N'企业2')");
        var map2 = await c.ExecuteScalarAsync<long>("INSERT INTO dbo.supplier_mapping(org_id,supplier_id,enterprise_version_id,state) OUTPUT INSERTED.id VALUES(@orgId,@sup2,@evId2,N'MANUAL_CONFIRMED')", new { orgId, sup2, evId2 });
        var snapId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.source_snapshot(org_id,source_system_id,source_document_key,document_type,version,completeness_status,content_hash)
OUTPUT INSERTED.id VALUES(@orgId,@sysId,N'PI-G1',N'PURCHASE_IN',1,N'COMPLETE',@hash)", new { orgId, sysId, hash = new string('0', 64) });
        await c.ExecuteAsync("INSERT INTO dbo.inbound_match(org_id,env,source_snapshot_id,supplier_dwbh,match_state) VALUES(@orgId,N'MOCK',@snapId,N'DWG1',N'COMPLETE')", new { orgId, snapId });
        var taskId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upload_task(org_id,env,event_key,task_type,source_snapshot_id,source_hash,payload_hash,state)
OUTPUT INSERTED.id VALUES(@orgId,N'MOCK',N'KSOA:PI-G1:PURCHASE_IN',N'PURCHASE_IN_102',@snapId,@hash,@hash,N'Ready')",
            new { orgId, snapId, hash = new string('0', 64) });
        var ver1 = RowVer.ToHex((await c.ExecuteScalarAsync<byte[]>("SELECT ver FROM dbo.supplier_mapping WHERE id=@id", new { id = map1 }))!);
        var ver2 = RowVer.ToHex((await c.ExecuteScalarAsync<byte[]>("SELECT ver FROM dbo.supplier_mapping WHERE id=@id", new { id = map2 }))!);
        var client = await LoginAsync();
        NewKey(client);
        var blocked = await client.PostAsJsonAsync($"/api/v1/supplier-mappings/{map1}/disable", new { expectedVersion = ver1, reason = "解除" });
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Contains("TASKS_IN_FLIGHT", await blocked.Content.ReadAsStringAsync());
        NewKey(client);
        var ok = await client.PostAsJsonAsync($"/api/v1/supplier-mappings/{map2}/disable", new { expectedVersion = ver2, reason = "无关供应商解除" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }
}
