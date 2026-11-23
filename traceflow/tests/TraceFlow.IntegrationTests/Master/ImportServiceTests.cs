using System.Runtime.Versioning; // brief 缺失此 using（SupportedOSPlatform 所需）；与 SettingsServiceTests/SourceSyncTests 标注模式一致（T6 偏差 1 同型）
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Domain.Versioning;
using TraceFlow.Infrastructure.Jobs;
using TraceFlow.Infrastructure.Master;
using TraceFlow.Infrastructure.Security;
using TraceFlow.Infrastructure.Settings;
using TraceFlow.IntegrationTests.Sources;
using Xunit;

namespace TraceFlow.IntegrationTests.Master;

[SupportedOSPlatform("windows")] // SettingsService 依赖 DpapiProtector（brief 缺失此标注→CA1416，TreatWarningsAsErrors；与 SettingsServiceTests 同模式）
public class ImportServiceTests : IAsyncLifetime
{
    private readonly Sql2008Fixture _fx = new();
    public async Task InitializeAsync() => await _fx.InitializeAsync();
    public async Task DisposeAsync() => await _fx.DisposeAsync();

    // brief 偏差（实证）：brief 的 PRODUCT 数据行在药名前多 1 个逗号（"P002,,,,维生素C片"），
    // CsvReader 实测 name=""、approval_no=维生素C片 → ValidateRow "name 必填"（0003 DDL name NOT NULL），
    // 与 success_rows=2/可提交断言矛盾。最小修复：药名前 4 逗号改 3、药名后 5 逗号改 6（仍 13 列，unit=盒 不变）。
    private const string ProductHeader =
        "spid,display_code,barcode,name,approval_no,manufacturer,dosage_form,strength,package_spec,unit,trace_classification,classification_reason,manufacturer_identity";

    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    private ImportService NewSvc() => new(_fx.ConnectionString,
        new SettingsService(_fx.ConnectionString, new DpapiProtector()),
        new JobQueue(_fx.ConnectionString, new AuditLog()), new AuditLog());

    private async Task<(long OrgId, long BatchId, long JobId)> StageAsync(string kind, string csv,
        string? fileName = null, string? hashOverride = null, string? contentOverride = null)
    {
        await using var c = new SqlConnection(_fx.ConnectionString);
        await c.OpenAsync();
        var (orgId, _) = await TestSeed.EnsureAsync(c);
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
        var (batchId, jobId) = await NewSvc().StageAsync(c, tx, orgId, "MOCK", kind, fileName,
            hashOverride ?? Hash(csv), contentOverride ?? B64(csv), "tester", CancellationToken.None);
        await tx.CommitAsync();
        return (orgId, batchId, jobId);
    }

    private async Task RunStageJobAsync()
    {
        var queue = new JobQueue(_fx.ConnectionString, new AuditLog());
        var job = await queue.ClaimAsync("test-worker", "MOCK", new[] { "import-stage" }, TimeSpan.FromMinutes(5), CancellationToken.None);
        Assert.NotNull(job);
        await new ImportStageJobHandler(_fx.ConnectionString).HandleAsync(job!, CancellationToken.None);
        await queue.CompleteAsync(job!.Id, "test-worker", job.FencingToken, CancellationToken.None);
    }

    private async Task<string> BatchVersionAsync(long batchId)
    {
        await using var c = new SqlConnection(_fx.ConnectionString);
        await c.OpenAsync();
        var ver = await c.ExecuteScalarAsync<byte[]>("SELECT ver FROM dbo.import_batch WHERE id=@id", new { id = batchId });
        return RowVer.ToHex(ver!); // brief 原文 ver 为 byte[]?（Dapper ExecuteScalarAsync<T> 返回 T?）→CS8604；批次刚创建必存在，null 包容
    }

