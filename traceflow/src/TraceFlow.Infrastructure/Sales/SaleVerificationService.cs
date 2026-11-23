using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Domain.Codes;
using TraceFlow.Domain.Sales;
using TraceFlow.Domain.Sources;
using TraceFlow.Domain.Top;
using TraceFlow.Infrastructure.Codes;
using TraceFlow.Infrastructure.Settings;
using TraceFlow.Infrastructure.Top;

namespace TraceFlow.Infrastructure.Sales;

/// <summary>
/// 销售核验（SALE-01..04）：COMPLETE SALE 快照 → 本地规则（<see cref="SaleRules"/>）→ 逐码依赖门禁与平台证据
/// → verification/verification_code → 全部通过后同事务调用 <see cref="RetailTaskFactory"/>（321）。
/// 平台调用在事务外（§2.9），全部落库在一个事务内；平台限流/通道暂停 → WAIT_RETRY 交作业退避重排。
/// 同时有正负行的单据：正行进 321；负行只落 Blocked + RETURN_CONFIRM_REQUIRED 的 116 占位任务
/// （无分片/占用/outbox），由 Task 6 确认后经工厂提升（R-M3-7 双任务）。
/// </summary>
public sealed class SaleVerificationService
{
    public const string JobKind = "sale-verify";
    /// <summary>TOP 码查询方法（G-TOP 前为合同候选；MOCK 由 fixture/Fake 网关应答）。</summary>
    public const string CodedetailMethod = "alibaba.alihealth.drugtrace.top.lsyd.query.codedetail";

    public const string ReasonWaitDependency = "WAIT_DEPENDENCY";
    public const string ReasonWaitVerify = "WAIT_VERIFY";
    public const string ReasonVerifyFailedPrefix = "VERIFY_FAILED:";
    public const string StateWaitRetry = "WAIT_RETRY";
    public const string ReasonReturnConfirmRequired = "RETURN_CONFIRM_REQUIRED";
    public const string ReasonPlatformNotFound = "PLATFORM_NOT_FOUND";

    private const string TaskTypeSale = RetailTaskFactory.Retail321;
    private const string TaskTypeReturn = RetailTaskFactory.ConsumerReturn116;
    private const int CodeChunk = 500;
    private const int ReasonMaxChars = 500;
    private const int EvidenceMaxChars = 4000;

    private readonly string _cs;
    private readonly string _env;
    private readonly SettingsService _settings;
    private readonly TopClientService _top;
    private readonly RetailTaskFactory _factory;
    private readonly IJobQueue _queue;
    private readonly IAuditLog _audit;

    public SaleVerificationService(string connectionString, string env, SettingsService settings, TopClientService top,
        RetailTaskFactory factory, IJobQueue queue, IAuditLog audit)
    {
        _cs = connectionString;
        _env = env;
        _settings = settings;
        _top = top;
        _factory = factory;
        _queue = queue;
        _audit = audit;
    }

