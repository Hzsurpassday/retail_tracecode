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

    private static TopRawResponse PlatformNotFound() => new(true, 200, null,
        """{"response_success":true,"request_id":"r-404","result":{"_verified":false,"model":{"exists":false,"instore":false}}}""", null);

    private static TopRawResponse BusinessRejected() => new(true, 200, null,
        """{"response_success":false,"msg_code":"40004","sub_msg":"码不在当前企业","request_id":"r-rejected"}""", null);

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
        => await SeedSaleVersionAsync(c, docKey, 1, lines);

    /// <summary>同单据新版本快照（version 递增、来源摘要随版本变化）：验证规则版本/退货占位刷新等重算路径。</summary>
    private async Task<Seed> SeedSaleVersionAsync(SqlConnection c, string docKey, int version, params LineSeed[] lines)
    {
        var (orgId, sysId) = await TestSeed.EnsureAsync(c);
        foreach (var spid in lines.Select(l => l.Spid).Distinct(StringComparer.Ordinal))
        {
            var spec = lines.First(l => l.Spid == spid);
            await EnsureProductAsync(c, orgId, sysId, spid, spec.Classification, spec.Reason, spec.Mapped);
        }
        var snapId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.source_snapshot(org_id,source_system_id,source_document_key,document_type,version,completeness_status,content_hash,business_datetime)
OUTPUT INSERTED.id VALUES(@orgId,@sysId,@key,N'SALE',@ver,N'COMPLETE',@hash,@biz)",
            new { orgId, sysId, key = docKey, ver = version, hash = Sha256(docKey + "#" + version),
                  biz = new DateTime(2026, 11, 20, 6, 0, 0) });
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
                    hash = Sha256(docKey + "#" + version + "|" + line.Key), kind = line.EventKind,
                });
            lineIds.Add((lineId, line.Key));
            if (normalized.Normalized is not null && !codeIds.ContainsKey(normalized.Normalized))
                codeIds[normalized.Normalized] = await EnsureCodeAsync(c, normalized.Normalized);
        }
        return new Seed(orgId, sysId, snapId, lineIds, codeIds);
    }

    /// <summary>他人的 321 任务 + 同码 ACTIVE 占用：用于复核阻断不越界与 CODE_CONTENDED 争用。</summary>
    private static async Task<long> SeedOtherTaskWithClaimAsync(SqlConnection c, long orgId, string code, string key)
    {
        var codeId = await EnsureCodeAsync(c, code);
        var taskId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upload_task(org_id,env,event_key,task_type,source_hash,payload_hash,state,source_integrity,match_state,
    verification_state,dependency_state)
OUTPUT INSERTED.id
VALUES(@orgId,N'MOCK',@key,N'RETAIL_321',@hash,@hash,N'Blocked',N'COMPLETE',N'COMPLETE',N'PASSED',N'NONE')",
            new { orgId, key = "KSOA:" + key + ":RETAIL_321", hash = Sha256(key) });
        await c.ExecuteAsync(@"
INSERT INTO dbo.code_claim(org_id,env,code_id,event_scope,upload_task_id,claim_state)
VALUES(@orgId,N'MOCK',@codeId,N'RETAIL:321',@taskId,N'ACTIVE')",
            new { orgId, codeId, taskId });
        return taskId;
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

    // ---------- Fix Round 1（评审 HIGH 1/2/3、MEDIUM 4/5/7） ----------

    [Fact]
    public async Task Return_only_doc_creates_116_placeholder_and_no_321()
    {
        await using var c = await _fx.OpenAsync();
        var returnCode = Code(20);
        var seed = await SeedSaleAsync(c, "SALE-F1",
            new LineSeed("1", "SP0001", -1, "CONSUMER_RETURN", returnCode));

        var id = await NewService(_ => throw new InvalidOperationException("退货行不做平台核验"))
            .VerifySnapshotAsync(seed.SnapshotId, default);

        Assert.Equal("PASSED", await ScalarAsync<string>("SELECT state FROM dbo.verification WHERE id=@id", new { id }));
        Assert.Equal(1, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.verification WHERE id=@id AND upload_task_id IS NULL", new { id }));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.upload_task WHERE task_type=N'RETAIL_321'"));
        var returnTaskId = await ScalarAsync<long>(
            "SELECT id FROM dbo.upload_task WHERE event_key=N'KSOA:SALE-F1:CONSUMER_RETURN_116'");
        Assert.Equal("Blocked", await ScalarAsync<string>("SELECT state FROM dbo.upload_task WHERE id=@id", new { id = returnTaskId }));
        Assert.Equal("RETURN_CONFIRM_REQUIRED", await ScalarAsync<string>(
            "SELECT state_reason FROM dbo.upload_task WHERE id=@id", new { id = returnTaskId }));
        Assert.Contains(returnCode, await ScalarAsync<string>(
            "SELECT payload_json FROM dbo.upload_task WHERE id=@id", new { id = returnTaskId }));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.upload_part"));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.code_claim"));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.outbox_job"));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.verification_code"));
    }

    [Fact]
    public async Task All_exempt_doc_passes_without_task()
    {
        await using var c = await _fx.OpenAsync();
        var seed = await SeedSaleAsync(c, "SALE-F2",
            new LineSeed("1", "SP0001", 2, "SALE", null, "EXEMPT", "OTC 不纳入追溯"));

        var id = await NewService(_ => throw new InvalidOperationException("全 EXEMPT 不应查平台"))
            .VerifySnapshotAsync(seed.SnapshotId, default);

        Assert.Equal("PASSED", await ScalarAsync<string>("SELECT state FROM dbo.verification WHERE id=@id", new { id }));
        Assert.Contains(SaleVerificationService.ReasonNoUploadableLines, await ScalarAsync<string>(
            "SELECT result_json FROM dbo.verification WHERE id=@id", new { id }));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.upload_task"));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.verification_code"));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.outbox_job"));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.code_claim"));
    }

    [Fact]
    public async Task Rule_version_change_reevaluates_passed_snapshot()
    {
        await using var c = await _fx.OpenAsync();
        var code = Code(21);
        var seed = await SeedSaleAsync(c, "SALE-F3", new LineSeed("1", "SP0001", 1, "SALE", code));
        await SeedPurchaseAsync(c, seed, code, "Succeeded");
        var svc = NewService(_ => throw new InvalidOperationException("依赖已满足，不应查平台"));

        var first = await svc.VerifySnapshotAsync(seed.SnapshotId, default);
        Assert.Equal("PASSED", await ScalarAsync<string>("SELECT state FROM dbo.verification WHERE id=@id", new { id = first }));
        Assert.Equal("M3-1", await ScalarAsync<string>("SELECT rule_version FROM dbo.verification WHERE id=@id", new { id = first }));

        await SetSettingAsync("sale.verify.ruleVersion", "\"M3-2\"");
        var second = await svc.VerifySnapshotAsync(seed.SnapshotId, default);

        Assert.NotEqual(first, second); // 规则版本变化 → 不得复用已定局 PASSED
        Assert.Equal(2, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.verification"));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.verification WHERE is_current=1"));
        Assert.Equal("M3-2", await ScalarAsync<string>(
            "SELECT rule_version FROM dbo.verification WHERE id=@id AND is_current=1", new { id = second }));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.upload_task WHERE task_type=N'RETAIL_321'"));
        Assert.Equal("Ready", await ScalarAsync<string>(
            "SELECT state FROM dbo.upload_task WHERE event_key=N'KSOA:SALE-F3:RETAIL_321'"));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.upload_part"));
    }

    [Fact]
    public async Task Failed_reverification_cancels_unsent_parts_jobs_and_claims()
    {
        await using var c = await _fx.OpenAsync();
        var code = Code(22);
        var seed = await SeedSaleAsync(c, "SALE-F4", new LineSeed("1", "SP0001", 1, "SALE", code));
        await SeedPurchaseAsync(c, seed, code, "Succeeded");
        var svc = NewService(_ => throw new InvalidOperationException("依赖已满足，不应查平台"));
        var first = await svc.VerifySnapshotAsync(seed.SnapshotId, default);
        Assert.Equal("PASSED", await ScalarAsync<string>("SELECT state FROM dbo.verification WHERE id=@id", new { id = first }));
        var taskId = await ScalarAsync<long>("SELECT id FROM dbo.upload_task WHERE event_key=N'KSOA:SALE-F4:RETAIL_321'");
        // 其他任务的占用：复核阻断不得越界
        var otherTaskId = await SeedOtherTaskWithClaimAsync(c, seed.OrgId, Code(23), "SALE-F4-OTHER");

        var v2 = await SeedSaleVersionAsync(c, "SALE-F4", 2, new LineSeed("1", "SP0001", 1, "SALE", Code(24)));
        var failed = await NewService(_ => PlatformNotFound()).VerifySnapshotAsync(v2.SnapshotId, default);

        Assert.Equal("FAILED", await ScalarAsync<string>("SELECT state FROM dbo.verification WHERE id=@id", new { id = failed }));
        Assert.Equal("Blocked", await ScalarAsync<string>("SELECT state FROM dbo.upload_task WHERE id=@id", new { id = taskId }));
        Assert.StartsWith("VERIFY_FAILED:", await ScalarAsync<string>(
            "SELECT state_reason FROM dbo.upload_task WHERE id=@id", new { id = taskId }));
        Assert.Equal(0, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.upload_part WHERE task_id=@id AND state<>N'Cancelled'", new { id = taskId }));
        Assert.Equal(1, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=N'upload-dispatch' AND job_state=N'CANCELLED'"));
        Assert.Equal(0, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=N'upload-dispatch' AND lease_owner IS NOT NULL"));
        Assert.Equal(0, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.code_claim WHERE upload_task_id=@id AND claim_state=N'ACTIVE'", new { id = taskId }));
        Assert.Equal(1, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.code_claim WHERE upload_task_id=@id AND claim_state=N'RELEASED' AND released_by=N'SYSTEM'",
            new { id = taskId }));
        Assert.Contains("VERIFY_BLOCKED", await ScalarAsync<string>(
            "SELECT release_evidence FROM dbo.code_claim WHERE upload_task_id=@id AND claim_state=N'RELEASED'", new { id = taskId }));
        Assert.Equal(1, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.audit_log WHERE action=N'task.verify.blocked'"));
        // 他人占用原样保留
        Assert.Equal("ACTIVE", await ScalarAsync<string>(
            "SELECT claim_state FROM dbo.code_claim WHERE upload_task_id=@id", new { id = otherTaskId }));
    }

    [Fact]
    public async Task Failed_after_upload_attempt_goes_manual_action()
    {
        await using var c = await _fx.OpenAsync();
        var code = Code(25);
        var seed = await SeedSaleAsync(c, "SALE-F5", new LineSeed("1", "SP0001", 1, "SALE", code));
        await SeedPurchaseAsync(c, seed, code, "Succeeded");
        var svc = NewService(_ => throw new InvalidOperationException("依赖已满足，不应查平台"));
        var first = await svc.VerifySnapshotAsync(seed.SnapshotId, default);
        Assert.Equal("PASSED", await ScalarAsync<string>("SELECT state FROM dbo.verification WHERE id=@id", new { id = first }));
        var taskId = await ScalarAsync<long>("SELECT id FROM dbo.upload_task WHERE event_key=N'KSOA:SALE-F5:RETAIL_321'");
        await c.ExecuteAsync(@"
INSERT INTO dbo.upload_attempt(task_id,attempt_no,attempt_kind,env,fencing_token)
VALUES(@taskId,1,N'UPLOAD',N'MOCK',0)", new { taskId });

        var v2 = await SeedSaleVersionAsync(c, "SALE-F5", 2, new LineSeed("1", "SP0001", 1, "SALE", Code(26)));
        var failed = await NewService(_ => PlatformNotFound()).VerifySnapshotAsync(v2.SnapshotId, default);

        Assert.Equal("FAILED", await ScalarAsync<string>("SELECT state FROM dbo.verification WHERE id=@id", new { id = failed }));
        Assert.Equal("ManualAction", await ScalarAsync<string>("SELECT state FROM dbo.upload_task WHERE id=@id", new { id = taskId }));
        Assert.Equal(SaleVerificationService.ReasonVerifyChangedAfterSend, await ScalarAsync<string>(
            "SELECT state_reason FROM dbo.upload_task WHERE id=@id", new { id = taskId }));
        // 已发送：分片/占用/发送作业一律不动
        Assert.Equal("Ready", await ScalarAsync<string>(
            "SELECT state FROM dbo.upload_part WHERE task_id=@id", new { id = taskId }));
        Assert.Equal(1, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.code_claim WHERE upload_task_id=@id AND claim_state=N'ACTIVE'", new { id = taskId }));
        Assert.Equal("PENDING", await ScalarAsync<string>(
            "SELECT job_state FROM dbo.outbox_job WHERE job_kind=N'upload-dispatch'"));
        Assert.Equal(1, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.audit_log WHERE action=N'task.verify.manual_action'"));
    }

    [Fact]
    public async Task Scheduler_tick_skips_wait_retry_snapshot()
    {
        await using var c = await _fx.OpenAsync();
        var (orgId, sysId) = await TestSeed.EnsureAsync(c);
        var waitRetrySnap = await SeedBareSnapshotAsync(c, orgId, sysId, "SALE-F6-WR");
        var dependencySnap = await SeedBareSnapshotAsync(c, orgId, sysId, "SALE-F6-DEP");
        await SeedVerificationRowAsync(c, orgId, waitRetrySnap, "SALE-F6-WR", "WAIT_RETRY");
        await SeedVerificationRowAsync(c, orgId, dependencySnap, "SALE-F6-DEP", "UNRESOLVED");

        var scheduler = new SyncScheduler(_fx.ConnectionString, new JobQueue(_fx.ConnectionString, new AuditLog()),
            new SettingsService(_fx.ConnectionString, new DpapiProtector()), "MOCK");
        var unix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var bucket = DateTimeOffset.FromUnixTimeSeconds(unix - unix % 300).UtcDateTime;
        await scheduler.TickAsync(bucket, default);
        await scheduler.TickAsync(bucket.AddSeconds(30), default); // 新 poll 桶、验核 300s 桶不变
        await scheduler.TickAsync(bucket.AddSeconds(60), default);

        // WAIT_RETRY 已由作业退避重排：调度不得再补（否则形成重试风暴）；UNRESOLVED 仍须兜底。
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=N'sale-verify'"));
        Assert.Equal(0, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=N'sale-verify' AND payload_json=@p",
            new { p = JsonSerializer.Serialize(new { orgId, env = "MOCK", snapshotId = waitRetrySnap }) }));
        Assert.Equal(1, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=N'sale-verify' AND payload_json=@p",
            new { p = JsonSerializer.Serialize(new { orgId, env = "MOCK", snapshotId = dependencySnap }) }));
    }

    [Fact]
    public async Task Return_placeholder_refreshes_on_new_snapshot_before_confirm()
    {
        await using var c = await _fx.OpenAsync();
        var oldReturn = Code(27);
        var newReturn = Code(28);
        var svc = NewService(_ => throw new InvalidOperationException("退货行不做平台核验"));
        var v1 = await SeedSaleVersionAsync(c, "SALE-F7", 1,
            new LineSeed("1", "SP0001", -1, "CONSUMER_RETURN", oldReturn));
        await svc.VerifySnapshotAsync(v1.SnapshotId, default);
        var placeholderId = await ScalarAsync<long>(
            "SELECT id FROM dbo.upload_task WHERE event_key=N'KSOA:SALE-F7:CONSUMER_RETURN_116'");
        Assert.Contains(oldReturn, await ScalarAsync<string>(
            "SELECT payload_json FROM dbo.upload_task WHERE id=@id", new { id = placeholderId }));

        var v2 = await SeedSaleVersionAsync(c, "SALE-F7", 2,
            new LineSeed("1", "SP0001", -2, "CONSUMER_RETURN", newReturn));
        await svc.VerifySnapshotAsync(v2.SnapshotId, default);

        Assert.Equal(1, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.upload_task WHERE task_type=N'CONSUMER_RETURN_116'"));
        Assert.Equal(placeholderId, await ScalarAsync<long>(
            "SELECT id FROM dbo.upload_task WHERE event_key=N'KSOA:SALE-F7:CONSUMER_RETURN_116'"));
        Assert.Equal(v2.SnapshotId, await ScalarAsync<long>(
            "SELECT source_snapshot_id FROM dbo.upload_task WHERE id=@id", new { id = placeholderId }));
        var payload = await ScalarAsync<string>("SELECT payload_json FROM dbo.upload_task WHERE id=@id", new { id = placeholderId });
        Assert.Contains(newReturn, payload);
        Assert.DoesNotContain(oldReturn, payload);
        Assert.Equal("Blocked", await ScalarAsync<string>("SELECT state FROM dbo.upload_task WHERE id=@id", new { id = placeholderId }));
        Assert.Equal("RETURN_CONFIRM_REQUIRED", await ScalarAsync<string>(
            "SELECT state_reason FROM dbo.upload_task WHERE id=@id", new { id = placeholderId }));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.upload_part"));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.upload_attempt"));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.outbox_job"));
    }

    [Fact]
    public async Task Code_contended_is_wait_retry_not_failed()
    {
        await using var c = await _fx.OpenAsync();
        var code = Code(29);
        var seed = await SeedSaleAsync(c, "SALE-F8", new LineSeed("1", "SP0001", 1, "SALE", code));
        await SeedPurchaseAsync(c, seed, code, "Succeeded");
        var otherTaskId = await SeedOtherTaskWithClaimAsync(c, seed.OrgId, code, "SALE-F8-OTHER");
        var svc = NewService(_ => throw new InvalidOperationException("依赖已满足，不应查平台"));

        var first = await svc.VerifySnapshotAsync(seed.SnapshotId, default);

        Assert.Equal("WAIT_RETRY", await ScalarAsync<string>("SELECT state FROM dbo.verification WHERE id=@id", new { id = first }));
        Assert.Contains("CODE_CONTENDED", await ScalarAsync<string>("SELECT result_json FROM dbo.verification WHERE id=@id", new { id = first }));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.upload_task WHERE event_key=N'KSOA:SALE-F8:RETAIL_321'"));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.upload_part"));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.verification_code"));

        // 释放争用后重跑可成功：证明 WAIT_RETRY 非终态
        await c.ExecuteAsync(@"
UPDATE dbo.code_claim SET claim_state=N'RELEASED', released_at=SYSUTCDATETIME(), released_by=N'test'
WHERE upload_task_id=@id AND claim_state=N'ACTIVE'", new { id = otherTaskId });
        var second = await svc.VerifySnapshotAsync(seed.SnapshotId, default);

        Assert.NotEqual(first, second);
        Assert.Equal("PASSED", await ScalarAsync<string>("SELECT state FROM dbo.verification WHERE id=@id", new { id = second }));
        Assert.Equal("Ready", await ScalarAsync<string>(
            "SELECT state FROM dbo.upload_task WHERE event_key=N'KSOA:SALE-F8:RETAIL_321'"));
        Assert.Equal(1, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.upload_task WHERE event_key=N'KSOA:SALE-F8:RETAIL_321'"));
    }

    [Fact]
    public async Task Platform_rejection_is_failed_code_not_dependency()
    {
        await using var c = await _fx.OpenAsync();
        var code = Code(30);
        var seed = await SeedSaleAsync(c, "SALE-F9", new LineSeed("1", "SP0001", 1, "SALE", code));

        var id = await NewService(_ => BusinessRejected()).VerifySnapshotAsync(seed.SnapshotId, default);

        Assert.Equal("FAILED", await ScalarAsync<string>("SELECT state FROM dbo.verification WHERE id=@id", new { id }));
        Assert.Equal("FAIL", await ScalarAsync<string>(
            "SELECT result FROM dbo.verification_code WHERE verification_id=@id", new { id }));
        Assert.Equal("PLATFORM_REJECTED", await ScalarAsync<string>(
            "SELECT reason FROM dbo.verification_code WHERE verification_id=@id", new { id }));
        // 平台拒绝是逐码 FAIL（终态），不是依赖等待；新建单据无既有任务 → 不建任务、无分片/作业。
        Assert.Contains("\"dependencyState\":\"NONE\"", await ScalarAsync<string>(
            "SELECT result_json FROM dbo.verification WHERE id=@id", new { id }));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.upload_task"));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.upload_part"));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.outbox_job"));
    }

    [Fact]
    public async Task Platform_not_found_blocks_with_PLATFORM_NOT_FOUND_no_parts_or_outbox()
    {
        await using var c = await _fx.OpenAsync();
        var code = Code(31);
        var seed = await SeedSaleAsync(c, "SALE-F10", new LineSeed("1", "SP0001", 1, "SALE", code));

        var id = await NewService(_ => PlatformNotFound()).VerifySnapshotAsync(seed.SnapshotId, default);

        Assert.Equal("FAILED", await ScalarAsync<string>("SELECT state FROM dbo.verification WHERE id=@id", new { id }));
        Assert.Equal("FAIL", await ScalarAsync<string>(
            "SELECT result FROM dbo.verification_code WHERE verification_id=@id", new { id }));
        Assert.Equal(SaleVerificationService.ReasonPlatformNotFound, await ScalarAsync<string>(
            "SELECT reason FROM dbo.verification_code WHERE verification_id=@id", new { id }));
        // 平台查无此码 → FAIL（PLATFORM_NOT_FOUND）终态：新建单据无既有任务 → 不建任务、无分片/占用/作业。
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.upload_task"));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.upload_part"));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.code_claim"));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.outbox_job"));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.code_event"));
    }

    [Fact]
    public async Task Wait_retry_then_pass_leaves_one_current_row_and_one_task()
    {
        await using var c = await _fx.OpenAsync();
        var code = Code(32);
        var seed = await SeedSaleAsync(c, "SALE-F11", new LineSeed("1", "SP0001", 1, "SALE", code));

        var first = await NewService(_ => throw new HttpRequestException("网络中断"))
            .VerifySnapshotAsync(seed.SnapshotId, default);
        Assert.Equal("WAIT_RETRY", await ScalarAsync<string>("SELECT state FROM dbo.verification WHERE id=@id", new { id = first }));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.upload_task"));

        var second = await NewService(_ => Instore()).VerifySnapshotAsync(seed.SnapshotId, default);

        Assert.NotEqual(first, second);
        Assert.Equal(2, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.verification"));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.verification WHERE is_current=1"));
        Assert.Equal("PASSED", await ScalarAsync<string>(
            "SELECT state FROM dbo.verification WHERE id=@id AND is_current=1", new { id = second }));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.upload_task WHERE task_type=N'RETAIL_321'"));
        Assert.Equal("Ready", await ScalarAsync<string>(
            "SELECT state FROM dbo.upload_task WHERE event_key=N'KSOA:SALE-F11:RETAIL_321'"));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.upload_part"));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=N'upload-dispatch'"));
    }

    // ---------- Fix Round 2（F1–F7） ----------

    /// <summary>F1：退货单（销售桶空）的新版本必须把上一版本遗留的 Ready 321 变为不可发送（SALE_LINES_REMOVED）。</summary>
    [Fact]
    public async Task Return_only_snapshot_blocks_previously_ready_sale_task()
    {
        await using var c = await _fx.OpenAsync();
        var saleCode = Code(40);
        var seed = await SeedSaleAsync(c, "SALE-F12", new LineSeed("1", "SP0001", 1, "SALE", saleCode));
        await SeedPurchaseAsync(c, seed, saleCode, "Succeeded");
        var first = await NewService(_ => throw new InvalidOperationException("依赖已满足，不应查平台"))
            .VerifySnapshotAsync(seed.SnapshotId, default);
        Assert.Equal("PASSED", await ScalarAsync<string>("SELECT state FROM dbo.verification WHERE id=@id", new { id = first }));
        var taskId = await ScalarAsync<long>("SELECT id FROM dbo.upload_task WHERE event_key=N'KSOA:SALE-F12:RETAIL_321'");
        Assert.Equal("Ready", await ScalarAsync<string>("SELECT state FROM dbo.upload_task WHERE id=@id", new { id = taskId }));
        Assert.Equal(1, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.code_claim WHERE upload_task_id=@id AND claim_state=N'ACTIVE'", new { id = taskId }));

        // 新版本：只有退货行 → 销售桶为空（不得让上一版本的 321 仍可发送）
        var v2 = await SeedSaleVersionAsync(c, "SALE-F12", 2,
            new LineSeed("1", "SP0001", -1, "CONSUMER_RETURN", Code(41)));
        var second = await NewService(_ => throw new InvalidOperationException("退货行不做平台核验"))
            .VerifySnapshotAsync(v2.SnapshotId, default);

        Assert.Equal("PASSED", await ScalarAsync<string>("SELECT state FROM dbo.verification WHERE id=@id", new { id = second }));
        Assert.Equal("Blocked", await ScalarAsync<string>("SELECT state FROM dbo.upload_task WHERE id=@id", new { id = taskId }));
        Assert.Equal("SALE_LINES_REMOVED", await ScalarAsync<string>(
            "SELECT state_reason FROM dbo.upload_task WHERE id=@id", new { id = taskId }));
        Assert.Equal(0, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.upload_part WHERE task_id=@id AND state<>N'Cancelled'", new { id = taskId }));
        Assert.Equal(0, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=N'upload-dispatch' AND job_state<>N'CANCELLED'"));
        Assert.Equal(1, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.code_claim WHERE upload_task_id=@id AND claim_state=N'RELEASED' AND released_by=N'SYSTEM'",
            new { id = taskId }));
        Assert.Equal(1, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.audit_log WHERE action=N'task.verify.blocked' AND reason=N'SALE_LINES_REMOVED'"));
        Assert.Equal("Blocked", await ScalarAsync<string>(
            "SELECT state FROM dbo.upload_task WHERE event_key=N'KSOA:SALE-F12:CONSUMER_RETURN_116'"));
    }

    /// <summary>F1：新版本已无退货行 → 未确认的 116 占位转 Cancelled + RETURN_LINES_REMOVED 并留审计。</summary>
    [Fact]
    public async Task Snapshot_without_return_lines_cancels_unconfirmed_return_placeholder()
    {
        await using var c = await _fx.OpenAsync();
        var returnCode = Code(42);
        var svc = NewService(_ => throw new InvalidOperationException("退货行不做平台核验"));
        var v1 = await SeedSaleVersionAsync(c, "SALE-F13", 1,
            new LineSeed("1", "SP0001", -1, "CONSUMER_RETURN", returnCode));
        await svc.VerifySnapshotAsync(v1.SnapshotId, default);
        var placeholderId = await ScalarAsync<long>(
            "SELECT id FROM dbo.upload_task WHERE event_key=N'KSOA:SALE-F13:CONSUMER_RETURN_116'");
        Assert.Equal("Blocked", await ScalarAsync<string>("SELECT state FROM dbo.upload_task WHERE id=@id", new { id = placeholderId }));

        // 新版本：全 EXEMPT（两桶皆空）→ 退货占位必须撤销。用独立 SPID 播种 EXEMPT+依据（同 SPID 的分类在商品主数据上不可变）。
        var v2 = await SeedSaleVersionAsync(c, "SALE-F13", 2,
            new LineSeed("1", "SP0020", 2, "SALE", null, "EXEMPT", "OTC 不纳入追溯"));
        var second = await svc.VerifySnapshotAsync(v2.SnapshotId, default);

        Assert.Equal("PASSED", await ScalarAsync<string>("SELECT state FROM dbo.verification WHERE id=@id", new { id = second }));
        Assert.Equal("Cancelled", await ScalarAsync<string>("SELECT state FROM dbo.upload_task WHERE id=@id", new { id = placeholderId }));
        Assert.Equal("RETURN_LINES_REMOVED", await ScalarAsync<string>(
            "SELECT state_reason FROM dbo.upload_task WHERE id=@id", new { id = placeholderId }));
        Assert.Equal(2, await ScalarAsync<int>("SELECT version FROM dbo.upload_task WHERE id=@id", new { id = placeholderId }));
        Assert.Equal(1, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.audit_log WHERE action=N'task.return_placeholder.cancelled' AND reason=N'RETURN_LINES_REMOVED'"));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.upload_task WHERE task_type=N'RETAIL_321'"));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.upload_part"));
    }

    /// <summary>F2：发送作业 RUNNING（零尝试）视为已发送 → 只转 ManualAction，分片/占用原样保留。</summary>
    [Fact]
    public async Task Running_dispatch_makes_block_manual_action_without_touching_parts()
    {
        await using var c = await _fx.OpenAsync();
        var code = Code(43);
        var seed = await SeedSaleAsync(c, "SALE-F14", new LineSeed("1", "SP0001", 1, "SALE", code));
        await SeedPurchaseAsync(c, seed, code, "Succeeded");
        await NewService(_ => throw new InvalidOperationException("依赖已满足，不应查平台"))
            .VerifySnapshotAsync(seed.SnapshotId, default);
        var taskId = await ScalarAsync<long>("SELECT id FROM dbo.upload_task WHERE event_key=N'KSOA:SALE-F14:RETAIL_321'");
        var partId = await ScalarAsync<long>("SELECT id FROM dbo.upload_part WHERE task_id=@id", new { id = taskId });
        await c.ExecuteAsync(@"
UPDATE dbo.outbox_job SET job_state=N'RUNNING', lease_owner=N'test-worker',
       lease_until=DATEADD(minute,5,SYSUTCDATETIME())
WHERE job_kind=N'upload-dispatch' AND intent_key=@k", new { k = "part:" + partId });

        var v2 = await SeedSaleVersionAsync(c, "SALE-F14", 2, new LineSeed("1", "SP0001", 1, "SALE", Code(44)));
        var failed = await NewService(_ => PlatformNotFound()).VerifySnapshotAsync(v2.SnapshotId, default);

        Assert.Equal("FAILED", await ScalarAsync<string>("SELECT state FROM dbo.verification WHERE id=@id", new { id = failed }));
        Assert.Equal("ManualAction", await ScalarAsync<string>("SELECT state FROM dbo.upload_task WHERE id=@id", new { id = taskId }));
        // state_reason 仍是唯一 UI 可匹配码；审计用更精确的 VERIFY_CHANGED_WHILE_DISPATCHING
        Assert.Equal(SaleVerificationService.ReasonVerifyChangedAfterSend, await ScalarAsync<string>(
            "SELECT state_reason FROM dbo.upload_task WHERE id=@id", new { id = taskId }));
        Assert.Equal("Ready", await ScalarAsync<string>(
            "SELECT state FROM dbo.upload_part WHERE id=@id", new { id = partId }));
        Assert.Equal(1, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.code_claim WHERE upload_task_id=@id AND claim_state=N'ACTIVE'", new { id = taskId }));
        Assert.Equal("RUNNING", await ScalarAsync<string>(
            "SELECT job_state FROM dbo.outbox_job WHERE intent_key=@k", new { k = "part:" + partId }));
        Assert.Equal(1, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.audit_log WHERE action=N'task.verify.manual_action' AND reason=N'VERIFY_CHANGED_WHILE_DISPATCHING'"));
        var evidence = await ScalarAsync<string>(
            "SELECT after_digest FROM dbo.audit_log WHERE action=N'task.verify.manual_action'");
        Assert.Contains("\"dispatching\":true", evidence);
        Assert.Contains("\"priorState\":\"Ready\"", evidence);
    }

    /// <summary>F4：零分片任务被阻断时仍必须释放占用并写 task.verify.blocked 审计。</summary>
    [Fact]
    public async Task Blocked_task_without_parts_still_releases_claims_and_audits()
    {
        await using var c = await _fx.OpenAsync();
        var code = Code(45);
        var seed = await SeedSaleAsync(c, "SALE-F15", new LineSeed("1", "SP0001", 1, "SALE", code));
        var codeId = seed.CodeIds[code];
        var taskId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upload_task(org_id,env,event_key,task_type,source_hash,payload_hash,state,source_integrity,match_state,
    verification_state,dependency_state)
OUTPUT INSERTED.id
VALUES(@orgId,N'MOCK',N'KSOA:SALE-F15:RETAIL_321',N'RETAIL_321',@hash,@hash,N'Ready',N'COMPLETE',N'COMPLETE',N'PASSED',N'NONE')",
            new { orgId = seed.OrgId, hash = Sha256("SALE-F15") });
        await c.ExecuteAsync(@"
INSERT INTO dbo.code_claim(org_id,env,code_id,event_scope,upload_task_id,claim_state)
VALUES(@orgId,N'MOCK',@codeId,N'RETAIL:321',@taskId,N'ACTIVE')", new { seed.OrgId, codeId, taskId });

        var failed = await NewService(_ => PlatformNotFound()).VerifySnapshotAsync(seed.SnapshotId, default);

        Assert.Equal("FAILED", await ScalarAsync<string>("SELECT state FROM dbo.verification WHERE id=@id", new { id = failed }));
        Assert.Equal("Blocked", await ScalarAsync<string>("SELECT state FROM dbo.upload_task WHERE id=@id", new { id = taskId }));
        Assert.Equal("RELEASED", await ScalarAsync<string>(
            "SELECT claim_state FROM dbo.code_claim WHERE upload_task_id=@id", new { id = taskId }));
        Assert.Equal("SYSTEM", await ScalarAsync<string>(
            "SELECT released_by FROM dbo.code_claim WHERE upload_task_id=@id", new { id = taskId }));
        Assert.Equal(1, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.audit_log WHERE action=N'task.verify.blocked'"));
        Assert.Contains("\"parts\":0", await ScalarAsync<string>(
            "SELECT after_digest FROM dbo.audit_log WHERE action=N'task.verify.blocked'"));
    }

    /// <summary>F3：Succeeded 等终态不得被 ManualAction 覆盖；审计记录 preservedState=true 与 priorState。</summary>
    [Fact]
    public async Task Succeeded_task_is_not_downgraded_and_manual_action_audits_preserved_state()
    {
        await using var c = await _fx.OpenAsync();
        var code = Code(46);
        var seed = await SeedSaleAsync(c, "SALE-F16", new LineSeed("1", "SP0001", 1, "SALE", code));
        await SeedPurchaseAsync(c, seed, code, "Succeeded");
        await NewService(_ => throw new InvalidOperationException("依赖已满足，不应查平台"))
            .VerifySnapshotAsync(seed.SnapshotId, default);
        var taskId = await ScalarAsync<long>("SELECT id FROM dbo.upload_task WHERE event_key=N'KSOA:SALE-F16:RETAIL_321'");
        await c.ExecuteAsync(@"
INSERT INTO dbo.upload_attempt(task_id,attempt_no,attempt_kind,env,fencing_token)
VALUES(@taskId,1,N'UPLOAD',N'MOCK',0)", new { taskId });
        await c.ExecuteAsync("UPDATE dbo.upload_task SET state=N'Succeeded', state_reason=N'PLATFORM_ACCEPTED' WHERE id=@id",
            new { id = taskId });

        var v2 = await SeedSaleVersionAsync(c, "SALE-F16", 2, new LineSeed("1", "SP0001", 1, "SALE", Code(47)));
        var failed = await NewService(_ => PlatformNotFound()).VerifySnapshotAsync(v2.SnapshotId, default);

        Assert.Equal("FAILED", await ScalarAsync<string>("SELECT state FROM dbo.verification WHERE id=@id", new { id = failed }));
        Assert.Equal("Succeeded", await ScalarAsync<string>("SELECT state FROM dbo.upload_task WHERE id=@id", new { id = taskId }));
        Assert.Equal("PLATFORM_ACCEPTED", await ScalarAsync<string>(
            "SELECT state_reason FROM dbo.upload_task WHERE id=@id", new { id = taskId }));
        Assert.Equal("FAILED", await ScalarAsync<string>(
            "SELECT verification_state FROM dbo.upload_task WHERE id=@id", new { id = taskId }));
        Assert.Equal(1, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.audit_log WHERE action=N'task.verify.manual_action'"));
        var evidence = await ScalarAsync<string>(
            "SELECT after_digest FROM dbo.audit_log WHERE action=N'task.verify.manual_action'");
        Assert.Contains("\"preservedState\":true", evidence);
        Assert.Contains("\"priorState\":\"Succeeded\"", evidence);
        Assert.Contains("\"priorReason\":\"PLATFORM_ACCEPTED\"", evidence);
        // 已发送证据不动
        Assert.Equal("Ready", await ScalarAsync<string>("SELECT state FROM dbo.upload_part WHERE task_id=@id", new { id = taskId }));
        Assert.Equal(1, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.code_claim WHERE upload_task_id=@id AND claim_state=N'ACTIVE'", new { id = taskId }));
    }

    /// <summary>F6：规则版本升级后未发送 Ready 任务的载荷必须按新规则版本重建（不得沿用旧 payload_hash）。</summary>
    [Fact]
    public async Task Rule_version_bump_rebuilds_ready_task_payload()
    {
        await using var c = await _fx.OpenAsync();
        var code = Code(48);
        var seed = await SeedSaleAsync(c, "SALE-F17", new LineSeed("1", "SP0001", 1, "SALE", code));
        await SeedPurchaseAsync(c, seed, code, "Succeeded");
        var svc = NewService(_ => throw new InvalidOperationException("依赖已满足，不应查平台"));
        await svc.VerifySnapshotAsync(seed.SnapshotId, default);
        var taskId = await ScalarAsync<long>("SELECT id FROM dbo.upload_task WHERE event_key=N'KSOA:SALE-F17:RETAIL_321'");
        var oldHash = await ScalarAsync<string>("SELECT payload_hash FROM dbo.upload_task WHERE id=@id", new { id = taskId });
        Assert.Contains("\"ruleVersion\":\"M3-1\"", await ScalarAsync<string>(
            "SELECT payload_json FROM dbo.upload_task WHERE id=@id", new { id = taskId }));

        await SetSettingAsync("sale.verify.ruleVersion", "\"M3-2\"");
        var second = await svc.VerifySnapshotAsync(seed.SnapshotId, default);

        Assert.Equal("PASSED", await ScalarAsync<string>("SELECT state FROM dbo.verification WHERE id=@id", new { id = second }));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.upload_task WHERE task_type=N'RETAIL_321'"));
        Assert.Equal(taskId, await ScalarAsync<long>("SELECT id FROM dbo.upload_task WHERE event_key=N'KSOA:SALE-F17:RETAIL_321'"));
        Assert.Equal("Ready", await ScalarAsync<string>("SELECT state FROM dbo.upload_task WHERE id=@id", new { id = taskId }));
        var newHash = await ScalarAsync<string>("SELECT payload_hash FROM dbo.upload_task WHERE id=@id", new { id = taskId });
        Assert.NotEqual(oldHash.Trim(), newHash.Trim());
        var payload = await ScalarAsync<string>("SELECT payload_json FROM dbo.upload_task WHERE id=@id", new { id = taskId });
        Assert.Contains("\"ruleVersion\":\"M3-2\"", payload);
        Assert.DoesNotContain("\"ruleVersion\":\"M3-1\"", payload);
        var partPayload = await ScalarAsync<string>("SELECT payload_json FROM dbo.upload_part WHERE task_id=@id", new { id = taskId });
        Assert.Contains("\"ruleVersion\":\"M3-2\"", partPayload);
        Assert.Equal(1, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.code_claim WHERE upload_task_id=@id AND claim_state=N'ACTIVE'", new { id = taskId }));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.upload_part WHERE task_id=@id", new { id = taskId }));
    }

    /// <summary>F5：依赖等待单据不再走 5 分钟桶，改由每小时桶兜底（确定性唤醒仍是采购成功事件）。</summary>
    [Fact]
    public async Task Scheduler_tick_defers_dependency_wait_to_hourly_bucket()
    {
        await using var c = await _fx.OpenAsync();
        var (orgId, sysId) = await TestSeed.EnsureAsync(c);
        var snapId = await SeedBareSnapshotAsync(c, orgId, sysId, "SALE-F18-DEP");
        await c.ExecuteAsync(@"
INSERT INTO dbo.upload_task(org_id,env,event_key,task_type,source_hash,payload_hash,state,state_reason,source_integrity,
    match_state,verification_state,dependency_state)
VALUES(@orgId,N'MOCK',N'KSOA:SALE-F18-DEP:RETAIL_321',N'RETAIL_321',@hash,@hash,N'Blocked',N'WAIT_DEPENDENCY',
       N'COMPLETE',N'COMPLETE',N'UNRESOLVED',N'WAIT')", new { orgId, hash = Sha256("SALE-F18-DEP") });

        var scheduler = new SyncScheduler(_fx.ConnectionString, new JobQueue(_fx.ConnectionString, new AuditLog()),
            new SettingsService(_fx.ConnectionString, new DpapiProtector()), "MOCK");
        var unix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var bucket = DateTimeOffset.FromUnixTimeSeconds(unix - unix % 300).UtcDateTime;
        await scheduler.TickAsync(bucket, default);
        await scheduler.TickAsync(bucket.AddSeconds(30), default); // 新 poll 桶、300s 验核桶不变

        Assert.Equal(0, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=N'sale-verify' AND intent_key LIKE @p",
            new { p = $"snapshot:{snapId}:retry:%" }));
        Assert.Equal(1, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=N'sale-verify' AND intent_key LIKE @p",
            new { p = $"snapshot:{snapId}:dep:%" }));
        Assert.Equal(1, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=N'sale-verify'"));
    }

    private static async Task<long> SeedBareSnapshotAsync(SqlConnection c, long orgId, long sysId, string docKey)
        => await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.source_snapshot(org_id,source_system_id,source_document_key,document_type,version,completeness_status,content_hash)
OUTPUT INSERTED.id VALUES(@orgId,@sysId,@key,N'SALE',1,N'COMPLETE',@hash)",
            new { orgId, sysId, key = docKey, hash = Sha256(docKey) });

    private static Task SeedVerificationRowAsync(SqlConnection c, long orgId, long snapId, string docKey, string state)
        => c.ExecuteAsync(@"
INSERT INTO dbo.verification(org_id,env,event_key,source_snapshot_id,source_hash,code_set_hash,rule_version,state,is_current)
VALUES(@orgId,N'MOCK',@key,@snap,REPLICATE('a',64),REPLICATE('b',64),N'M3-1',@state,1)",
            new { orgId, key = "KSOA:" + docKey + ":SALE", snap = snapId, state });
}
