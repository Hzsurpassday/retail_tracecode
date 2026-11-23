using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Domain.Codes;
using TraceFlow.Domain.Sales;
using TraceFlow.Domain.Uploads;
using TraceFlow.Infrastructure.Settings;

namespace TraceFlow.Infrastructure.Sales;

/// <summary>
/// 首次退货人工确认命令（RET-02/DEF-02）：ExpectedVersion 为 upload_task.version 的乐观锁；
/// PredecessorEventIds 只是「回显」，服务端必须以锁定后重新解析的结果为准（决策 8）；
/// EvidenceJson 必填并落 return_confirmation.evidence_json；IdemKey 由端点传入（服务内命名空间化落幂等表）。
/// </summary>
public sealed record ReturnConfirmCommand(long ExpectedVersion, string Decision,
    IReadOnlyList<long> PredecessorEventIds, string EvidenceJson, string IdemKey, string Actor);

/// <summary>确认结果：State 为确认后的任务状态（拒绝仍为 Blocked）。Idempotent 表示命中幂等重放。</summary>
public sealed record ReturnConfirmResult(long TaskId, string State, bool Idempotent);

/// <summary>
/// 消费者退货 116 / 采购退货 202 首次人工确认（RET-02/RET-04/DEF-02/R-M3-4）：
/// 占位任务（Blocked + RETURN_CONFIRM_REQUIRED）就地经 <see cref="RetailTaskFactory"/> 提升
/// （分片/占用/CONSUMER_RETURN|PURCHASE_RETURN 事件/outbox），再走状态机 Blocked→Ready（EligibilitySatisfied），
/// 同事务写 return_confirmation 与审计；拒绝则保持 Blocked 并改写原因 RETURN_CONFIRM_REJECTED。
/// 拒绝对「该来源版本」终态（R-M3-16）：后续新快照/退货行消失时由 <see cref="SaleVerificationService"/>
/// 把占位撤销为 Cancelled + RETURN_CONFIRM_REJECTED_SUPERSEDED，本服务不提供重开（v1 无 reopen 流程）。
/// 前序语义（决策 4）：每码唯一未退货前序事件（116=SALE，202=PURCHASE_IN），0 条 MISSING、多条 AMBIGUOUS、
/// 已退货 ALREADY_RETURNED；前序事件所属任务必须 RETAIL_321/PURCHASE_IN_102 且 Succeeded 或有平台受理证据。
/// 并发（决策 5）：先按 id 升序 UPDLOCK/HOLDLOCK 锁前序任务行，再锁自身任务行并重解析前序；
/// 后到者看到胜者的退货事件 → ALREADY_RETURNED，另由 UX_claim_active 唯一索引兜底，绝不双提交。
/// </summary>
public sealed class ReturnConfirmService
{
    public const string DecisionConfirmed = "CONFIRMED";
    public const string DecisionRejected = "REJECTED";
    public const string ReasonConfirmRequired = "RETURN_CONFIRM_REQUIRED";
    public const string ReasonRejected = "RETURN_CONFIRM_REJECTED";
    public const string ReasonConfirmed = "RETURN_CONFIRMED";
    public const string ErrorNotReturnable = "NOT_RETURNABLE";
    public const string ErrorVersionConflict = "VERSION_CONFLICT";
    public const string ErrorPredecessorMismatch = "PREDECESSOR_MISMATCH";
    public const string ActionConfirmed = "task.return.confirmed";
    public const string ActionRejected = "task.return.rejected";

    private const int BatchSize = 500;
    /// <summary>return_confirmation 的唯一约束（一任务一决定）；用于区分重复键来源（F6）。</summary>
    private const string ReturnConfirmationTaskConstraint = "UQ_retconf_task";
    /// <summary>幂等表的唯一约束（idem_key + user_id）；用于区分重复键来源（F6）。</summary>
    private const string IdempotencyKeyConstraint = "UQ_idempotency_user_key";

    private readonly string _cs;
    private readonly string _env;
    private readonly SettingsService _settings;
    private readonly RetailTaskFactory _factory;
    private readonly IIdempotencyStore _idempotency;
    private readonly IAuditLog _audit;

    public ReturnConfirmService(string connectionString, string env, SettingsService settings,
        RetailTaskFactory factory, IIdempotencyStore idempotency, IAuditLog audit)
    {
        _cs = connectionString;
        _env = env;
        _settings = settings;
        _factory = factory;
        _idempotency = idempotency;
        _audit = audit;
    }

