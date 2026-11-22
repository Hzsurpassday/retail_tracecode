using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace TraceFlow.IntegrationTests.Migrations;

public class UploadConstraintTests : IClassFixture<Sql2008Fixture>
{
    private readonly Sql2008Fixture _fx;
    public UploadConstraintTests(Sql2008Fixture fx) => _fx = fx;

    // 与 brief 种子代码的最小偏差（DDL 脚本保持 verbatim）：
    // 1) brief 硬编码 org_id=1，但每类独立测试库的 organization 为空（IDENTITY 未种子），直接插入触发 FK 547。
    //    沿用 Task 6/7 模式：幂等共享门店（IF NOT EXISTS + SELECT，0001 全库仅一个 active 门店），org_id 参数化。
    // 2) brief 的 upload_part 插入遗漏 payload_json，而 0006 verbatim DDL 中该列 NOT NULL（插入报 515）。
    //    测试补 N'{}' 占位载荷。
    // 3) brief 的 format 种子 N'Supported8Prefix20' 长 18 字符，超出 0005 verbatim DDL 的 format nvarchar(16)
    //    （插入报 2628/8152 截断）。沿用 Task 7 约定改用 N'Supported'（枚举名→存储 token 映射属 M1 应用层职责）。
    // 4) brief 的 Bill_code_unique_and_part_index_unique 仅断言 bill_code 唯一；按测试名补 part_index 重复断言
    //    （同 task 同 part_index、不同 bill_code → UQ_part_index 2601/2627）。
    private static async Task<long> SeedOrgAsync(SqlConnection c)
        => await c.ExecuteScalarAsync<long>(@"IF NOT EXISTS (SELECT 1 FROM dbo.organization WHERE org_code = N'O1')
                                              INSERT INTO dbo.organization(org_code,name) VALUES(N'O1',N'店');
                                            SELECT id FROM dbo.organization WHERE org_code = N'O1'");

    [Fact]
    public async Task Business_event_key_unique()
    {
        await using var c = await _fx.OpenAsync();
        var orgId = await SeedOrgAsync(c);
        await c.ExecuteAsync(@"INSERT INTO dbo.upload_task(org_id,env,event_key,task_type,source_hash,payload_hash,state)
                               VALUES(@orgId,N'LIVE',N'O1|KSOA|L1|SALE',N'RETAIL_321',REPLICATE('a',64),REPLICATE('b',64),N'BLOCKED')", new { orgId });
        var ex = await Assert.ThrowsAsync<SqlException>(() => c.ExecuteAsync(
            @"INSERT INTO dbo.upload_task(org_id,env,event_key,task_type,source_hash,payload_hash,state)
              VALUES(@orgId,N'LIVE',N'O1|KSOA|L1|SALE',N'RETAIL_321',REPLICATE('c',64),REPLICATE('d',64),N'BLOCKED')", new { orgId }));
        Assert.True(ex.Number is 2601 or 2627);
    }

    [Fact]
    public async Task Mock_task_with_same_event_key_isolated()
    {
        await using var c = await _fx.OpenAsync();
        var orgId = await SeedOrgAsync(c);
        await c.ExecuteAsync(@"INSERT INTO dbo.upload_task(org_id,env,event_key,task_type,source_hash,payload_hash,state)
                               VALUES(@orgId,N'LIVE',N'O1|KSOA|L2|SALE',N'RETAIL_321',REPLICATE('a',64),REPLICATE('b',64),N'BLOCKED')", new { orgId });
        await c.ExecuteAsync(@"INSERT INTO dbo.upload_task(org_id,env,event_key,task_type,source_hash,payload_hash,state)
                               VALUES(@orgId,N'MOCK',N'O1|KSOA|L2|SALE',N'RETAIL_321',REPLICATE('a',64),REPLICATE('b',64),N'BLOCKED')", new { orgId });
    }

    [Fact]
    public async Task Bill_code_unique_and_part_index_unique()
    {
        await using var c = await _fx.OpenAsync();
        var orgId = await SeedOrgAsync(c);
        var taskId = await c.ExecuteScalarAsync<long>(@"INSERT INTO dbo.upload_task(org_id,env,event_key,task_type,source_hash,payload_hash,state)
            OUTPUT inserted.id VALUES(@orgId,N'LIVE',N'O1|KSOA|L3|SALE',N'RETAIL_321',REPLICATE('a',64),REPLICATE('b',64),N'BLOCKED')", new { orgId });
        await c.ExecuteAsync(@"INSERT INTO dbo.upload_part(task_id,org_id,env,part_index,bill_code,payload_json,payload_hash,code_set_hash,state)
                               VALUES(@taskId,@orgId,N'LIVE',0,N'TF1',N'{}',REPLICATE('b',64),REPLICATE('c',64),N'BLOCKED')", new { taskId, orgId });
        var ex = await Assert.ThrowsAsync<SqlException>(() => c.ExecuteAsync(
            @"INSERT INTO dbo.upload_part(task_id,org_id,env,part_index,bill_code,payload_json,payload_hash,code_set_hash,state)
              VALUES(@taskId,@orgId,N'LIVE',1,N'TF1',N'{}',REPLICATE('b',64),REPLICATE('c',64),N'BLOCKED')", new { taskId, orgId }));
        Assert.True(ex.Number is 2601 or 2627); // UQ_part_billcode（org+env+bill_code，IDEM-02）
        var ex2 = await Assert.ThrowsAsync<SqlException>(() => c.ExecuteAsync(
            @"INSERT INTO dbo.upload_part(task_id,org_id,env,part_index,bill_code,payload_json,payload_hash,code_set_hash,state)
              VALUES(@taskId,@orgId,N'LIVE',0,N'TF1-DUP',N'{}',REPLICATE('b',64),REPLICATE('c',64),N'BLOCKED')", new { taskId, orgId }));
        Assert.True(ex2.Number is 2601 or 2627); // UQ_part_index（task+part_index）
    }

    [Fact]
    public async Task Code_result_unknown_allowed()
    {
        await using var c = await _fx.OpenAsync();
        var orgId = await SeedOrgAsync(c);
        var taskId = await c.ExecuteScalarAsync<long>(@"INSERT INTO dbo.upload_task(org_id,env,event_key,task_type,source_hash,payload_hash,state)
            OUTPUT inserted.id VALUES(@orgId,N'LIVE',N'O1|KSOA|L4|SALE',N'RETAIL_321',REPLICATE('a',64),REPLICATE('b',64),N'BLOCKED')", new { orgId });
        var partId = await c.ExecuteScalarAsync<long>(@"INSERT INTO dbo.upload_part(task_id,org_id,env,part_index,bill_code,payload_json,payload_hash,code_set_hash,state)
            OUTPUT inserted.id VALUES(@taskId,@orgId,N'LIVE',0,N'TF2',N'{}',REPLICATE('b',64),REPLICATE('c',64),N'BLOCKED')", new { taskId, orgId });
        var codeId = await c.ExecuteScalarAsync<long>("INSERT INTO dbo.trace_code(code_normalized,code_raw,format) OUTPUT inserted.id VALUES(N'81234567890123456705',N'81234567890123456705',N'Supported')");
        var attemptId = await c.ExecuteScalarAsync<long>(@"INSERT INTO dbo.upload_attempt(task_id,part_id,attempt_no,attempt_kind,env,fencing_token)
            OUTPUT inserted.id VALUES(@taskId,@partId,1,N'QUERY',N'LIVE',1)", new { taskId, partId });
        await c.ExecuteAsync(@"INSERT INTO dbo.code_result(task_id,part_id,code_id,result,attempt_id)
                               VALUES(@taskId,@partId,@codeId,N'UNKNOWN',@attemptId)", new { taskId, partId, codeId, attemptId });
    }
}