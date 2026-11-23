using System.Runtime.Versioning; // DpapiProtector 仅 Windows（CA1416），与 RetailTaskTests 同标注
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Domain.Codes;
using TraceFlow.Infrastructure.Jobs;
using TraceFlow.Infrastructure.Sales;
using TraceFlow.Infrastructure.Security;
using TraceFlow.Infrastructure.Settings;
using TraceFlow.Infrastructure.Sources;
using TraceFlow.Infrastructure.Top;
using TraceFlow.IntegrationTests.Sources;
using Xunit;

namespace TraceFlow.IntegrationTests.Sales;

/// <summary>
/// T2 销售核验（SALE-01..04）：本地规则失败/依赖等待/平台证据/幂等/分批/退货占位/作业重排/调度兜底。
/// 每测试独立库；数据（org/系统/快照/行/产品/映射/平台药）全部直接 SQL 播种，平台一侧只注入 Fake 网关委托，
/// 不依赖 fixtures 目录、不触网、不写 ERP。
/// </summary>
[SupportedOSPlatform("windows")]
public class SaleVerificationTests : IAsyncLifetime
{
    private readonly Sql2008Fixture _fx = new();
    public Task InitializeAsync() => _fx.InitializeAsync();
    public Task DisposeAsync() => _fx.DisposeAsync();

    private sealed record LineSeed(string Key, string Spid, decimal Qty, string EventKind, string? Code,
        string Classification = "REQUIRED", string? Reason = null, bool Mapped = true);

    private sealed record Seed(long OrgId, long SysId, long SnapshotId, IReadOnlyList<(long LineId, string Key)> Lines,
        IReadOnlyDictionary<string, long> CodeIds);

    private SaleVerificationService NewService(Func<TopCall, TopRawResponse> handler)
    {
        var settings = new SettingsService(_fx.ConnectionString, new DpapiProtector());
        var audit = new AuditLog();
        var queue = new JobQueue(_fx.ConnectionString, audit);
        var top = new TopClientService(_fx.ConnectionString, new FakeTopGateway(handler), new RateBudgetService(),
            new ChannelPauseService(), audit);
        return new SaleVerificationService(_fx.ConnectionString, "MOCK", settings, top,
            new RetailTaskFactory(settings, queue), queue, audit);
    }

    private static TopRawResponse Instore() => new(true, 200, null,
        """{"response_success":true,"request_id":"r-instore","result":{"_verified":false,"model":{"exists":true,"instore":true}}}""", null);

    private static TopRawResponse ExistsNotInstore() => new(true, 200, null,
        """{"response_success":true,"request_id":"r-notinstore","result":{"_verified":false,"model":{"exists":true,"instore":false}}}""", null);

    private static TopRawResponse RateLimited() => new(true, 200,
        """{"code":7,"sub_code":"FAIL_BIZ_RATE_LIMIT","sub_msg":"触发限流"}""", null, 30);

    /// <summary>20 位 8 前缀码（同库内按 n 唯一）。</summary>
    private static string Code(int n) => "8" + n.ToString("D19");

