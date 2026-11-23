using System.Runtime.Versioning; // brief 缺失此 using（SupportedOSPlatform 所需）；与 M1ApiGateTests 标注模式一致（已知 brief 缺陷同型）
using System.Net;
using System.Net.Http.Json;
using Dapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using TraceFlow.Domain.Versioning;
using TraceFlow.IntegrationTests.Sources;
using Xunit;

namespace TraceFlow.IntegrationTests.Master;

[SupportedOSPlatform("windows")]
public class ClassificationTests : IAsyncLifetime
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
        // 触发宿主启动（OrgBootstrap 种子 DEFAULT 组织 + KSOA 来源系统）
        using var warm = _factory.CreateClient();
        (await warm.GetAsync("/api/v1/health")).EnsureSuccessStatusCode();
        await SeedUserAsync("clsadmin", "分类管理员", "Cls-Password-123", new[] { "master:classify", "master:match", "trace:read" });
        await SeedUserAsync("clsviewer", "只读", "Cls-Password-123", new[] { "trace:read" });
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
        var resp = await client.PostAsJsonAsync("/api/v1/auth/login", new { username = user, password = pass });
        resp.EnsureSuccessStatusCode();
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
        var orgId = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.organization WHERE is_active=1");
        var sysId = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.source_system WHERE system_code=N'KSOA'");
        return (orgId, sysId);
    }

    private async Task<long> SeedProductAsync(string spid, string name, string cls = "UNKNOWN", string? reason = null)
    {
        var (orgId, sysId) = await OrgAsync();
        await using var c = await _fx.OpenAsync();
        return await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.product(org_id,source_system_id,spid,name,trace_classification,classification_reason)
OUTPUT inserted.id VALUES(@org,@sys,@spid,@name,@cls,@reason)",
            new { org = orgId, sys = sysId, spid, name, cls, reason });
    }

    private async Task<string> ProductVersionAsync(long id)
    {
        await using var c = await _fx.OpenAsync();
        return RowVer.ToHex((await c.ExecuteScalarAsync<byte[]>("SELECT ver FROM dbo.product WHERE id=@id", new { id }))!); // brief 原文 CS8604（Dapper ExecuteScalarAsync<T> 返回 T?）；商品刚种子必存在，null 包容（T10 同型）
    }

    private async Task<T> ScalarAsync<T>(string sql, object? param = null)
    {
        await using var c = await _fx.OpenAsync();
        return (await c.ExecuteScalarAsync<T>(sql, param))!; // brief 原文 CS8603（Dapper ExecuteScalarAsync<T> 返回 T?）；调用方断言标量必存在，null 包容
    }

    [Fact]
    public async Task Products_list_paginates_and_filters()
    {
        await SeedProductAsync("CP1", "阿莫西林胶囊", "REQUIRED", "处方药");
        await SeedProductAsync("CP2", "维生素C片", "EXEMPT", "化学药豁免");
        await SeedProductAsync("CP3", "板蓝根颗粒");
        var (client, _) = await LoginAsync("clsadmin", "Cls-Password-123");

        var page1 = await client.GetAsync("/api/v1/products?page=1&pageSize=2");
        Assert.Equal(HttpStatusCode.OK, page1.StatusCode);
        var body1 = await page1.Content.ReadAsStringAsync();
        Assert.Contains("\"total\":3", body1);
        Assert.Contains("CP1", body1);
        Assert.DoesNotContain("CP3", body1); // 第 2 页才有

        var kw = await client.GetAsync("/api/v1/products?keyword=阿莫西林");
        Assert.Contains("CP1", await kw.Content.ReadAsStringAsync());
        Assert.Contains("\"total\":1", await kw.Content.ReadAsStringAsync());

        var cls = await client.GetAsync("/api/v1/products?classification=EXEMPT");
        var clsBody = await cls.Content.ReadAsStringAsync();
        Assert.Contains("CP2", clsBody);
        Assert.DoesNotContain("CP1", clsBody);

        var none = await client.GetAsync("/api/v1/products?mappingState=NONE");
        Assert.Contains("\"total\":3", await none.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Products_list_includes_active_mapping_identity()
    {
        var pid = await SeedProductAsync("CP10", "匹配药");
        var (orgId, _) = await OrgAsync();
        await using (var c = await _fx.OpenAsync())
        {
            var drugId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.platform_drug(platform_drug_id,approval_no_norm,manufacturer_identity,dosage_form,strength,package_spec,source_kind)
OUTPUT inserted.id VALUES(N'PD-EXT-1',N'国药准字H99999999',N'某药厂',N'片剂',N'10mg',N'12片',N'MANUAL')");
            await c.ExecuteAsync(@"
INSERT INTO dbo.product_mapping(org_id,product_id,platform_drug_id,state,rule_version)
VALUES(@org,@pid,@drug,N'AUTO_CONFIRMED',N'match-1')", new { org = orgId, pid, drug = drugId });
        }
        var (client, _) = await LoginAsync("clsadmin", "Cls-Password-123");
        var body = await (await client.GetAsync("/api/v1/products?keyword=匹配药")).Content.ReadAsStringAsync();
        Assert.Contains("\"mappingState\":\"AUTO_CONFIRMED\"", body);
        Assert.Contains("PD-EXT-1", body);
        Assert.Contains("国药准字H99999999", body);
        Assert.Contains("某药厂", body);
        Assert.Contains("\"isLocked\":false", body);
    }

    [Fact]
    public async Task Trace_policy_updates_and_audits()
    {
        var pid = await SeedProductAsync("CP20", "分类药", "REQUIRED", "处方药");
        var ver = await ProductVersionAsync(pid);
        var (client, _) = await LoginAsync("clsadmin", "Cls-Password-123");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var resp = await client.PostAsJsonAsync($"/api/v1/products/{pid}/trace-policy",
            new { classification = "EXEMPT", reason = "化学药豁免", expectedVersion = ver });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"traceClassification\":\"EXEMPT\"", body);
        Assert.Contains("\"affectedNonTerminalTasks\":0", body); // M1 无上传任务产生路径（诚实声明）
        Assert.Equal("EXEMPT", await ScalarAsync<string>("SELECT trace_classification FROM dbo.product WHERE id=@id", new { id = pid }));
        Assert.Equal("化学药豁免", await ScalarAsync<string>("SELECT classification_reason FROM dbo.product WHERE id=@id", new { id = pid }));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.audit_log WHERE action=N'master.classify' AND object_id=@id", new { id = pid.ToString() }));
        Assert.NotEqual(ver, await ProductVersionAsync(pid));
    }

    [Fact]
    public async Task Trace_policy_validation_and_concurrency()
    {
        var pid = await SeedProductAsync("CP21", "守卫药");
        var ver = await ProductVersionAsync(pid);
        var (client, _) = await LoginAsync("clsadmin", "Cls-Password-123");

        async Task<HttpStatusCode> Post(object payload)
        {
            client.DefaultRequestHeaders.Remove("Idempotency-Key");
            client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
            return (await client.PostAsJsonAsync($"/api/v1/products/{pid}/trace-policy", payload)).StatusCode;
        }
        Assert.Equal((HttpStatusCode)422, await Post(new { classification = "FOO", reason = "x", expectedVersion = ver }));
        Assert.Equal((HttpStatusCode)422, await Post(new { classification = "UNKNOWN", reason = "x", expectedVersion = ver }));
        Assert.Equal((HttpStatusCode)422, await Post(new { classification = "EXEMPT", reason = "", expectedVersion = ver }));
        Assert.Equal((HttpStatusCode)422, await Post(new { classification = "EXEMPT", reason = "缺版本" }));
        Assert.Equal(HttpStatusCode.Conflict, await Post(new { classification = "EXEMPT", reason = "旧版本", expectedVersion = "0000000000000000" }));
        client.DefaultRequestHeaders.Remove("Idempotency-Key");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var missing = await client.PostAsJsonAsync("/api/v1/products/999999/trace-policy",
            new { classification = "EXEMPT", reason = "不存在", expectedVersion = ver });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        // 未被任何非法请求改动
        Assert.Equal("UNKNOWN", await ScalarAsync<string>("SELECT trace_classification FROM dbo.product WHERE id=@id", new { id = pid }));
    }

    [Fact]
    public async Task Trace_policy_requires_permission()
    {
        var pid = await SeedProductAsync("CP22", "权限药");
        var ver = await ProductVersionAsync(pid);
        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync($"/api/v1/products/{pid}/trace-policy",
            new { classification = "EXEMPT", reason = "匿名", expectedVersion = ver })).StatusCode);
        var (viewer, _) = await LoginAsync("clsviewer", "Cls-Password-123");
        viewer.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync($"/api/v1/products/{pid}/trace-policy",
            new { classification = "EXEMPT", reason = "越权", expectedVersion = ver })).StatusCode);
        // 列表 trace:read 可访问
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/v1/products")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/v1/suppliers")).StatusCode);
    }

    [Fact]
    public async Task Suppliers_list_filters_and_mapping()
    {
        var (orgId, sysId) = await OrgAsync();
        await using (var c = await _fx.OpenAsync())
        {
            var sid = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.supplier(org_id,source_system_id,dwbh,name,credit_code,role)
OUTPUT inserted.id VALUES(@org,@sys,N'D200',N'华东医药供应链',N'91310000MA1FL0001X',N'SUPPLIER')",
                new { org = orgId, sys = sysId });
            await c.ExecuteAsync("INSERT INTO dbo.supplier(org_id,source_system_id,dwbh,name) VALUES(@org,@sys,N'D201',N'无关供应商')",
                new { org = orgId, sys = sysId });
            var evId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.platform_enterprise_version(ref_ent_id,ent_id,name,credit_code,audit_status,role)
OUTPUT inserted.id VALUES(N'REF-D200',N'ENT-D200',N'华东医药（平台名）',N'91310000MA1FL0001X',N'审核通过',N'SUPPLIER')");
            await c.ExecuteAsync(@"
INSERT INTO dbo.supplier_mapping(org_id,supplier_id,enterprise_version_id,state)
VALUES(@org,@sid,@ev,N'AUTO_CONFIRMED')", new { org = orgId, sid, ev = evId });
        }
        var (client, _) = await LoginAsync("clsadmin", "Cls-Password-123");
        var body = await (await client.GetAsync("/api/v1/suppliers?keyword=华东")).Content.ReadAsStringAsync();
        Assert.Contains("\"total\":1", body);
        Assert.Contains("D200", body);
        Assert.Contains("REF-D200", body);
        Assert.Contains("ENT-D200", body);
        Assert.Contains("华东医药（平台名）", body);
        Assert.Contains("审核通过", body);
        Assert.Contains("\"mappingState\":\"AUTO_CONFIRMED\"", body);
        var none = await (await client.GetAsync("/api/v1/suppliers?mappingState=NONE")).Content.ReadAsStringAsync();
        Assert.Contains("D201", none);
        Assert.DoesNotContain("D200", none);
    }
}
