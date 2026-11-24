using System.Globalization;
using System.Runtime.Versioning; // DpapiProtector 仅 Windows（CA1416），与 SaleVerificationTests 同标注
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
using TraceFlow.Infrastructure.Top;
using TraceFlow.IntegrationTests.Sources;
using Xunit;

namespace TraceFlow.IntegrationTests.Sales;

/// <summary>
/// Task 6 消费者退货 116 首次人工确认（RET-01/02/04、DEF-02/R-M3-4）：
/// 混合/退货单据信号、占位任务就地提升（占用/CONSUMER_RETURN 事件/outbox）、拒绝、幂等/版本冲突、
/// 前序缺失/未就绪/重复退货、202 夹具任务，以及同一前序并发确认只提交一次。
/// 每测试独立库；数据全部直接 SQL 播种，平台一侧不触网。
/// </summary>
[SupportedOSPlatform("windows")]
public class ReturnConfirmTests : IAsyncLifetime
{
    private readonly Sql2008Fixture _fx = new();
    public Task InitializeAsync() => _fx.InitializeAsync();
    public Task DisposeAsync() => _fx.DisposeAsync();

    private const string Evidence = "{\"reason\":\"原码退回\",\"operator\":\"tester\"}";

    private sealed record LineSeed(string Key, string Spid, decimal Qty, string EventKind, string? Code,
        string Classification = "REQUIRED", string? Reason = null, bool Mapped = true);

    private sealed record Seed(long OrgId, long SysId, long SnapshotId, IReadOnlyList<(long LineId, string Key)> Lines,
        IReadOnlyDictionary<string, long> CodeIds);

    private sealed record Scenario(long OrgId, long SaleTaskId, long SaleEventId, string SaleBillCode, long ReturnTaskId);

    private SettingsService NewSettings() => new(_fx.ConnectionString, new DpapiProtector());

    private SaleVerificationService NewVerifyService(Func<TopCall, TopRawResponse> handler)
    {
        var settings = NewSettings();
        var audit = new AuditLog();
        var queue = new JobQueue(_fx.ConnectionString, audit);
        var top = new TopClientService(_fx.ConnectionString, new FakeTopGateway(handler), new RateBudgetService(),
            new ChannelPauseService(), audit);
        return new SaleVerificationService(_fx.ConnectionString, "MOCK", settings, top,
            new RetailTaskFactory(settings, queue), queue, audit);
    }

    private ReturnConfirmService NewConfirmService()
        => NewConfirmService(new IdempotencyStore());

    private ReturnConfirmService NewConfirmService(IIdempotencyStore idempotency)
    {
        var settings = NewSettings();
        var audit = new AuditLog();
        var factory = new RetailTaskFactory(settings, new JobQueue(_fx.ConnectionString, audit));
        return new ReturnConfirmService(_fx.ConnectionString, "MOCK", settings, factory, idempotency, audit);
    }

    /// <summary>
    /// F6 确定性竞态模拟（不依赖随机并发）：进入服务时的幂等读发生在胜者提交之前（返回 null），
    /// 随后在**独立连接、已提交**的条件下预置胜者行，再委托真实存储执行 SaveAsync →
    /// 真实触发 UQ_idempotency_user_key 2601/2627；冲突后的重读（tx=null）委托真实存储，
    /// 用于验证「胜者回放」与「异摘要 409」两条分支。胜者行自身的业务提交不在本模拟范围内。
    /// </summary>
    private sealed class WinnerRaceIdempotencyStore : IIdempotencyStore
    {
        private readonly string _cs;
        private readonly long _winnerTaskId;
        private readonly string? _winnerHashOverride;
        private readonly IdempotencyStore _inner = new();
        private bool _winnerSeeded;
        private string _key = "";
        private string _hash = "";
        private long _userId;

        public WinnerRaceIdempotencyStore(string connectionString, long winnerTaskId, string? winnerHashOverride = null)
        {
            _cs = connectionString;
            _winnerTaskId = winnerTaskId;
            _winnerHashOverride = winnerHashOverride;
        }

        public Task<IdempotencyHit?> TryGetAsync(SqlConnection conn, SqlTransaction tx, string key, string requestHash,
            long userId, CancellationToken ct)
        {
            if (_winnerSeeded) return _inner.TryGetAsync(conn, tx, key, requestHash, userId, ct);
            // 赛道读：胜者尚未提交 → 读空；记录本次指纹（胜者必须以同 key/user + 指定摘要落库）。
            _key = key;
            _hash = requestHash;
            _userId = userId;
            return Task.FromResult<IdempotencyHit?>(null);
        }

        public async Task SaveAsync(SqlConnection conn, SqlTransaction tx, string key, string requestHash, long userId,
            int status, string body, CancellationToken ct)
        {
            if (!_winnerSeeded)
            {
                _winnerSeeded = true;
                await using var winnerConn = new SqlConnection(_cs);
                await winnerConn.OpenAsync(ct);
                // 独立连接 + 无显式事务（自动提交）：胜者行在本次 SaveAsync 之前已提交。
                await winnerConn.ExecuteAsync(@"
INSERT INTO dbo.idempotency_record(idem_key,request_hash,user_id,response_status,response_body)
VALUES(@key,@hash,@userId,200,@body)", new
                {
                    key = _key, hash = _winnerHashOverride ?? _hash, userId = _userId,
                    body = JsonSerializer.Serialize(new { taskId = _winnerTaskId, state = "Ready" }),
                });
            }
            await _inner.SaveAsync(conn, tx, key, requestHash, userId, status, body, ct);
        }
    }

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
        if (!mapped) return;
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

    private async Task<Seed> SeedSaleAsync(SqlConnection c, string docKey, int version, params LineSeed[] lines)
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

    /// <summary>前序采购入库证据（PURCHASE_IN + Succeeded）：让 321 核验无需平台调用即通过。</summary>
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

    private async Task VerifyAsync(Seed seed)
        => await NewVerifyService(_ => throw new InvalidOperationException("依赖已满足/退货行不应查平台"))
            .VerifySnapshotAsync(seed.SnapshotId, default);

