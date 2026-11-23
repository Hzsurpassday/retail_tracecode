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
public class MatchingServiceTests : IAsyncLifetime
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

    private async Task<long> SeedProductAsync(string spid, string? approval = "国药准字H11111111",
        string? manufacturer = "华北制药", string? form = "胶囊剂", string? strength = "0.25g", string? spec = "24粒")
    {
        var (org, sys) = await SeedAsync();
        await using var c = new SqlConnection(_fx.ConnectionString);
        await c.OpenAsync();
        return await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.product(org_id,source_system_id,spid,name,approval_no_norm,manufacturer_identity,dosage_form,strength,package_spec)
OUTPUT inserted.id VALUES(@org,@sys,@spid,@name,@an,@mi,@df,@st,@ps)",
            new { org, sys, spid, name = $"商品{spid}", an = approval, mi = manufacturer, df = form, st = strength, ps = spec });
    }

    private async Task<long> SeedDrugAsync(string pdid, string? approval = "国药准字H11111111",
        string? manufacturer = "华北制药", string? form = "胶囊剂", string? strength = "0.25g", string? spec = "24粒")
    {
        await using var c = new SqlConnection(_fx.ConnectionString);
        await c.OpenAsync();
        return await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.platform_drug(platform_drug_id,approval_no_norm,manufacturer_identity,dosage_form,strength,package_spec,source_kind)
OUTPUT inserted.id VALUES(@pdid,@an,@mi,@df,@st,@ps,N'MANUAL')",
            new { pdid, an = approval, mi = manufacturer, df = form, st = strength, ps = spec });
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

    private async Task ExecAsync(string sql, object? param = null)
    {
        await using var c = new SqlConnection(_fx.ConnectionString);
        await c.OpenAsync();
        await c.ExecuteAsync(sql, param);
    }

    [Fact]
    public async Task Full_match_bidirectional_unique_auto_confirms()
    {
        var pid = await SeedProductAsync("MP1");
        await SeedDrugAsync("PD-M1");
        var result = await RunAsync();
        Assert.Equal(1, result.ProductsAutoConfirmed);
        Assert.Equal("AUTO_CONFIRMED", await ScalarAsync<string>("SELECT state FROM dbo.product_mapping WHERE product_id=@pid", new { pid }));
        Assert.Equal("match-1", await ScalarAsync<string>("SELECT rule_version FROM dbo.product_mapping WHERE product_id=@pid", new { pid }));
        Assert.Contains("\"bidirectionalUnique\":true", await ScalarAsync<string>("SELECT evidence_json FROM dbo.product_mapping WHERE product_id=@pid", new { pid }));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.product_mapping_history WHERE product_id=@pid AND state=N'AUTO_CONFIRMED'", new { pid }));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.audit_log WHERE action=N'master.match.auto'"));
    }

    [Fact]
    public async Task Missing_field_never_wildcards()
    {
        var pid = await SeedProductAsync("MP2", manufacturer: null, form: null, strength: null, spec: null);
        await SeedDrugAsync("PD-M2");
        var result = await RunAsync();
        Assert.Equal(0, result.ProductsAutoConfirmed);
        Assert.Equal("CANDIDATE", await ScalarAsync<string>("SELECT state FROM dbo.product_mapping WHERE product_id=@pid", new { pid }));
    }

    [Fact]
    public async Task Field_conflict_marks_conflict()
    {
        var pid = await SeedProductAsync("MP3", strength: "0.5g");
        await SeedDrugAsync("PD-M3");
        await RunAsync();
        Assert.Equal("CONFLICT", await ScalarAsync<string>("SELECT state FROM dbo.product_mapping WHERE product_id=@pid", new { pid }));
    }

    [Fact]
    public async Task Two_full_candidates_conflict()
    {
        var pid = await SeedProductAsync("MP4");
        await SeedDrugAsync("PD-M4A");
        await SeedDrugAsync("PD-M4B");
        await RunAsync();
        Assert.Equal("CONFLICT", await ScalarAsync<string>("SELECT state FROM dbo.product_mapping WHERE product_id=@pid", new { pid }));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.product_mapping WHERE state IN (N'AUTO_CONFIRMED',N'MANUAL_CONFIRMED')"));
    }

    [Fact]
    public async Task Bidirectional_unique_blocks_identical_twins()
    {
        var p1 = await SeedProductAsync("MP5A");
        var p2 = await SeedProductAsync("MP5B"); // 五项完全相同的双胞胎
        await SeedDrugAsync("PD-M5");
        await RunAsync();
        // 无法区分→双方 CONFLICT，平台药不被任何一方独占
        Assert.Equal("CONFLICT", await ScalarAsync<string>("SELECT state FROM dbo.product_mapping WHERE product_id=@pid", new { pid = p1 }));
        Assert.Equal("CONFLICT", await ScalarAsync<string>("SELECT state FROM dbo.product_mapping WHERE product_id=@pid", new { pid = p2 }));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.product_mapping WHERE state IN (N'AUTO_CONFIRMED',N'MANUAL_CONFIRMED')"));
    }

    [Fact]
    public async Task Locked_mapping_untouched()
    {
        var pid = await SeedProductAsync("MP6", strength: "0.25g");
        var drugId = await SeedDrugAsync("PD-M6", strength: "9.99g"); // 身份已不同
        var (org, _) = await SeedAsync();
        await ExecAsync(@"
INSERT INTO dbo.product_mapping(org_id,product_id,platform_drug_id,state,is_locked,version)
VALUES(@org,@pid,@drug,N'MANUAL_CONFIRMED',1,7)", new { org, pid, drug = drugId });
        await RunAsync();
        Assert.Equal("MANUAL_CONFIRMED", await ScalarAsync<string>("SELECT state FROM dbo.product_mapping WHERE product_id=@pid", new { pid }));
        Assert.Equal(7, await ScalarAsync<int>("SELECT version FROM dbo.product_mapping WHERE product_id=@pid", new { pid }));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.product_mapping_history WHERE product_id=@pid", new { pid }));
    }

    [Fact]
    public async Task Active_mapping_revalidation_detects_platform_change()
    {
        var pid = await SeedProductAsync("MP7");
        var drugId = await SeedDrugAsync("PD-M7");
        await RunAsync(); // AUTO_CONFIRMED
        await ExecAsync("UPDATE dbo.platform_drug SET strength=N'9.99g' WHERE id=@id", new { id = drugId });
        var result = await RunAsync();
        Assert.Equal(1, result.ProductsReviewRequired);
        Assert.Equal("REVIEW_REQUIRED", await ScalarAsync<string>("SELECT state FROM dbo.product_mapping WHERE product_id=@pid", new { pid }));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.product_mapping_history WHERE product_id=@pid AND state=N'REVIEW_REQUIRED'", new { pid }));
    }

    [Fact]
    public async Task No_candidates_creates_no_row()
    {
        var pid = await SeedProductAsync("MP8", approval: "国药准字H00000000");
        await SeedDrugAsync("PD-M8");
        var result = await RunAsync();
        Assert.Equal(1, result.ProductsUnresolved);
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.product_mapping WHERE product_id=@pid", new { pid }));
    }

    [Fact]
    public async Task Prefix_index_rebuild_and_conflict()
    {
        await SeedProductAsync("MP9A");
        await SeedProductAsync("MP9B", approval: "国药准字H22222222");
        var d1 = await SeedDrugAsync("PD-M9A");
        var d2 = await SeedDrugAsync("PD-M9B", approval: "国药准字H22222222");
        var (org, sys) = await SeedAsync();
        await RunAsync(); // 两个 AUTO_CONFIRMED
        // 两个 S8P20 来源码共享前 7 位 8111111，分属两个已映射商品
        await ExecAsync(@"
INSERT INTO dbo.source_snapshot(org_id,source_system_id,source_document_key,document_type,version,content_hash,completeness_status)
OUTPUT inserted.id VALUES(@org,@sys,N'PM-DOC-1',N'sale',1,REPLICATE(N'a',64),N'COMPLETE')", new { org, sys });
        var snapId = await ScalarAsync<long>("SELECT id FROM dbo.source_snapshot WHERE source_document_key=N'PM-DOC-1'");
        await ExecAsync(@"
INSERT INTO dbo.source_line(snapshot_id,source_line_key,product_source_id,quantity_original,line_hash,event_kind,trace_code_normalized,code_format)
VALUES(@snap,N'1',N'MP9A',1,REPLICATE(N'b',64),N'SALE',N'81111112222333344445',N'S8P20');
INSERT INTO dbo.source_line(snapshot_id,source_line_key,product_source_id,quantity_original,line_hash,event_kind,trace_code_normalized,code_format)
VALUES(@snap,N'2',N'MP9B',1,REPLICATE(N'c',64),N'SALE',N'81111119999888877776',N'S8P20');", new { snap = snapId });
        var result = await RunAsync();
        Assert.Equal(2, result.PrefixRows);
        Assert.Equal(1, result.PrefixConflicts); // 一个冲突前缀（两行）
        Assert.Equal(2, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.drug_prefix_index WHERE prefix=N'8111111' AND conflict_state=N'CONFLICT'"));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.drug_prefix_index WHERE prefix=N'8111111' AND platform_drug_id=@d", new { d = d1 }));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.drug_prefix_index WHERE prefix=N'8111111' AND platform_drug_id=@d", new { d = d2 }));
    }
}