    private static string Sha256(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    private static async Task EnsureProductAsync(SqlConnection c, long orgId, long sysId, string spid,
        string classification, string? reason, bool mapped)
    {
        await c.ExecuteAsync(@"
IF NOT EXISTS (SELECT 1 FROM dbo.product WHERE org_id=@orgId AND source_system_id=@sysId AND spid=@spid)
INSERT INTO dbo.product(org_id,source_system_id,spid,name,trace_classification,classification_reason)
VALUES(@orgId,@sysId,@spid,@spid,@cls,@reason)",
            new { orgId, sysId, spid, cls = classification, reason });
        if (!mapped) return; // 缺映射 → 核验须按 UNKNOWN 阻断
        var drugId = await c.ExecuteScalarAsync<long>(@"
IF NOT EXISTS (SELECT 1 FROM dbo.platform_drug WHERE platform_drug_id=@pid)
INSERT INTO dbo.platform_drug(platform_drug_id,source_kind) VALUES(@pid,N'FIXTURE');
SELECT id FROM dbo.platform_drug WHERE platform_drug_id=@pid", new { pid = "DRUG-" + spid });
        var productId = await c.ExecuteScalarAsync<long>(
            "SELECT id FROM dbo.product WHERE org_id=@orgId AND source_system_id=@sysId AND spid=@spid",
            new { orgId, sysId, spid });
        await c.ExecuteAsync(@"
IF NOT EXISTS (SELECT 1 FROM dbo.product_mapping
               WHERE product_id=@productId AND state IN (N'AUTO_CONFIRMED',N'MANUAL_CONFIRMED'))
INSERT INTO dbo.product_mapping(org_id,product_id,platform_drug_id,state)
VALUES(@orgId,@productId,@drugId,N'AUTO_CONFIRMED')", new { orgId, productId, drugId });
    }

    private static async Task<long> EnsureCodeAsync(SqlConnection c, string code)
        => await c.ExecuteScalarAsync<long>(@"
IF NOT EXISTS (SELECT 1 FROM dbo.trace_code WHERE code_normalized=@code)
INSERT INTO dbo.trace_code(code_normalized,code_raw,format) VALUES(@code,@code,N'S8P20');
SELECT id FROM dbo.trace_code WHERE code_normalized=@code", new { code });

    private async Task<Seed> SeedSaleAsync(SqlConnection c, string docKey, params LineSeed[] lines)
    {
        var (orgId, sysId) = await TestSeed.EnsureAsync(c);
        foreach (var spid in lines.Select(l => l.Spid).Distinct(StringComparer.Ordinal))
        {
            var spec = lines.First(l => l.Spid == spid);
            await EnsureProductAsync(c, orgId, sysId, spid, spec.Classification, spec.Reason, spec.Mapped);
        }
        var snapId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.source_snapshot(org_id,source_system_id,source_document_key,document_type,version,completeness_status,content_hash,business_datetime)
OUTPUT INSERTED.id VALUES(@orgId,@sysId,@key,N'SALE',1,N'COMPLETE',@hash,@biz)",
            new { orgId, sysId, key = docKey, hash = Sha256(docKey), biz = new DateTime(2026, 11, 20, 6, 0, 0) });
        var lineIds = new List<(long LineId, string Key)>();
        var codeIds = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var normalized = TraceCodeNormalizer.Normalize(line.Code);
            var lineId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.source_line(snapshot_id,source_line_key,product_source_id,quantity_original,unit_original,batch_raw,
    trace_code_raw,trace_code_normalized,code_format,line_hash,event_kind)
OUTPUT INSERTED.id VALUES(@snapId,@key,@spid,@qty,N'盒',N'B2026',@raw,@norm,@fmt,@hash,@kind)",
                new
                {
                    snapId, key = line.Key, spid = line.Spid, qty = line.Qty, raw = line.Code,
                    norm = normalized.Normalized, fmt = CodeFormatStorage.ToToken(normalized.Format),
                    hash = Sha256(docKey + "|" + line.Key), kind = line.EventKind,
                });
            lineIds.Add((lineId, line.Key));
            if (normalized.Normalized is not null && !codeIds.ContainsKey(normalized.Normalized))
                codeIds[normalized.Normalized] = await EnsureCodeAsync(c, normalized.Normalized);
        }
        return new Seed(orgId, sysId, snapId, lineIds, codeIds);
    }

    /// <summary>播种前序采购入库：code_event(PURCHASE_IN) 指向指定状态的任务（Succeeded 才满足依赖）。</summary>
    private static async Task SeedPurchaseAsync(SqlConnection c, Seed seed, string code, string state)
    {
        var taskId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upload_task(org_id,env,event_key,task_type,source_hash,payload_hash,state,source_integrity,match_state,
    verification_state,dependency_state)
OUTPUT INSERTED.id
VALUES(@orgId,N'MOCK',@key,N'PURCHASE_IN_102',@hash,@hash,@state,N'COMPLETE',N'COMPLETE',N'PASSED',N'NONE')",
            new { orgId = seed.OrgId, key = $"KSOA:{code}:PURCHASE_IN", hash = Sha256(code), state });
        await c.ExecuteAsync(@"
INSERT INTO dbo.code_event(org_id,env,code_id,event_type,direction,upload_task_id)
VALUES(@orgId,N'MOCK',@codeId,N'PURCHASE_IN',N'IN',@taskId)",
            new { orgId = seed.OrgId, codeId = seed.CodeIds[code], taskId });
    }