    /// <summary>
    /// 核验单个快照；返回当前 verification id。非 COMPLETE SALE 快照返回 null（作业直接完成）。
    /// 幂等：现状已 PASSED 且来源未变 → 直接返回既有 id，零写入、零平台调用。
    /// </summary>
    public async Task<long?> VerifySnapshotAsync(long snapshotId, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        var snap = await conn.QuerySingleOrDefaultAsync<SnapshotRow>(new CommandDefinition(@"
SELECT id, org_id, source_system_id, source_document_key, document_type, completeness_status, content_hash, business_datetime
FROM dbo.source_snapshot WHERE id=@id", new { id = snapshotId }, cancellationToken: ct));
        if (snap is null || snap.document_type != "SALE" || snap.completeness_status != "COMPLETE")
            return null;

        var settings = await _settings.GetSaleVerifySettingsAsync(ct);
        var eventKey = $"KSOA:{snap.source_document_key}:SALE";
        var settled = await ReadCurrentAsync(conn, snap.org_id, eventKey, null, ct);
        if (IsSettledPassed(settled, snap))
            return settled!.id;

        var org = await conn.QuerySingleAsync<OrgRow>(new CommandDefinition(
            "SELECT ent_id, ref_ent_id FROM dbo.organization WHERE id=@id", new { id = snap.org_id }, cancellationToken: ct));
        var rows = await LoadLinesAsync(conn, snap, ct);
        var rules = SaleRules.Evaluate(rows.Select(ToInput).ToList());
        if (!rules.Ok)
            return await WriteRuleFailureAsync(conn, snap, eventKey, settings.RuleVersion, rules, ct);

        try
        {
            return await RunVerifiedAsync(conn, snap, eventKey, settings, rules, rows, org, ct);
        }
        catch (Exception ex) when (ex is AllocationConflictException or AllocationValidationException)
        {
            return await WriteTaskConflictAsync(conn, snap, eventKey, settings.RuleVersion, rules, ex, ct);
        }
    }

    /// <summary>verification 当前状态（作业判定是否需退避重排）；不存在返回 null。</summary>
    public async Task<string?> GetVerificationStateAsync(long verificationId, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT state FROM dbo.verification WHERE id=@id", new { id = verificationId }, cancellationToken: ct));
    }

    /// <summary>
    /// 前序采购入库成功后重唤醒（Task 5 调用，调用方事务）：把覆盖这些码且当前挂 WAIT_DEPENDENCY 的
    /// sale-verify 重新入队。返回入队（含幂等命中）的快照数。
    /// </summary>
    public async Task<int> EnqueueDependentVerificationsAsync(SqlConnection conn, SqlTransaction tx,
        IReadOnlyCollection<long> codeIds, long orgId, string env, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(tx);
        ArgumentNullException.ThrowIfNull(codeIds);
        if (codeIds.Count == 0) return 0;

        var ids = codeIds.Distinct().ToList();
        var codes = new List<string>();
        for (var offset = 0; offset < ids.Count; offset += CodeChunk)
        {
            var chunk = ids.Skip(offset).Take(CodeChunk).ToList();
            codes.AddRange(await conn.QueryAsync<string>(new CommandDefinition(
                "SELECT code_normalized FROM dbo.trace_code WHERE id IN @ids", new { ids = chunk }, tx, cancellationToken: ct)));
        }
        if (codes.Count == 0) return 0;

        var snapshotIds = new List<long>();
        for (var offset = 0; offset < codes.Count; offset += CodeChunk)
        {
            var chunk = codes.Skip(offset).Take(CodeChunk).ToList();
            snapshotIds.AddRange(await conn.QueryAsync<long>(new CommandDefinition(@"
SELECT DISTINCT t.source_snapshot_id
FROM dbo.upload_task t
JOIN dbo.source_line l ON l.snapshot_id=t.source_snapshot_id
WHERE t.org_id=@orgId AND t.env=@env AND t.state=N'Blocked' AND t.state_reason=@reason
  AND t.source_snapshot_id IS NOT NULL AND l.trace_code_normalized IN @codes",
                new { orgId, env, reason = ReasonWaitDependency, codes = chunk }, tx, cancellationToken: ct)));
        }

        var unix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var enqueued = 0;
        foreach (var snapshotId in snapshotIds.Distinct().OrderBy(id => id))
        {
            await _queue.EnqueueAsync(conn, tx, orgId, env, JobKind, $"snapshot:{snapshotId}:dep:{unix}",
                SaleVerifyPayload(orgId, env, snapshotId), DateTime.UtcNow, ct);
            enqueued++;
        }
        return enqueued;
    }

    private async Task<long> RunVerifiedAsync(SqlConnection conn, SnapshotRow snap, string eventKey,
        SaleVerifySettings settings, SaleRuleResult rules, IReadOnlyList<LineRow> rows, OrgRow org, CancellationToken ct)
    {
        // ---- 阶段 B：逐码依赖门禁 + 平台证据（事务外；同码本次运行至多一次 codedetail）----
        var ordered = OrderedSaleCodes(rules);
        var priorPass = await LoadPriorPassCodesAsync(conn, snap, eventKey, settings.RuleVersion, ct);
        var pending = ordered.Where(c => !priorPass.Contains(c.Code)).ToList();
        var batch = pending.Take(settings.MaxCodesPerRun).ToList();
        var dependencySatisfied = await LoadPurchaseSucceededAsync(conn, snap, batch, ct);

        var checkedConclusions = new List<CodeConclusion>();
        var waitRetry = false;
        var platformCalls = 0;
        foreach (var entry in batch)
        {
            if (dependencySatisfied.Contains(entry.Code))
            {
                // 依赖门禁已由本地证据满足（采购入库任务成功），不再消耗平台查询预算、也不冒“只查不上传”风控。
                checkedConclusions.Add(new CodeConclusion(entry, "PASS", "DEPENDENCY_PURCHASE_IN_SUCCEEDED", null));
                continue;
            }
            platformCalls++;
            CodeConclusion conclusion;
            try
            {
                var outcome = await _top.CallAsync(_env, CodedetailMethod, CodedetailParameters(entry.Code, org),
                    org.ref_ent_id, "SYSTEM", ct);
                (conclusion, var retry) = Classify(entry, outcome);
                waitRetry = retry;
            }
            catch (Exception ex) when (ex is TopRateLimitedException or ChannelPausedException
                or TopNotCertifiedException or SqlException or HttpRequestException)
            {
                conclusion = new CodeConclusion(entry, "UNKNOWN", "PLATFORM_UNAVAILABLE", Truncate(ex.Message, EvidenceMaxChars));
                waitRetry = true;
            }
            checkedConclusions.Add(conclusion);
            if (waitRetry) break;
        }

        var deferred = pending.Count - checkedConclusions.Count;
        var conclusions = ComposeConclusions(ordered, priorPass, checkedConclusions);
        var failed = conclusions.FirstOrDefault(c => c.Result == "FAIL");
        var dependencyMissing = conclusions.Any(c => c.Result == "UNKNOWN" && c.Reason == ReasonWaitDependency);
        var state = waitRetry ? StateWaitRetry
            : failed is not null ? "FAILED"
            : conclusions.Any(c => c.Result != "PASS") || deferred > 0 ? "UNRESOLVED"
            : "PASSED";
        var reason = state == "FAILED" ? ReasonVerifyFailedPrefix + failed!.Code
            : state == "UNRESOLVED" && dependencyMissing ? ReasonWaitDependency
            : state == "UNRESOLVED" ? ReasonWaitVerify
            : state == StateWaitRetry ? StateWaitRetry
            : null;
        var dependencyState = dependencyMissing ? "WAIT" : "NONE";

        var resultJson = JsonSerializer.Serialize(new
        {
            state,
            reason,
            failCode = failed?.Reason,
            eventKey,
            ruleVersion = settings.RuleVersion,
            saleLineKeys = rules.SaleLines.Select(l => l.SourceLineKey).ToArray(),
            returnLineKeys = rules.ReturnLines.Select(l => l.SourceLineKey).ToArray(),
            codes = conclusions.Select(c => new { code = c.Code, lineKey = c.Entry.SourceLineKey, result = c.Result, reason = c.Reason }),
            deferredCodes = deferred,
            platformCalls,
            dependencyState,
        });
        var codeSetHash = SourceHasher.CodeSetHash(rules.SaleLines.Select(l => l.CodeNormalized!).ToList());

        // ---- 阶段 C：单事务落库（verification + verification_code + 任务）----
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        var current = await ReadCurrentAsync(conn, snap.org_id, eventKey, tx, ct);
        if (IsSettledPassed(current, snap))
        {
            await tx.CommitAsync(ct);
            return current!.id;
        }
        if (current is not null)
            await FlipCurrentAsync(conn, tx, snap.org_id, eventKey, ct);

        var codeIds = await EnsureCodeIdsAsync(conn, tx, rules, ct);
        var saleLines = BuildRetailLines(rules.SaleLines, rows, codeIds);
        var returnLines = BuildRetailLines(rules.ReturnLines, rows, codeIds);
        var mappingDigest = MappingDigest(rows);

        long? taskId = null;
        if (state == "PASSED")
            taskId = await CreateSaleTaskAsync(conn, tx, snap, settings.RuleVersion, mappingDigest, saleLines, ct);
        else if (state == "FAILED")
            taskId = await BlockExistingTaskAsync(conn, tx, snap, reason!, "NONE", "FAILED", ct);
        else if (state == "UNRESOLVED")
            taskId = await EnsurePlaceholderTaskAsync(conn, tx, snap, reason!, dependencyState,
                settings.RuleVersion, mappingDigest, saleLines, ct);
        // WAIT_RETRY：只落结论，任务不建/不改，由作业退避重排后重跑。

        var verificationId = await InsertVerificationAsync(conn, tx, snap, eventKey, settings.RuleVersion, state, resultJson,
            codeSetHash, ct);
        if (taskId is not null)
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE dbo.verification SET upload_task_id=@taskId WHERE id=@id",
                new { taskId, id = verificationId }, tx, cancellationToken: ct));
        await InsertVerificationCodesAsync(conn, tx, snap, verificationId, conclusions, codeIds, ct);

        if (state == "PASSED" && returnLines.Count > 0)
            await EnsureReturnPlaceholderTaskAsync(conn, tx, snap, settings.RuleVersion, mappingDigest, returnLines, ct);

        await _audit.AppendAsync(conn, tx, new AuditEntry("SYSTEM", "Worker", "sale.verify", "source_snapshot",
            snap.id.ToString(CultureInfo.InvariantCulture), reason, null,
            JsonSerializer.Serialize(new { verificationId, state, reason, codes = conclusions.Count, deferred, platformCalls }),
            null, settings.RuleVersion, _env));
        await tx.CommitAsync(ct);
        return verificationId;
    }