    private async Task CommitAsync(long batchId, string version, string? reason = "提交")
    {
        await using var c = new SqlConnection(_fx.ConnectionString);
        await c.OpenAsync();
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
        await NewSvc().CommitAsync(c, tx, batchId, "MOCK", version, reason, "tester", CancellationToken.None);
        await tx.CommitAsync();
    }

    private async Task SetSettingAsync(string key, string valueJson)
    {
        await using var c = new SqlConnection(_fx.ConnectionString);
        await c.OpenAsync();
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
        var svc = new SettingsService(_fx.ConnectionString, new DpapiProtector());
        var view = (await svc.ListAsync(CancellationToken.None)).Single(v => v.Key == key);
        await svc.UpdateAsync(c, tx, key, valueJson, null, view.Id == 0 ? "0" : view.Version, "调整上限", "tester", CancellationToken.None);
        await tx.CommitAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql, object? param = null)
    {
        await using var c = new SqlConnection(_fx.ConnectionString);
        await c.OpenAsync();
        return (await c.ExecuteScalarAsync<T>(sql, param))!; // brief 原文直接 return T?→CS8603（Dapper 可空注解）；测试标量查询均断言非空结果
    }

    [Fact]
    public async Task Stage_product_creates_batch_then_handler_writes_rows()
    {
        var csv = ProductHeader + "\n" +
            "P001,,6901234567890,阿莫西林胶囊,国药准字H12345678,华北制药,胶囊剂,0.25g,24粒,盒,REQUIRED,,\n" +
            "P002,,,维生素C片,,,,,,盒,EXEMPT,普通化学药豁免,\n" +
            "P003,,,板蓝根颗粒,,,,,,盒,EXEMPT,,\n";
        var (_, batchId, jobId) = await StageAsync("PRODUCT", csv);
        Assert.True(jobId > 0);
        Assert.Equal("STAGED", await ScalarAsync<string>("SELECT status FROM dbo.import_batch WHERE id=@id", new { id = batchId }));
        Assert.Equal(3, await ScalarAsync<int>("SELECT total_rows FROM dbo.import_batch WHERE id=@id", new { id = batchId }));

        await RunStageJobAsync();

        Assert.Equal(2, await ScalarAsync<int>("SELECT success_rows FROM dbo.import_batch WHERE id=@id", new { id = batchId }));
        Assert.Equal(1, await ScalarAsync<int>("SELECT failed_rows FROM dbo.import_batch WHERE id=@id", new { id = batchId }));
        Assert.Equal(3, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.import_row WHERE batch_id=@id", new { id = batchId }));
        var error = await ScalarAsync<string?>("SELECT error FROM dbo.import_row WHERE batch_id=@id AND status=N'ERROR'", new { id = batchId });
        Assert.Contains("classification_reason", error);
        // 原始行完整保留（含 EXEMPT 空 reason 的缺陷行）
        var raw = await ScalarAsync<string>("SELECT raw_json FROM dbo.import_row WHERE batch_id=@id AND status=N'ERROR'", new { id = batchId });
        Assert.Contains("P003", raw);
    }

    [Fact]
    public async Task Stage_hash_mismatch_rejected()
    {
        var csv = ProductHeader + "\nP001,,,药,,,,,,盒,UNKNOWN,,\n";
        await Assert.ThrowsAsync<SettingValidationException>(() => StageAsync("PRODUCT", csv, hashOverride: Hash("别的内容")));
    }

    [Fact]
    public async Task Stage_invalid_base64_rejected()
    {
        var csv = ProductHeader + "\nP001,,,药,,,,,,盒,UNKNOWN,,\n";
        await Assert.ThrowsAsync<SettingValidationException>(() => StageAsync("PRODUCT", csv, contentOverride: "!!!不是Base64!!!"));
    }

    [Fact]
    public async Task Stage_oversize_rejected()
    {
        await SetSettingAsync("import.maxFileBytes", "1024");
        var csv = ProductHeader + "\nP001," + new string('9', 900) + ",,名,,,,,盒,UNKNOWN,,\n";
        await Assert.ThrowsAsync<ImportBoundsException>(() => StageAsync("PRODUCT", csv));
    }

    [Fact]
    public async Task Stage_too_many_rows_rejected()
    {
        await SetSettingAsync("import.maxRows", "1");
        var csv = ProductHeader + "\nP001,,,,药一,,,,,盒,UNKNOWN,,\nP002,,,,药二,,,,,盒,UNKNOWN,,\n";
        await Assert.ThrowsAsync<ImportBoundsException>(() => StageAsync("PRODUCT", csv));
    }

    [Fact]
    public async Task Stage_same_hash_idempotent()
    {
        var csv = ProductHeader + "\nP001,,,药,,,,,,盒,UNKNOWN,,\n";
        var (_, batch1, job1) = await StageAsync("PRODUCT", csv);
        var (_, batch2, job2) = await StageAsync("PRODUCT", csv);
        Assert.Equal(batch1, batch2);
        Assert.Equal(job1, job2); // intent 去重→重选同一作业
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.import_batch"));
    }

    [Fact]
    public async Task Stage_header_only_batch_becomes_failed()
    {
        var (_, batchId, _) = await StageAsync("PRODUCT", ProductHeader + "\n");
        await RunStageJobAsync();
        Assert.Equal("FAILED", await ScalarAsync<string>("SELECT status FROM dbo.import_batch WHERE id=@id", new { id = batchId }));
        await Assert.ThrowsAsync<SettingValidationException>(async () => await CommitAsync(batchId, await BatchVersionAsync(batchId))); // brief lambda 缺 async/await→CS4034/CS4014（两处同型）
    }

    [Fact]
    public async Task Commit_product_update_never_touches_classification()
    {
        await using (var c = new SqlConnection(_fx.ConnectionString))
        {
            await c.OpenAsync();
            var (orgId, sysId) = await TestSeed.EnsureAsync(c);
            await c.ExecuteAsync(@"
INSERT INTO dbo.product(org_id,source_system_id,spid,name,trace_classification,classification_reason)
VALUES(@org,@sys,N'P100',N'旧名',N'REQUIRED',N'处方药')", new { org = orgId, sys = sysId });
        }
        var csv = ProductHeader + "\n" +
            "P100,,,新名,,,,,,盒,EXEMPT,试图覆盖,\n" +
            "P101,,,新药,,,,,,盒,EXEMPT,豁免理由,\n";
        var (_, batchId, _) = await StageAsync("PRODUCT", csv);
        await RunStageJobAsync();
        await CommitAsync(batchId, await BatchVersionAsync(batchId));

        Assert.Equal("新名", await ScalarAsync<string>("SELECT name FROM dbo.product WHERE spid=N'P100'"));
        Assert.Equal("REQUIRED", await ScalarAsync<string>("SELECT trace_classification FROM dbo.product WHERE spid=N'P100'"));
        Assert.Equal("处方药", await ScalarAsync<string>("SELECT classification_reason FROM dbo.product WHERE spid=N'P100'"));
        Assert.Equal("EXEMPT", await ScalarAsync<string>("SELECT trace_classification FROM dbo.product WHERE spid=N'P101'"));
        Assert.Equal("豁免理由", await ScalarAsync<string>("SELECT classification_reason FROM dbo.product WHERE spid=N'P101'"));
        Assert.Equal(2, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.import_row WHERE batch_id=@id AND status=N'APPLIED'", new { id = batchId }));
        Assert.Equal("COMMITTED", await ScalarAsync<string>("SELECT status FROM dbo.import_batch WHERE id=@id", new { id = batchId }));
    }

    [Fact]
    public async Task Commit_version_conflict_and_double_commit()
    {
        var csv = ProductHeader + "\nP001,,,药,,,,,,盒,UNKNOWN,,\n";
        var (_, batchId, _) = await StageAsync("PRODUCT", csv);
        await RunStageJobAsync();
        await Assert.ThrowsAsync<SettingVersionException>(() => CommitAsync(batchId, "0000000000000000"));
        var ver = await BatchVersionAsync(batchId);
        await CommitAsync(batchId, ver);
        // 提交后 ver 已变→旧版本冲突；用新版本再提交→状态守卫 422
        await Assert.ThrowsAsync<SettingVersionException>(() => CommitAsync(batchId, ver));
        await Assert.ThrowsAsync<SettingValidationException>(async () => await CommitAsync(batchId, await BatchVersionAsync(batchId))); // brief lambda 缺 async/await→CS4034/CS4014（两处同型）
    }

    [Fact]
    public async Task Commit_enterprise_insert_only()
    {
        await using (var c = new SqlConnection(_fx.ConnectionString))
        {
            await c.OpenAsync();
            await c.ExecuteAsync("INSERT INTO dbo.platform_enterprise_version(ref_ent_id,ent_id,name) VALUES(N'REF1',N'ENT1',N'旧名')");
        }
        var csv = "ref_ent_id,ent_id,name,credit_code,audit_status,role\n" +
            "REF1,ENT1,新名,91110000MA001,审核通过,SUPPLIER\n" +
            "REF2,ENT2,另一家,,,\n";
        var (_, batchId, _) = await StageAsync("ENTERPRISE", csv);
        await RunStageJobAsync();
        await CommitAsync(batchId, await BatchVersionAsync(batchId));
        Assert.Equal("旧名", await ScalarAsync<string>("SELECT name FROM dbo.platform_enterprise_version WHERE ref_ent_id=N'REF1' AND ent_id=N'ENT1'"));
        Assert.Equal("另一家", await ScalarAsync<string>("SELECT name FROM dbo.platform_enterprise_version WHERE ref_ent_id=N'REF2' AND ent_id=N'ENT2'"));
        Assert.Equal(2, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.platform_enterprise_version"));
    }

    [Fact]
    public async Task Commit_enqueues_match_job_and_audit()
    {
        var csv = ProductHeader + "\nP001,,,药,,,,,,盒,UNKNOWN,,\n";
        var (_, batchId, _) = await StageAsync("PRODUCT", csv);
        await RunStageJobAsync();
        await CommitAsync(batchId, await BatchVersionAsync(batchId));
        Assert.Equal(1, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=N'master-match' AND intent_key=@k",
            new { k = $"match:import:{batchId}" }));
        Assert.Equal(1, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.audit_log WHERE action=N'master.import.commit' AND object_id=@id",
            new { id = batchId.ToString() }));
    }

    // ---- T16 携带项 4（服务级）：COMMITTED/FAILED 批次重暂存→JobId=0；handler 对 COMMITTED 重跑无操作 ----

    [Fact]
    public async Task Stage_committed_batch_restage_returns_zero_job()
    {
        var csv = ProductHeader + "\nP200,,,重暂存药,,,,,,盒,UNKNOWN,,\n";
        var (_, batchId, jobId) = await StageAsync("PRODUCT", csv);
        Assert.True(jobId > 0);
        await RunStageJobAsync();
        await CommitAsync(batchId, await BatchVersionAsync(batchId));
        // StageAsync 重选路径：既有批次非 STAGED→返回 (batchId, 0)，不产生新作业
        var (_, againBatch, againJob) = await StageAsync("PRODUCT", csv);
        Assert.Equal(batchId, againBatch);
        Assert.Equal(0, againJob);
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=N'import-stage'"));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.import_batch"));
    }

