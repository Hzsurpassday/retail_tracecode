using System.Runtime.Versioning; // brief 缺失此 using（SupportedOSPlatform 所需）；与 SourceSyncTests/SettingsServiceTests 标注模式一致（T6 偏差 1 同型）
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Infrastructure.Security;
using TraceFlow.Infrastructure.Settings;
using TraceFlow.Infrastructure.Sources;

namespace TraceFlow.IntegrationTests.Sources;

[SupportedOSPlatform("windows")]
public class SourceStabilizationTests : IClassFixture<Sql2008Fixture>
{
    private readonly Sql2008Fixture _fx;
    public SourceStabilizationTests(Sql2008Fixture fx) => _fx = fx;

    private static readonly DateTime Start = new(2026, 11, 19, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime End = new(2026, 11, 22, 0, 0, 0, DateTimeKind.Utc);

    private SourceSyncService NewSvc(string dir)
        => new(_fx.ConnectionString, new FixtureKsoaAdapter(dir),
               new SettingsService(_fx.ConnectionString, new DpapiProtector()), new AuditLog());

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "tf-stab-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    // 稳定期测试统一 5 秒稳定期 + 5 秒轮间隔（目录允许的最小值），幂等写入
    private async Task UseFastStabilizationAsync()
    {
        var svc = new SettingsService(_fx.ConnectionString, new DpapiProtector());
        foreach (var (key, json) in new[] { ("sync.stabilizeSeconds", "5"), ("sync.roundIntervalSeconds", "5") })
        {
            var view = (await svc.ListAsync(CancellationToken.None)).Single(v => v.Key == key);
            if (view.ValueJson == json) continue;
            await using var c = new SqlConnection(_fx.ConnectionString);
            await c.OpenAsync();
            await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
            await svc.UpdateAsync(c, tx, key, json, null, view.Id == 0 ? "0" : view.Version, "稳定期测试", "tester", CancellationToken.None);
            await tx.CommitAsync();
        }
    }

    private async Task<long> SeedAsync()
    {
        await using var c = new SqlConnection(_fx.ConnectionString);
        await c.OpenAsync();
        var (orgId, _) = await TestSeed.EnsureAsync(c);
        return orgId;
    }

    private static string Doc(string key, string lines) => $$"""
    { "salemast": [{ "lshh": "{{key}}", "rq": "2026-11-20", "ontime": "08:00:00" }],
      "salemxls": [{{lines}}] }
    """;

    [Fact]
    public async Task Two_consistent_rounds_with_interval_promote_complete() // SYNC-02 / AT-004
    {
        await UseFastStabilizationAsync();
        var orgId = await SeedAsync();
        var dir = TempDir();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "s.json"),
                Doc("S1", """{ "lshh": "S1", "plh": "1", "spid": "SP0001", "shl": 1, "TraceCode": "81234567890123456789" }"""));
            var r1 = await NewSvc(dir).RunAsync(orgId, "MOCK", "sale", Start, End, CancellationToken.None);
            Assert.Equal(0, r1.Completed);
            await using var c = new SqlConnection(_fx.ConnectionString);
            var snapId = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.source_snapshot WHERE source_document_key=N'S1'");
            Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT stabilize_rounds FROM dbo.source_snapshot WHERE id=@id", new { id = snapId }));
            // 立即第二轮：轮间隔不足，rounds 不增
            await NewSvc(dir).RunAsync(orgId, "MOCK", "sale", Start, End, CancellationToken.None);
            Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT stabilize_rounds FROM dbo.source_snapshot WHERE id=@id", new { id = snapId }));
            await Task.Delay(6000); // 超过 5 秒轮间隔 + 5 秒稳定期
            var r3 = await NewSvc(dir).RunAsync(orgId, "MOCK", "sale", Start, End, CancellationToken.None);
            Assert.Equal(1, r3.Completed);
            Assert.Equal("COMPLETE", await c.ExecuteScalarAsync<string>("SELECT completeness_status FROM dbo.source_snapshot WHERE id=@id", new { id = snapId }));
            Assert.Equal(2, await c.ExecuteScalarAsync<int>("SELECT stabilize_rounds FROM dbo.source_snapshot WHERE id=@id", new { id = snapId }));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Zero_line_doc_never_completes()
    {
        await UseFastStabilizationAsync();
        var orgId = await SeedAsync();
        var dir = TempDir();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "e.json"), Doc("E2", ""));
            await NewSvc(dir).RunAsync(orgId, "MOCK", "sale", Start, End, CancellationToken.None);
            await Task.Delay(6000);
            var r = await NewSvc(dir).RunAsync(orgId, "MOCK", "sale", Start, End, CancellationToken.None);
            Assert.Equal(0, r.Completed);
            await using var c = new SqlConnection(_fx.ConnectionString);
            Assert.Equal("DISCOVERED", await c.ExecuteScalarAsync<string>(
                "SELECT completeness_status FROM dbo.source_snapshot WHERE source_document_key=N'E2'"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Complete_doc_changed_marks_source_changed_and_task_payload_immutable() // AT-057
    {
        await UseFastStabilizationAsync();
        var orgId = await SeedAsync();
        var dir = TempDir();
        try
        {
            var file = Path.Combine(dir, "s.json");
            await File.WriteAllTextAsync(file,
                Doc("S2", """{ "lshh": "S2", "plh": "1", "spid": "SP0001", "shl": 1, "TraceCode": "81234567890123456789" }"""));
            await NewSvc(dir).RunAsync(orgId, "MOCK", "sale", Start, End, CancellationToken.None);
            await Task.Delay(6000);
            await NewSvc(dir).RunAsync(orgId, "MOCK", "sale", Start, End, CancellationToken.None);
            await using var c = new SqlConnection(_fx.ConnectionString);
            var v1 = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.source_snapshot WHERE source_document_key=N'S2' AND version=1");
            Assert.Equal("COMPLETE", await c.ExecuteScalarAsync<string>("SELECT completeness_status FROM dbo.source_snapshot WHERE id=@id", new { id = v1 }));
            // 种子上传任务引用 v1（M1 无任务生成逻辑，直接 SQL 种子验证 payload 不可变）
            var payload = """{"codes":["81234567890123456789"]}""";
            var payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
            var sourceHash = await c.ExecuteScalarAsync<string>("SELECT content_hash FROM dbo.source_snapshot WHERE id=@id", new { id = v1 });
            await c.ExecuteAsync(@"
INSERT INTO dbo.upload_task(org_id,env,event_key,task_type,source_snapshot_id,source_hash,payload_json,payload_hash)
VALUES(@org,N'MOCK',N'STAB-TEST-1',N'RETAIL_321',@snap,@sh,@pj,@ph)",
                new { org = orgId, snap = v1, sh = sourceHash, pj = payload, ph = payloadHash });
            // 来源变化：shl 1→2
            await File.WriteAllTextAsync(file,
                Doc("S2", """{ "lshh": "S2", "plh": "1", "spid": "SP0001", "shl": 2, "TraceCode": "81234567890123456789" }"""));
            await NewSvc(dir).RunAsync(orgId, "MOCK", "sale", Start, End, CancellationToken.None);
            Assert.Equal("SOURCE_CHANGED", await c.ExecuteScalarAsync<string>("SELECT completeness_status FROM dbo.source_snapshot WHERE id=@id", new { id = v1 }));
            Assert.Equal("STABILIZING", await c.ExecuteScalarAsync<string>(
                "SELECT completeness_status FROM dbo.source_snapshot WHERE source_document_key=N'S2' AND version=2"));
            // 任务 payload 字节不动、不产生重复业务
            Assert.Equal(payload, await c.ExecuteScalarAsync<string>("SELECT payload_json FROM dbo.upload_task WHERE event_key=N'STAB-TEST-1'"));
            Assert.Equal(payloadHash, await c.ExecuteScalarAsync<string>("SELECT payload_hash FROM dbo.upload_task WHERE event_key=N'STAB-TEST-1'"));
            Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.upload_task"));
            // SOURCE_CHANGED 台账：变更标记，立即 RESOLVED 且证据指向新版本
            var evidence = await c.ExecuteScalarAsync<string>(
                "SELECT resolution_evidence FROM dbo.ingest_exception WHERE source_document_key=N'S2' AND exception_type=N'SOURCE_CHANGED' AND status=N'RESOLVED'");
            Assert.Contains("snapshotId", evidence);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Late_lines_new_version_resolves_empty_detail_and_completes() // AT-005
    {
        await UseFastStabilizationAsync();
        var orgId = await SeedAsync();
        var dir = TempDir();
        try
        {
            var file = Path.Combine(dir, "s.json");
            await File.WriteAllTextAsync(file, Doc("L1", ""));
            await NewSvc(dir).RunAsync(orgId, "MOCK", "sale", Start, End, CancellationToken.None);
            await using var c = new SqlConnection(_fx.ConnectionString);
            Assert.Equal("DISCOVERED", await c.ExecuteScalarAsync<string>(
                "SELECT completeness_status FROM dbo.source_snapshot WHERE source_document_key=N'L1' AND version=1"));
            // 迟到明细
            await File.WriteAllTextAsync(file,
                Doc("L1", """{ "lshh": "L1", "plh": "1", "spid": "SP0001", "shl": 1, "TraceCode": "81234567890123456789" }"""));
            await NewSvc(dir).RunAsync(orgId, "MOCK", "sale", Start, End, CancellationToken.None);
            Assert.Equal("STABILIZING", await c.ExecuteScalarAsync<string>(
                "SELECT completeness_status FROM dbo.source_snapshot WHERE source_document_key=N'L1' AND version=2"));
            Assert.Equal("RESOLVED", await c.ExecuteScalarAsync<string>(
                "SELECT status FROM dbo.ingest_exception WHERE source_document_key=N'L1' AND exception_type=N'EMPTY_DETAIL'"));
            await Task.Delay(6000);
            var r = await NewSvc(dir).RunAsync(orgId, "MOCK", "sale", Start, End, CancellationToken.None);
            Assert.Equal(1, r.Completed);
            Assert.Equal("COMPLETE", await c.ExecuteScalarAsync<string>(
                "SELECT completeness_status FROM dbo.source_snapshot WHERE source_document_key=N'L1' AND version=2"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