    public async Task<ReturnConfirmResult> ConfirmAsync(long taskId, ReturnConfirmCommand cmd, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        var decision = NormalizeDecision(cmd.Decision);
        var predecessors = NormalizePredecessors(cmd.PredecessorEventIds);
        var evidence = RequireEvidence(cmd.EvidenceJson);
        var actor = RequireActor(cmd.Actor);
        if (string.IsNullOrWhiteSpace(cmd.IdemKey))
            throw new AllocationValidationException("VALIDATION", "IdemKey 必填");
        if (cmd.ExpectedVersion <= 0)
            throw new AllocationValidationException("VALIDATION", "ExpectedVersion 必须为正整数");

        var fallbackRuleVersion = (await _settings.GetSaleVerifySettingsAsync(ct)).RuleVersion;
        var requestHash = RequestHash(taskId, decision, predecessors, evidence);
        var storeKey = StoreKey(cmd.IdemKey);

        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        var userId = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT id FROM dbo.app_user WHERE username=@u", new { u = actor }, tx, cancellationToken: ct)) ?? 0L;
        var hit = await _idempotency.TryGetAsync(conn, tx, storeKey, requestHash, userId, ct);
        if (hit is not null)
        {
            await tx.CommitAsync(ct);
            return ParseHit(taskId, hit);
        }

        var task = await LoadTaskAsync(conn, tx, taskId, forUpdate: false, ct);
        EnsureReturnTask(task);
        if (task!.version != cmd.ExpectedVersion)
            throw new AllocationConflictException(ErrorVersionConflict, "任务版本已变化，请刷新后重试");
        EnsureConfirmableState(task);

        var ruleVersion = ResolveRuleVersion(task, fallbackRuleVersion);
        var beforeDigest = Digest(task.version, task.state, task.state_reason, predecessors);
        ReturnConfirmResult result;
        string afterDigest;
        string auditAction;

        if (decision == DecisionRejected)
        {
            // 拒绝：不改分片/占用/事件/outbox，只留不可变确认行与审计，原因转 RETURN_CONFIRM_REJECTED。
            var locked = await LoadTaskAsync(conn, tx, taskId, forUpdate: true, ct);
            EnsureReturnTask(locked);
            if (locked!.version != cmd.ExpectedVersion)
                throw new AllocationConflictException(ErrorVersionConflict, "任务版本已变化，请刷新后重试");
            EnsureConfirmableState(locked);
            var moved = await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.upload_task SET state_reason=@reason, version=version+1
WHERE id=@id AND state=N'Blocked' AND version=@ver",
                new { id = taskId, reason = ReasonRejected, ver = cmd.ExpectedVersion }, tx, cancellationToken: ct));
            if (moved != 1)
                throw new AllocationConflictException(ErrorVersionConflict, "任务状态已变化，请刷新后重试");
            await InsertReturnConfirmationAsync(conn, tx, task, DecisionRejected, evidence, actor,
                task.predecessor_task_id, ct);
            result = new ReturnConfirmResult(taskId, "Blocked", false);
            afterDigest = Digest(task.version + 1, "Blocked", ReasonRejected, predecessors);
            auditAction = ActionRejected;
        }
        else
        {
            var lines = await LoadLinesAsync(conn, tx, task, ct);
            var mappingDigest = await MappingDigestAsync(conn, tx, task, ct);

            // 锁序（决策 5）：先按 id 升序锁前序任务行 → 再锁自身任务行；锁后必须重解析前序，
            // 后到者会看到胜者已提交的退货事件（ALREADY_RETURNED），因此不会双写占用/事件。
            var first = await ResolvePredecessorsAsync(conn, tx, task, lines, ct);
            EnsureSubmittedMatches(first, predecessors);
            await LockTasksAsync(conn, tx, first.TaskIds, ct);
            var lockedPredecessors = await ResolvePredecessorsAsync(conn, tx, task, lines, ct);
            EnsureSubmittedMatches(lockedPredecessors, predecessors);

            var locked = await LoadTaskAsync(conn, tx, taskId, forUpdate: true, ct);
            EnsureReturnTask(locked);
            if (locked!.version != cmd.ExpectedVersion)
                throw new AllocationConflictException(ErrorVersionConflict, "任务版本已变化，请刷新后重试");
            EnsureConfirmableState(locked);
            if (await CountUploadAttemptsAsync(conn, tx, taskId, ct) > 0)
                throw new AllocationValidationException(ErrorNotReturnable, "任务已有发送尝试，不可人工确认改写");

            // F5：夹具播种的 202 无来源快照，保持 NULL（不得用 0 冒充快照 id）。
            var ctx = new RetailTaskContext(task.org_id, _env, task.source_snapshot_id,
                DocKey(task.event_key, task.task_type), task.business_datetime, task.source_hash.Trim(),
                mappingDigest, ruleVersion, lines, task.task_type)
            {
                PredecessorEventByCodeId = lockedPredecessors.Map,
            };
            await _factory.CreateAsync(conn, tx, ctx, ct);

            if (!UploadStateMachine.TryGetNext(UploadState.Blocked, UploadEvent.EligibilitySatisfied, out var next))
                throw new InvalidOperationException("状态机缺少 Blocked→EligibilitySatisfied 转换");
            var moved = await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.upload_task SET state=@state, state_reason=@reason, version=version+1
WHERE id=@id AND state=N'Blocked'",
                new { id = taskId, state = next.ToString(), reason = ReasonConfirmed }, tx, cancellationToken: ct));
            if (moved != 1)
                throw new AllocationConflictException(ErrorVersionConflict, "任务状态已变化，请刷新后重试");
            var finalVersion = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT version FROM dbo.upload_task WHERE id=@id", new { id = taskId }, tx, cancellationToken: ct));
            await InsertReturnConfirmationAsync(conn, tx, task, DecisionConfirmed, evidence, actor,
                lockedPredecessors.SingleTaskId, ct);
            result = new ReturnConfirmResult(taskId, next.ToString(), false);
            afterDigest = Digest(finalVersion, next.ToString(), ReasonConfirmed, predecessors);
            auditAction = ActionConfirmed;
        }

        await _audit.AppendAsync(conn, tx, new AuditEntry(actor, "User", auditAction, "upload_task",
            taskId.ToString(CultureInfo.InvariantCulture),
            decision == DecisionConfirmed ? "首次退货人工确认通过" : "首次退货人工确认拒绝",
            beforeDigest, afterDigest, null, ruleVersion, _env));

        try
        {
            await _idempotency.SaveAsync(conn, tx, storeKey, requestHash, userId, 200,
                JsonSerializer.Serialize(new { taskId = result.TaskId, state = result.State }), ct);
            await tx.CommitAsync(ct);
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            // F6：按约束名区分重复键，而非吞掉所有 2601/2627。
            // UQ_retconf_task = 同一任务已有确认决定（并发穿透 Blocked 守卫）→ 409 冲突，提示刷新；
            // UQ_idempotency_user_key = 幂等键并发竞态败者 → 业务+幂等整体回滚后重读胜者记录（与 IdempotencyExecutor 同裁定）；
            // 其他唯一键冲突原样抛出，交由调用方回滚。
            await tx.RollbackAsync(ct);
            if (ex.Message.Contains(ReturnConfirmationTaskConstraint, StringComparison.Ordinal))
                throw new AllocationConflictException(ErrorVersionConflict, "任务已有退货确认决定，请刷新后重试");
            if (!ex.Message.Contains(IdempotencyKeyConstraint, StringComparison.Ordinal))
                throw;
            var winner = await _idempotency.TryGetAsync(conn, null!, storeKey, requestHash, userId, ct);
            if (winner is null) throw new IdempotencyConflictException();
            return ParseHit(taskId, winner);
        }
        return result;
    }

    // ---------- 校验 ----------

    private static string NormalizeDecision(string? decision)
        => decision?.Trim().ToUpperInvariant() switch
        {
            DecisionConfirmed => DecisionConfirmed,
            DecisionRejected => DecisionRejected,
            _ => throw new AllocationValidationException("VALIDATION", "decision 只能为 CONFIRMED 或 REJECTED"),
        };

    private static IReadOnlyList<long> NormalizePredecessors(IReadOnlyList<long>? ids)
    {
        var list = (ids ?? Array.Empty<long>()).ToList();
        if (list.Any(id => id <= 0))
            throw new AllocationValidationException("VALIDATION", "predecessorEventIds 含非法事件 id");
        return list.Distinct().OrderBy(id => id).ToList();
    }

    private static string RequireEvidence(string? evidenceJson)
    {
        if (string.IsNullOrWhiteSpace(evidenceJson))
            throw new AllocationValidationException("VALIDATION", "EvidenceJson 必填");
        try
        {
            using var doc = JsonDocument.Parse(evidenceJson);
        }
        catch (JsonException)
        {
            throw new AllocationValidationException("VALIDATION", "EvidenceJson 须为合法 JSON");
        }
        return evidenceJson;
    }

    private static string RequireActor(string? actor)
    {
        var value = actor?.Trim() ?? "";
        if (value.Length == 0)
            throw new AllocationValidationException("VALIDATION", "actor 必填");
        if (value.Length > 64)
            throw new AllocationValidationException("VALIDATION", "actor 超长（≤64）");
        return value;
    }

    private static void EnsureReturnTask(TaskRow? task)
    {
        if (task is null
            || task.task_type is not (RetailTaskFactory.ConsumerReturn116 or RetailTaskFactory.PurchaseReturn202))
            throw new AllocationValidationException(ErrorNotReturnable, "退货任务不存在或类型不支持");
    }

    private static void EnsureConfirmableState(TaskRow task)
    {
        if (task.state != "Blocked"
            || !string.Equals(task.state_reason, ReasonConfirmRequired, StringComparison.Ordinal))
            throw new AllocationValidationException(ErrorNotReturnable,
                $"任务处于 {task.state}/{task.state_reason}，不可人工确认");
    }

    private static void EnsureSubmittedMatches(ResolvedPredecessors resolved, IReadOnlyList<long> submitted)
    {
        var expected = resolved.Map.Values.Distinct().OrderBy(id => id).ToList();
        if (!expected.SequenceEqual(submitted))
            throw new AllocationValidationException(ErrorPredecessorMismatch,
                "客户端提交的前序事件与服务端解析不一致，请刷新后重试");
    }

    private static string RequestHash(long taskId, string decision, IReadOnlyList<long> predecessors, string evidence)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            taskId.ToString(CultureInfo.InvariantCulture) + "|" + decision + "|" +
            string.Join(",", predecessors) + "|" + evidence)));

    /// <summary>幂等键命名空间化：避免与端点 IdempotencyExecutor 的同名 HTTP 键（不同请求摘要）互相冲突。</summary>
    private static string StoreKey(string idemKey)
        => "rc:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(idemKey)))[..40];

    private static ReturnConfirmResult ParseHit(long taskId, IdempotencyHit hit)
    {
        try
        {
            using var doc = JsonDocument.Parse(hit.Body);
            var root = doc.RootElement;
            var id = root.TryGetProperty("taskId", out var t) && t.TryGetInt64(out var value) ? value : taskId;
            var state = root.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.String
                ? s.GetString() ?? "" : "";
            return new ReturnConfirmResult(id, state, true);
        }
        catch (JsonException)
        {
            return new ReturnConfirmResult(taskId, "", true);
        }
    }

    private static string Digest(int version, string state, string? reason, IReadOnlyList<long> predecessorIds)
        => JsonSerializer.Serialize(new
        {
            version, state, reason, predecessorEventIds = predecessorIds.OrderBy(id => id).ToArray(),
        });

    // ---------- 读取 ----------

    private async Task<TaskRow?> LoadTaskAsync(SqlConnection conn, SqlTransaction tx, long taskId, bool forUpdate,
        CancellationToken ct)
    {
        var hint = forUpdate ? " WITH (UPDLOCK, HOLDLOCK)" : "";
        return await conn.QuerySingleOrDefaultAsync<TaskRow>(new CommandDefinition($@"
SELECT t.id, t.org_id, t.task_type, t.event_key, t.source_snapshot_id, t.source_hash, t.payload_json,
       t.state, t.state_reason, t.predecessor_task_id, t.version, s.business_datetime
FROM dbo.upload_task t{hint}
LEFT JOIN dbo.source_snapshot s ON s.id=t.source_snapshot_id
WHERE t.id=@id AND t.env=@env",
            new { id = taskId, env = _env }, tx, cancellationToken: ct));
    }

    private static Task<int> CountUploadAttemptsAsync(SqlConnection conn, SqlTransaction tx, long taskId, CancellationToken ct)
        => conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM dbo.upload_attempt WHERE task_id=@id AND attempt_kind=N'UPLOAD'",
            new { id = taskId }, tx, cancellationToken: ct));

    /// <summary>按 id 升序加 UPDLOCK/HOLDLOCK：并发确认同一前序任务时确定性串行化（锁序固定，无环）。</summary>
    private static async Task LockTasksAsync(SqlConnection conn, SqlTransaction tx, IReadOnlyList<long> taskIds,
        CancellationToken ct)
    {
        if (taskIds.Count == 0) return;
        await conn.ExecuteAsync(new CommandDefinition(
            "SELECT id FROM dbo.upload_task WITH (UPDLOCK, HOLDLOCK) WHERE id IN @ids ORDER BY id",
            new { ids = taskIds }, tx, cancellationToken: ct));
    }

    /// <summary>
    /// 服务端前序解析（决策 4/8）：每码取唯一未退货前序事件，并校验前序事件归属任务类型与可用性
    /// （Succeeded 或 upload_attempt/upload_part 有平台受理证据）。客户端提交值仅作比对，不参与解析。
    /// </summary>
    private async Task<ResolvedPredecessors> ResolvePredecessorsAsync(SqlConnection conn, SqlTransaction tx,
        TaskRow task, IReadOnlyList<RetailLine> lines, CancellationToken ct)
    {
        var consumer = task.task_type == RetailTaskFactory.ConsumerReturn116;
        var predecessorType = consumer ? "SALE" : "PURCHASE_IN";
        var returnType = consumer ? "CONSUMER_RETURN" : "PURCHASE_RETURN";
        var codeText = new Dictionary<long, string>();
        foreach (var line in lines)
            foreach (var code in line.Codes)
                codeText.TryAdd(code.CodeId, code.CodeNormalized);
        var codeIds = codeText.Keys.ToList();

        var events = new Dictionary<long, List<(long Id, DateTime At)>>();
        var returned = new List<long>();
        for (var start = 0; start < codeIds.Count; start += BatchSize)
        {
            var chunk = codeIds.Skip(start).Take(BatchSize).ToList();
            var rows = await conn.QueryAsync<PredecessorEventRow>(new CommandDefinition(@"
SELECT code_id, id, occurred_at FROM dbo.code_event
WHERE org_id=@orgId AND env=@env AND event_type=@type AND code_id IN @ids
ORDER BY occurred_at, id",
                new { orgId = task.org_id, env = _env, type = predecessorType, ids = chunk }, tx, cancellationToken: ct));
            foreach (var row in rows)
            {
                if (!events.TryGetValue(row.code_id, out var list))
                {
                    list = new List<(long, DateTime)>();
                    events[row.code_id] = list;
                }
                list.Add((row.id, row.occurred_at));
            }
            returned.AddRange(await conn.QueryAsync<long>(new CommandDefinition(@"
SELECT predecessor_event_id FROM dbo.code_event
WHERE org_id=@orgId AND env=@env AND event_type=@type AND predecessor_event_id IS NOT NULL
  AND code_id IN @ids",
                new { orgId = task.org_id, env = _env, type = returnType, ids = chunk }, tx, cancellationToken: ct)));
        }

        var input = new List<(long CodeId, DateTime? OccurredAt, IReadOnlyList<long> SaleEventIds)>(codeIds.Count);
        foreach (var codeId in codeIds)
        {
            var list = events.TryGetValue(codeId, out var found) ? found : new List<(long Id, DateTime At)>();
            input.Add((codeId, list.Count == 0 ? null : list.Max(x => x.At), list.Select(x => x.Id).ToList()));
        }
        var candidates = ReturnRules.ResolvePredecessors(input, returned);
        var failure = candidates.FirstOrDefault(c => c.Error is not null);
        if (failure is not null)
        {
            var code = codeText.TryGetValue(failure.CodeId, out var text)
                ? text
                : failure.CodeId.ToString(CultureInfo.InvariantCulture);
            throw new AllocationValidationException(failure.Error!, PredecessorMessage(failure.Error!, code, consumer));
        }
        var map = candidates.ToDictionary(c => c.CodeId, c => c.PredecessorEventId!.Value);

        // 前序事件 → 所属任务（缺失/无任务 → MISMATCH）；任务类型与平台受理证据再校验。
        var owners = new Dictionary<long, long?>();
        var eventIds = map.Values.Distinct().ToList();
        for (var start = 0; start < eventIds.Count; start += BatchSize)
        {
            var chunk = eventIds.Skip(start).Take(BatchSize).ToList();
            var rows = await conn.QueryAsync<EventOwnerRow>(new CommandDefinition(
                "SELECT id, upload_task_id FROM dbo.code_event WHERE id IN @ids",
                new { ids = chunk }, tx, cancellationToken: ct));
            foreach (var row in rows) owners[row.id] = row.upload_task_id;
        }
        if (eventIds.Any(id => !owners.TryGetValue(id, out var owner) || owner is null))
            throw new AllocationValidationException(ErrorPredecessorMismatch, "前序事件未关联任何上传任务，无法确认退货");
        var taskIds = owners.Values.Select(owner => owner!.Value).Distinct().OrderBy(id => id).ToList();
        var expectedType = consumer ? RetailTaskFactory.Retail321 : "PURCHASE_IN_102";
        var taskRows = (await conn.QueryAsync<PredecessorTaskRow>(new CommandDefinition(
            "SELECT id, task_type, state FROM dbo.upload_task WHERE id IN @ids",
            new { ids = taskIds }, tx, cancellationToken: ct))).ToDictionary(r => r.id);
        foreach (var predecessorTaskId in taskIds)
        {
            if (!taskRows.TryGetValue(predecessorTaskId, out var row) || !string.Equals(row.task_type, expectedType, StringComparison.Ordinal)
                || (row.state != "Succeeded"
                    && !await HasPlatformReceiptAsync(conn, tx, predecessorTaskId, ct)))
                throw new AllocationValidationException(ErrorPredecessorMismatch, consumer
                    ? "前序销售任务尚未成功上传且无平台受理证据，不能确认退货"
                    : "前序采购入库任务尚未成功上传且无平台受理证据，不能确认采购退货");
        }
        return new ResolvedPredecessors(map, taskIds.Count == 1 ? taskIds[0] : null, taskIds);
    }

    /// <summary>平台受理证据：UPLOAD 尝试（非过期）已有 HTTP 200/平台任务号，或分片已进入受理及之后状态。</summary>
    private static Task<bool> HasPlatformReceiptAsync(SqlConnection conn, SqlTransaction tx, long taskId,
        CancellationToken ct)
        => conn.ExecuteScalarAsync<bool>(new CommandDefinition(@"
SELECT CASE WHEN EXISTS (
    SELECT 1 FROM dbo.upload_attempt a
    WHERE a.task_id=@id AND a.attempt_kind=N'UPLOAD' AND a.is_stale_evidence=0
      AND (a.http_status=200 OR a.platform_task_id IS NOT NULL)
) OR EXISTS (
    SELECT 1 FROM dbo.upload_part p
    WHERE p.task_id=@id AND p.state IN (N'Accepted',N'Processing',N'PartialSuccess',N'Succeeded')
) THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END", new { id = taskId }, tx, cancellationToken: ct));

    private static string PredecessorMessage(string error, string code, bool consumer) => error switch
    {
        ReturnRules.ErrorMissing => consumer
            ? $"码 {code} 无销售出库事件，无法退货"
            : $"码 {code} 无采购入库事件，无法退货",
        ReturnRules.ErrorAmbiguous => $"码 {code} 存在多条未退货{(consumer ? "销售" : "采购入库")}事件，无法确定前序",
        _ => $"码 {code} 已发生退货，禁止重复退货",
    };

    /// <summary>从占位任务不可变载荷构建工厂行（116 退货码 / 202 夹具载荷共用同一形状）。</summary>
    private async Task<IReadOnlyList<RetailLine>> LoadLinesAsync(SqlConnection conn, SqlTransaction tx, TaskRow task,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(task.payload_json))
            throw new AllocationValidationException(ErrorNotReturnable, "退货任务缺少不可变载荷，无法确认");
        var raw = new List<RawLine>();
        try
        {
            using var doc = JsonDocument.Parse(task.payload_json);
            if (!doc.RootElement.TryGetProperty("lines", out var linesEl) || linesEl.ValueKind != JsonValueKind.Array)
                throw new AllocationValidationException(ErrorNotReturnable, "退货任务载荷缺少 lines");
            foreach (var lineEl in linesEl.EnumerateArray())
            {
                var key = StringProp(lineEl, "sourceLineKey");
                var quantity = QuantityProp(lineEl);
                var codes = new List<string>();
                if (lineEl.TryGetProperty("codes", out var codesEl) && codesEl.ValueKind == JsonValueKind.Array)
                    foreach (var codeEl in codesEl.EnumerateArray())
                        if (codeEl.ValueKind == JsonValueKind.String && codeEl.GetString() is { Length: > 0 } code)
                            codes.Add(code);
                if (string.IsNullOrEmpty(key) || quantity is null || codes.Count == 0)
                    throw new AllocationValidationException(ErrorNotReturnable, "退货任务载荷行缺少行键/数量/码");
                raw.Add(new RawLine(key, StringProp(lineEl, "productSourceId"), StringProp(lineEl, "platformDrugId"),
                    quantity.Value, NullIfEmpty(StringProp(lineEl, "unit")), NullIfEmpty(StringProp(lineEl, "batchNo")),
                    codes));
            }
        }
        catch (JsonException)
        {
            throw new AllocationValidationException(ErrorNotReturnable, "退货任务载荷不是合法 JSON");
        }
        if (raw.Count == 0)
            throw new AllocationValidationException(ErrorNotReturnable, "退货任务载荷不含退货行");

        var perLineCodes = new List<(int LineIndex, string Code)>();
        for (var index = 0; index < raw.Count; index++)
            foreach (var code in raw[index].Codes)
            {
                var normalized = TraceCodeNormalizer.Normalize(code).Normalized;
                if (normalized is null)
                    throw new AllocationValidationException(ErrorNotReturnable, $"码 {code} 非法，禁止确认退货");
                perLineCodes.Add((index, normalized));
            }
        var distinctCodes = perLineCodes.Select(x => x.Code).Distinct(StringComparer.Ordinal).ToList();
        var codeIds = new Dictionary<string, long>(StringComparer.Ordinal);
        for (var start = 0; start < distinctCodes.Count; start += BatchSize)
        {
            var chunk = distinctCodes.Skip(start).Take(BatchSize).ToList();
            var rows = await conn.QueryAsync<CodeIdRow>(new CommandDefinition(
                "SELECT id, code_normalized FROM dbo.trace_code WHERE code_normalized IN @codes",
                new { codes = chunk }, tx, cancellationToken: ct));
            foreach (var row in rows) codeIds[row.code_normalized] = row.id;
        }
        foreach (var code in distinctCodes)
            if (!codeIds.ContainsKey(code))
                throw new AllocationValidationException(ErrorNotReturnable, $"码 {code} 未登记，禁止确认退货");

        var lineIds = new Dictionary<string, long>(StringComparer.Ordinal);
        if (task.source_snapshot_id is long snapshotId)
        {
            var rows = await conn.QueryAsync<SourceLineRow>(new CommandDefinition(
                "SELECT id, source_line_key FROM dbo.source_line WHERE snapshot_id=@snap",
                new { snap = snapshotId }, tx, cancellationToken: ct));
            foreach (var row in rows) lineIds[row.source_line_key] = row.id;
        }

        var result = new List<RetailLine>(raw.Count);
        for (var index = 0; index < raw.Count; index++)
        {
            var codes = perLineCodes.Where(x => x.LineIndex == index)
                .GroupBy(x => x.Code, StringComparer.Ordinal)
                .Select(g => (codeIds[g.Key], g.Key))
                .ToList();
            var line = raw[index];
            result.Add(new RetailLine(lineIds.TryGetValue(line.SourceLineKey, out var sourceLineId) ? sourceLineId : 0L,
                line.SourceLineKey, line.ProductSourceId, line.PlatformDrugId, line.Quantity, line.Unit, line.BatchNo,
                codes));
        }
        return result;
    }

    /// <summary>与 SaleVerificationService.MappingDigest 同一公式重算（mapping_version_snapshot 为摘要，不可反演）。</summary>
    private static async Task<string> MappingDigestAsync(SqlConnection conn, SqlTransaction tx, TaskRow task,
        CancellationToken ct)
    {
        if (task.source_snapshot_id is not long snapshotId) return "";
        var rows = await conn.QueryAsync<MappingRow>(new CommandDefinition(@"
SELECT l.product_source_id, pm.version AS mapping_version
FROM dbo.source_line l
LEFT JOIN dbo.product p
  ON p.org_id=@orgId
 AND p.source_system_id=(SELECT source_system_id FROM dbo.source_snapshot WHERE id=@snap)
 AND p.spid=l.product_source_id
LEFT JOIN dbo.product_mapping pm ON pm.product_id=p.id AND pm.state IN (N'AUTO_CONFIRMED',N'MANUAL_CONFIRMED')
WHERE l.snapshot_id=@snap",
            new { orgId = task.org_id, snap = snapshotId }, tx, cancellationToken: ct));
        return string.Join("|", rows
            .Where(r => r.mapping_version is not null)
            .Select(r => $"product:{r.product_source_id}:{r.mapping_version}")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal));
    }

    /// <summary>
    /// 规则版本：占位载荷由写入方固化（116 由核验事务、202 由夹具/上级写入口），载荷恒带 ruleVersion；
    /// 缺失时退回当前设置版本。不再按 verification 事件键回查（F3：原回退用的是 SALE 核验键，
    /// 既不是任务键也与 verification 的键空间不匹配，属不可达死代码）。
    /// </summary>
    private static string ResolveRuleVersion(TaskRow task, string fallback)
        => RuleVersionFromPayload(task.payload_json) ?? fallback;

    private async Task InsertReturnConfirmationAsync(SqlConnection conn, SqlTransaction tx, TaskRow task,
        string decision, string evidence, string actor, long? predecessorTaskId, CancellationToken ct)
        => await conn.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.return_confirmation(org_id,env,task_id,decision,relation_kind,predecessor_task_id,evidence_json,confirmed_by)