    private async Task<long> WriteRuleFailureAsync(SqlConnection conn, SnapshotRow snap, string eventKey,
        string ruleVersion, SaleRuleResult rules, CancellationToken ct)
    {
        var resultJson = JsonSerializer.Serialize(new
        {
            state = "FAILED",
            failCode = rules.FailCode,
            eventKey,
            ruleVersion,
            duplicateCodes = rules.DuplicateCodes.ToArray(),
            missingCodeLines = rules.MissingCodeLines.Select(l => l.SourceLineKey).ToArray(),
            unknownClassLines = rules.UnknownClassLines.Select(l => l.SourceLineKey).ToArray(),
            saleLineKeys = rules.SaleLines.Select(l => l.SourceLineKey).ToArray(),
            returnLineKeys = rules.ReturnLines.Select(l => l.SourceLineKey).ToArray(),
            codes = Array.Empty<object>(),
            deferredCodes = 0,
            platformCalls = 0,
            dependencyState = "NONE",
        });
        var codeSetHash = SourceHasher.CodeSetHash(
            rules.SaleLines.Where(l => !string.IsNullOrWhiteSpace(l.CodeNormalized)).Select(l => l.CodeNormalized!).ToList());

        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        var current = await ReadCurrentAsync(conn, snap.org_id, eventKey, tx, ct);
        if (current is not null && current.state == "FAILED" && current.source_snapshot_id == snap.id
            && string.Equals(current.source_hash, snap.content_hash, StringComparison.Ordinal)
            && string.Equals(current.result_json, resultJson, StringComparison.Ordinal))
        {
            await tx.CommitAsync(ct); // 同一结论重跑：零写入
            return current.id;
        }
        await FlipCurrentAsync(conn, tx, snap.org_id, eventKey, ct);
        var taskId = await BlockExistingTaskAsync(conn, tx, snap, rules.FailCode!, "NONE", "FAILED", ct);
        var verificationId = await InsertVerificationAsync(conn, tx, snap, eventKey, ruleVersion, "FAILED", resultJson,
            codeSetHash, ct);
        if (taskId is not null)
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE dbo.verification SET upload_task_id=@taskId WHERE id=@id",
                new { taskId, id = verificationId }, tx, cancellationToken: ct));
        await _audit.AppendAsync(conn, tx, new AuditEntry("SYSTEM", "Worker", "sale.verify", "source_snapshot",
            snap.id.ToString(CultureInfo.InvariantCulture), rules.FailCode, null,
            JsonSerializer.Serialize(new { verificationId, state = "FAILED", failCode = rules.FailCode }),
            null, ruleVersion, _env));
        await tx.CommitAsync(ct);
        return verificationId;
    }

    /// <summary>核验通过但任务创建冲突（CODE_CONTENDED/SOURCE_CHANGED/分片超限等）：事务已回滚，落 FAILED 结论留证。</summary>
    private async Task<long> WriteTaskConflictAsync(SqlConnection conn, SnapshotRow snap, string eventKey,
        string ruleVersion, SaleRuleResult rules, Exception error, CancellationToken ct)
    {
        var errorCode = error is AllocationConflictException conflict
            ? conflict.ErrorCode
            : ((AllocationValidationException)error).ErrorCode;
        var resultJson = JsonSerializer.Serialize(new
        {
            state = "FAILED",
            failCode = errorCode,
            eventKey,
            ruleVersion,
            message = error.Message,
            saleLineKeys = rules.SaleLines.Select(l => l.SourceLineKey).ToArray(),
            returnLineKeys = rules.ReturnLines.Select(l => l.SourceLineKey).ToArray(),
            codes = Array.Empty<object>(),
            deferredCodes = 0,
            platformCalls = 0,
            dependencyState = "NONE",
        });
        var codeSetHash = SourceHasher.CodeSetHash(rules.SaleLines.Select(l => l.CodeNormalized!).ToList());
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        await FlipCurrentAsync(conn, tx, snap.org_id, eventKey, ct);
        var taskId = await BlockExistingTaskAsync(conn, tx, snap, ReasonVerifyFailedPrefix + errorCode,
            "NONE", "FAILED", ct);
        var verificationId = await InsertVerificationAsync(conn, tx, snap, eventKey, ruleVersion, "FAILED", resultJson,
            codeSetHash, ct);
        if (taskId is not null)
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE dbo.verification SET upload_task_id=@taskId WHERE id=@id",
                new { taskId, id = verificationId }, tx, cancellationToken: ct));
        await _audit.AppendAsync(conn, tx, new AuditEntry("SYSTEM", "Worker", "sale.verify", "source_snapshot",
            snap.id.ToString(CultureInfo.InvariantCulture), errorCode, null,
            JsonSerializer.Serialize(new { verificationId, state = "FAILED", failCode = errorCode }),
            null, ruleVersion, _env));
        await tx.CommitAsync(ct);
        return verificationId;
    }

    // ---------- 读取 ----------

    private async Task<List<LineRow>> LoadLinesAsync(SqlConnection conn, SnapshotRow snap, CancellationToken ct)
        => (await conn.QueryAsync<LineRow>(new CommandDefinition(@"
SELECT l.id AS line_id, l.source_line_key, l.product_source_id, l.quantity_original, l.unit_original,
       l.batch_raw, l.trace_code_raw, l.trace_code_normalized, l.event_kind,
       p.trace_classification, p.classification_reason,
       pm.version AS mapping_version, pd.platform_drug_id
FROM dbo.source_line l
LEFT JOIN dbo.product p
  ON p.org_id=@orgId AND p.source_system_id=@sysId AND p.spid=l.product_source_id
LEFT JOIN dbo.product_mapping pm
  ON pm.product_id=p.id AND pm.state IN (N'AUTO_CONFIRMED',N'MANUAL_CONFIRMED')
LEFT JOIN dbo.platform_drug pd ON pd.id=pm.platform_drug_id
WHERE l.snapshot_id=@snapId
ORDER BY l.id",
            new { orgId = snap.org_id, sysId = snap.source_system_id, snapId = snap.id }, cancellationToken: ct))).ToList();

    /// <summary>分类仅认经 AUTO/MANUAL_CONFIRMED 映射的商品；缺映射一律 UNKNOWN（不得默认可上传）。</summary>
    private static SaleLineInput ToInput(LineRow r)
    {
        var mapped = r.mapping_version is not null && !string.IsNullOrEmpty(r.platform_drug_id)
                     && r.trace_classification is not null;
        var classification = mapped ? r.trace_classification! : SaleRules.ClassificationUnknown;
        var code = TraceCodeNormalizer.Normalize(r.trace_code_normalized ?? r.trace_code_raw);
        return new SaleLineInput(r.line_id, r.source_line_key, r.product_source_id, r.quantity_original,
            code.Normalized, r.event_kind, classification, mapped ? r.classification_reason : null);
    }

    private static List<CodeEntry> OrderedSaleCodes(SaleRuleResult rules)
        => rules.SaleLines
            .Select(l => new CodeEntry(l.CodeNormalized!, l.SourceLineId, l.SourceLineKey))
            .OrderBy(c => c.SourceLineId)
            .ThenBy(c => c.Code, StringComparer.Ordinal)
            .GroupBy(c => c.Code, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();

    /// <summary>上次运行（同快照+来源摘要+规则版本）已判 PASS 的码 → 本轮直接沿用，保证 maxCodesPerRun 分批可完成。</summary>
    private async Task<HashSet<string>> LoadPriorPassCodesAsync(SqlConnection conn, SnapshotRow snap, string eventKey,
        string ruleVersion, CancellationToken ct)
    {
        var priorId = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(@"
SELECT TOP 1 v.id FROM dbo.verification v
WHERE v.org_id=@orgId AND v.env=@env AND v.event_key=@key AND v.is_current=1
  AND v.source_snapshot_id=@snap AND v.source_hash=@sh AND v.rule_version=@rv",
            new { orgId = snap.org_id, env = _env, key = eventKey, snap = snap.id, sh = snap.content_hash, rv = ruleVersion },
            cancellationToken: ct));
        if (priorId is null) return new HashSet<string>(StringComparer.Ordinal);
        var codes = await conn.QueryAsync<string>(new CommandDefinition(@"
SELECT tc.code_normalized FROM dbo.verification_code vc
JOIN dbo.trace_code tc ON tc.id=vc.code_id
WHERE vc.verification_id=@id AND vc.result=N'PASS'", new { id = priorId.Value }, cancellationToken: ct));
        return new HashSet<string>(codes, StringComparer.Ordinal);
    }

    private async Task<HashSet<string>> LoadPurchaseSucceededAsync(SqlConnection conn, SnapshotRow snap,
        IReadOnlyList<CodeEntry> batch, CancellationToken ct)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (batch.Count == 0) return result;
        var codes = batch.Select(b => b.Code).ToList();
        for (var offset = 0; offset < codes.Count; offset += CodeChunk)
        {
            var chunk = codes.Skip(offset).Take(CodeChunk).ToList();
            var rows = await conn.QueryAsync<string>(new CommandDefinition(@"
SELECT DISTINCT tc.code_normalized
FROM dbo.code_event e
JOIN dbo.upload_task t ON t.id=e.upload_task_id
JOIN dbo.trace_code tc ON tc.id=e.code_id
WHERE e.org_id=@orgId AND e.env=@env AND e.event_type=N'PURCHASE_IN' AND t.state=N'Succeeded'
  AND tc.code_normalized IN @codes",
                new { orgId = snap.org_id, env = _env, codes = chunk }, cancellationToken: ct));
            foreach (var code in rows) result.Add(code);
        }
        return result;
    }

    private static IReadOnlyDictionary<string, string> CodedetailParameters(string code, OrgRow org)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal) { ["code"] = code };
        if (!string.IsNullOrWhiteSpace(org.ref_ent_id)) parameters["ref_ent_id"] = org.ref_ent_id!;
        return parameters;
    }

    // ---------- 平台结论 ----------

    private static (CodeConclusion Conclusion, bool WaitRetry) Classify(CodeEntry entry, TopOutcome outcome)
    {
        var evidence = Evidence(outcome);
        switch (outcome.Kind)
        {
            case TopOutcomeKind.Success:
            case TopOutcomeKind.PartialEvidence:
                var (exists, instore) = ReadCodeEvidence(outcome.ResultJson);
                if (exists == false)
                    return (new CodeConclusion(entry, "FAIL", ReasonPlatformNotFound, evidence), false);
                if (exists == true && instore == true)
                    return (new CodeConclusion(entry, "PASS", "PLATFORM_INSTORE", evidence), false);
                // instore=false / 字段缺失：码存在性未证实或不在店 → 依赖未满足，等采购入库成功。
                return (new CodeConclusion(entry, "UNKNOWN", ReasonWaitDependency, evidence), false);
            case TopOutcomeKind.BusinessRejected:
            case TopOutcomeKind.DuplicateBillCode:
                return (new CodeConclusion(entry, "UNKNOWN", ReasonWaitDependency, evidence), false);
            default:
                return (new CodeConclusion(entry, "UNKNOWN", "PLATFORM_UNAVAILABLE", evidence), true);
        }
    }

    /// <summary>候选结构 {"model":{"exists":true,"instore":true}}；model 缺省时回退根节点（R-M3-2 同型兼容）。</summary>
    private static (bool? Exists, bool? Instore) ReadCodeEvidence(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson)) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(resultJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, null);
            if (root.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.Object)
                root = model;
            return (ReadBool(root, "exists"), ReadBool(root, "instore"));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static bool? ReadBool(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static string? Evidence(TopOutcome outcome)
    {
        if (outcome.ResultJson is null && outcome.Message is null) return null;
        return Truncate(JsonSerializer.Serialize(new
        {
            kind = outcome.Kind.ToString(),
            message = outcome.Message,
            result = outcome.ResultJson,
        }), EvidenceMaxChars);
    }

    private static List<CodeConclusion> ComposeConclusions(IReadOnlyList<CodeEntry> ordered,
        IReadOnlySet<string> priorPass, IReadOnlyList<CodeConclusion> checkedConclusions)
    {
        var done = checkedConclusions.ToDictionary(c => c.Code, StringComparer.Ordinal);
        var list = new List<CodeConclusion>(done.Count + priorPass.Count);
        foreach (var entry in ordered)
        {
            if (done.TryGetValue(entry.Code, out var conclusion)) list.Add(conclusion);
            else if (priorPass.Contains(entry.Code)) list.Add(new CodeConclusion(entry, "PASS", "PASS_CARRIED", null));
        }
        return list;
    }

    // ---------- 写入（调用方事务） ----------

    private static async Task<Dictionary<string, long>> EnsureCodeIdsAsync(SqlConnection conn, SqlTransaction tx,
        SaleRuleResult rules, CancellationToken ct)
    {
        var map = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var line in rules.SaleLines.Concat(rules.ReturnLines))
        {
            var code = TraceCodeNormalizer.Normalize(line.CodeNormalized).Normalized!;
            if (map.ContainsKey(code)) continue;
            var stored = await TraceCodeStore.EnsureAsync(conn, tx, code, ct);
            map[code] = stored.Id;
        }
        return map;
    }

    private static List<RetailLine> BuildRetailLines(IReadOnlyList<SaleLineInput> lines, IReadOnlyList<LineRow> rows,
        IReadOnlyDictionary<string, long> codeIds)
    {
        var byId = rows.ToDictionary(r => r.line_id);
        var result = new List<RetailLine>(lines.Count);
        foreach (var line in lines)
        {
            var row = byId[line.SourceLineId];
            var code = TraceCodeNormalizer.Normalize(line.CodeNormalized).Normalized!;
            result.Add(new RetailLine(line.SourceLineId, line.SourceLineKey, line.ProductSourceId,
                row.platform_drug_id ?? "", line.Quantity, row.unit_original, row.batch_raw,
                new[] { (codeIds[code], code) }));
        }
        return result;
    }

    private static string MappingDigest(IEnumerable<LineRow> rows)
        => string.Join("|", rows
            .Where(r => r.mapping_version is not null)
            .Select(r => $"product:{r.product_source_id}:{r.mapping_version}")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal));

    private static string TaskEventKey(SnapshotRow snap, string taskType)
        => $"KSOA:{snap.source_document_key}:{taskType}";

    /// <summary>
    /// 321 任务：委托工厂（分片/占用/事件/outbox 全在工厂内）。若已存在核验占位或行政取消的任务且未发送，
    /// 先置 Cancelled 走工厂的同行复活路径（保持单任务不变量，单号由工厂按规则重生成）。
    /// </summary>
    private async Task<long> CreateSaleTaskAsync(SqlConnection conn, SqlTransaction tx, SnapshotRow snap,
        string ruleVersion, string mappingDigest, IReadOnlyList<RetailLine> lines, CancellationToken ct)
    {
        var eventKey = TaskEventKey(snap, TaskTypeSale);
        var existing = await LoadTaskAsync(conn, tx, snap.org_id, eventKey, ct);
        if (existing is not null && existing.state is "Blocked" or "Cancelled")
        {
            var sent = await CountUploadAttemptsAsync(conn, tx, existing.id, ct);
            if (sent == 0)
                await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.upload_task SET state=N'Cancelled', state_reason=N'核验重算（未发送），由核验事务重建', version=version+1
WHERE id=@id AND state IN (N'Blocked',N'Cancelled')", new { id = existing.id }, tx, cancellationToken: ct));
        }
        var ctx = new RetailTaskContext(snap.org_id, _env, snap.id, snap.source_document_key, snap.business_datetime,
            snap.content_hash, mappingDigest, ruleVersion, lines, TaskTypeSale);
        var result = await _factory.CreateAsync(conn, tx, ctx, ct);
        return result.TaskId;
    }

    /// <summary>把已存在的 321 任务改挂 Blocked/原因（已发送任务冻结，绝不回退）。</summary>
    private async Task<long?> BlockExistingTaskAsync(SqlConnection conn, SqlTransaction tx, SnapshotRow snap,
        string reason, string dependencyState, string verificationState, CancellationToken ct)
    {
        var eventKey = TaskEventKey(snap, TaskTypeSale);
        var existing = await LoadTaskAsync(conn, tx, snap.org_id, eventKey, ct);
        if (existing is null) return null;
        var sent = await CountUploadAttemptsAsync(conn, tx, existing.id, ct);
        if (sent > 0) return existing.id;
        await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.upload_task SET state=N'Blocked', state_reason=@reason, verification_state=@vs, dependency_state=@dep,
       version=version+1
WHERE id=@id", new { id = existing.id, reason = Truncate(reason, ReasonMaxChars), vs = verificationState, dep = dependencyState },
            tx, cancellationToken: ct));
        return existing.id;
    }

    /// <summary>
    /// 未决（WAIT_DEPENDENCY/WAIT_VERIFY）占位任务：无分片/占用/outbox/code_event，只承载不可变载荷与状态，
    /// 供 EnqueueDependentVerifications 重唤醒与 T4 前置状态校验。
    /// </summary>
    private async Task<long> EnsurePlaceholderTaskAsync(SqlConnection conn, SqlTransaction tx, SnapshotRow snap,
        string reason, string dependencyState, string ruleVersion, string mappingDigest,
        IReadOnlyList<RetailLine> lines, CancellationToken ct)
    {
        var eventKey = TaskEventKey(snap, TaskTypeSale);
        var payloadJson = DocPayloadJson(eventKey, snap, ruleVersion, TaskTypeSale, lines);
        var payloadHash = Sha(payloadJson);
        var mappingSnapshot = Sha(mappingDigest);
        var existing = await LoadTaskAsync(conn, tx, snap.org_id, eventKey, ct);
        if (existing is null)
            return await conn.ExecuteScalarAsync<long>(new CommandDefinition(@"
INSERT INTO dbo.upload_task(org_id,env,event_key,task_type,source_snapshot_id,source_hash,mapping_version_snapshot,
    payload_json,payload_hash,state,state_reason,source_integrity,match_state,verification_state,dependency_state)
OUTPUT INSERTED.id
VALUES(@orgId,@env,@key,@taskType,@snap,@sh,@mv,@pj,@ph,N'Blocked',@reason,N'COMPLETE',N'COMPLETE',N'UNRESOLVED',@dep)",
                new { orgId = snap.org_id, env = _env, key = eventKey, taskType = TaskTypeSale, snap = snap.id,
                      sh = snap.content_hash, mv = mappingSnapshot, pj = payloadJson, ph = payloadHash,
                      reason = Truncate(reason, ReasonMaxChars), dep = dependencyState }, tx, cancellationToken: ct));

        var sent = await CountUploadAttemptsAsync(conn, tx, existing.id, ct);
        if (sent > 0 || existing.state is not ("Blocked" or "Ready")) return existing.id;
        var parts = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM dbo.upload_part WHERE task_id=@id", new { id = existing.id }, tx, cancellationToken: ct));
        if (parts == 0)
            await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.upload_task SET state=N'Blocked', state_reason=@reason, verification_state=N'UNRESOLVED', dependency_state=@dep,
       source_snapshot_id=@snap, source_hash=@sh, mapping_version_snapshot=@mv, payload_json=@pj, payload_hash=@ph,
       version=version+1
WHERE id=@id",
                new { id = existing.id, reason = Truncate(reason, ReasonMaxChars), dep = dependencyState, snap = snap.id,
                      sh = snap.content_hash, mv = mappingSnapshot, pj = payloadJson, ph = payloadHash }, tx, cancellationToken: ct));
        else
            await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.upload_task SET state=N'Blocked', state_reason=@reason, verification_state=N'UNRESOLVED', dependency_state=@dep,
       version=version+1