    [Fact]
    public async Task Stage_failed_batch_restage_returns_zero_job()
    {
        var csv = ProductHeader + "\n"; // 仅表头→handler 判 FAILED
        var (_, batchId, _) = await StageAsync("PRODUCT", csv);
        await RunStageJobAsync();
        Assert.Equal("FAILED", await ScalarAsync<string>("SELECT status FROM dbo.import_batch WHERE id=@id", new { id = batchId }));
        var (_, againBatch, againJob) = await StageAsync("PRODUCT", csv);
        Assert.Equal(batchId, againBatch);
        Assert.Equal(0, againJob);
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=N'import-stage'"));
    }

    [Fact]
    public async Task Stage_handler_rerun_on_committed_batch_is_noop()
    {
        var csv = ProductHeader + "\nP201,,,重跑药,,,,,,盒,UNKNOWN,,\n";
        var (orgId, batchId, jobId) = await StageAsync("PRODUCT", csv);
        await RunStageJobAsync();
        await CommitAsync(batchId, await BatchVersionAsync(batchId));
        var appliedBefore = await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.import_row WHERE batch_id=@id AND status=N'APPLIED'", new { id = batchId });
        var stagedAuditBefore = await ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.audit_log WHERE action=N'master.import.staged' AND object_id=@id", new { id = batchId.ToString() });
        // 模拟租约失效重跑：以原作业 payload 直接调 handler；status!=STAGED 守卫应无操作（不删行/不重计数/不重复审计）
        var payload = System.Text.Json.JsonSerializer.Serialize(new { orgId, env = "MOCK", batchId });
        var claimed = new ClaimedJob(jobId, "import-stage", $"import-stage:{batchId}", payload,
            1, DateTime.UtcNow.AddMinutes(5), 2);
        await new ImportStageJobHandler(_fx.ConnectionString).HandleAsync(claimed, CancellationToken.None);
        Assert.Equal("COMMITTED", await ScalarAsync<string>("SELECT status FROM dbo.import_batch WHERE id=@id", new { id = batchId }));
        Assert.Equal(appliedBefore, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.import_row WHERE batch_id=@id AND status=N'APPLIED'", new { id = batchId }));
        Assert.Equal(1, await ScalarAsync<int>("SELECT success_rows FROM dbo.import_batch WHERE id=@id", new { id = batchId }));
        Assert.Equal(stagedAuditBefore, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.audit_log WHERE action=N'master.import.staged' AND object_id=@id", new { id = batchId.ToString() }));
    }

    [Fact]
    public async Task Commit_supplier_and_platform_drug_upsert()
    {
        var csv1 = "dwbh,name,credit_code,role\nD100,供应商甲,91110000MA002,SUPPLIER\n";
        var (_, b1, _) = await StageAsync("SUPPLIER", csv1);
        await RunStageJobAsync();
        await CommitAsync(b1, await BatchVersionAsync(b1));
        Assert.Equal("供应商甲", await ScalarAsync<string>("SELECT name FROM dbo.supplier WHERE dwbh=N'D100'"));

        var csv2 = "dwbh,name,credit_code,role\nD100,供应商乙,91110000MA002,SUPPLIER\n";
        var (_, b2, _) = await StageAsync("SUPPLIER", csv2);
        await RunStageJobAsync();
        await CommitAsync(b2, await BatchVersionAsync(b2));
        Assert.Equal("供应商乙", await ScalarAsync<string>("SELECT name FROM dbo.supplier WHERE dwbh=N'D100'"));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.supplier"));

        var csv3 = "platform_drug_id,approval_no,manufacturer_identity,dosage_form,strength,package_spec,source_kind\n" +
            "PD100,国药准字H12345678,华北制药,胶囊剂,0.25g,24粒,MANUAL\n";
        var (_, b3, _) = await StageAsync("PLATFORM_DRUG", csv3);
        await RunStageJobAsync();
        await CommitAsync(b3, await BatchVersionAsync(b3));
        Assert.Equal("国药准字H12345678", await ScalarAsync<string>("SELECT approval_no_norm FROM dbo.platform_drug WHERE platform_drug_id=N'PD100'"));
        Assert.Equal("MANUAL", await ScalarAsync<string>("SELECT source_kind FROM dbo.platform_drug WHERE platform_drug_id=N'PD100'"));
    }
}