    /// <summary>可确认场景：前序销售 321（可选置 Succeeded）+ 退货单 116 占位。</summary>
    private async Task<Scenario> SeedConfirmableAsync(SqlConnection c, string saleDoc, string returnDoc, string code,
        bool saleSucceeded = true)
    {
        var sale = await SeedSaleAsync(c, saleDoc, 1, new LineSeed("1", "SP0001", 1, "SALE", code));
        await SeedPurchaseAsync(c, sale, code, "Succeeded");
        await VerifyAsync(sale);
        var saleTaskId = await ScalarAsync<long>(c, "SELECT id FROM dbo.upload_task WHERE event_key=@k",
            new { k = $"KSOA:{saleDoc}:RETAIL_321" });
        if (saleSucceeded)
            await c.ExecuteAsync(@"
UPDATE dbo.upload_task SET state=N'Succeeded', state_reason=N'PLATFORM_ACCEPTED' WHERE id=@id",
                new { id = saleTaskId });
        var saleEventId = await ScalarAsync<long>(c, @"
SELECT id FROM dbo.code_event WHERE upload_task_id=@id AND event_type=N'SALE'", new { id = saleTaskId });
        var saleBillCode = await ScalarAsync<string>(c,
            "SELECT bill_code FROM dbo.upload_part WHERE task_id=@id", new { id = saleTaskId });
        var ret = await SeedSaleAsync(c, returnDoc, 1, new LineSeed("1", "SP0001", -1, "CONSUMER_RETURN", code));
        await VerifyAsync(ret);
        var returnTaskId = await ScalarAsync<long>(c, "SELECT id FROM dbo.upload_task WHERE event_key=@k",
            new { k = $"KSOA:{returnDoc}:CONSUMER_RETURN_116" });
        return new Scenario(sale.OrgId, saleTaskId, saleEventId, saleBillCode, returnTaskId);
    }

    private static Task<T> ScalarAsync<T>(SqlConnection c, string sql, object? param = null)
        => c.ExecuteScalarAsync<T>(sql, param)!;

    private static async Task<(ReturnConfirmResult? Result, Exception? Error)> AttemptAsync(
        Func<Task<ReturnConfirmResult>> action)
    {
        try
        {
            return (await action(), null);
        }
        catch (Exception ex)
        {
            return (null, ex);
        }
    }

    // ---------- 用例 ----------

    /// <summary>
    /// RET-01/02 混合小票夹具：正行进 321，负行只建 Blocked + RETURN_CONFIRM_REQUIRED 的 116（无分片/占用/outbox），
    /// result_json.returnCandidates 给出逐码前序结论（本例退货码无历史 SALE → PREDECESSOR_MISSING）。
    /// </summary>
    [Fact]
    public async Task Signals_mixed_return_fixture_creates_321_and_blocked_116()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "ksoa", "sale-mixed-return.json");
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var seeds = new List<LineSeed>();
        var positiveCode = "";
        var returnCode = "";
        foreach (var line in fixture.RootElement.GetProperty("salemxls").EnumerateArray())
        {
            var key = line.GetProperty("plh").GetString()!;
            var qty = line.GetProperty("shl").GetDecimal();
            var code = line.TryGetProperty("TraceCode", out var codeEl) && codeEl.ValueKind == JsonValueKind.String
                ? codeEl.GetString() : null;
            if (qty > 0)
            {
                positiveCode = code!;
                seeds.Add(new LineSeed(key, "SP0001", qty, "SALE", code));
            }
            else if (qty < 0)
            {
                returnCode = code!;
                seeds.Add(new LineSeed(key, "SP0001", qty, "CONSUMER_RETURN", code));
            }
            else
            {
                seeds.Add(new LineSeed(key, "SP0003", qty, "SALE", null, "EXEMPT", "OTC 不纳入追溯"));
            }
        }
        Assert.NotEmpty(positiveCode);
        Assert.NotEmpty(returnCode);

        await using var c = await _fx.OpenAsync();
        var seed = await SeedSaleAsync(c, "SALE-T6-MIX", 1, seeds.ToArray());
        await SeedPurchaseAsync(c, seed, positiveCode, "Succeeded");

        var verificationId = await NewVerifyService(_ => throw new InvalidOperationException("依赖已满足，不应查平台"))
            .VerifySnapshotAsync(seed.SnapshotId, default);

        Assert.Equal("PASSED", await ScalarAsync<string>(c, "SELECT state FROM dbo.verification WHERE id=@id",
            new { id = verificationId }));
        var saleTaskId = await ScalarAsync<long>(c, "SELECT id FROM dbo.upload_task WHERE event_key=@k",
            new { k = "KSOA:SALE-T6-MIX:RETAIL_321" });
        Assert.Equal("Ready", await ScalarAsync<string>(c, "SELECT state FROM dbo.upload_task WHERE id=@id",
            new { id = saleTaskId }));
        var salePayload = await ScalarAsync<string>(c, "SELECT payload_json FROM dbo.upload_task WHERE id=@id",
            new { id = saleTaskId });
        Assert.Contains(positiveCode, salePayload);
        Assert.DoesNotContain(returnCode, salePayload); // RET-01：负行绝不进 321 载荷

        var returnTaskId = await ScalarAsync<long>(c, "SELECT id FROM dbo.upload_task WHERE event_key=@k",
            new { k = "KSOA:SALE-T6-MIX:CONSUMER_RETURN_116" });
        Assert.Equal("Blocked", await ScalarAsync<string>(c, "SELECT state FROM dbo.upload_task WHERE id=@id",
            new { id = returnTaskId }));
        Assert.Equal("RETURN_CONFIRM_REQUIRED", await ScalarAsync<string>(c,
            "SELECT state_reason FROM dbo.upload_task WHERE id=@id", new { id = returnTaskId }));
        var returnPayload = await ScalarAsync<string>(c, "SELECT payload_json FROM dbo.upload_task WHERE id=@id",
            new { id = returnTaskId });
        Assert.Contains(returnCode, returnPayload);
        Assert.DoesNotContain(positiveCode, returnPayload);
        Assert.Equal(0, await ScalarAsync<int>(c, "SELECT COUNT(*) FROM dbo.upload_part WHERE task_id=@id",
            new { id = returnTaskId }));
        Assert.Equal(0, await ScalarAsync<int>(c, "SELECT COUNT(*) FROM dbo.code_claim WHERE upload_task_id=@id",
            new { id = returnTaskId }));
        Assert.Equal(0, await ScalarAsync<int>(c, "SELECT COUNT(*) FROM dbo.code_event WHERE upload_task_id=@id",
            new { id = returnTaskId }));