WHERE id=@id", new { id = existing.id, reason = Truncate(reason, ReasonMaxChars), dep = dependencyState }, tx,
                cancellationToken: ct));
        return existing.id;
    }

    /// <summary>
    /// 116 退货占位：核验通过即落 Blocked + RETURN_CONFIRM_REQUIRED（不可变载荷=退货行），无分片/占用/outbox；
    /// Task 6 确认事务内经工厂同行提升（R-M3-7）。已存在则一字不改（幂等）。
    /// </summary>
    private async Task EnsureReturnPlaceholderTaskAsync(SqlConnection conn, SqlTransaction tx, SnapshotRow snap,
        string ruleVersion, string mappingDigest, IReadOnlyList<RetailLine> lines, CancellationToken ct)
    {
        var eventKey = TaskEventKey(snap, TaskTypeReturn);
        var existing = await LoadTaskAsync(conn, tx, snap.org_id, eventKey, ct);
        if (existing is not null) return;
        var payloadJson = DocPayloadJson(eventKey, snap, ruleVersion, TaskTypeReturn, lines);
        await conn.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.upload_task(org_id,env,event_key,task_type,source_snapshot_id,source_hash,mapping_version_snapshot,
    payload_json,payload_hash,state,state_reason,source_integrity,match_state,verification_state,dependency_state)
VALUES(@orgId,@env,@key,@taskType,@snap,@sh,@mv,@pj,@ph,N'Blocked',@reason,N'COMPLETE',N'COMPLETE',N'PASSED',N'NONE')",
            new { orgId = snap.org_id, env = _env, key = eventKey, taskType = TaskTypeReturn, snap = snap.id,
                  sh = snap.content_hash, mv = Sha(mappingDigest), pj = payloadJson, ph = Sha(payloadJson),
                  reason = ReasonReturnConfirmRequired }, tx, cancellationToken: ct));
    }

    private async Task<CurrentRow?> ReadCurrentAsync(SqlConnection conn, long orgId, string eventKey,
        SqlTransaction? tx, CancellationToken ct)
        => await conn.QuerySingleOrDefaultAsync<CurrentRow>(new CommandDefinition(@"
SELECT id, state, source_snapshot_id, source_hash, result_json
FROM dbo.verification WITH (UPDLOCK, HOLDLOCK)
WHERE org_id=@orgId AND env=@env AND event_key=@key AND is_current=1",
            new { orgId, env = _env, key = eventKey }, tx, cancellationToken: ct));

    private static bool IsSettledPassed(CurrentRow? current, SnapshotRow snap)
        => current is not null && current.state == "PASSED" && current.source_snapshot_id == snap.id
           && string.Equals(current.source_hash, snap.content_hash, StringComparison.Ordinal);

    private Task<int> FlipCurrentAsync(SqlConnection conn, SqlTransaction tx, long orgId, string eventKey, CancellationToken ct)
        => conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.verification SET is_current=0
