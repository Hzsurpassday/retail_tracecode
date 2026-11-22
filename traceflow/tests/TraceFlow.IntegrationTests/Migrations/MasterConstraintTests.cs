using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace TraceFlow.IntegrationTests.Migrations;

public class MasterConstraintTests : IClassFixture<Sql2008Fixture>
{
    private readonly Sql2008Fixture _fx;
    public MasterConstraintTests(Sql2008Fixture fx) => _fx = fx;

    // 说明：IClassFixture 为整类共享一个测试库。0001 的 UX_organization_single_active 限定全库仅一个 active 门店，
    // 故门店幂等共享（org_id=1）；来源系统/商品/平台药品用 tag 后缀按用例隔离，
    // 否则固定编码（KSOA/SP1/D1）在第二个用例即违反唯一约束，且用例间的有效映射会互相污染过滤唯一索引。
    // platform_drug.source_kind 为 NOT NULL 无默认值，种子必须显式提供。
    private static async Task<(long orgId, long sysId, long p1, long p2, long drugId)> SeedAsync(SqlConnection c, string tag)
    {
        var orgId = await c.ExecuteScalarAsync<long>(@"IF NOT EXISTS (SELECT 1 FROM dbo.organization WHERE org_code = N'O1')
                                                        INSERT INTO dbo.organization(org_code,name) VALUES(N'O1',N'店');
                                                      SELECT id FROM dbo.organization WHERE org_code = N'O1'");
        var sysId = await c.ExecuteScalarAsync<long>("INSERT INTO dbo.source_system(system_code,name,adapter_contract_version) OUTPUT inserted.id VALUES(@code,N'KSOA',N'v0')", new { code = "KSOA" + tag });
        var p1 = await c.ExecuteScalarAsync<long>("INSERT INTO dbo.product(org_id,source_system_id,spid,name) OUTPUT inserted.id VALUES(@orgId,@sysId,@spid,N'药1')", new { orgId, sysId, spid = "SP1" + tag });
        var p2 = await c.ExecuteScalarAsync<long>("INSERT INTO dbo.product(org_id,source_system_id,spid,name) OUTPUT inserted.id VALUES(@orgId,@sysId,@spid,N'药2')", new { orgId, sysId, spid = "SP2" + tag });
        var drugId = await c.ExecuteScalarAsync<long>("INSERT INTO dbo.platform_drug(platform_drug_id,source_kind) OUTPUT inserted.id VALUES(@code,N'PLATFORM')", new { code = "D1" + tag });
        return (orgId, sysId, p1, p2, drugId);
    }

    [Fact]
    public async Task Two_active_mappings_to_same_platform_drug_rejected()
    {
        await using var c = await _fx.OpenAsync();
        var (orgId, _, p1, p2, drugId) = await SeedAsync(c, "A");
        await c.ExecuteAsync("INSERT INTO dbo.product_mapping(org_id,product_id,platform_drug_id,state) VALUES(@orgId,@p1,@drugId,N'MANUAL_CONFIRMED')", new { orgId, p1, drugId });
        var ex = await Assert.ThrowsAsync<SqlException>(() => c.ExecuteAsync(
            "INSERT INTO dbo.product_mapping(org_id,product_id,platform_drug_id,state) VALUES(@orgId,@p2,@drugId,N'AUTO_CONFIRMED')", new { orgId, p2, drugId }));
        Assert.True(ex.Number is 2601 or 2627);
    }

    [Fact]
    public async Task Two_active_mappings_for_same_local_product_rejected()
    {
        await using var c = await _fx.OpenAsync();
        var (orgId, _, p1, _, drugId) = await SeedAsync(c, "B");
        var drug2 = await c.ExecuteScalarAsync<long>("INSERT INTO dbo.platform_drug(platform_drug_id,source_kind) OUTPUT inserted.id VALUES(N'D2B',N'PLATFORM')");
        await c.ExecuteAsync("INSERT INTO dbo.product_mapping(org_id,product_id,platform_drug_id,state) VALUES(@orgId,@p1,@drugId,N'MANUAL_CONFIRMED')", new { orgId, p1, drugId });
        await c.ExecuteAsync("INSERT INTO dbo.product_mapping(org_id,product_id,platform_drug_id,state) VALUES(@orgId,@p1,@drug2,N'CANDIDATE')", new { orgId, p1, drug2 }); // 非有效态允许
        var ex = await Assert.ThrowsAsync<SqlException>(() => c.ExecuteAsync(
            "INSERT INTO dbo.product_mapping(org_id,product_id,platform_drug_id,state) VALUES(@orgId,@p1,@drug2,N'MANUAL_CONFIRMED')", new { orgId, p1, drug2 }));
        Assert.True(ex.Number is 2601 or 2627);
    }

    [Fact]
    public async Task Snapshot_version_unique_and_history_appendable()
    {
        await using var c = await _fx.OpenAsync();
        var (orgId, sysId, _, _, _) = await SeedAsync(c, "C");
        await c.ExecuteAsync(@"INSERT INTO dbo.source_snapshot(org_id,source_system_id,source_document_key,document_type,version,content_hash)
                               VALUES(@orgId,@sysId,N'L1',N'SALE',1,REPLICATE('a',64))", new { orgId, sysId });
        await c.ExecuteAsync(@"INSERT INTO dbo.source_snapshot(org_id,source_system_id,source_document_key,document_type,version,content_hash)
                               VALUES(@orgId,@sysId,N'L1',N'SALE',2,REPLICATE('b',64))", new { orgId, sysId });
        var ex = await Assert.ThrowsAsync<SqlException>(() => c.ExecuteAsync(
            @"INSERT INTO dbo.source_snapshot(org_id,source_system_id,source_document_key,document_type,version,content_hash)
              VALUES(@orgId,@sysId,N'L1',N'SALE',1,REPLICATE('c',64))", new { orgId, sysId }));
        Assert.True(ex.Number is 2601 or 2627);
    }
}