VALUES(@orgId,@env,@taskId,@decision,@relation,@pred,@evidence,@actor)",
            new
            {
                orgId = task.org_id, env = _env, taskId = task.id, decision,
                relation = task.task_type == RetailTaskFactory.ConsumerReturn116 ? "SALE" : "PURCHASE_IN",
                pred = predecessorTaskId, evidence, actor,
            }, tx, cancellationToken: ct));

    // ---------- JSON 小工具 ----------

    private static string StringProp(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";

    private static decimal? QuantityProp(JsonElement element)
    {
        if (!element.TryGetProperty("quantity", out var value)) return null;
        if (value.ValueKind == JsonValueKind.String
            && decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)) return number;
        return null;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static string? RuleVersionFromPayload(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            return doc.RootElement.TryGetProperty("ruleVersion", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string DocKey(string eventKey, string taskType)
    {
        const string prefix = "KSOA:";
        var suffix = ":" + taskType;
        return eventKey.StartsWith(prefix, StringComparison.Ordinal)
               && eventKey.EndsWith(suffix, StringComparison.Ordinal)
               && eventKey.Length > prefix.Length + suffix.Length
            ? eventKey[prefix.Length..^suffix.Length]
            : eventKey;
    }

    // ---------- 行类型 ----------

    private sealed record RawLine(string SourceLineKey, string ProductSourceId, string PlatformDrugId, decimal Quantity,
        string? Unit, string? BatchNo, IReadOnlyList<string> Codes);

    private sealed record ResolvedPredecessors(IReadOnlyDictionary<long, long> Map, long? SingleTaskId,
        IReadOnlyList<long> TaskIds);

    private sealed class TaskRow
    {
        public long id { get; set; }
        public long org_id { get; set; }
        public string task_type { get; set; } = "";
        public string event_key { get; set; } = "";
        public long? source_snapshot_id { get; set; }
        public string source_hash { get; set; } = "";
        public string? payload_json { get; set; }
        public string state { get; set; } = "";
        public string? state_reason { get; set; }
        public long? predecessor_task_id { get; set; }
        public int version { get; set; }
        public DateTime? business_datetime { get; set; }
    }

    private sealed class PredecessorEventRow
    {
        public long code_id { get; set; }
        public long id { get; set; }
        public DateTime occurred_at { get; set; }
    }

    private sealed class EventOwnerRow
    {
        public long id { get; set; }
        public long? upload_task_id { get; set; }
    }

    private sealed class PredecessorTaskRow
    {
        public long id { get; set; }
        public string task_type { get; set; } = "";
        public string state { get; set; } = "";
    }

    private sealed class CodeIdRow
    {
        public long id { get; set; }
        public string code_normalized { get; set; } = "";
    }

    private sealed class SourceLineRow
    {
        public long id { get; set; }
        public string source_line_key { get; set; } = "";
    }

    private sealed class MappingRow
    {
        public string product_source_id { get; set; } = "";
        public int? mapping_version { get; set; }
    }
}