        var resultJson = await ScalarAsync<string>(c, @"SELECT result_json FROM dbo.verification v
JOIN dbo.upload_task t ON t.source_snapshot_id=v.source_snapshot_id AND t.id=@taskId
WHERE v.id=@id", new { id = verificationId, taskId = returnTaskId });
        using var resultDoc = JsonDocument.Parse(resultJson);
        var candidate = resultDoc.RootElement.GetProperty("returnCandidates").EnumerateArray()
            .Single(x => x.GetProperty("codeId").GetInt64() == seed.CodeIds[returnCode]);
        Assert.Equal(JsonValueKind.Null, candidate.GetProperty("predecessorEventId").ValueKind);
        Assert.Equal("PREDECESSOR_MISSING", candidate.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Confirm_creates_claims_events_and_dispatch_outbox()
    {
        await using var c = await _fx.OpenAsync();
        var code = Code(1);
        var scenario = await SeedConfirmableAsync(c, "SALE-T6-C1", "RET-T6-C1", code);

        var result = await NewConfirmService().ConfirmAsync(scenario.ReturnTaskId,
            new ReturnConfirmCommand(1, "CONFIRMED", new[] { scenario.SaleEventId }, Evidence, "idem-t6-c1", "tester", FirstReportDeclared: true),
            default);

        Assert.False(result.Idempotent);
        Assert.Equal("Ready", result.State);
        Assert.Equal(scenario.ReturnTaskId, result.TaskId);

        Assert.Equal("Ready", await ScalarAsync<string>(c, "SELECT state FROM dbo.upload_task WHERE id=@id",
            new { id = scenario.ReturnTaskId }));
        Assert.Equal("RETURN_CONFIRMED", await ScalarAsync<string>(c,
            "SELECT state_reason FROM dbo.upload_task WHERE id=@id", new { id = scenario.ReturnTaskId }));
        Assert.Equal(3, await ScalarAsync<int>(c, "SELECT version FROM dbo.upload_task WHERE id=@id",
            new { id = scenario.ReturnTaskId }));
        Assert.Equal(scenario.SaleTaskId, await ScalarAsync<long>(c,
            "SELECT predecessor_task_id FROM dbo.upload_task WHERE id=@id", new { id = scenario.ReturnTaskId }));

        Assert.Equal(1, await ScalarAsync<int>(c, @"
SELECT COUNT(*) FROM dbo.code_claim WHERE upload_task_id=@id AND event_scope=N'RETURN:116' AND claim_state=N'ACTIVE'",
            new { id = scenario.ReturnTaskId }));
        Assert.Equal(1, await ScalarAsync<int>(c, @"
SELECT COUNT(*) FROM dbo.code_event WHERE upload_task_id=@id AND event_type=N'CONSUMER_RETURN'
   AND direction=N'IN' AND predecessor_event_id=@pred", new { id = scenario.ReturnTaskId, pred = scenario.SaleEventId }));
        var partId = await ScalarAsync<long>(c, "SELECT id FROM dbo.upload_part WHERE task_id=@id",
            new { id = scenario.ReturnTaskId });
        Assert.Equal(1, await ScalarAsync<int>(c, @"
SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=N'upload-dispatch' AND intent_key=@k AND job_state=N'PENDING'",
            new { k = $"part:{partId}" }));

        Assert.Equal("CONFIRMED", await ScalarAsync<string>(c,
            "SELECT decision FROM dbo.return_confirmation WHERE task_id=@id", new { id = scenario.ReturnTaskId }));
        Assert.Equal("SALE", await ScalarAsync<string>(c,
            "SELECT relation_kind FROM dbo.return_confirmation WHERE task_id=@id", new { id = scenario.ReturnTaskId }));
        Assert.Equal(scenario.SaleTaskId, await ScalarAsync<long>(c,
            "SELECT predecessor_task_id FROM dbo.return_confirmation WHERE task_id=@id", new { id = scenario.ReturnTaskId }));
        Assert.Equal("tester", await ScalarAsync<string>(c,
            "SELECT confirmed_by FROM dbo.return_confirmation WHERE task_id=@id", new { id = scenario.ReturnTaskId }));
        // 首次上报声明并入证据落库（保留操作员原证据），并以 firstReportDeclared=true 固化。
        var storedEvidence = await ScalarAsync<string>(c,
            "SELECT evidence_json FROM dbo.return_confirmation WHERE task_id=@id", new { id = scenario.ReturnTaskId });
        using (var evidenceDoc = JsonDocument.Parse(storedEvidence))
        {
            Assert.Equal("原码退回", evidenceDoc.RootElement.GetProperty("reason").GetString());
            Assert.Equal("tester", evidenceDoc.RootElement.GetProperty("operator").GetString());
            Assert.True(evidenceDoc.RootElement.GetProperty("firstReportDeclared").GetBoolean());
        }
        Assert.Equal(1, await ScalarAsync<int>(c,
            "SELECT COUNT(*) FROM dbo.audit_log WHERE action=N'task.return.confirmed' AND object_id=@id AND env=N'LIVE'",
            new { id = scenario.ReturnTaskId.ToString(CultureInfo.InvariantCulture) }));
        // 审计 env 恒 LIVE（控制面/人工操作），但被操作任务与确认行自身 env 原样保留（MOCK）。
        Assert.Equal("MOCK", await ScalarAsync<string>(c, "SELECT env FROM dbo.upload_task WHERE id=@id",
            new { id = scenario.ReturnTaskId }));
        Assert.Equal("MOCK", await ScalarAsync<string>(c,
            "SELECT env FROM dbo.return_confirmation WHERE task_id=@id", new { id = scenario.ReturnTaskId }));
        Assert.Equal(1, await ScalarAsync<int>(c, "SELECT COUNT(*) FROM dbo.idempotency_record"));
    }

    /// <summary>
    /// 终审修复（DEF-02 服务端落实）：CONFIRMED 必须携带 firstReportDeclared=true；
    /// 缺失或 false 一律 422 FIRST_REPORT_NOT_DECLARED，且零写入（无确认行/占用/事件/outbox/审计/幂等）。
    /// </summary>
    [Fact]
    public async Task Confirm_without_first_report_declaration_is_422_and_writes_nothing()
    {
        await using var c = await _fx.OpenAsync();
        var code = Code(13);
        var scenario = await SeedConfirmableAsync(c, "SALE-T6-D1", "RET-T6-D1", code);

        var (result, failure) = await AttemptAsync(() => NewConfirmService().ConfirmAsync(scenario.ReturnTaskId,
            new ReturnConfirmCommand(1, "CONFIRMED", new[] { scenario.SaleEventId }, Evidence, "idem-t6-d1", "tester",
                FirstReportDeclared: false), default));

        Assert.Null(result);
        var validation = Assert.IsType<AllocationValidationException>(failure);
        Assert.Equal(ReturnConfirmService.ErrorFirstReportNotDeclared, validation.ErrorCode);
        Assert.Contains("首次上报声明必须勾选", validation.Message);

        // 重复键证据：JsonDocument 接受但 JsonNode 拒绝——必须 422 校验失败而非 500（仍零写入）。
        var (dupResult, dupFailure) = await AttemptAsync(() => NewConfirmService().ConfirmAsync(scenario.ReturnTaskId,
            new ReturnConfirmCommand(1, "CONFIRMED", new[] { scenario.SaleEventId }, "{\"a\":1,\"a\":2}",
                "idem-t6-d1-dup", "tester", FirstReportDeclared: true), default));
        Assert.Null(dupResult);
        Assert.Equal("VALIDATION", Assert.IsType<AllocationValidationException>(dupFailure).ErrorCode);

        Assert.Equal("Blocked", await ScalarAsync<string>(c, "SELECT state FROM dbo.upload_task WHERE id=@id",
            new { id = scenario.ReturnTaskId }));
        Assert.Equal("RETURN_CONFIRM_REQUIRED", await ScalarAsync<string>(c,
            "SELECT state_reason FROM dbo.upload_task WHERE id=@id", new { id = scenario.ReturnTaskId }));
        Assert.Equal(1, await ScalarAsync<int>(c, "SELECT version FROM dbo.upload_task WHERE id=@id",
            new { id = scenario.ReturnTaskId }));
        Assert.Equal(0, await ScalarAsync<int>(c, "SELECT COUNT(*) FROM dbo.return_confirmation"));
        Assert.Equal(0, await ScalarAsync<int>(c, "SELECT COUNT(*) FROM dbo.upload_part WHERE task_id=@id",
            new { id = scenario.ReturnTaskId }));
        Assert.Equal(0, await ScalarAsync<int>(c, "SELECT COUNT(*) FROM dbo.code_claim WHERE upload_task_id=@id",
            new { id = scenario.ReturnTaskId }));
        Assert.Equal(0, await ScalarAsync<int>(c, "SELECT COUNT(*) FROM dbo.code_event WHERE upload_task_id=@id",
            new { id = scenario.ReturnTaskId }));
        Assert.Equal(0, await ScalarAsync<int>(c, @"
SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=N'upload-dispatch' AND payload_json LIKE @p",
            new { p = "%" + scenario.ReturnTaskId + "%" }));
        Assert.Equal(0, await ScalarAsync<int>(c, @"
SELECT COUNT(*) FROM dbo.audit_log WHERE action LIKE N'task.return.%' AND object_id=@id",
            new { id = scenario.ReturnTaskId.ToString(CultureInfo.InvariantCulture) }));
        Assert.Equal(0, await ScalarAsync<int>(c, "SELECT COUNT(*) FROM dbo.idempotency_record"));
    }

    [Fact]
    public async Task Reject_keeps_blocked_and_writes_no_claim()
    {
        await using var c = await _fx.OpenAsync();
        var code = Code(2);
        var scenario = await SeedConfirmableAsync(c, "SALE-T6-R1", "RET-T6-R1", code);

        var result = await NewConfirmService().ConfirmAsync(scenario.ReturnTaskId,
            new ReturnConfirmCommand(1, "REJECTED", new[] { scenario.SaleEventId }, Evidence, "idem-t6-r1", "tester", FirstReportDeclared: false),
            default);

        Assert.False(result.Idempotent);
        Assert.Equal("Blocked", result.State);
        Assert.Equal("Blocked", await ScalarAsync<string>(c, "SELECT state FROM dbo.upload_task WHERE id=@id",
            new { id = scenario.ReturnTaskId }));
        Assert.Equal("RETURN_CONFIRM_REJECTED", await ScalarAsync<string>(c,
            "SELECT state_reason FROM dbo.upload_task WHERE id=@id", new { id = scenario.ReturnTaskId }));
        Assert.Equal(2, await ScalarAsync<int>(c, "SELECT version FROM dbo.upload_task WHERE id=@id",
            new { id = scenario.ReturnTaskId }));
        Assert.Equal(0, await ScalarAsync<int>(c, "SELECT COUNT(*) FROM dbo.upload_part WHERE task_id=@id",
            new { id = scenario.ReturnTaskId }));
        Assert.Equal(0, await ScalarAsync<int>(c, "SELECT COUNT(*) FROM dbo.code_claim WHERE upload_task_id=@id",
            new { id = scenario.ReturnTaskId }));
        Assert.Equal(0, await ScalarAsync<int>(c, "SELECT COUNT(*) FROM dbo.code_event WHERE upload_task_id=@id",
            new { id = scenario.ReturnTaskId }));
        Assert.Equal(0, await ScalarAsync<int>(c, @"
SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=N'upload-dispatch' AND payload_json LIKE @p",
            new { p = "%" + scenario.ReturnTaskId + "%" }));
        Assert.Equal("REJECTED", await ScalarAsync<string>(c,
            "SELECT decision FROM dbo.return_confirmation WHERE task_id=@id", new { id = scenario.ReturnTaskId }));
        Assert.Equal(Evidence, await ScalarAsync<string>(c,
            "SELECT evidence_json FROM dbo.return_confirmation WHERE task_id=@id", new { id = scenario.ReturnTaskId }));
        Assert.Equal(1, await ScalarAsync<int>(c,
            "SELECT COUNT(*) FROM dbo.audit_log WHERE action=N'task.return.rejected'"));
    }

    /// <summary>
    /// F1/R-M3-16：拒绝对「当时那个来源版本」终态。同单新快照（摘要不同）到达 → 占位撤销为
    /// Cancelled + RETURN_CONFIRM_REJECTED_SUPERSEDED、version+1、审计留 priorReason；
    /// 既有 return_confirmation(REJECTED) 原样保留（不重开、不新建任务/确认行）。
    /// </summary>
    [Fact]
    public async Task Rejected_placeholder_is_cancelled_when_source_snapshot_changes()
    {
        await using var c = await _fx.OpenAsync();
        var code = Code(12);
        var scenario = await SeedConfirmableAsync(c, "SALE-T6-S1", "RET-T6-S1", code);
        await NewConfirmService().ConfirmAsync(scenario.ReturnTaskId,
            new ReturnConfirmCommand(1, "REJECTED", new[] { scenario.SaleEventId }, Evidence, "idem-t6-s1", "tester", FirstReportDeclared: false),
            default);
        Assert.Equal("RETURN_CONFIRM_REJECTED", await ScalarAsync<string>(c,
            "SELECT state_reason FROM dbo.upload_task WHERE id=@id", new { id = scenario.ReturnTaskId }));

        // 同一退货单的 v2 快照（摘要不同）：重跑核验 → 被拒绝占位不得继续携带过期载荷。
        var ret2 = await SeedSaleAsync(c, "RET-T6-S1", 2, new LineSeed("1", "SP0001", -1, "CONSUMER_RETURN", code));
        await VerifyAsync(ret2);

        Assert.Equal("Cancelled", await ScalarAsync<string>(c, "SELECT state FROM dbo.upload_task WHERE id=@id",
            new { id = scenario.ReturnTaskId }));
        Assert.Equal("RETURN_CONFIRM_REJECTED_SUPERSEDED", await ScalarAsync<string>(c,
            "SELECT state_reason FROM dbo.upload_task WHERE id=@id", new { id = scenario.ReturnTaskId }));
        Assert.Equal(3, await ScalarAsync<int>(c, "SELECT version FROM dbo.upload_task WHERE id=@id",
            new { id = scenario.ReturnTaskId }));
        Assert.Equal(1, await ScalarAsync<int>(c,
            "SELECT COUNT(*) FROM dbo.return_confirmation WHERE task_id=@id AND decision=N'REJECTED'",
            new { id = scenario.ReturnTaskId }));
        Assert.Equal(1, await ScalarAsync<int>(c, @"
SELECT COUNT(*) FROM dbo.audit_log WHERE action=N'task.return_placeholder.cancelled' AND object_id=@id",
            new { id = scenario.ReturnTaskId.ToString(CultureInfo.InvariantCulture) }));
        var supersededEvidence = await ScalarAsync<string>(c, @"
SELECT after_digest FROM dbo.audit_log WHERE action=N'task.return_placeholder.cancelled' AND object_id=@id",
            new { id = scenario.ReturnTaskId.ToString(CultureInfo.InvariantCulture) });
        Assert.Contains("RETURN_CONFIRM_REJECTED", supersededEvidence); // priorReason 留证
        Assert.Contains("RETURN_CONFIRM_REJECTED_SUPERSEDED", supersededEvidence);
        // 不重开：同一事件键仍只有一行退货任务（不新建 REQUIRED 占位）。
        Assert.Equal(1, await ScalarAsync<int>(c,
            "SELECT COUNT(*) FROM dbo.upload_task WHERE task_type=N'CONSUMER_RETURN_116'"));
    }

    [Fact]
    public async Task Repeat_same_confirmation_is_idempotent()
    {
        await using var c = await _fx.OpenAsync();
        var code = Code(3);
        var scenario = await SeedConfirmableAsync(c, "SALE-T6-I1", "RET-T6-I1", code);
        var svc = NewConfirmService();
        var cmd = new ReturnConfirmCommand(1, "CONFIRMED", new[] { scenario.SaleEventId }, Evidence, "idem-t6-i1", "tester", FirstReportDeclared: true);

        var first = await svc.ConfirmAsync(scenario.ReturnTaskId, cmd, default);
        var second = await svc.ConfirmAsync(scenario.ReturnTaskId, cmd, default);

        Assert.False(first.Idempotent);
        Assert.True(second.Idempotent);
        Assert.Equal(first.TaskId, second.TaskId);
        Assert.Equal("Ready", second.State);
        Assert.Equal(1, await ScalarAsync<int>(c,
            "SELECT COUNT(*) FROM dbo.code_claim WHERE event_scope=N'RETURN:116' AND claim_state=N'ACTIVE'"));
        Assert.Equal(1, await ScalarAsync<int>(c,
            "SELECT COUNT(*) FROM dbo.code_event WHERE event_type=N'CONSUMER_RETURN'"));
        Assert.Equal(1, await ScalarAsync<int>(c, "SELECT COUNT(*) FROM dbo.idempotency_record"));
        Assert.Equal(1, await ScalarAsync<int>(c, "SELECT COUNT(*) FROM dbo.return_confirmation"));
    }

    /// <summary>
    /// F4：幂等键按任务隔离（storeKey = rc:+SHA256(IdemKey)，请求指纹含 taskId）。
    /// 同一 IdemKey 换任务 → 409 IDEMPOTENCY_CONFLICT，且第二个任务零写入。
    /// </summary>
    [Fact]
    public async Task Same_idempotency_key_for_different_task_is_409()
    {
        await using var c = await _fx.OpenAsync();
        var first = await SeedConfirmableAsync(c, "SALE-T6-K1", "RET-T6-K1", Code(10));
        var second = await SeedConfirmableAsync(c, "SALE-T6-K2", "RET-T6-K2", Code(11));
        var svc = NewConfirmService();
        await svc.ConfirmAsync(first.ReturnTaskId,
            new ReturnConfirmCommand(1, "CONFIRMED", new[] { first.SaleEventId }, Evidence, "idem-t6-shared", "tester", FirstReportDeclared: true),
            default);

        await Assert.ThrowsAsync<IdempotencyConflictException>(() => svc.ConfirmAsync(second.ReturnTaskId,
            new ReturnConfirmCommand(1, "CONFIRMED", new[] { second.SaleEventId }, Evidence, "idem-t6-shared", "tester", FirstReportDeclared: true),
            default));

        Assert.Equal("Blocked", await ScalarAsync<string>(c, "SELECT state FROM dbo.upload_task WHERE id=@id",
            new { id = second.ReturnTaskId }));
        Assert.Equal("RETURN_CONFIRM_REQUIRED", await ScalarAsync<string>(c,
            "SELECT state_reason FROM dbo.upload_task WHERE id=@id", new { id = second.ReturnTaskId }));
        Assert.Equal(1, await ScalarAsync<int>(c, "SELECT version FROM dbo.upload_task WHERE id=@id",
            new { id = second.ReturnTaskId }));
        Assert.Equal(0, await ScalarAsync<int>(c,
            "SELECT COUNT(*) FROM dbo.code_claim WHERE upload_task_id=@id AND claim_state=N'ACTIVE'",
            new { id = second.ReturnTaskId }));
        Assert.Equal(0, await ScalarAsync<int>(c, "SELECT COUNT(*) FROM dbo.return_confirmation WHERE task_id=@id",
            new { id = second.ReturnTaskId }));
        Assert.Equal(1, await ScalarAsync<int>(c, "SELECT COUNT(*) FROM dbo.idempotency_record"));
    }

    [Fact]
    public async Task Stale_version_is_409()
    {
        await using var c = await _fx.OpenAsync();
        var code = Code(4);
        var scenario = await SeedConfirmableAsync(c, "SALE-T6-V1", "RET-T6-V1", code);
        var svc = NewConfirmService();
        var cmd = new ReturnConfirmCommand(1, "CONFIRMED", new[] { scenario.SaleEventId }, Evidence, "idem-t6-v1", "tester", FirstReportDeclared: true);
        await svc.ConfirmAsync(scenario.ReturnTaskId, cmd, default);

        var ex = await Assert.ThrowsAsync<AllocationConflictException>(() =>
            svc.ConfirmAsync(scenario.ReturnTaskId, cmd with { IdemKey = "idem-t6-v2" }, default));

        Assert.Equal("VERSION_CONFLICT", ex.ErrorCode);
        Assert.Equal(1, await ScalarAsync<int>(c,
            "SELECT COUNT(*) FROM dbo.code_claim WHERE event_scope=N'RETURN:116' AND claim_state=N'ACTIVE'"));
        Assert.Equal(1, await ScalarAsync<int>(c, "SELECT COUNT(*) FROM dbo.return_confirmation"));
        Assert.Equal(1, await ScalarAsync<int>(c,
            "SELECT COUNT(*) FROM dbo.audit_log WHERE action=N'task.return.confirmed'"));
    }

    [Fact]
    public async Task Predecessor_missing_blocks_confirmation()
    {
        await using var c = await _fx.OpenAsync();
        var code = Code(5);
        var ret = await SeedSaleAsync(c, "RET-T6-M1", 1, new LineSeed("1", "SP0001", -1, "CONSUMER_RETURN", code));
        await VerifyAsync(ret);
        var returnTaskId = await ScalarAsync<long>(c, "SELECT id FROM dbo.upload_task WHERE event_key=@k",
            new { k = "KSOA:RET-T6-M1:CONSUMER_RETURN_116" });

        var ex = await Assert.ThrowsAsync<AllocationValidationException>(() =>
            NewConfirmService().ConfirmAsync(returnTaskId,
                new ReturnConfirmCommand(1, "CONFIRMED", Array.Empty<long>(), Evidence, "idem-t6-m1", "tester", FirstReportDeclared: true), default));

        Assert.Equal("PREDECESSOR_MISSING", ex.ErrorCode);
        Assert.Equal("Blocked", await ScalarAsync<string>(c, "SELECT state FROM dbo.upload_task WHERE id=@id",
            new { id = returnTaskId }));
        Assert.Equal("RETURN_CONFIRM_REQUIRED", await ScalarAsync<string>(c,
            "SELECT state_reason FROM dbo.upload_task WHERE id=@id", new { id = returnTaskId }));
        Assert.Equal(0, await ScalarAsync<int>(c, "SELECT COUNT(*) FROM dbo.code_claim WHERE upload_task_id=@id",
            new { id = returnTaskId }));
        Assert.Equal(0, await ScalarAsync<int>(c, "SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=N'upload-dispatch'"));
        Assert.Equal(0, await ScalarAsync<int>(c, "SELECT COUNT(*) FROM dbo.return_confirmation"));
    }

    [Fact]
    public async Task Confirmed_116_payload_contains_original_sale_bill()
    {
        await using var c = await _fx.OpenAsync();
        var code = Code(6);
        var scenario = await SeedConfirmableAsync(c, "SALE-T6-B1", "RET-T6-B1", code);

        await NewConfirmService().ConfirmAsync(scenario.ReturnTaskId,
            new ReturnConfirmCommand(1, "CONFIRMED", new[] { scenario.SaleEventId }, Evidence, "idem-t6-b1", "tester", FirstReportDeclared: true),
            default);

        var taskPayload = await ScalarAsync<string>(c, "SELECT payload_json FROM dbo.upload_task WHERE id=@id",
            new { id = scenario.ReturnTaskId });
        Assert.Contains(scenario.SaleBillCode, taskPayload); // RET-04：原销售单号进入退货载荷
        var partPayload = await ScalarAsync<string>(c, "SELECT payload_json FROM dbo.upload_part WHERE task_id=@id",
            new { id = scenario.ReturnTaskId });
        Assert.Contains(scenario.SaleBillCode, partPayload);
        Assert.Equal("CONSUMER_RETURN_116", await ScalarAsync<string>(c,
            "SELECT task_type FROM dbo.upload_task WHERE id=@id", new { id = scenario.ReturnTaskId }));
    }

    /// <summary>R-M3-4：202 无 KSOA 采集，用夹具播种占位 + 前序 102 任务即可完成确认（不伪造来源）。</summary>
    [Fact]
    public async Task Confirmation_works_for_202_when_fixture_task_seeded()
    {
        await using var c = await _fx.OpenAsync();
        var (orgId, _) = await TestSeed.EnsureAsync(c);
        var code = Code(7);
        var codeId = await EnsureCodeAsync(c, code);
        var purchaseTaskId = await ScalarAsync<long>(c, @"
INSERT INTO dbo.upload_task(org_id,env,event_key,task_type,source_hash,payload_hash,state,source_integrity,match_state,
    verification_state,dependency_state)
OUTPUT INSERTED.id
VALUES(@orgId,N'MOCK',N'KSOA:PI-202:PURCHASE_IN_102',N'PURCHASE_IN_102',@hash,@hash,N'Succeeded',N'COMPLETE',N'COMPLETE',N'PASSED',N'NONE')",
            new { orgId, hash = Sha256("PI-202") });
        var purchaseEventId = await ScalarAsync<long>(c, @"
INSERT INTO dbo.code_event(org_id,env,code_id,event_type,direction,upload_task_id)
OUTPUT INSERTED.id VALUES(@orgId,N'MOCK',@codeId,N'PURCHASE_IN',N'IN',@taskId)",
            new { orgId, codeId, taskId = purchaseTaskId });
        var payload = JsonSerializer.Serialize(new
        {
            eventKey = "KSOA:PRET-202:PURCHASE_RETURN_202",
            taskType = "PURCHASE_RETURN_202",
            ruleVersion = "M3-1",
            lines = new[]
            {
                new
                {
                    sourceLineKey = "1", productSourceId = "SP0001", platformDrugId = "",
                    quantity = "-1.000000", unit = "盒", batchNo = "B2026", codes = new[] { code },
                },
            },
        });
        var taskId = await ScalarAsync<long>(c, @"
INSERT INTO dbo.upload_task(org_id,env,event_key,task_type,source_hash,payload_json,payload_hash,state,state_reason,
    source_integrity,match_state,verification_state,dependency_state)
OUTPUT INSERTED.id
VALUES(@orgId,N'MOCK',N'KSOA:PRET-202:PURCHASE_RETURN_202',N'PURCHASE_RETURN_202',@hash,@payload,@ph,N'Blocked',
    N'RETURN_CONFIRM_REQUIRED',N'COMPLETE',N'COMPLETE',N'PASSED',N'NONE')",
            new { orgId, hash = new string('0', 64), payload, ph = Sha256(payload) });

        var result = await NewConfirmService().ConfirmAsync(taskId,
            new ReturnConfirmCommand(1, "CONFIRMED", new[] { purchaseEventId }, Evidence, "idem-t6-202", "tester", FirstReportDeclared: true), default);

        Assert.Equal("Ready", result.State);
        Assert.Equal("PURCHASE_RETURN_202", await ScalarAsync<string>(c,
            "SELECT task_type FROM dbo.upload_task WHERE id=@id", new { id = taskId }));
        Assert.Equal(1, await ScalarAsync<int>(c, @"
SELECT COUNT(*) FROM dbo.code_claim WHERE upload_task_id=@id AND event_scope=N'RETURN:202' AND claim_state=N'ACTIVE'",
            new { id = taskId }));
        Assert.Equal(1, await ScalarAsync<int>(c, @"
SELECT COUNT(*) FROM dbo.code_event WHERE upload_task_id=@id AND event_type=N'PURCHASE_RETURN'
   AND direction=N'OUT' AND predecessor_event_id=@pred", new { id = taskId, pred = purchaseEventId }));
        Assert.Equal(1, await ScalarAsync<int>(c,
            "SELECT COUNT(*) FROM dbo.upload_part WHERE task_id=@id", new { id = taskId }));
        Assert.Equal("PURCHASE_IN", await ScalarAsync<string>(c,
            "SELECT relation_kind FROM dbo.return_confirmation WHERE task_id=@id", new { id = taskId }));
        // F5：夹具 202 无来源快照 → 确认提升后仍保持 NULL（不得写成 0 冒充快照 id），事件同理。
        Assert.Null(await ScalarAsync<long?>(c,
            "SELECT source_snapshot_id FROM dbo.upload_task WHERE id=@id", new { id = taskId }));
        Assert.Equal(0, await ScalarAsync<int>(c, @"
SELECT COUNT(*) FROM dbo.code_event WHERE upload_task_id=@id AND source_snapshot_id IS NOT NULL",
            new { id = taskId }));
    }

    /// <summary>
    /// 决策 5：同一前序（同一码）的两个 116 同时确认 → 前序任务行锁串行化 + 锁后重解析，
    /// 恰一个提交占用/事件；后到者 ALREADY_RETURNED，绝无双提交（UX_claim_active 兜底）。
    /// </summary>
    [Fact]
    public async Task Concurrent_confirmations_on_same_predecessor_commit_once()
    {
        await using var c = await _fx.OpenAsync();
        var code = Code(8);
        var first = await SeedConfirmableAsync(c, "SALE-T6-X1", "RET-T6-X1", code);
        // 第二张退货单引用同一码（同一前序），独立 116 占位
        var second = await SeedSaleAsync(c, "RET-T6-X2", 1, new LineSeed("1", "SP0001", -1, "CONSUMER_RETURN", code));
        await VerifyAsync(second);
        var secondTaskId = await ScalarAsync<long>(c, "SELECT id FROM dbo.upload_task WHERE event_key=@k",
            new { k = "KSOA:RET-T6-X2:CONSUMER_RETURN_116" });

        var outcomes = await Task.WhenAll(
            AttemptAsync(() => NewConfirmService().ConfirmAsync(first.ReturnTaskId,
                new ReturnConfirmCommand(1, "CONFIRMED", new[] { first.SaleEventId }, Evidence, "idem-t6-x1", "tester", FirstReportDeclared: true),
                default)),
            AttemptAsync(() => NewConfirmService().ConfirmAsync(secondTaskId,
                new ReturnConfirmCommand(1, "CONFIRMED", new[] { first.SaleEventId }, Evidence, "idem-t6-x2", "tester", FirstReportDeclared: true),
                default)));

        Assert.Single(outcomes.Where(o => o.Result is not null));
        var failure = Assert.Single(outcomes.Where(o => o.Error is not null)).Error!;
        var validation = Assert.IsType<AllocationValidationException>(failure);
        Assert.Equal("ALREADY_RETURNED", validation.ErrorCode);

        Assert.Equal(1, await ScalarAsync<int>(c,
            "SELECT COUNT(*) FROM dbo.code_claim WHERE event_scope=N'RETURN:116' AND claim_state=N'ACTIVE'"));
        Assert.Equal(1, await ScalarAsync<int>(c,
            "SELECT COUNT(*) FROM dbo.code_event WHERE event_type=N'CONSUMER_RETURN'"));
        Assert.Equal(1, await ScalarAsync<int>(c, "SELECT COUNT(*) FROM dbo.return_confirmation"));
        Assert.Equal(1, await ScalarAsync<int>(c,
            "SELECT COUNT(*) FROM dbo.upload_task WHERE task_type=N'CONSUMER_RETURN_116' AND state=N'Ready'"));
        Assert.Equal(1, await ScalarAsync<int>(c,
            "SELECT COUNT(*) FROM dbo.upload_task WHERE task_type=N'CONSUMER_RETURN_116' AND state=N'Blocked'"));
    }

    /// <summary>前序 321 仅 Ready（未 Succeeded、无平台受理证据）→ 422 PREDECESSOR_MISMATCH，零提交。</summary>
    [Fact]
    public async Task Predecessor_ready_without_platform_receipt_is_422()
    {
        await using var c = await _fx.OpenAsync();
        var code = Code(9);
        var scenario = await SeedConfirmableAsync(c, "SALE-T6-N1", "RET-T6-N1", code, saleSucceeded: false);
        Assert.Equal("Ready", await ScalarAsync<string>(c, "SELECT state FROM dbo.upload_task WHERE id=@id",
            new { id = scenario.SaleTaskId }));

        var ex = await Assert.ThrowsAsync<AllocationValidationException>(() =>
            NewConfirmService().ConfirmAsync(scenario.ReturnTaskId,
                new ReturnConfirmCommand(1, "CONFIRMED", new[] { scenario.SaleEventId }, Evidence, "idem-t6-n1", "tester", FirstReportDeclared: true),
                default));

        Assert.Equal("PREDECESSOR_MISMATCH", ex.ErrorCode);
        Assert.Equal("Blocked", await ScalarAsync<string>(c, "SELECT state FROM dbo.upload_task WHERE id=@id",
            new { id = scenario.ReturnTaskId }));
        Assert.Equal("RETURN_CONFIRM_REQUIRED", await ScalarAsync<string>(c,
            "SELECT state_reason FROM dbo.upload_task WHERE id=@id", new { id = scenario.ReturnTaskId }));
        Assert.Equal(0, await ScalarAsync<int>(c, "SELECT COUNT(*) FROM dbo.code_claim WHERE upload_task_id=@id",
            new { id = scenario.ReturnTaskId }));
        Assert.Equal(0, await ScalarAsync<int>(c, "SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=N'upload-dispatch' AND payload_json LIKE @p",
            new { p = "%" + scenario.ReturnTaskId + "%" }));
    }

    // ---------- F6：唯一键冲突（UQ_idempotency_user_key）两条分支的确定性覆盖 ----------

    /// <summary>
    /// F6 分支一（确定性，不依赖随机并发）：赛道读落空后预置同 storeKey/user/requestHash 的幂等胜者
    /// （响应体含 {taskId,state}），本次业务写入完成即撞 <c>UQ_idempotency_user_key</c> →
    /// 业务+幂等整体回滚 → 重读胜者并回放其响应。断言证明：回放的是胜者结论，且任务/分片/确认行/
    /// 审计/占用/事件/发送作业与幂等表都只有胜者那一份（本事务零残留）。
    /// </summary>
    [Fact]
    public async Task Idempotency_race_winner_is_replayed_and_business_writes_roll_back()
    {
        await using var c = await _fx.OpenAsync();
        var code = Code(14);
        var scenario = await SeedConfirmableAsync(c, "SALE-T6-W1", "RET-T6-W1", code);
        var svc = NewConfirmService(new WinnerRaceIdempotencyStore(_fx.ConnectionString, scenario.ReturnTaskId));

        var result = await svc.ConfirmAsync(scenario.ReturnTaskId,
            new ReturnConfirmCommand(1, "CONFIRMED", new[] { scenario.SaleEventId }, Evidence, "idem-t6-w1", "tester",
                FirstReportDeclared: true), default);

        // 回放胜者响应（ParseHit 与胜者响应体一致），而非本次业务结果。
        Assert.True(result.Idempotent);
        Assert.Equal(scenario.ReturnTaskId, result.TaskId);
        Assert.Equal("Ready", result.State);

        // 本次业务写入零残留：任务仍停在 Blocked/RETURN_CONFIRM_REQUIRED、版本未变。
        Assert.Equal("Blocked", await ScalarAsync<string>(c, "SELECT state FROM dbo.upload_task WHERE id=@id",
            new { id = scenario.ReturnTaskId }));
        Assert.Equal("RETURN_CONFIRM_REQUIRED", await ScalarAsync<string>(c,
            "SELECT state_reason FROM dbo.upload_task WHERE id=@id", new { id = scenario.ReturnTaskId }));
        Assert.Equal(1, await ScalarAsync<int>(c, "SELECT version FROM dbo.upload_task WHERE id=@id",
            new { id = scenario.ReturnTaskId }));
        Assert.Null(await ScalarAsync<long?>(c,
            "SELECT predecessor_task_id FROM dbo.upload_task WHERE id=@id", new { id = scenario.ReturnTaskId }));
        // 确认行/审计/占用/分片/事件/发送作业均无第二次写入（全部来自被回滚的本次事务）。
        Assert.Equal(0, await ScalarAsync<int>(c, "SELECT COUNT(*) FROM dbo.return_confirmation"));
        Assert.Equal(0, await ScalarAsync<int>(c,
            "SELECT COUNT(*) FROM dbo.audit_log WHERE action LIKE N'task.return.%' AND object_id=@id",
            new { id = scenario.ReturnTaskId.ToString(CultureInfo.InvariantCulture) }));
        Assert.Equal(0, await ScalarAsync<int>(c, "SELECT COUNT(*) FROM dbo.upload_part WHERE task_id=@id",
            new { id = scenario.ReturnTaskId }));
        Assert.Equal(0, await ScalarAsync<int>(c, "SELECT COUNT(*) FROM dbo.code_claim WHERE upload_task_id=@id",
            new { id = scenario.ReturnTaskId }));
        Assert.Equal(0, await ScalarAsync<int>(c, "SELECT COUNT(*) FROM dbo.code_event WHERE upload_task_id=@id",
            new { id = scenario.ReturnTaskId }));
        Assert.Equal(0, await ScalarAsync<int>(c, @"
SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=N'upload-dispatch' AND payload_json LIKE @p",
            new { p = "%" + scenario.ReturnTaskId + "%" }));
        // 幂等表只有胜者一行，响应体即回放的来源。
        Assert.Equal(1, await ScalarAsync<int>(c, "SELECT COUNT(*) FROM dbo.idempotency_record"));
        var body = await ScalarAsync<string>(c, "SELECT response_body FROM dbo.idempotency_record");
        Assert.Contains("\"state\":\"Ready\"", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// F6 分支二（确定性）：赛道读落空后预置**同 storeKey/user 但不同 requestHash** 的幂等记录，
    /// 本次 SaveAsync 撞 <c>UQ_idempotency_user_key</c> → 回滚 → 重读胜者时摘要不一致 →
    /// <see cref="IdempotencyConflictException"/>（409 IDEMPOTENCY_CONFLICT 语义），业务写入全部回滚。
    /// 与 <see cref="Same_idempotency_key_for_different_task_is_409"/> 互补：那条覆盖"赛道读命中"，
    /// 本条覆盖"读落空后写冲突"的同一裁定。
    /// </summary>
    [Fact]
    public async Task Idempotency_race_with_different_request_hash_is_409_and_rolls_back()
    {
        await using var c = await _fx.OpenAsync();
        var code = Code(15);
        var scenario = await SeedConfirmableAsync(c, "SALE-T6-W2", "RET-T6-W2", code);
        var svc = NewConfirmService(
            new WinnerRaceIdempotencyStore(_fx.ConnectionString, scenario.ReturnTaskId, new string('F', 64)));

        await Assert.ThrowsAsync<IdempotencyConflictException>(() => svc.ConfirmAsync(scenario.ReturnTaskId,
            new ReturnConfirmCommand(1, "CONFIRMED", new[] { scenario.SaleEventId }, Evidence, "idem-t6-w2", "tester",
                FirstReportDeclared: true), default));

        Assert.Equal("Blocked", await ScalarAsync<string>(c, "SELECT state FROM dbo.upload_task WHERE id=@id",
            new { id = scenario.ReturnTaskId }));
        Assert.Equal("RETURN_CONFIRM_REQUIRED", await ScalarAsync<string>(c,
            "SELECT state_reason FROM dbo.upload_task WHERE id=@id", new { id = scenario.ReturnTaskId }));
        Assert.Equal(1, await ScalarAsync<int>(c, "SELECT version FROM dbo.upload_task WHERE id=@id",
            new { id = scenario.ReturnTaskId }));
        Assert.Equal(0, await ScalarAsync<int>(c, "SELECT COUNT(*) FROM dbo.return_confirmation"));
        Assert.Equal(0, await ScalarAsync<int>(c,
            "SELECT COUNT(*) FROM dbo.audit_log WHERE action LIKE N'task.return.%' AND object_id=@id",
            new { id = scenario.ReturnTaskId.ToString(CultureInfo.InvariantCulture) }));
        Assert.Equal(0, await ScalarAsync<int>(c, "SELECT COUNT(*) FROM dbo.upload_part WHERE task_id=@id",
            new { id = scenario.ReturnTaskId }));
        Assert.Equal(0, await ScalarAsync<int>(c, "SELECT COUNT(*) FROM dbo.code_claim WHERE upload_task_id=@id",
            new { id = scenario.ReturnTaskId }));
        Assert.Equal(0, await ScalarAsync<int>(c, "SELECT COUNT(*) FROM dbo.code_event WHERE upload_task_id=@id",
            new { id = scenario.ReturnTaskId }));
        Assert.Equal(0, await ScalarAsync<int>(c, @"
SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=N'upload-dispatch' AND payload_json LIKE @p",
            new { p = "%" + scenario.ReturnTaskId + "%" }));
        // 幂等表只有预置的异摘要记录（本次插入随事务回滚）。
        Assert.Equal(1, await ScalarAsync<int>(c, "SELECT COUNT(*) FROM dbo.idempotency_record"));
        Assert.Equal(new string('F', 64), await ScalarAsync<string>(c,
            "SELECT request_hash FROM dbo.idempotency_record"));
    }

    /// <summary>
    /// F6 死分支的等价性证据：同一任务的两个并发确认由「插入前 UPDLOCK/HOLDLOCK 锁任务行 + 锁后重解析」
    /// 串行化，败者必落 4xx 业务错误（VERSION_CONFLICT / NOT_RETURNABLE / ALREADY_RETURNED），
    /// 绝不抛原始 <see cref="SqlException"/>（若 UQ_retconf_task 冲突逃出会成 500）。
    /// 恰一条确认行/占用/确认审计，证明没有双写；UQ_retconf_task 的 catch 分支因此仅作防御。
    /// </summary>
    [Fact]
    public async Task Concurrent_confirmation_of_same_task_never_escapes_raw_sql_error()
    {
        await using var c = await _fx.OpenAsync();
        var code = Code(16);
        var scenario = await SeedConfirmableAsync(c, "SALE-T6-W3", "RET-T6-W3", code);

        var outcomes = await Task.WhenAll(
            AttemptAsync(() => NewConfirmService().ConfirmAsync(scenario.ReturnTaskId,
                new ReturnConfirmCommand(1, "CONFIRMED", new[] { scenario.SaleEventId }, Evidence, "idem-t6-w3a", "tester",
                    FirstReportDeclared: true), default)),
            AttemptAsync(() => NewConfirmService().ConfirmAsync(scenario.ReturnTaskId,
                new ReturnConfirmCommand(1, "CONFIRMED", new[] { scenario.SaleEventId }, Evidence, "idem-t6-w3b", "tester",
                    FirstReportDeclared: true), default)));

        Assert.Single(outcomes.Where(o => o.Result is not null));
        var failure = Assert.Single(outcomes.Where(o => o.Error is not null)).Error!;
        Assert.False(failure is SqlException, $"并发同任务确认不得抛原始 SQL 异常：{failure}");
        var errorCode = failure switch
        {
            AllocationConflictException conflict => conflict.ErrorCode,
            AllocationValidationException validation => validation.ErrorCode,
            _ => throw new Xunit.Sdk.XunitException($"意外的异常类型 {failure.GetType().Name}: {failure.Message}"),
        };
        Assert.Contains(errorCode, new[]
        {
            ReturnConfirmService.ErrorVersionConflict, ReturnConfirmService.ErrorNotReturnable, "ALREADY_RETURNED",
        });

        Assert.Equal(1, await ScalarAsync<int>(c, "SELECT COUNT(*) FROM dbo.return_confirmation"));
        Assert.Equal(1, await ScalarAsync<int>(c,
            "SELECT COUNT(*) FROM dbo.code_claim WHERE event_scope=N'RETURN:116' AND claim_state=N'ACTIVE'"));
        Assert.Equal(1, await ScalarAsync<int>(c,
            "SELECT COUNT(*) FROM dbo.audit_log WHERE action=N'task.return.confirmed' AND object_id=@id",
            new { id = scenario.ReturnTaskId.ToString(CultureInfo.InvariantCulture) }));
        Assert.Equal(1, await ScalarAsync<int>(c, "SELECT COUNT(*) FROM dbo.idempotency_record"));
    }
}