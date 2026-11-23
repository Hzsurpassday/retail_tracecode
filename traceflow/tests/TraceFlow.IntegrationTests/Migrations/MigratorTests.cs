using Dapper;
using TraceFlow.DbMigrator;
using TraceFlow.IntegrationTests;
using TraceFlow.IntegrationTests.Sources; // TestSeed（brief 未列 using；与 Migration0008Tests 同一标注方式）
using Xunit;

namespace TraceFlow.IntegrationTests.Migrations;

public class MigratorTests : IClassFixture<Sql2008Fixture>
{
    private readonly Sql2008Fixture _fx;
    public MigratorTests(Sql2008Fixture fx) => _fx = fx;

    [Fact]
    public async Task Second_run_is_idempotent_skip()
    {
        var scripts = Path.Combine(AppContext.BaseDirectory, "db", "migrations");
        var report = await new MigrationRunner(_fx.ConnectionString, scripts).RunAsync();
        Assert.Empty(report.Applied);
        Assert.NotEmpty(report.Skipped);
    }

    [Fact]
    public async Task Checksum_mismatch_stops_and_does_not_partially_apply()
    {
        var src = Path.Combine(AppContext.BaseDirectory, "db", "migrations");
        var tampered = Path.Combine(Path.GetTempPath(), "tf_tamper_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tampered);
        foreach (var f in Directory.GetFiles(src, "*.sql")) File.Copy(f, Path.Combine(tampered, Path.GetFileName(f)));
        var first = Directory.GetFiles(tampered, "*.sql").Order().First();
        await File.AppendAllTextAsync(first, "\n-- tampered\n");
        await Assert.ThrowsAsync<MigrationException>(() => new MigrationRunner(_fx.ConnectionString, tampered).RunAsync());
        Directory.Delete(tampered, true);
    }

    [Fact]
    public async Task Verification_snapshot_index_exists_after_migration()
    {
        // 0009 的兜底索引（SyncScheduler/SourceSyncJobHandler 的判重查询依赖它）：迁移后必须真实存在且未禁用。
        await using var conn = await _fx.OpenAsync();
        var index = await conn.QuerySingleOrDefaultAsync<(string name, int disabled)>(
            "SELECT i.name, i.is_disabled FROM sys.indexes i JOIN sys.tables t ON t.object_id=i.object_id " +
            "WHERE t.name=N'verification' AND i.name=N'IX_verif_snapshot'");
        Assert.False(string.IsNullOrEmpty(index.name));
        Assert.Equal(0, index.disabled);
    }

    [Fact]
    public async Task Core_tables_exist_and_single_active_org_enforced()
    {
        await using var conn = await _fx.OpenAsync();
        foreach (var t in new[] { "organization", "source_system", "app_setting", "app_user", "user_permission", "audit_log", "idempotency_record" })
            Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sys.tables WHERE name=@t", new { t }));

        await conn.ExecuteAsync("INSERT INTO dbo.organization(org_code,name) VALUES(N'T1',N'店1')");
        var ex = await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() =>
            conn.ExecuteAsync("INSERT INTO dbo.organization(org_code,name) VALUES(N'T2',N'店2')"));
        Assert.True(ex.Number is 2601 or 2627); // 过滤唯一索引：仅一个 active 门店
    }
}

// ---- M3 / 0009：销售核验与上传流水线（Task 1） ----
// 独立测试类 + 独立 fixture 库：本组用例播种 organization（TestSeed O1），与
// MigratorTests.Core_tables_exist_and_single_active_org_enforced 的「仅一个 active 门店」互斥，
// 同库同组会互相污染（首跑实测即因此失败），故按 Migration0008Tests 的「一类一库」模式隔离。
public class Migration0009Tests : IClassFixture<Sql2008Fixture>
{
    private readonly Sql2008Fixture _fx;
    public Migration0009Tests(Sql2008Fixture fx) => _fx = fx;

