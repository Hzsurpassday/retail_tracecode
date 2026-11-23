using Dapper;
using TraceFlow.IntegrationTests.Sources; // brief 缺失此 using（TestSeed 所在命名空间）；与 MatchingServiceTests 标注模式一致
using Xunit;

namespace TraceFlow.IntegrationTests.Migrations;

public class Migration0008Tests : IClassFixture<Sql2008Fixture>
{
    private readonly Sql2008Fixture _fx;
    public Migration0008Tests(Sql2008Fixture fx) => _fx = fx;

    [Fact]
    public async Task M2_tables_exist_with_expected_columns()
    {
        await using var c = await _fx.OpenAsync();
        var n = await c.ExecuteScalarAsync<int>(@"
SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES
WHERE TABLE_NAME IN ('inbound_match','top_rate_budget','top_channel_pause')");
        Assert.Equal(3, n);
        var dwbh = await c.ExecuteScalarAsync<int>(@"
SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_NAME='inbound_match' AND COLUMN_NAME='supplier_dwbh' AND CHARACTER_MAXIMUM_LENGTH=64");
        Assert.Equal(1, dwbh);
        var win = await c.ExecuteScalarAsync<int>(@"
SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_NAME='download_progress' AND COLUMN_NAME IN ('window_start','window_end') AND DATA_TYPE='datetime2'");
        Assert.Equal(2, win);
    }

    [Fact]
    public async Task Inbound_match_unique_per_snapshot_and_state_check()
    {
        await using var c = await _fx.OpenAsync();
        var (orgId, sysId) = await TestSeed.EnsureAsync(c);
        var snapId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.source_snapshot(org_id,source_system_id,source_document_key,document_type,version,content_hash,raw_header)
OUTPUT inserted.id
VALUES(@org,@sys,N'M2-MATCH-1',N'PURCHASE_IN',1,REPLICATE('b',64),N'{}')", new { org = orgId, sys = sysId });
        await c.ExecuteAsync(@"
INSERT INTO dbo.inbound_match(org_id,env,source_snapshot_id,supplier_dwbh,match_state)
VALUES(@org,N'MOCK',@snap,N'DW0001',N'UNMATCHED')", new { org = orgId, snap = snapId });
        // 同快照重复→唯一约束拒绝
        await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => c.ExecuteAsync(@"
INSERT INTO dbo.inbound_match(org_id,env,source_snapshot_id,match_state)
VALUES(@org,N'MOCK',@snap,N'COMPLETE')", new { org = orgId, snap = snapId }));
        // 非法 match_state→CHECK 拒绝
        await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => c.ExecuteAsync(@"
INSERT INTO dbo.inbound_match(org_id,env,source_snapshot_id,match_state)
VALUES(@org,N'MOCK',@snap,N'MATCHED')", new { org = orgId, snap = snapId }));
    }

    [Fact]
    public async Task Rate_budget_unique_per_env_key_and_pause_filtered_unique()
    {
        await using var c = await _fx.OpenAsync();
        await c.ExecuteAsync(@"
INSERT INTO dbo.top_rate_budget(env,budget_key) VALUES(N'MOCK',N'appkey:total')");
        await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => c.ExecuteAsync(@"
INSERT INTO dbo.top_rate_budget(env,budget_key) VALUES(N'MOCK',N'appkey:total')"));
        // LIVE/MOCK 同键互不冲突（硬隔离）
        await c.ExecuteAsync(@"
INSERT INTO dbo.top_rate_budget(env,budget_key) VALUES(N'LIVE',N'appkey:total')");
        await c.ExecuteAsync(@"
INSERT INTO dbo.top_channel_pause(env,scope_key,reason,paused_by) VALUES(N'MOCK',N'method:listupout',N'auth',N'test')");
        await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => c.ExecuteAsync(@"
INSERT INTO dbo.top_channel_pause(env,scope_key,reason,paused_by) VALUES(N'MOCK',N'method:listupout',N'auth2',N'test')"));
        // 解除后允许再次暂停（过滤唯一索引只约束 ACTIVE）
        await c.ExecuteAsync("UPDATE dbo.top_channel_pause SET is_active=0 WHERE env=N'MOCK' AND scope_key=N'method:listupout'");
        await c.ExecuteAsync(@"
INSERT INTO dbo.top_channel_pause(env,scope_key,reason,paused_by) VALUES(N'MOCK',N'method:listupout',N'auth3',N'test')");
    }

    [Fact]
    public async Task Task_and_part_state_checks_accept_cancelled()
    {
        await using var c = await _fx.OpenAsync();
        var (orgId, _) = await TestSeed.EnsureAsync(c);
        var taskId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upload_task(org_id,env,event_key,task_type,source_hash,payload_hash,state)
OUTPUT inserted.id
VALUES(@org,N'MOCK',N'KSOA:M2-CANCEL:PURCHASE_IN',N'PURCHASE_IN_102',REPLICATE('c',64),REPLICATE('d',64),N'Cancelled')",
            new { org = orgId });
        Assert.True(taskId > 0);
        await c.ExecuteAsync(@"
INSERT INTO dbo.upload_part(task_id,org_id,env,part_index,bill_code,payload_json,payload_hash,code_set_hash,state)
VALUES(@t,@org,N'MOCK',0,N'TFM2CANCEL',N'{}',REPLICATE('e',64),REPLICATE('f',64),N'Cancelled')",
            new { t = taskId, org = orgId });
        // 非法状态仍被拒绝
        await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => c.ExecuteAsync(@"
INSERT INTO dbo.upload_task(org_id,env,event_key,task_type,source_hash,payload_hash,state)
VALUES(@org,N'MOCK',N'KSOA:M2-BAD:PURCHASE_IN',N'PURCHASE_IN_102',REPLICATE('c',64),REPLICATE('d',64),N'Deleted')",
            new { org = orgId }));
    }

    [Fact]
    public async Task Migration_0008_recorded_with_checksum()
    {
        await using var c = await _fx.OpenAsync();
        var ok = await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.schema_version WHERE version=8 AND success=1 AND name LIKE '0008_%'");
        Assert.Equal(1, ok);
    }
}
