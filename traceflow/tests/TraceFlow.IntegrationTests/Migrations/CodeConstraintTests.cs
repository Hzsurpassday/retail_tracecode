using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace TraceFlow.IntegrationTests.Migrations;

public class CodeConstraintTests : IClassFixture<Sql2008Fixture>
{
    private readonly Sql2008Fixture _fx;
    public CodeConstraintTests(Sql2008Fixture fx) => _fx = fx;

    // 与 brief 种子代码的两处最小偏差（DDL 脚本保持 verbatim）：
    // 1) brief 硬编码 org_id=1，但每类独立测试库的 organization 为空（IDENTITY 未种子），直接插入会触发 FK 547。
    //    沿用 Task 6 模式：幂等共享门店（IF NOT EXISTS + SELECT，0001 全库仅一个 active 门店），org_id 参数化。
    // 2) brief 的 format 种子 N'Supported8Prefix20' 长 18 字符，超出 0005 verbatim DDL 的 format nvarchar(16)
    //    （插入报 2628/8152 截断）。format 列无 CHECK，枚举名→存储 token 的映射属 M1 应用层职责，
    //    测试改用 ≤16 字符的 N'Supported'。
    private static async Task<long> SeedOrgAsync(SqlConnection c)
        => await c.ExecuteScalarAsync<long>(@"IF NOT EXISTS (SELECT 1 FROM dbo.organization WHERE org_code = N'O1')
                                              INSERT INTO dbo.organization(org_code,name) VALUES(N'O1',N'店');
                                            SELECT id FROM dbo.organization WHERE org_code = N'O1'");

    private static async Task<long> SeedCodeAsync(SqlConnection c, string code)
        => await c.ExecuteScalarAsync<long>(
            "INSERT INTO dbo.trace_code(code_normalized,code_raw,format) OUTPUT inserted.id VALUES(@code,@code,N'Supported')", new { code });

    [Fact]
    public async Task Active_claim_unique_per_store_env_code_scope()
    {
        await using var c = await _fx.OpenAsync();
        var orgId = await SeedOrgAsync(c);
        var codeId = await SeedCodeAsync(c, "81234567890123456701");
        await c.ExecuteAsync("INSERT INTO dbo.code_claim(org_id,env,code_id,event_scope,claim_state) VALUES(@orgId,N'LIVE',@codeId,N'OUTBOUND_FLOW',N'ACTIVE')", new { orgId, codeId });
        var ex = await Assert.ThrowsAsync<SqlException>(() => c.ExecuteAsync(
            "INSERT INTO dbo.code_claim(org_id,env,code_id,event_scope,claim_state) VALUES(@orgId,N'LIVE',@codeId,N'OUTBOUND_FLOW',N'ACTIVE')", new { orgId, codeId }));
        Assert.True(ex.Number is 2601 or 2627);
    }

    [Fact]
    public async Task Mock_and_live_claims_isolated()
    {
        await using var c = await _fx.OpenAsync();
        var orgId = await SeedOrgAsync(c);
        var codeId = await SeedCodeAsync(c, "81234567890123456702");
        await c.ExecuteAsync("INSERT INTO dbo.code_claim(org_id,env,code_id,event_scope,claim_state) VALUES(@orgId,N'LIVE',@codeId,N'OUTBOUND_FLOW',N'ACTIVE')", new { orgId, codeId });
        await c.ExecuteAsync("INSERT INTO dbo.code_claim(org_id,env,code_id,event_scope,claim_state) VALUES(@orgId,N'MOCK',@codeId,N'OUTBOUND_FLOW',N'ACTIVE')", new { orgId, codeId }); // 不冲突
        Assert.Equal(2, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.code_claim WHERE code_id=@codeId", new { codeId }));
    }

    [Fact]
    public async Task Released_claim_allows_new_active_claim()
    {
        await using var c = await _fx.OpenAsync();
        var orgId = await SeedOrgAsync(c);
        var codeId = await SeedCodeAsync(c, "81234567890123456703");
        await c.ExecuteAsync("INSERT INTO dbo.code_claim(org_id,env,code_id,event_scope,claim_state) VALUES(@orgId,N'LIVE',@codeId,N'OUTBOUND_FLOW',N'RELEASED')", new { orgId, codeId });
        await c.ExecuteAsync("INSERT INTO dbo.code_claim(org_id,env,code_id,event_scope,claim_state) VALUES(@orgId,N'LIVE',@codeId,N'OUTBOUND_FLOW',N'ACTIVE')", new { orgId, codeId });
    }

    [Fact]
    public async Task Same_code_multiple_events_allowed_but_event_code_unique()
    {
        await using var c = await _fx.OpenAsync();
        var orgId = await SeedOrgAsync(c);
        var codeId = await SeedCodeAsync(c, "81234567890123456704");
        await c.ExecuteAsync("INSERT INTO dbo.code_event(org_id,env,code_id,event_type,direction,source_line_id) VALUES(@orgId,N'LIVE',@codeId,N'PURCHASE_IN',N'IN',1)", new { orgId, codeId });
        await c.ExecuteAsync("INSERT INTO dbo.code_event(org_id,env,code_id,event_type,direction,source_line_id) VALUES(@orgId,N'LIVE',@codeId,N'SALE',N'OUT',2)", new { orgId, codeId });
        var ex = await Assert.ThrowsAsync<SqlException>(() => c.ExecuteAsync(
            "INSERT INTO dbo.code_event(org_id,env,code_id,event_type,direction,source_line_id) VALUES(@orgId,N'LIVE',@codeId,N'SALE',N'OUT',2)", new { orgId, codeId }));
        Assert.True(ex.Number is 2601 or 2627);
    }

    [Fact]
    public async Task Same_bill_no_different_upstream_subjects_not_merged()
    {
        await using var c = await _fx.OpenAsync();
        var orgId = await SeedOrgAsync(c);
        await c.ExecuteAsync("INSERT INTO dbo.upstream_bill(org_id,env,upstream_ref_ent_id,platform_bill_id,bill_no) VALUES(@orgId,N'LIVE',N'E1',N'B1',N'NO-1')", new { orgId });
        await c.ExecuteAsync("INSERT INTO dbo.upstream_bill(org_id,env,upstream_ref_ent_id,platform_bill_id,bill_no) VALUES(@orgId,N'LIVE',N'E2',N'B1',N'NO-1')", new { orgId });
        Assert.Equal(2, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.upstream_bill WHERE bill_no=N'NO-1'"));
    }
}
