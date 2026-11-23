using System.Runtime.Versioning; // brief 缺失此 using（SupportedOSPlatform 所需）；与 ImportServiceTests/SourceSyncTests 标注模式一致（T6 偏差 1 同型）
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Infrastructure.Master;
using TraceFlow.Infrastructure.Security;
using TraceFlow.Infrastructure.Settings;
using TraceFlow.IntegrationTests.Sources;
using Xunit;

namespace TraceFlow.IntegrationTests.Master;

[SupportedOSPlatform("windows")] // SettingsService 依赖 DpapiProtector（brief 缺失此标注→CA1416，TreatWarningsAsErrors；与 ImportServiceTests 同模式）
public class SupplierMatchingTests : IAsyncLifetime
{
    private readonly Sql2008Fixture _fx = new();
    public async Task InitializeAsync() => await _fx.InitializeAsync();
    public async Task DisposeAsync() => await _fx.DisposeAsync();

    private async Task<(long OrgId, long SysId)> SeedAsync()
    {
        await using var c = new SqlConnection(_fx.ConnectionString);
        await c.OpenAsync();
        return await TestSeed.EnsureAsync(c);
    }

    private async Task<long> SeedSupplierAsync(string dwbh, string name = "华东医药", string? credit = "91310000MA1FL0001X", string? role = "SUPPLIER")
    {
        var (org, sys) = await SeedAsync();
        await using var c = new SqlConnection(_fx.ConnectionString);
        await c.OpenAsync();
        return await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.supplier(org_id,source_system_id,dwbh,name,credit_code,role)
OUTPUT inserted.id VALUES(@org,@sys,@dwbh,@name,@cc,@role)",
            new { org, sys, dwbh, name, cc = credit, role });
    }

    private async Task<long> SeedEntAsync(string refId, string entId, string name = "华东医药（平台）",
        string? credit = "91310000MA1FL0001X", string? audit = "审核通过", string? role = "SUPPLIER")
    {
        await using var c = new SqlConnection(_fx.ConnectionString);
        await c.OpenAsync();
        return await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.platform_enterprise_version(ref_ent_id,ent_id,name,credit_code,audit_status,role)
OUTPUT inserted.id VALUES(@ref,@ent,@name,@cc,@audit,@role)",
            new { @ref = refId, ent = entId, name, cc = credit, audit, role });
    }

    private async Task SetAuditPassAsync(string json)
    {
        await using var c = new SqlConnection(_fx.ConnectionString);
        await c.OpenAsync();
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
        var svc = new SettingsService(_fx.ConnectionString, new DpapiProtector());
        var view = (await svc.ListAsync(CancellationToken.None)).Single(v => v.Key == "supplier.auditPassValues");
        await svc.UpdateAsync(c, tx, "supplier.auditPassValues", json, null, view.Id == 0 ? "0" : view.Version, "审核白名单", "tester", CancellationToken.None);
        await tx.CommitAsync();
    }

    private async Task<MatchingService.MatchResult> RunAsync()
    {
        var (org, _) = await SeedAsync();
        var svc = new MatchingService(_fx.ConnectionString,
            new SettingsService(_fx.ConnectionString, new DpapiProtector()), new AuditLog());
        return await svc.RunAsync(org, "MOCK", CancellationToken.None);
    }

    private async Task<T> ScalarAsync<T>(string sql, object? param = null)
    {
        await using var c = new SqlConnection(_fx.ConnectionString);
        await c.OpenAsync();
        return (await c.ExecuteScalarAsync<T>(sql, param))!; // brief 原文返回 T?（Dapper [MaybeNull]）→CS8603；查询必返标量，null 包容（已知 brief 缺陷同型）
    }

    [Fact]
    public async Task Credit_exact_audit_pass_unique_auto_confirms()
    {
        var sid = await SeedSupplierAsync("SD1");
        await SeedEntAsync("REF-SD1", "ENT-SD1");
        await SetAuditPassAsync("[\"审核通过\"]");
        var result = await RunAsync();
        Assert.Equal(1, result.SuppliersAutoConfirmed);
        Assert.Equal("AUTO_CONFIRMED", await ScalarAsync<string>("SELECT state FROM dbo.supplier_mapping WHERE supplier_id=@sid", new { sid }));
    }

    [Fact]
    public async Task Default_empty_pass_set_never_auto_confirms()
    {
        var sid = await SeedSupplierAsync("SD2");
        await SeedEntAsync("REF-SD2", "ENT-SD2");
        var result = await RunAsync(); // 未配置审核通过值（G-TOP：真实平台枚举未取证前不自动）
        Assert.Equal(0, result.SuppliersAutoConfirmed);
        Assert.Equal("CANDIDATE", await ScalarAsync<string>("SELECT state FROM dbo.supplier_mapping WHERE supplier_id=@sid", new { sid }));
    }

    [Fact]
    public async Task Multiple_credit_matches_conflict()
    {
        var sid = await SeedSupplierAsync("SD3");
        await SeedEntAsync("REF-SD3A", "ENT-SD3A");
        await SeedEntAsync("REF-SD3B", "ENT-SD3B");
        await SetAuditPassAsync("[\"审核通过\"]");
        await RunAsync();
        Assert.Equal("CONFLICT", await ScalarAsync<string>("SELECT state FROM dbo.supplier_mapping WHERE supplier_id=@sid", new { sid }));
    }

    [Fact]
    public async Task Name_only_is_candidate_never_auto()
    {
        var sid = await SeedSupplierAsync("SD4", name: "同名企业", credit: null);
        await SeedEntAsync("REF-SD4", "ENT-SD4", name: "同名企业", credit: "91999999MA9999999X");
        await SetAuditPassAsync("[\"审核通过\"]");
        await RunAsync();
        Assert.Equal("CANDIDATE", await ScalarAsync<string>("SELECT state FROM dbo.supplier_mapping WHERE supplier_id=@sid", new { sid }));
    }

    [Fact]
    public async Task Rename_new_version_mapping_stays()
    {
        var sid = await SeedSupplierAsync("SD5");
        var ev1 = await SeedEntAsync("REF-SD5", "ENT-SD5-V1", name: "旧名");
        await SetAuditPassAsync("[\"审核通过\"]");
        await RunAsync(); // AUTO_CONFIRMED → ev1
        await SeedEntAsync("REF-SD5", "ENT-SD5-V2", name: "新名"); // 改名→新版本行（AT-020）
        await RunAsync();
        Assert.Equal(ev1, await ScalarAsync<long>("SELECT enterprise_version_id FROM dbo.supplier_mapping WHERE supplier_id=@sid", new { sid }));
        Assert.Equal("AUTO_CONFIRMED", await ScalarAsync<string>("SELECT state FROM dbo.supplier_mapping WHERE supplier_id=@sid", new { sid }));
    }

    [Fact]
    public async Task Roles_are_independent()
    {
        var sid = await SeedSupplierAsync("SD6", role: "SHIPPER");
        await SeedEntAsync("REF-SD6", "ENT-SD6", role: "SUPPLIER");
        await SetAuditPassAsync("[\"审核通过\"]");
        await RunAsync();
        Assert.Equal("AUTO_CONFIRMED", await ScalarAsync<string>("SELECT state FROM dbo.supplier_mapping WHERE supplier_id=@sid", new { sid }));
        Assert.Equal("SHIPPER", await ScalarAsync<string>("SELECT role FROM dbo.supplier WHERE id=@sid", new { sid }));
        Assert.Equal("SUPPLIER", await ScalarAsync<string>("SELECT role FROM dbo.platform_enterprise_version WHERE ref_ent_id=N'REF-SD6'"));
    }

    [Fact]
    public async Task Locked_supplier_mapping_untouched()
    {
        var sid = await SeedSupplierAsync("SD7", credit: "91111111MA1111111X");
        var ev = await SeedEntAsync("REF-SD7", "ENT-SD7", credit: "91222222MA2222222X");
        var (org, _) = await SeedAsync();
        await using (var c = new SqlConnection(_fx.ConnectionString))
        {
            await c.OpenAsync();
            await c.ExecuteAsync(@"
INSERT INTO dbo.supplier_mapping(org_id,supplier_id,enterprise_version_id,state,is_locked,version)
VALUES(@org,@sid,@ev,N'MANUAL_CONFIRMED',1,5)", new { org, sid, ev });
        }
        await RunAsync();
        Assert.Equal("MANUAL_CONFIRMED", await ScalarAsync<string>("SELECT state FROM dbo.supplier_mapping WHERE supplier_id=@sid", new { sid }));
        Assert.Equal(5, await ScalarAsync<int>("SELECT version FROM dbo.supplier_mapping WHERE supplier_id=@sid", new { sid }));
    }

    private async Task<long> SeedShadowOrgAsync()
        => await ScalarAsync<long>(@"
IF NOT EXISTS (SELECT 1 FROM dbo.organization WHERE org_code = N'O2-FIX')
    INSERT INTO dbo.organization(org_code,name,is_active) VALUES(N'O2-FIX',N'影子组织',0);
SELECT id FROM dbo.organization WHERE org_code = N'O2-FIX'");

    private async Task ExecAsync(string sql, object? param = null)
    {
        await using var c = new SqlConnection(_fx.ConnectionString);
        await c.OpenAsync();
        await c.ExecuteAsync(sql, param);
    }

    [Fact]
    public async Task Fallback_unique_violation_never_downgrades_locked_confirmed()
    {
        // AT-015/MD-01 确定性接缝（与商品侧对称）：初始加载按 org 过滤，2601 兜底重选只按 supplier_id（无 org 过滤）。
        // 另一 org 下的锁定 MANUAL_CONFIRMED 行占据 UX_smap_pair/UX_smap_active_platform，
        // 本 org 自动匹配 INSERT AUTO_CONFIRMED 必撞 2601，兜底重选命中锁定行——基线强制 CONFLICT（RED），修复后整体跳过。
        var sid = await SeedSupplierAsync("SF1");
        var ev = await SeedEntAsync("REF-SF1", "ENT-SF1");
        await SetAuditPassAsync("[\"审核通过\"]");
        var shadowOrg = await SeedShadowOrgAsync();
        await ExecAsync(@"
INSERT INTO dbo.supplier_mapping(org_id,supplier_id,enterprise_version_id,state,is_locked,version)
VALUES(@org,@sid,@ev,N'MANUAL_CONFIRMED',1,5)", new { org = shadowOrg, sid, ev });
        var mapId = await ScalarAsync<long>("SELECT id FROM dbo.supplier_mapping WHERE supplier_id=@sid", new { sid });
        var result = await RunAsync();
        Assert.Equal("MANUAL_CONFIRMED", await ScalarAsync<string>("SELECT state FROM dbo.supplier_mapping WHERE id=@id", new { id = mapId }));
        Assert.Equal(5, await ScalarAsync<int>("SELECT version FROM dbo.supplier_mapping WHERE id=@id", new { id = mapId }));
        Assert.Equal(1, await ScalarAsync<int>("SELECT CAST(is_locked AS int) FROM dbo.supplier_mapping WHERE id=@id", new { id = mapId }));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.supplier_mapping_history WHERE mapping_id=@id", new { id = mapId }));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.supplier_mapping WHERE supplier_id=@sid AND state=N'AUTO_CONFIRMED'", new { sid }));
        Assert.Equal(0, result.SuppliersConflict); // 跳过降级→不产生 CONFLICT 计数
    }

    [Fact]
    public async Task Fallback_unique_violation_non_locked_still_conflicts()
    {
        // 回归钉住：非锁定/非有效行的 2601 兜底语义必须与修复前逐字一致（降级 CONFLICT + version+1 + 历史行 + 计数）。
        var sid = await SeedSupplierAsync("SF2");
        var ev = await SeedEntAsync("REF-SF2", "ENT-SF2");
        await SetAuditPassAsync("[\"审核通过\"]");
        var shadowOrg = await SeedShadowOrgAsync();
        await ExecAsync(@"
INSERT INTO dbo.supplier_mapping(org_id,supplier_id,enterprise_version_id,state,is_locked,version)
VALUES(@org,@sid,@ev,N'CONFLICT',0,3)", new { org = shadowOrg, sid, ev });
        var mapId = await ScalarAsync<long>("SELECT id FROM dbo.supplier_mapping WHERE supplier_id=@sid", new { sid });
        var result = await RunAsync();
        Assert.Equal("CONFLICT", await ScalarAsync<string>("SELECT state FROM dbo.supplier_mapping WHERE id=@id", new { id = mapId }));
        Assert.Equal(4, await ScalarAsync<int>("SELECT version FROM dbo.supplier_mapping WHERE id=@id", new { id = mapId }));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.supplier_mapping_history WHERE mapping_id=@id AND state=N'CONFLICT'", new { id = mapId }));
        Assert.Equal(1, result.SuppliersConflict);
    }
}
