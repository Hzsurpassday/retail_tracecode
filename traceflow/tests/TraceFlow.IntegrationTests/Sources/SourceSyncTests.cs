using System.Runtime.Versioning; // brief 缺失此 using（SupportedOSPlatform 所需）；与 SettingsServiceTests/ApiGateTests 标注模式一致（T6 偏差 1 同型）
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Infrastructure.Security;
using TraceFlow.Infrastructure.Settings;
using TraceFlow.Infrastructure.Sources;

namespace TraceFlow.IntegrationTests.Sources;

[SupportedOSPlatform("windows")] // SettingsService 依赖 DpapiProtector
// 每测试独立测试库（xUnit 每方法新建类实例→IAsyncLifetime 各建一库）：
// 默认 fixture 的单据键（000123 等）与审计计数断言要求测试间完全隔离，类级共享库会依执行顺序而碎。
public class SourceSyncTests : IAsyncLifetime
{
    private Sql2008Fixture _fx = null!;
    public async Task InitializeAsync() { _fx = new Sql2008Fixture(); await _fx.InitializeAsync(); }
    public Task DisposeAsync() => _fx.DisposeAsync();

    private static string FixtureDir => Path.Combine(AppContext.BaseDirectory, "fixtures", "ksoa");
    private static readonly DateTime Start = new(2026, 11, 19, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime End = new(2026, 11, 22, 0, 0, 0, DateTimeKind.Utc);

    private SourceSyncService NewSvc(string? dir = null)
        => new(_fx.ConnectionString, new FixtureKsoaAdapter(dir ?? FixtureDir),
               new SettingsService(_fx.ConnectionString, new DpapiProtector()), new AuditLog());

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "tf-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    // 每测试私有 fixture 副本：与每测试独立库配合，保证自定义 fixture 测试互不干扰（目录用后即删）。
    private static string PrivateFixtures()
    {
        var d = TempDir();
        foreach (var f in Directory.GetFiles(FixtureDir, "*.json"))
            File.Copy(f, Path.Combine(d, Path.GetFileName(f)));
        return d;
    }

    private async Task<(long OrgId, long SysId)> SeedAsync()
    {
        await using var c = new SqlConnection(_fx.ConnectionString);
        await c.OpenAsync();
        return await TestSeed.EnsureAsync(c);
    }

    [Fact]
    public async Task Sale_ingest_per_line_codes_utc_time_checkpoint_and_audit() // AT-002
    {
        var dir = PrivateFixtures();
        try
        {
        var (orgId, sysId) = await SeedAsync();
        var r = await NewSvc(dir).RunAsync(orgId, "MOCK", "sale", Start, End, CancellationToken.None);
        Assert.True(r.DocumentsSeen >= 3);
        Assert.True(r.NewVersions >= 3);
        await using var c = new SqlConnection(_fx.ConnectionString);
        var snapId = await c.ExecuteScalarAsync<long>(
            "SELECT id FROM dbo.source_snapshot WHERE source_document_key=N'000123' AND version=1");
        Assert.Equal(new DateTime(2026, 11, 20, 6, 32, 5), await c.ExecuteScalarAsync<DateTime>(
            "SELECT business_datetime FROM dbo.source_snapshot WHERE id=@id", new { id = snapId }));
        Assert.Equal("STABILIZING", await c.ExecuteScalarAsync<string>(
            "SELECT completeness_status FROM dbo.source_snapshot WHERE id=@id", new { id = snapId }));
        // 码随行走：plh=1 的行携带自己的码，plh=3 无码
        Assert.Equal("81234567890123456789", await c.ExecuteScalarAsync<string>(
            "SELECT trace_code_raw FROM dbo.source_line WHERE snapshot_id=@id AND source_line_key=N'1'", new { id = snapId }));
        Assert.Equal("S8P20", await c.ExecuteScalarAsync<string>(
            "SELECT code_format FROM dbo.source_line WHERE snapshot_id=@id AND source_line_key=N'1'", new { id = snapId }));
        Assert.Null(await c.ExecuteScalarAsync<string?>(
            "SELECT trace_code_raw FROM dbo.source_line WHERE snapshot_id=@id AND source_line_key=N'3'", new { id = snapId }));
        Assert.Equal("EMPTY", await c.ExecuteScalarAsync<string>(
            "SELECT code_format FROM dbo.source_line WHERE snapshot_id=@id AND source_line_key=N'3'", new { id = snapId }));
        // 检查点同事务落库
        Assert.Equal(End, await c.ExecuteScalarAsync<DateTime>(
            "SELECT window_end FROM dbo.sync_checkpoint WHERE source_system_id=@s AND entity=N'sale' AND checkpoint_kind=N'WINDOW'", new { s = sysId }));
        // 审计 env=MOCK
        Assert.Equal(1, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.audit_log WHERE action=N'source.sync' AND env=N'MOCK' AND actor_kind=N'Worker'"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Second_identical_run_dedups_by_hash_no_new_version()
    {
        var dir = PrivateFixtures();
        try
        {
        var (orgId, _) = await SeedAsync();
        var r1 = await NewSvc(dir).RunAsync(orgId, "MOCK", "sale", Start, End, CancellationToken.None);
        var r2 = await NewSvc(dir).RunAsync(orgId, "MOCK", "sale", Start, End, CancellationToken.None);
        Assert.True(r1.NewVersions > 0);
        Assert.Equal(0, r2.NewVersions);
        await using var c = new SqlConnection(_fx.ConnectionString);
        Assert.Equal(r1.NewVersions, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.source_snapshot"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Missing_plh_doc_is_invalid_with_raw_exception_no_lines() // SRC-02
    {
        var dir = PrivateFixtures();
        try
        {
        var (orgId, _) = await SeedAsync();
        await NewSvc(dir).RunAsync(orgId, "MOCK", "sale", Start, End, CancellationToken.None);
        await using var c = new SqlConnection(_fx.ConnectionString);
        Assert.Equal("INVALID", await c.ExecuteScalarAsync<string>(
            "SELECT completeness_status FROM dbo.source_snapshot WHERE source_document_key=N'000124'"));
        Assert.Equal(0, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.source_line l JOIN dbo.source_snapshot s ON l.snapshot_id=s.id WHERE s.source_document_key=N'000124'"));
        var detail = await c.ExecuteScalarAsync<string>(
            "SELECT detail FROM dbo.ingest_exception WHERE source_document_key=N'000124' AND exception_type=N'MISSING_LINE_KEY' AND status=N'OPEN'");
        Assert.Contains("81234567890123456791", detail); // raw 行完整保留
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Raw_codes_preserved_verbatim_leading_zeros_and_control_chars() // AT-008 / SRC-04
    {
        var dir = TempDir();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "c.json"), """
            { "salemast": [{ "lshh": "C1", "rq": "2026-11-20", "ontime": "08:00:00" }],
              "salemxls": [
                { "lshh": "C1", "plh": "1", "spid": "SP0001", "shl": 1, "TraceCode": "00123456789012345678" },
                { "lshh": "C1", "plh": "2", "spid": "SP0001", "shl": 1, "TraceCode": "\u000281234567890123456789\u0003" },
                { "lshh": "C1", "plh": "3", "spid": "SP0001", "shl": 1, "TraceCode": "81234\u001f567890123456789" } ] }
            """);
            var (orgId, _) = await SeedAsync();
            await NewSvc(dir).RunAsync(orgId, "MOCK", "sale", Start, End, CancellationToken.None);
            await using var c = new SqlConnection(_fx.ConnectionString);
            var snapId = await c.ExecuteScalarAsync<long>(
                "SELECT id FROM dbo.source_snapshot WHERE source_document_key=N'C1'");
            // 前导零不转数值、不截断；raw 原样
            Assert.Equal("00123456789012345678", await c.ExecuteScalarAsync<string>(
                "SELECT trace_code_raw FROM dbo.source_line WHERE snapshot_id=@id AND source_line_key=N'1'", new { id = snapId }));
            Assert.Equal("UNSUP", await c.ExecuteScalarAsync<string>(
                "SELECT code_format FROM dbo.source_line WHERE snapshot_id=@id AND source_line_key=N'1'", new { id = snapId }));
            // 边缘控制符规范化移除，raw 保留原控制符
            Assert.Equal("\u000281234567890123456789\u0003", await c.ExecuteScalarAsync<string>(
                "SELECT trace_code_raw FROM dbo.source_line WHERE snapshot_id=@id AND source_line_key=N'2'", new { id = snapId }));
            Assert.Equal("81234567890123456789", await c.ExecuteScalarAsync<string>(
                "SELECT trace_code_normalized FROM dbo.source_line WHERE snapshot_id=@id AND source_line_key=N'2'", new { id = snapId }));
            Assert.Equal("S8P20", await c.ExecuteScalarAsync<string>(
                "SELECT code_format FROM dbo.source_line WHERE snapshot_id=@id AND source_line_key=N'2'", new { id = snapId }));
            // 内部控制符属未验证情形→UNSUP 异常保存，不静默清洗
            Assert.Equal("UNSUP", await c.ExecuteScalarAsync<string>(
                "SELECT code_format FROM dbo.source_line WHERE snapshot_id=@id AND source_line_key=N'3'", new { id = snapId }));
            Assert.Contains("\u001f", await c.ExecuteScalarAsync<string>(
                "SELECT trace_code_normalized FROM dbo.source_line WHERE snapshot_id=@id AND source_line_key=N'3'", new { id = snapId }));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Overlong_doc_key_fails_whole_run_no_snapshot_no_checkpoint() // AT-006
    {
        var dir = TempDir();
        try
        {
            var longKey = new string('K', 130);
            await File.WriteAllTextAsync(Path.Combine(dir, "long.json"),
                "{ \"salemast\": [{ \"lshh\": \"" + longKey + "\", \"rq\": \"2026-11-20\", \"ontime\": \"08:00:00\" }], \"salemxls\": [] }");
            var (orgId, sysId) = await SeedAsync();
            await Assert.ThrowsAnyAsync<SqlException>(
                () => NewSvc(dir).RunAsync(orgId, "MOCK", "sale", Start, End, CancellationToken.None));
            await using var c = new SqlConnection(_fx.ConnectionString);
            Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.source_snapshot"));
            Assert.Equal(0, await c.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM dbo.sync_checkpoint WHERE source_system_id=@s", new { s = sysId }));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Cross_day_reuse_across_runs_invalidates_new_version_old_intact() // AT-003
    {
        var dirA = TempDir(); var dirB = TempDir();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dirA, "a.json"), """
            { "salemast": [{ "lshh": "R2", "rq": "2026-11-20", "ontime": "08:00:00" }],
              "salemxls": [{ "lshh": "R2", "plh": "1", "spid": "SP0001", "shl": 1 }] }
            """);
            await File.WriteAllTextAsync(Path.Combine(dirB, "b.json"), """
            { "salemast": [{ "lshh": "R2", "rq": "2026-11-21", "ontime": "08:00:00" }],
              "salemxls": [{ "lshh": "R2", "plh": "1", "spid": "SP0001", "shl": 2 }] }
            """);
            var (orgId, _) = await SeedAsync();
            await NewSvc(dirA).RunAsync(orgId, "MOCK", "sale", Start, End, CancellationToken.None);
            await NewSvc(dirB).RunAsync(orgId, "MOCK", "sale", Start, End, CancellationToken.None);
            await using var c = new SqlConnection(_fx.ConnectionString);
            Assert.Equal("STABILIZING", await c.ExecuteScalarAsync<string>(
                "SELECT completeness_status FROM dbo.source_snapshot WHERE source_document_key=N'R2' AND version=1"));
            Assert.Equal("INVALID", await c.ExecuteScalarAsync<string>(
                "SELECT completeness_status FROM dbo.source_snapshot WHERE source_document_key=N'R2' AND version=2"));
            Assert.Equal(1, await c.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM dbo.ingest_exception WHERE source_document_key=N'R2' AND exception_type=N'CROSS_DAY_REUSE'"));
        }
        finally { Directory.Delete(dirA, recursive: true); Directory.Delete(dirB, recursive: true); }
    }

    [Fact]
    public async Task Same_doc_key_across_document_types_is_key_reuse() // SRC-02 跨表键碰撞
    {
        var dirS = TempDir(); var dirP = TempDir();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dirS, "s.json"), """
            { "salemast": [{ "lshh": "COLL1", "rq": "2026-11-20", "ontime": "08:00:00" }],
              "salemxls": [{ "lshh": "COLL1", "plh": "1", "spid": "SP0001", "shl": 1 }] }
            """);
            await File.WriteAllTextAsync(Path.Combine(dirP, "p.json"), """
            { "jh_rkhz": [{ "djbh": "COLL1", "rq": "2026-11-20", "ontime": "08:00:00", "dwbh": "DW0001" }],
              "jh_rkmx": [{ "djbh": "COLL1", "dj_sn": "1", "spid": "SP0001", "pihao": "P1", "shl": 5 }] }
            """);
            var (orgId, _) = await SeedAsync();
            await NewSvc(dirS).RunAsync(orgId, "MOCK", "sale", Start, End, CancellationToken.None);
            await NewSvc(dirP).RunAsync(orgId, "MOCK", "purchase-in", Start, End, CancellationToken.None);
            await using var c = new SqlConnection(_fx.ConnectionString);
            Assert.Equal("INVALID", await c.ExecuteScalarAsync<string>(
                "SELECT completeness_status FROM dbo.source_snapshot WHERE source_document_key=N'COLL1' AND document_type=N'PURCHASE_IN'"));
            Assert.Equal(1, await c.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM dbo.ingest_exception WHERE source_document_key=N'COLL1' AND exception_type=N'KEY_REUSE'"));
        }
        finally { Directory.Delete(dirS, recursive: true); Directory.Delete(dirP, recursive: true); }
    }

    [Fact]
    public async Task Zero_line_doc_discovered_with_empty_detail_exception()
    {
        var dir = TempDir();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "empty.json"), """
            { "salemast": [{ "lshh": "E1", "rq": "2026-11-20", "ontime": "08:00:00" }], "salemxls": [] }
            """);
            var (orgId, _) = await SeedAsync();
            await NewSvc(dir).RunAsync(orgId, "MOCK", "sale", Start, End, CancellationToken.None);
            await using var c = new SqlConnection(_fx.ConnectionString);
            Assert.Equal("DISCOVERED", await c.ExecuteScalarAsync<string>(
                "SELECT completeness_status FROM dbo.source_snapshot WHERE source_document_key=N'E1'"));
            Assert.Equal(1, await c.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM dbo.ingest_exception WHERE source_document_key=N'E1' AND exception_type=N'EMPTY_DETAIL'"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Qty_out_of_range_becomes_issue_raw_preserved_line_not_silently_rounded() // SRC-05
    {
        var dir = TempDir();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "q.json"), """
            { "salemast": [{ "lshh": "Q1", "rq": "2026-11-20", "ontime": "08:00:00" }],
              "salemxls": [{ "lshh": "Q1", "plh": "1", "spid": "SP0001", "shl": 10000000000000 }] }
            """);
            var (orgId, _) = await SeedAsync();
            await NewSvc(dir).RunAsync(orgId, "MOCK", "sale", Start, End, CancellationToken.None);
            await using var c = new SqlConnection(_fx.ConnectionString);
            Assert.Equal("INVALID", await c.ExecuteScalarAsync<string>(
                "SELECT completeness_status FROM dbo.source_snapshot WHERE source_document_key=N'Q1'"));
            var detail = await c.ExecuteScalarAsync<string>(
                "SELECT detail FROM dbo.ingest_exception WHERE source_document_key=N'Q1' AND exception_type=N'QTY_RANGE'");
            Assert.Contains("10000000000000", detail);
            Assert.Equal(0, await c.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM dbo.source_line l JOIN dbo.source_snapshot s ON l.snapshot_id=s.id WHERE s.source_document_key=N'Q1'"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Live_env_refuses_sync_in_m1()
    {
        var (orgId, _) = await SeedAsync();
        await Assert.ThrowsAsync<AdapterNotCertifiedException>(
            () => NewSvc().RunAsync(orgId, "LIVE", "sale", Start, End, CancellationToken.None));
    }
}