WHERE org_id=@orgId AND env=@env AND event_key=@key AND is_current=1",
            new { orgId, env = _env, key = eventKey }, tx, cancellationToken: ct));

    private async Task<long> InsertVerificationAsync(SqlConnection conn, SqlTransaction tx, SnapshotRow snap,
        string eventKey, string ruleVersion, string state, string resultJson, string codeSetHash, CancellationToken ct)
        => await conn.ExecuteScalarAsync<long>(new CommandDefinition(@"
INSERT INTO dbo.verification(org_id,env,event_key,upload_task_id,source_snapshot_id,source_hash,code_set_hash,
    rule_version,state,result_json,verified_at,is_current)
OUTPUT INSERTED.id
VALUES(@orgId,@env,@key,NULL,@snap,@sh,@csh,@rv,@state,@rj,SYSUTCDATETIME(),1)",
            new { orgId = snap.org_id, env = _env, key = eventKey, snap = snap.id, sh = snap.content_hash,
                  csh = codeSetHash, rv = ruleVersion, state, rj = resultJson }, tx, cancellationToken: ct));

    private async Task InsertVerificationCodesAsync(SqlConnection conn, SqlTransaction tx, SnapshotRow snap,
        long verificationId, IReadOnlyList<CodeConclusion> conclusions, IReadOnlyDictionary<string, long> codeIds,
        CancellationToken ct)
    {
        foreach (var conclusion in conclusions)
        {
            await conn.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.verification_code(verification_id,org_id,env,code_id,source_line_id,result,reason,evidence_json)
VALUES(@vid,@orgId,@env,@codeId,@lineId,@result,@reason,@evidence)",
                new { vid = verificationId, orgId = snap.org_id, env = _env, codeId = codeIds[conclusion.Code],
                      lineId = conclusion.Entry.SourceLineId, result = conclusion.Result,
                      reason = Truncate(conclusion.Reason, ReasonMaxChars), evidence = conclusion.EvidenceJson },
                tx, cancellationToken: ct));
        }
    }

    private async Task<TaskRow?> LoadTaskAsync(SqlConnection conn, SqlTransaction tx, long orgId, string eventKey,
        CancellationToken ct)
        => await conn.QuerySingleOrDefaultAsync<TaskRow>(new CommandDefinition(@"
SELECT id, state, state_reason FROM dbo.upload_task WITH (UPDLOCK, HOLDLOCK)
WHERE org_id=@orgId AND env=@env AND event_key=@key",
            new { orgId, env = _env, key = eventKey }, tx, cancellationToken: ct));

    private static Task<int> CountUploadAttemptsAsync(SqlConnection conn, SqlTransaction tx, long taskId, CancellationToken ct)
        => conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM dbo.upload_attempt WHERE task_id=@id AND attempt_kind=N'UPLOAD'",
            new { id = taskId }, tx, cancellationToken: ct));

    // ---------- 载荷/摘要 ----------

    private static string DocPayloadJson(string eventKey, SnapshotRow snap, string ruleVersion, string taskType,
        IReadOnlyList<RetailLine> lines)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["eventKey"] = eventKey,
            ["taskType"] = taskType,
            ["businessDateTimeUtc"] = snap.business_datetime,
            ["ruleVersion"] = ruleVersion,
            ["lines"] = lines.Select(LineProjection).ToArray(),
        };
        return JsonSerializer.Serialize(payload);
    }

    private static object LineProjection(RetailLine line) => new
    {
        sourceLineKey = line.SourceLineKey,
        productSourceId = line.ProductSourceId,
        platformDrugId = line.PlatformDrugId,
        quantity = line.Quantity.ToString("0.000000", CultureInfo.InvariantCulture),
        unit = line.Unit,
        batchNo = line.BatchNo,
        codes = line.Codes.Select(c => c.CodeNormalized).ToArray(),
    };

    private static string SaleVerifyPayload(long orgId, string env, long snapshotId)
        => JsonSerializer.Serialize(new { orgId, env, snapshotId });

    private static string Sha(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    private static string? Truncate(string? value, int max)
        => value is null || value.Length <= max ? value : value[..max];

    // ---------- 行类型 ----------

    private sealed record CodeEntry(string Code, long SourceLineId, string SourceLineKey);

    private sealed record CodeConclusion(CodeEntry Entry, string Result, string? Reason, string? EvidenceJson)
    {
        public string Code => Entry.Code;
    }

    private sealed class SnapshotRow
    {
        public long id { get; set; }
        public long org_id { get; set; }
        public long source_system_id { get; set; }
        public string source_document_key { get; set; } = "";
        public string document_type { get; set; } = "";
        public string completeness_status { get; set; } = "";
        public string content_hash { get; set; } = "";
        public DateTime? business_datetime { get; set; }
    }

    private sealed class OrgRow
    {
        public string? ent_id { get; set; }
        public string? ref_ent_id { get; set; }
    }

    private sealed class LineRow
    {
        public long line_id { get; set; }
        public string source_line_key { get; set; } = "";
        public string product_source_id { get; set; } = "";
        public decimal quantity_original { get; set; }
        public string? unit_original { get; set; }
        public string? batch_raw { get; set; }
        public string? trace_code_raw { get; set; }
        public string? trace_code_normalized { get; set; }
        public string event_kind { get; set; } = "";
        public string? trace_classification { get; set; }
        public string? classification_reason { get; set; }
        public int? mapping_version { get; set; }
        public string? platform_drug_id { get; set; }
    }

    private sealed class CurrentRow
    {
        public long id { get; set; }
        public string state { get; set; } = "";
        public long? source_snapshot_id { get; set; }
        public string source_hash { get; set; } = "";
        public string? result_json { get; set; }
    }

    private sealed class TaskRow
    {
        public long id { get; set; }
        public string state { get; set; } = "";
        public string? state_reason { get; set; }
    }
}