    private async Task SetSettingAsync(string key, string value)
    {
        await using var c = await _fx.OpenAsync();
        await c.ExecuteAsync(@"
IF NOT EXISTS (SELECT 1 FROM dbo.app_setting WHERE setting_key=@k)
    INSERT INTO dbo.app_setting(setting_key,value_json,updated_by) VALUES(@k,@v,N'test');
ELSE UPDATE dbo.app_setting SET value_json=@v, updated_by=N'test' WHERE setting_key=@k",
            new { k = key, v = value });
    }

    private async Task<T> ScalarAsync<T>(string sql, object? param = null)
    {
        await using var c = await _fx.OpenAsync();
        return (await c.ExecuteScalarAsync<T>(sql, param))!;
    }

    // ---------- 用例 ----------

    [Fact]
    public async Task Passed_sale_creates_verification_rows_and_sale_task_ready()
    {
        await using var c = await _fx.OpenAsync();
        var code = Code(1);
        var seed = await SeedSaleAsync(c, "SALE-V1",
            new LineSeed("1", "SP0001", 1, "SALE", code),
            new LineSeed("2", "SP0002", 2, "SALE", null, "EXEMPT", "OTC 不纳入追溯"));
        await SeedPurchaseAsync(c, seed, code, "Succeeded");
        var calls = 0;
        var svc = NewService(_ => { calls++; return Instore(); });

        var id = await svc.VerifySnapshotAsync(seed.SnapshotId, default);

        Assert.NotNull(id);
        Assert.Equal(0, calls); // 依赖已由采购入库成功满足：不查平台
        Assert.Equal("PASSED", await ScalarAsync<string>("SELECT state FROM dbo.verification WHERE id=@id", new { id }));
        Assert.Equal("KSOA:SALE-V1:SALE", await ScalarAsync<string>("SELECT event_key FROM dbo.verification WHERE id=@id", new { id }));
        Assert.Equal("M3-1", await ScalarAsync<string>("SELECT rule_version FROM dbo.verification WHERE id=@id", new { id }));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.verification WHERE is_current=1"));
        Assert.Equal("PASS", await ScalarAsync<string>(
            "SELECT result FROM dbo.verification_code WHERE verification_id=@id", new { id }));
        Assert.Equal(1, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.verification_code WHERE verification_id=@id", new { id })); // EXEMPT 行不入逐码结论
        Assert.Equal("Ready", await ScalarAsync<string>(
            "SELECT state FROM dbo.upload_task WHERE event_key=N'KSOA:SALE-V1:RETAIL_321'"));
        Assert.Equal("PASSED", await ScalarAsync<string>(
            "SELECT verification_state FROM dbo.upload_task WHERE event_key=N'KSOA:SALE-V1:RETAIL_321'"));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.upload_part"));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.code_event WHERE event_type=N'SALE' AND direction=N'OUT'"));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.code_claim WHERE claim_state=N'ACTIVE'"));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=N'upload-dispatch'"));
    }

    [Fact]
    public async Task Unknown_classification_blocks_with_UNKNOWN_CLASSIFICATION_and_no_task()
    {
        await using var c = await _fx.OpenAsync();
        var seed = await SeedSaleAsync(c, "SALE-V2", new LineSeed("1", "SPUNK", 1, "SALE", Code(2), "UNKNOWN"));

        var id = await NewService(_ => Instore()).VerifySnapshotAsync(seed.SnapshotId, default);

        Assert.Equal("FAILED", await ScalarAsync<string>("SELECT state FROM dbo.verification WHERE id=@id", new { id }));
        Assert.Contains("UNKNOWN_CLASSIFICATION",
            await ScalarAsync<string>("SELECT result_json FROM dbo.verification WHERE id=@id", new { id }));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.upload_task")); // 本地失败且无既有任务：不建任务
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.verification_code"));
    }

    [Fact]
    public async Task Missing_mapping_is_treated_as_UNKNOWN_classification()
    {
        await using var c = await _fx.OpenAsync();
        var seed = await SeedSaleAsync(c, "SALE-V2B",
            new LineSeed("1", "SPNOMAP", 1, "SALE", Code(21), Mapped: false));

        var id = await NewService(_ => Instore()).VerifySnapshotAsync(seed.SnapshotId, default);

        Assert.Equal("FAILED", await ScalarAsync<string>("SELECT state FROM dbo.verification WHERE id=@id", new { id }));
        Assert.Contains("UNKNOWN_CLASSIFICATION",
            await ScalarAsync<string>("SELECT result_json FROM dbo.verification WHERE id=@id", new { id }));
    }

    [Fact]
    public async Task Missing_code_blocks_with_MISSING_CODE()
    {
        await using var c = await _fx.OpenAsync();
        var seed = await SeedSaleAsync(c, "SALE-V3", new LineSeed("1", "SP0001", 1, "SALE", null));

        var id = await NewService(_ => Instore()).VerifySnapshotAsync(seed.SnapshotId, default);

        Assert.Equal("FAILED", await ScalarAsync<string>("SELECT state FROM dbo.verification WHERE id=@id", new { id }));
        Assert.Contains("MISSING_CODE",
            await ScalarAsync<string>("SELECT result_json FROM dbo.verification WHERE id=@id", new { id }));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.upload_task"));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.verification_code"));
    }

    [Fact]
    public async Task Dependency_wait_blocks_with_WAIT_DEPENDENCY()
    {
        await using var c = await _fx.OpenAsync();
        var code = Code(4);
        var seed = await SeedSaleAsync(c, "SALE-V4", new LineSeed("1", "SP0001", 1, "SALE", code));
        var calls = 0;
        var svc = NewService(call =>
        {
            calls++;
            Assert.Equal(SaleVerificationService.CodedetailMethod, call.Method);
            Assert.Equal(code, call.Parameters["code"]);
            return ExistsNotInstore();
        });

        var id = await svc.VerifySnapshotAsync(seed.SnapshotId, default);

        Assert.Equal(1, calls); // 同码一次运行只查一次平台
        Assert.Equal("UNRESOLVED", await ScalarAsync<string>("SELECT state FROM dbo.verification WHERE id=@id", new { id }));
        Assert.Equal("Blocked", await ScalarAsync<string>(
            "SELECT state FROM dbo.upload_task WHERE event_key=N'KSOA:SALE-V4:RETAIL_321'"));
        Assert.Equal("WAIT_DEPENDENCY", await ScalarAsync<string>(
            "SELECT state_reason FROM dbo.upload_task WHERE event_key=N'KSOA:SALE-V4:RETAIL_321'"));
        Assert.Equal("WAIT", await ScalarAsync<string>(
            "SELECT dependency_state FROM dbo.upload_task WHERE event_key=N'KSOA:SALE-V4:RETAIL_321'"));
        Assert.Equal("UNKNOWN", await ScalarAsync<string>(
            "SELECT result FROM dbo.verification_code WHERE verification_id=@id", new { id }));
        Assert.Equal("WAIT_DEPENDENCY", await ScalarAsync<string>(
            "SELECT reason FROM dbo.verification_code WHERE verification_id=@id", new { id }));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.upload_part"));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.code_claim"));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.outbox_job"));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.code_event"));
    }

    [Fact]
    public async Task Platform_instore_evidence_satisfies_dependency()
    {
        await using var c = await _fx.OpenAsync();
        var code = Code(5);
        var seed = await SeedSaleAsync(c, "SALE-V5", new LineSeed("1", "SP0001", 1, "SALE", code));

        var id = await NewService(_ => Instore()).VerifySnapshotAsync(seed.SnapshotId, default);

        Assert.Equal("PASSED", await ScalarAsync<string>("SELECT state FROM dbo.verification WHERE id=@id", new { id }));
        Assert.Equal("PASS", await ScalarAsync<string>(
            "SELECT result FROM dbo.verification_code WHERE verification_id=@id", new { id }));
        Assert.Equal("PLATFORM_INSTORE", await ScalarAsync<string>(
            "SELECT reason FROM dbo.verification_code WHERE verification_id=@id", new { id }));
        Assert.Equal("Ready", await ScalarAsync<string>(
            "SELECT state FROM dbo.upload_task WHERE event_key=N'KSOA:SALE-V5:RETAIL_321'"));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.upload_part"));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.code_claim WHERE event_scope=N'RETAIL:321'"));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=N'upload-dispatch'"));
    }

    [Fact]
    public async Task Purchase_success_reenqueues_sale_verify()
    {
        await using var c = await _fx.OpenAsync();
        var code = Code(6);
        var seed = await SeedSaleAsync(c, "SALE-V6", new LineSeed("1", "SP0001", 1, "SALE", code));
        await SeedPurchaseAsync(c, seed, code, "Processing");
        var svc = NewService(_ => ExistsNotInstore());

        var first = await svc.VerifySnapshotAsync(seed.SnapshotId, default);
        Assert.Equal("UNRESOLVED", await ScalarAsync<string>("SELECT state FROM dbo.verification WHERE id=@id", new { id = first }));
        Assert.Equal("WAIT_DEPENDENCY", await ScalarAsync<string>(
            "SELECT state_reason FROM dbo.upload_task WHERE event_key=N'KSOA:SALE-V6:RETAIL_321'"));

        await c.ExecuteAsync("UPDATE dbo.upload_task SET state=N'Succeeded' WHERE event_key=@k",
            new { k = "KSOA:" + code + ":PURCHASE_IN" });

        // Task 5 语义：采购成功后在同一事务内重唤醒
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            var enqueued = await svc.EnqueueDependentVerificationsAsync(c, tx, new[] { seed.CodeIds[code] }, seed.OrgId,
                "MOCK", default);
            Assert.Equal(1, enqueued);
            await tx.CommitAsync();
        }
        Assert.Equal(1, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=N'sale-verify'"));

        var second = await NewService(_ => throw new InvalidOperationException("依赖已满足，不应再查平台"))
            .VerifySnapshotAsync(seed.SnapshotId, default);
        Assert.Equal("PASSED", await ScalarAsync<string>("SELECT state FROM dbo.verification WHERE id=@id", new { id = second }));
        Assert.Equal("DEPENDENCY_PURCHASE_IN_SUCCEEDED", await ScalarAsync<string>(
            "SELECT reason FROM dbo.verification_code WHERE verification_id=@id", new { id = second }));
        Assert.Equal("Ready", await ScalarAsync<string>(
            "SELECT state FROM dbo.upload_task WHERE event_key=N'KSOA:SALE-V6:RETAIL_321'"));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.upload_task WHERE task_type=N'RETAIL_321'"));
    }

    [Fact]
    public async Task EnqueueDependentVerifications_enqueues_wait_tasks()
    {
        await using var c = await _fx.OpenAsync();
        var waiting = Code(7);
        var passing = Code(8);
        var waitSeed = await SeedSaleAsync(c, "SALE-V7W", new LineSeed("1", "SP0001", 1, "SALE", waiting));
        var passSeed = await SeedSaleAsync(c, "SALE-V7P", new LineSeed("1", "SP0001", 1, "SALE", passing));
        await SeedPurchaseAsync(c, passSeed, passing, "Succeeded");
        var svc = NewService(_ => ExistsNotInstore());
        await svc.VerifySnapshotAsync(waitSeed.SnapshotId, default);
        await svc.VerifySnapshotAsync(passSeed.SnapshotId, default);

        int enqueued;
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            enqueued = await svc.EnqueueDependentVerificationsAsync(c, tx,
                new[] { waitSeed.CodeIds[waiting], passSeed.CodeIds[passing] }, waitSeed.OrgId, "MOCK", default);
            Assert.Equal(0, await svc.EnqueueDependentVerificationsAsync(c, tx, Array.Empty<long>(), waitSeed.OrgId,
                "MOCK", default));
            await tx.CommitAsync();
        }

        Assert.Equal(1, enqueued); // 仅 WAIT_DEPENDENCY 快照被重唤醒（PASSED 的任务不在 Blocked 状态）
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=N'sale-verify'"));
        Assert.Equal(1, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=N'sale-verify' AND intent_key LIKE @p",
            new { p = $"snapshot:{waitSeed.SnapshotId}:dep:%" }));
        Assert.Contains(waitSeed.SnapshotId.ToString(), await ScalarAsync<string>(
            "SELECT payload_json FROM dbo.outbox_job WHERE job_kind=N'sale-verify'"));
    }

    [Fact]
    public async Task Rerun_same_snapshot_is_idempotent()
    {
        await using var c = await _fx.OpenAsync();
        var code = Code(9);
        var seed = await SeedSaleAsync(c, "SALE-V8", new LineSeed("1", "SP0001", 1, "SALE", code));
        await SeedPurchaseAsync(c, seed, code, "Succeeded");
        var svc = NewService(_ => throw new InvalidOperationException("依赖已满足，不应查平台"));

        var first = await svc.VerifySnapshotAsync(seed.SnapshotId, default);
        var second = await svc.VerifySnapshotAsync(seed.SnapshotId, default);

        Assert.Equal(first, second); // 返回既有结论 id
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.verification"));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.upload_task WHERE task_type=N'RETAIL_321'"));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.upload_part"));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=N'upload-dispatch'"));
    }

    [Fact]
    public async Task Max_codes_per_run_defers_remainder_to_next_job()
    {
        await SetSettingAsync("sale.verify.maxCodesPerRun", "2");
        await using var c = await _fx.OpenAsync();
        var c1 = Code(10);
        var c2 = Code(11);
        var c3 = Code(12);
        var seed = await SeedSaleAsync(c, "SALE-V9",
            new LineSeed("1", "SP0001", 1, "SALE", c1),
            new LineSeed("2", "SP0001", 1, "SALE", c2),
            new LineSeed("3", "SP0001", 1, "SALE", c3));
        foreach (var code in new[] { c1, c2, c3 }) await SeedPurchaseAsync(c, seed, code, "Succeeded");
        var svc = NewService(_ => throw new InvalidOperationException("依赖已满足，不应查平台"));

        var first = await svc.VerifySnapshotAsync(seed.SnapshotId, default);

        Assert.Equal("UNRESOLVED", await ScalarAsync<string>("SELECT state FROM dbo.verification WHERE id=@id", new { id = first }));
        Assert.Equal(2, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.verification_code WHERE verification_id=@id", new { id = first }));
        Assert.Equal("WAIT_VERIFY", await ScalarAsync<string>(
            "SELECT state_reason FROM dbo.upload_task WHERE event_key=N'KSOA:SALE-V9:RETAIL_321'"));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.upload_part"));

        var second = await svc.VerifySnapshotAsync(seed.SnapshotId, default);

        Assert.NotEqual(first, second);
        Assert.Equal("PASSED", await ScalarAsync<string>("SELECT state FROM dbo.verification WHERE id=@id", new { id = second }));
        Assert.Equal(3, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.verification_code WHERE verification_id=@id", new { id = second }));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.verification WHERE is_current=1"));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.upload_task WHERE task_type=N'RETAIL_321'"));
        Assert.Equal("Ready", await ScalarAsync<string>(
            "SELECT state FROM dbo.upload_task WHERE event_key=N'KSOA:SALE-V9:RETAIL_321'"));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.upload_part"));
    }

    [Fact]
    public async Task Mixed_document_blocks_return_task_without_parts_or_outbox()
    {
        await using var c = await _fx.OpenAsync();
        var saleCode = Code(13);
        var returnCode = Code(14);
        var seed = await SeedSaleAsync(c, "SALE-V10",
            new LineSeed("1", "SP0001", 1, "SALE", saleCode),
            new LineSeed("2", "SP0001", -1, "CONSUMER_RETURN", returnCode));
        await SeedPurchaseAsync(c, seed, saleCode, "Succeeded");

        var id = await NewService(_ => throw new InvalidOperationException("依赖已满足，不应查平台"))
            .VerifySnapshotAsync(seed.SnapshotId, default);

        Assert.Equal("PASSED", await ScalarAsync<string>("SELECT state FROM dbo.verification WHERE id=@id", new { id }));
        var returnTaskId = await ScalarAsync<long>(
            "SELECT id FROM dbo.upload_task WHERE event_key=N'KSOA:SALE-V10:CONSUMER_RETURN_116'");
        Assert.Equal("Blocked", await ScalarAsync<string>("SELECT state FROM dbo.upload_task WHERE id=@id", new { id = returnTaskId }));
        Assert.Equal("RETURN_CONFIRM_REQUIRED", await ScalarAsync<string>(
            "SELECT state_reason FROM dbo.upload_task WHERE id=@id", new { id = returnTaskId }));
        Assert.Equal("PASSED", await ScalarAsync<string>(
            "SELECT verification_state FROM dbo.upload_task WHERE id=@id", new { id = returnTaskId }));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.upload_part WHERE task_id=@id", new { id = returnTaskId }));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.code_claim WHERE upload_task_id=@id", new { id = returnTaskId }));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.outbox_job")); // 只有 321 的 upload-dispatch
        // 退货占位载荷只含退货行（不可变），且退货码不进逐码结论（未做平台核验）
        Assert.Contains(returnCode, await ScalarAsync<string>(
            "SELECT payload_json FROM dbo.upload_task WHERE id=@id", new { id = returnTaskId }));
        Assert.DoesNotContain(saleCode, await ScalarAsync<string>(
            "SELECT payload_json FROM dbo.upload_task WHERE id=@id", new { id = returnTaskId }));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.verification_code"));
        Assert.Equal(saleCode, await ScalarAsync<string>(@"
SELECT tc.code_normalized FROM dbo.verification_code vc
JOIN dbo.trace_code tc ON tc.id=vc.code_id WHERE vc.verification_id=@id", new { id }));
    }

    [Fact]
    public async Task Wait_retry_job_is_rescheduled()
    {
        await using var c = await _fx.OpenAsync();
        var seed = await SeedSaleAsync(c, "SALE-V11", new LineSeed("1", "SP0001", 1, "SALE", Code(15)));
        var audit = new AuditLog();
        var queue = new JobQueue(_fx.ConnectionString, audit);
        var payload = JsonSerializer.Serialize(new { orgId = seed.OrgId, env = "MOCK", snapshotId = seed.SnapshotId });
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            await queue.EnqueueAsync(c, tx, seed.OrgId, "MOCK", SaleVerificationService.JobKind,
                $"snapshot:{seed.SnapshotId}", payload, DateTime.UtcNow, default);
            await tx.CommitAsync();
        }
        var job = await queue.ClaimAsync("test-worker", "MOCK", new[] { SaleVerificationService.JobKind },
            TimeSpan.FromMinutes(5), default);
        Assert.NotNull(job);

        var handler = new SaleVerifyJobHandler(NewService(_ => RateLimited()), queue, _fx.ConnectionString);
        await handler.HandleAsync(job!, default);

        Assert.Equal("WAIT_RETRY", await ScalarAsync<string>("SELECT state FROM dbo.verification WHERE source_snapshot_id=@s",
            new { s = seed.SnapshotId }));
        Assert.Equal("PENDING", await ScalarAsync<string>("SELECT job_state FROM dbo.outbox_job WHERE id=@id", new { id = job!.Id }));
        Assert.Equal("WAIT_RETRY", await ScalarAsync<string>("SELECT last_error FROM dbo.outbox_job WHERE id=@id", new { id = job.Id }));
        Assert.Null(await ScalarAsync<string>("SELECT lease_owner FROM dbo.outbox_job WHERE id=@id", new { id = job.Id }));
        var nextRun = await ScalarAsync<DateTime>("SELECT next_run_at FROM dbo.outbox_job WHERE id=@id", new { id = job.Id });
        Assert.True(nextRun > DateTime.UtcNow.AddSeconds(5), $"next_run_at={nextRun:O} 未按退避推后");
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.upload_task"));
    }

    [Fact]
    public async Task Source_sync_enqueues_sale_verify_for_complete_sale_snapshot()
    {
        await using var c = await _fx.OpenAsync();
        var seed = await SeedSaleAsync(c, "SALE-V12", new LineSeed("1", "SP0001", 1, "SALE", Code(16)));
        var emptyDir = Path.Combine(Path.GetTempPath(), "tf-nofix-" + Guid.NewGuid().ToString("N"));
        var audit = new AuditLog();
        var queue = new JobQueue(_fx.ConnectionString, audit);
        var handler = new SourceSyncJobHandler(_fx.ConnectionString, new FixtureKsoaAdapter(emptyDir),
            new SettingsService(_fx.ConnectionString, new DpapiProtector()), audit, queue);
        var start = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc);
        var payload = JsonSerializer.Serialize(new
        {
            orgId = seed.OrgId, env = "MOCK", entity = "sale", trigger = "manual",
            startUtc = start.ToString("O"), endUtc = end.ToString("O"),
        });
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            await queue.EnqueueAsync(c, tx, seed.OrgId, "MOCK", "source-sync",
                "sync-" + Guid.NewGuid().ToString("N"), payload, DateTime.UtcNow, default);
            await tx.CommitAsync();
        }
        var job = await queue.ClaimAsync("test-worker", "MOCK", new[] { "source-sync" },
            TimeSpan.FromMinutes(5), default);
        Assert.NotNull(job);

        await handler.HandleAsync(job!, default);

        Assert.Equal(1, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=N'sale-verify'"));
        Assert.Equal(1, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=N'sale-verify' AND intent_key=@k",
            new { k = $"snapshot:{seed.SnapshotId}" }));
    }
}