    [Fact]
    public async Task Migration_0009_recorded_with_checksum()
    {
        await using var c = await _fx.OpenAsync();
        var row = await c.QuerySingleAsync<(string name, string checksum)>(
            "SELECT name, checksum FROM dbo.schema_version WHERE version=9 AND success=1");
        Assert.StartsWith("0009_", row.name);
        // 0009 在本未合并分支上仍在演进（本轮加索引 + 核验兜底索引），期望值随文件字节更新；0001–0008 见下一用例（不可变）。
        Assert.Equal("99643903AC84A930CB1FD9E633445081F5CFE3E23E91557C7F3952E13D51D6BB",
            row.checksum.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Migrations_0001_to_0008_checksums_match_m2_gate_report()
    {
        // M2 gate report（docs/delivery/M2-gate-report.md ③）登记的真实 SHA-256；0001–0008 字节不可变。
        var expected = new (int Version, string Name, string Checksum)[]
        {
            (1, "0001_core.sql", "3A7F4E3A4A1D910208FDA22B1BB083DDB10D1EE1D80E644328506788E59DEFB9"),
            (2, "0002_source.sql", "90AABC500B52314EBB225AEB0CEA23991E608E8F38792A13723F63B68448FB43"),
            (3, "0003_master.sql", "0E1FF67C4A777B20BF2589F7D6708E14875DC89ADD431D02DE8CF518D5A9D1DE"),
            (4, "0004_upstream.sql", "DF82EAA935D8A38B402119D0DDD99350407FDE5ED709DD34C67B36DFE4938273"),
            (5, "0005_codes.sql", "5E1E1EF199C62658BE10761319392A2AAC0E19771D64D8CCB8F388CE3EC4EEDC"),
            (6, "0006_upload.sql", "8150CED244D824BE4DD78D5E80C999EA8E702A3B68C7433E75740569F0928FBE"),
            (7, "0007_m1_source_master.sql", "96D399B8CE9D834DCC119729059D1676152A2108A25876C2A94FFCE0E8715A39"),
            (8, "0008_m2_upstream_allocation.sql", "F3DC59F6F51AAAB2F07AA1B09C46A36B59896EEDC51794163784F5CA166C6EF0"),
        };
        await using var c = await _fx.OpenAsync();
        foreach (var e in expected)
        {
            var row = await c.QuerySingleAsync<(string name, string checksum)>(
                "SELECT name, checksum FROM dbo.schema_version WHERE version=@v AND success=1", new { v = e.Version });
            Assert.Equal(e.Name, row.name);
            Assert.Equal(e.Checksum, row.checksum.Trim(), StringComparer.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task M3_tables_and_columns_exist_with_expected_nullability()
    {
        await using var c = await _fx.OpenAsync();
        var tables = await c.ExecuteScalarAsync<int>(@"
SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES
WHERE TABLE_NAME IN ('verification_code','return_confirmation')");
        Assert.Equal(2, tables);

        var taskCol = await c.QuerySingleAsync<(string type, string nullable)>(
            "SELECT DATA_TYPE, IS_NULLABLE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='upload_task' AND COLUMN_NAME='predecessor_task_id'");
        Assert.Equal("bigint", taskCol.type);
        Assert.Equal("YES", taskCol.nullable);

        var (partType, partNullable) = await c.QuerySingleAsync<(string type, string nullable)>(@"
SELECT DATA_TYPE, IS_NULLABLE FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_NAME='upload_part' AND COLUMN_NAME='next_query_at'");
        Assert.Equal("datetime2", partType);
        Assert.Equal("YES", partNullable); // brief：next_query_at 可空
        Assert.Equal(1, await c.ExecuteScalarAsync<int>(@"
SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_NAME='upload_part' AND COLUMN_NAME='first_sent_at' AND DATA_TYPE='datetime2' AND IS_NULLABLE='YES'"));
        var qcount = await c.QuerySingleAsync<(string type, string nullable)>(@"
SELECT DATA_TYPE, IS_NULLABLE FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_NAME='upload_part' AND COLUMN_NAME='query_count'");
        Assert.Equal("int", qcount.type);
        Assert.Equal("NO", qcount.nullable);
        // 精确默认约束：具名 DF_part_qcount 且表达式为 ((0))（不含任何负值/其他字面量）
        var qcountDefault = await c.QuerySingleAsync<(string name, string definition)>(@"
SELECT dc.name, dc.definition
FROM sys.default_constraints dc
JOIN sys.columns col ON col.object_id=dc.parent_object_id AND col.column_id=dc.parent_column_id
WHERE dc.parent_object_id=OBJECT_ID(N'dbo.upload_part') AND col.name=N'query_count'");
        Assert.Equal("DF_part_qcount", qcountDefault.name);
        Assert.Equal("((0))", qcountDefault.definition);
    }

    [Fact]
    public async Task Verification_code_accepts_minimal_row_and_enforces_unique_and_check()
    {
        await using var c = await _fx.OpenAsync();
        var (orgId, sysId) = await TestSeed.EnsureAsync(c);
        var verifId = await SeedVerificationAsync(c, orgId, sysId, "M3-VCODE-1");
        var codeId = await SeedTraceCodeAsync(c, "TF-M3-VCODE-1");

        await c.ExecuteAsync(@"
INSERT INTO dbo.verification_code(verification_id,org_id,env,code_id,source_line_id,result)
VALUES(@v,@org,N'MOCK',@code,0,N'PASS')", new { v = verifId, org = orgId, code = codeId });
        Assert.Equal(1, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.verification_code WHERE verification_id=@v AND code_id=@code",
            new { v = verifId, code = codeId }));
        // 同核验同码重复→唯一约束拒绝（2601/2627 与既有 org 唯一索引断言同型）
        var dup = await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => c.ExecuteAsync(@"
INSERT INTO dbo.verification_code(verification_id,org_id,env,code_id,source_line_id,result)
VALUES(@v,@org,N'MOCK',@code,0,N'FAIL')", new { v = verifId, org = orgId, code = codeId }));
        Assert.True(dup.Number is 2601 or 2627, $"UQ_vcode 应报唯一约束，实际 {dup.Number}");
        // 非法结论→CHECK 约束拒绝（547）
        var code2 = await SeedTraceCodeAsync(c, "TF-M3-VCODE-2");
        var badResult = await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => c.ExecuteAsync(@"
INSERT INTO dbo.verification_code(verification_id,org_id,env,code_id,source_line_id,result)
VALUES(@v,@org,N'MOCK',@code,0,N'MAYBE')", new { v = verifId, org = orgId, code = code2 }));
        Assert.True(badResult.Number is 547, $"CK_vcode_result 应报 CHECK 约束，实际 {badResult.Number}");
    }

    [Fact]
    public async Task Return_confirmation_accepts_minimal_row_and_enforces_unique_and_checks()
    {
        await using var c = await _fx.OpenAsync();
        var (orgId, _) = await TestSeed.EnsureAsync(c);
        var taskId = await SeedUploadTaskAsync(c, orgId, "M3-RETCONF-1", "PURCHASE_RETURN_202");

        await c.ExecuteAsync(@"
INSERT INTO dbo.return_confirmation(org_id,env,task_id,decision,relation_kind,evidence_json,confirmed_by)
VALUES(@org,N'MOCK',@task,N'CONFIRMED',N'PURCHASE_IN',N'{}',N'tester')", new { org = orgId, task = taskId });
        Assert.Equal(1, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.return_confirmation WHERE task_id=@task", new { task = taskId }));
        // 同任务重复确认→唯一约束拒绝
        var dup = await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => c.ExecuteAsync(@"
INSERT INTO dbo.return_confirmation(org_id,env,task_id,decision,relation_kind,evidence_json,confirmed_by)
VALUES(@org,N'MOCK',@task,N'REJECTED',N'SALE',N'{}',N'tester')", new { org = orgId, task = taskId }));
        Assert.True(dup.Number is 2601 or 2627, $"UQ_retconf_task 应报唯一约束，实际 {dup.Number}");
        // 非法决策/关系→CHECK 拒绝（547）
        var task2 = await SeedUploadTaskAsync(c, orgId, "M3-RETCONF-2", "PURCHASE_RETURN_202");
        var badDecision = await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => c.ExecuteAsync(@"
INSERT INTO dbo.return_confirmation(org_id,env,task_id,decision,relation_kind,evidence_json,confirmed_by)
VALUES(@org,N'MOCK',@task,N'ACCEPTED',N'SALE',N'{}',N'tester')", new { org = orgId, task = task2 }));
        Assert.True(badDecision.Number is 547, $"CK_retconf_decision 应报 CHECK 约束，实际 {badDecision.Number}");
        var badRelation = await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => c.ExecuteAsync(@"
INSERT INTO dbo.return_confirmation(org_id,env,task_id,decision,relation_kind,evidence_json,confirmed_by)
VALUES(@org,N'MOCK',@task,N'CONFIRMED',N'OTHER',N'{}',N'tester')", new { org = orgId, task = task2 }));
        Assert.True(badRelation.Number is 547, $"CK_retconf_relation 应报 CHECK 约束，实际 {badRelation.Number}");
    }

    private static async Task<long> SeedVerificationAsync(Microsoft.Data.SqlClient.SqlConnection c, long orgId, long sysId, string key)
    {
        var snapId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.source_snapshot(org_id,source_system_id,source_document_key,document_type,version,content_hash,raw_header)
OUTPUT inserted.id
VALUES(@org,@sys,@key,N'SALE',1,REPLICATE('a',64),N'{}')", new { org = orgId, sys = sysId, key });
        return await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.verification(org_id,env,event_key,source_snapshot_id,source_hash,code_set_hash,rule_version)
OUTPUT inserted.id
VALUES(@org,N'MOCK',@key,@snap,REPLICATE('b',64),REPLICATE('c',64),N'M3-1')",
            new { org = orgId, key, snap = snapId });
    }

    private static async Task<long> SeedTraceCodeAsync(Microsoft.Data.SqlClient.SqlConnection c, string normalized)
        => await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.trace_code(code_normalized,code_raw,format) OUTPUT inserted.id
VALUES(@n,@n,N'MOCK')", new { n = normalized });

    private static async Task<long> SeedUploadTaskAsync(Microsoft.Data.SqlClient.SqlConnection c, long orgId, string eventKey, string taskType)
        => await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upload_task(org_id,env,event_key,task_type,source_hash,payload_hash,state)
OUTPUT inserted.id
VALUES(@org,N'MOCK',@key,@type,REPLICATE('d',64),REPLICATE('e',64),N'Blocked')",
            new { org = orgId, key = eventKey, type = taskType });
}
