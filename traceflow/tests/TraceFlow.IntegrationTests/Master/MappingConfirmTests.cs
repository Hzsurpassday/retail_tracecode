using System.Runtime.Versioning; // brief 缺失此 using（SupportedOSPlatform 所需）；与 ClassificationTests/M1ApiGateTests 标注模式一致（已知 brief 缺陷同型）
using System.Net;
using System.Net.Http.Json;
using System.Text.Json; // brief 注：Json 助手用 JsonSerializer
using Dapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using TraceFlow.Domain.Versioning;
using Xunit;

namespace TraceFlow.IntegrationTests.Master;

[SupportedOSPlatform("windows")]
public class MappingConfirmTests : IAsyncLifetime
{
    private readonly Sql2008Fixture _fx = new();
    private WebApplicationFactory<Program> _factory = null!;

    public async Task InitializeAsync()
    {
        await _fx.InitializeAsync();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:TraceFlow", _fx.ConnectionString);
            b.UseSetting("TraceFlow:Env", "Mock");
        });
        using var warm = _factory.CreateClient();
        (await warm.GetAsync("/api/v1/health")).EnsureSuccessStatusCode();
        await SeedUserAsync("mapadmin", "映射管理员", "Map-Password-123", new[] { "master:match", "trace:read" });
    }

    public async Task DisposeAsync() { _factory.Dispose(); await _fx.DisposeAsync(); }

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
        (await client.PostAsJsonAsync("/api/v1/auth/login", new { username = user, password = pass })).EnsureSuccessStatusCode();
        var session2 = await client.GetFromJsonAsync<SessionResp>("/api/v1/auth/session");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session2!.Data.CsrfToken);
        return (client, session2.Data.CsrfToken);
    }

    private sealed record SessionResp(SessionData Data);
    private sealed record SessionData(bool Authenticated, string CsrfToken);

    private async Task<(long OrgId, long SysId)> OrgAsync()
    {
        await using var c = await _fx.OpenAsync();
        return (await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.organization WHERE is_active=1"),
                await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.source_system WHERE system_code=N'KSOA'"));
    }

    private async Task<T> ScalarAsync<T>(string sql, object? param = null)
    {
        await using var c = await _fx.OpenAsync();
        return (await c.ExecuteScalarAsync<T>(sql, param))!; // brief 原文返回 T?（Dapper [MaybeNull]）→CS8603（本任务 RED 首轮实证）；查询必返标量，null 包容（T12 MatchingServiceTests 同型）
    }

    private async Task ExecAsync(string sql, object? param = null)
    {
        await using var c = await _fx.OpenAsync();
        await c.ExecuteAsync(sql, param);
    }

    // 建商品+平台药+指定态映射，返回 (mappingId, productSpid, drugKey, verHex)
    private async Task<(long MapId, string Spid, string DrugKey, string Ver)> SeedProductMappingAsync(string state, string spid = "CF1", string drugKey = "PD-CF1")
    {
        var (org, sys) = await OrgAsync();
        var pid = await ScalarAsync<long>(@"
INSERT INTO dbo.product(org_id,source_system_id,spid,name) OUTPUT inserted.id VALUES(@org,@sys,@spid,@name)",
            new { org, sys, spid, name = $"商品{spid}" });
        var drugId = await ScalarAsync<long>(@"
INSERT INTO dbo.platform_drug(platform_drug_id,source_kind) OUTPUT inserted.id VALUES(@k,N'MANUAL')", new { k = drugKey });
        var mapId = await ScalarAsync<long>(@"
INSERT INTO dbo.product_mapping(org_id,product_id,platform_drug_id,state) OUTPUT inserted.id VALUES(@org,@pid,@drug,@state)",
            new { org, pid, drug = drugId, state });
        var ver = RowVer.ToHex(await ScalarAsync<byte[]>("SELECT ver FROM dbo.product_mapping WHERE id=@id", new { id = mapId }));
        return (mapId, spid, drugKey, ver);
    }

    private async Task<(long MapId, string EntId, string Ver)> SeedSupplierMappingAsync(string state, string dwbh = "CF-D1", string entId = "ENT-CF1")
    {
        var (org, sys) = await OrgAsync();
        var sid = await ScalarAsync<long>(@"
INSERT INTO dbo.supplier(org_id,source_system_id,dwbh,name) OUTPUT inserted.id VALUES(@org,@sys,@dwbh,@name)",
            new { org, sys, dwbh, name = $"供应商{dwbh}" });
        var evId = await ScalarAsync<long>(@"
INSERT INTO dbo.platform_enterprise_version(ref_ent_id,ent_id,name) OUTPUT inserted.id VALUES(@ref,@ent,@name)",
            new { @ref = $"REF-{entId}", ent = entId, name = "平台企业" });
        var mapId = await ScalarAsync<long>(@"
INSERT INTO dbo.supplier_mapping(org_id,supplier_id,enterprise_version_id,state) OUTPUT inserted.id VALUES(@org,@sid,@ev,@state)",
            new { org, sid, ev = evId, state });
        var ver = RowVer.ToHex(await ScalarAsync<byte[]>("SELECT ver FROM dbo.supplier_mapping WHERE id=@id", new { id = mapId }));
        return (mapId, entId, ver);
    }

    private static StringContent Json(object o) => new(JsonSerializer.Serialize(o), System.Text.Encoding.UTF8, "application/json");

    [Fact]
    public async Task Confirm_candidate_sets_manual_locked()
    {
        var (mapId, _, drugKey, ver) = await SeedProductMappingAsync("CANDIDATE");
        var (client, _) = await LoginAsync("mapadmin", "Map-Password-123");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var resp = await client.PostAsync($"/api/v1/product-mappings/{mapId}/confirm",
            Json(new { targetId = drugKey, expectedVersion = ver, reason = "人工核对五项一致" }));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("MANUAL_CONFIRMED", await ScalarAsync<string>("SELECT state FROM dbo.product_mapping WHERE id=@id", new { id = mapId }));
        Assert.True(await ScalarAsync<bool>("SELECT is_locked FROM dbo.product_mapping WHERE id=@id", new { id = mapId }));
        Assert.Equal("mapadmin", await ScalarAsync<string>("SELECT confirmed_by FROM dbo.product_mapping WHERE id=@id", new { id = mapId }));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.product_mapping_history WHERE mapping_id=@id AND state=N'MANUAL_CONFIRMED'", new { id = mapId }));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.audit_log WHERE action=N'master.mapping.confirm' AND object_id=@id", new { id = mapId.ToString() }));
    }

    [Fact]
    public async Task Confirm_strong_conflict_rejected()
    {
        var (mapId, _, drugKey, ver) = await SeedProductMappingAsync("CONFLICT");
        var (client, _) = await LoginAsync("mapadmin", "Map-Password-123");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var resp = await client.PostAsync($"/api/v1/product-mappings/{mapId}/confirm",
            Json(new { targetId = drugKey, expectedVersion = ver, reason = "试图确认强冲突" }));
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Contains("STRONG_CONFLICT", await resp.Content.ReadAsStringAsync());
        Assert.Equal("CONFLICT", await ScalarAsync<string>("SELECT state FROM dbo.product_mapping WHERE id=@id", new { id = mapId }));
    }

    [Fact]
    public async Task Confirm_wrong_target_rejected_no_rebind()
    {
        var (mapId, _, _, ver) = await SeedProductMappingAsync("CANDIDATE");
        var (client, _) = await LoginAsync("mapadmin", "Map-Password-123");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var resp = await client.PostAsync($"/api/v1/product-mappings/{mapId}/confirm",
            Json(new { targetId = "PD-OTHER", expectedVersion = ver, reason = "试图改绑" }));
        Assert.Equal((HttpStatusCode)422, resp.StatusCode);
        Assert.Equal("CANDIDATE", await ScalarAsync<string>("SELECT state FROM dbo.product_mapping WHERE id=@id", new { id = mapId }));
    }

    [Fact]
    public async Task Confirm_version_conflict_and_validation_and_404()
    {
        var (mapId, _, drugKey, ver) = await SeedProductMappingAsync("CANDIDATE");
        var (client, _) = await LoginAsync("mapadmin", "Map-Password-123");
        async Task<HttpStatusCode> Post(long id, object body)
        {
            client.DefaultRequestHeaders.Remove("Idempotency-Key");
            client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
            return (await client.PostAsync($"/api/v1/product-mappings/{id}/confirm", Json(body))).StatusCode;
        }
        Assert.Equal(HttpStatusCode.Conflict, await Post(mapId, new { targetId = drugKey, expectedVersion = "0000000000000000", reason = "旧版本" }));
        Assert.Equal((HttpStatusCode)422, await Post(mapId, new { targetId = drugKey, expectedVersion = ver, reason = "" }));
        Assert.Equal((HttpStatusCode)422, await Post(mapId, new { targetId = drugKey, reason = "缺版本" }));
        Assert.Equal(HttpStatusCode.NotFound, await Post(999999, new { targetId = drugKey, expectedVersion = ver, reason = "不存在" }));
    }

    [Fact]
    public async Task Disable_active_then_reconfirm()
    {
        var (mapId, _, drugKey, ver) = await SeedProductMappingAsync("AUTO_CONFIRMED");
        var (client, _) = await LoginAsync("mapadmin", "Map-Password-123");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var dis = await client.PostAsync($"/api/v1/product-mappings/{mapId}/disable", Json(new { expectedVersion = ver, reason = "误配解除" }));
        Assert.Equal(HttpStatusCode.OK, dis.StatusCode);
        Assert.Equal("DISABLED", await ScalarAsync<string>("SELECT state FROM dbo.product_mapping WHERE id=@id", new { id = mapId }));
        Assert.False(await ScalarAsync<bool>("SELECT is_locked FROM dbo.product_mapping WHERE id=@id", new { id = mapId }));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.audit_log WHERE action=N'master.mapping.disable' AND object_id=@id", new { id = mapId.ToString() }));
        // 解除后可重新确认（re-enable 路径）
        var ver2 = RowVer.ToHex(await ScalarAsync<byte[]>("SELECT ver FROM dbo.product_mapping WHERE id=@id", new { id = mapId }));
        client.DefaultRequestHeaders.Remove("Idempotency-Key");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var re = await client.PostAsync($"/api/v1/product-mappings/{mapId}/confirm", Json(new { targetId = drugKey, expectedVersion = ver2, reason = "重新确认" }));
        Assert.Equal(HttpStatusCode.OK, re.StatusCode);
        Assert.Equal("MANUAL_CONFIRMED", await ScalarAsync<string>("SELECT state FROM dbo.product_mapping WHERE id=@id", new { id = mapId }));
    }

    [Fact]
    public async Task Disable_blocked_by_inflight_task()
    {
        var (mapId, spid, _, ver) = await SeedProductMappingAsync("AUTO_CONFIRMED", spid: "CF-INFLIGHT");
        var (org, sys) = await OrgAsync();
        var snapId = await ScalarAsync<long>(@"
INSERT INTO dbo.source_snapshot(org_id,source_system_id,source_document_key,document_type,version,content_hash)
OUTPUT inserted.id VALUES(@org,@sys,N'CF-DOC',N'sale',1,REPLICATE(N'a',64))", new { org, sys });
        await ExecAsync(@"
INSERT INTO dbo.source_line(snapshot_id,source_line_key,product_source_id,quantity_original,line_hash,event_kind)
VALUES(@snap,N'1',@spid,1,REPLICATE(N'b',64),N'SALE')", new { snap = snapId, spid });
        await ExecAsync(@"
INSERT INTO dbo.upload_task(org_id,env,event_key,task_type,source_snapshot_id,source_hash,payload_hash,state)
VALUES(@org,N'MOCK',N'CF-EVENT-1',N'RETAIL_321',@snap,REPLICATE(N'c',64),REPLICATE(N'd',64),N'Blocked')", new { org, snap = snapId });
        var (client, _) = await LoginAsync("mapadmin", "Map-Password-123");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var blocked = await client.PostAsync($"/api/v1/product-mappings/{mapId}/disable", Json(new { expectedVersion = ver, reason = "在途解除" }));
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Contains("TASKS_IN_FLIGHT", await blocked.Content.ReadAsStringAsync());
        Assert.Equal("AUTO_CONFIRMED", await ScalarAsync<string>("SELECT state FROM dbo.product_mapping WHERE id=@id", new { id = mapId }));
        // 任务终态后放行
        await ExecAsync("UPDATE dbo.upload_task SET state=N'Succeeded' WHERE event_key=N'CF-EVENT-1'");
        client.DefaultRequestHeaders.Remove("Idempotency-Key");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var ok = await client.PostAsync($"/api/v1/product-mappings/{mapId}/disable", Json(new { expectedVersion = ver, reason = "终态后解除" }));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task Supplier_confirm_and_disable()
    {
        var (mapId, entId, ver) = await SeedSupplierMappingAsync("CANDIDATE");
        var (client, _) = await LoginAsync("mapadmin", "Map-Password-123");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var conf = await client.PostAsync($"/api/v1/supplier-mappings/{mapId}/confirm", Json(new { targetId = entId, expectedVersion = ver, reason = "信用代码核对一致" }));
        Assert.Equal(HttpStatusCode.OK, conf.StatusCode);
        Assert.Equal("MANUAL_CONFIRMED", await ScalarAsync<string>("SELECT state FROM dbo.supplier_mapping WHERE id=@id", new { id = mapId }));
        Assert.True(await ScalarAsync<bool>("SELECT is_locked FROM dbo.supplier_mapping WHERE id=@id", new { id = mapId }));
        var ver2 = RowVer.ToHex(await ScalarAsync<byte[]>("SELECT ver FROM dbo.supplier_mapping WHERE id=@id", new { id = mapId }));
        client.DefaultRequestHeaders.Remove("Idempotency-Key");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var dis = await client.PostAsync($"/api/v1/supplier-mappings/{mapId}/disable", Json(new { expectedVersion = ver2, reason = "解除" }));
        Assert.Equal(HttpStatusCode.OK, dis.StatusCode);
        Assert.Equal("DISABLED", await ScalarAsync<string>("SELECT state FROM dbo.supplier_mapping WHERE id=@id", new { id = mapId }));
    }

    [Fact]
    public async Task Confirm_active_unique_race_rejected()
    {
        var (org, sys) = await OrgAsync();
        var drugId = await ScalarAsync<long>("INSERT INTO dbo.platform_drug(platform_drug_id,source_kind) OUTPUT inserted.id VALUES(N'PD-RACE',N'MANUAL')");
        var p1 = await ScalarAsync<long>("INSERT INTO dbo.product(org_id,source_system_id,spid,name) OUTPUT inserted.id VALUES(@org,@sys,N'RACE1',N'药一')", new { org, sys });
        var p2 = await ScalarAsync<long>("INSERT INTO dbo.product(org_id,source_system_id,spid,name) OUTPUT inserted.id VALUES(@org,@sys,N'RACE2',N'药二')", new { org, sys });
        await ExecAsync("INSERT INTO dbo.product_mapping(org_id,product_id,platform_drug_id,state) VALUES(@org,@p,@drug,N'AUTO_CONFIRMED')", new { org, p = p1, drug = drugId });
        var m2 = await ScalarAsync<long>("INSERT INTO dbo.product_mapping(org_id,product_id,platform_drug_id,state) OUTPUT inserted.id VALUES(@org,@p,@drug,N'CANDIDATE')", new { org, p = p2, drug = drugId });
        var ver = RowVer.ToHex(await ScalarAsync<byte[]>("SELECT ver FROM dbo.product_mapping WHERE id=@id", new { id = m2 }));
        var (client, _) = await LoginAsync("mapadmin", "Map-Password-123");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var resp = await client.PostAsync($"/api/v1/product-mappings/{m2}/confirm", Json(new { targetId = "PD-RACE", expectedVersion = ver, reason = "抢占已被占用的平台药" }));
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Contains("MAPPING_ACTIVE_CONFLICT", await resp.Content.ReadAsStringAsync());
        Assert.Equal("CANDIDATE", await ScalarAsync<string>("SELECT state FROM dbo.product_mapping WHERE id=@id", new { id = m2 }));
    }
}
