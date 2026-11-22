using Dapper;
using TraceFlow.Domain.Codes;
using Xunit;

namespace TraceFlow.IntegrationTests.Migrations;

public class Migration0007Tests : IClassFixture<Sql2008Fixture>
{
    private readonly Sql2008Fixture _fx;
    public Migration0007Tests(Sql2008Fixture fx) => _fx = fx;

    [Fact]
    public async Task Format_columns_widened_and_default_token()
    {
        await using var c = await _fx.OpenAsync();
        var len = await c.ExecuteScalarAsync<int>(@"
SELECT CHARACTER_MAXIMUM_LENGTH FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_NAME='source_line' AND COLUMN_NAME='code_format'");
        Assert.Equal(32, len);
        var len2 = await c.ExecuteScalarAsync<int>(@"
SELECT CHARACTER_MAXIMUM_LENGTH FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_NAME='trace_code' AND COLUMN_NAME='format'");
        Assert.Equal(32, len2);
        // 全枚举名（18 字符）现在也能放下；token 写入/读回一致
        await c.ExecuteAsync("INSERT INTO dbo.trace_code(code_normalized,code_raw,format) VALUES(N'81234567890123456789',N'81234567890123456789',@f)",
            new { f = CodeFormatStorage.ToToken(CodeFormat.Supported8Prefix20) });
        var stored = await c.ExecuteScalarAsync<string>("SELECT format FROM dbo.trace_code WHERE code_normalized=N'81234567890123456789'");
        Assert.Equal(CodeFormat.Supported8Prefix20, CodeFormatStorage.FromToken(stored!));
    }

    [Fact]
    public async Task Snapshot_stabilization_columns_exist()
    {
        await using var c = await _fx.OpenAsync();
        var n = await c.ExecuteScalarAsync<int>(@"
SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_NAME='source_snapshot' AND COLUMN_NAME IN ('stabilize_rounds','last_round_at')");
        Assert.Equal(2, n);
    }

    [Fact]
    public async Task Idempotency_unique_now_user_scoped()
    {
        await using var c = await _fx.OpenAsync();
        // 同键不同用户允许两条（M0 携带项 3：跨用户重放隔离）
        await c.ExecuteAsync(@"
INSERT INTO dbo.idempotency_record(idem_key,request_hash,user_id,response_status,response_body)
VALUES(N'k0007',REPLICATE('a',64),1,200,N'{}')");
        await c.ExecuteAsync(@"
INSERT INTO dbo.idempotency_record(idem_key,request_hash,user_id,response_status,response_body)
VALUES(N'k0007',REPLICATE('a',64),2,200,N'{}')");
        // 同键同用户仍唯一
        await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => c.ExecuteAsync(@"
INSERT INTO dbo.idempotency_record(idem_key,request_hash,user_id,response_status,response_body)
VALUES(N'k0007',REPLICATE('a',64),1,200,N'{}')"));
    }

    [Fact]
    public async Task Mapping_pair_unique_indexes_exist()
    {
        await using var c = await _fx.OpenAsync();
        var n = await c.ExecuteScalarAsync<int>(@"
SELECT COUNT(*) FROM sys.indexes WHERE name IN ('UX_pmap_pair','UX_smap_pair')");
        Assert.Equal(2, n);
    }

    [Fact]
    public async Task Import_batch_staged_content_column_exists()
    {
        await using var c = await _fx.OpenAsync();
        var ok = await c.ExecuteScalarAsync<int>(@"
SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_NAME='import_batch' AND COLUMN_NAME='staged_content' AND DATA_TYPE='nvarchar' AND CHARACTER_MAXIMUM_LENGTH=-1");
        Assert.Equal(1, ok);
    }

    [Fact]
    public async Task Migration_0007_recorded_with_checksum()
    {
        await using var c = await _fx.OpenAsync();
        var ok = await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.schema_version WHERE version=7 AND success=1 AND name LIKE '0007_%'");
        Assert.Equal(1, ok);
    }
}
