using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Domain.Uploads;
using TraceFlow.Infrastructure.Settings;

namespace TraceFlow.Infrastructure.Uploads;

/// <summary>人工核对命令（UI-09）：decision ∈ ACCEPTED/PROCESSING/NOT_ACCEPTED；证据由端点解析后传入。</summary>
/// <param name="PlatformBillCode">ACCEPTED/PROCESSING 必填：必须等于该任务某分片的 bill_code（平台受理回执单号）。</param>
/// <param name="ContractEvidence">NOT_ACCEPTED 必填（合同证据）；ACCEPTED/PROCESSING 可选补充。</param>
/// <param name="ExpectedVersion">可选乐观锁（upload_task.version）；提供且不匹配 → 409 VERSION_CONFLICT。</param>
public sealed record ManualVerifyCommand(string Decision, string? PlatformBillCode, JsonElement? ContractEvidence,
    int? ExpectedVersion);

/// <summary>人工核对结果：State/Version 为事务内重算后的任务状态。</summary>
public sealed record ManualVerifyResult(long TaskId, string State, int Version, string Decision,
    IReadOnlyList<long> PartIds);

/// <summary>受控重试结果：JobsReset 为被复位的 upload-dispatch 作业数（0 表示原本没有作业、已新建待执行作业）。</summary>
public sealed record ControlledRetryResult(long TaskId, string State, int Version, IReadOnlyList<long> PartIds,
    int JobsReset);

/// <summary>
/// 上传中心人工操作（UI-09 / IDEM-05）：
/// <list type="number">
/// <item><b>manual-verify</b>：仅 <c>ManualAction</c> 任务。ACCEPTED/PROCESSING 需 platformBillCode 命中任务分片单号，
/// 经 <see cref="UploadStateMachine"/> 推进 EvidenceVerifiedAccepted/Processing；NOT_ACCEPTED 需一致性窗口已过
/// 且提供合同证据，经 QueryConfirmedNotAccepted → RetryWait 并复位发送作业（否则 RetryWait 永远不会被再发送）。</item>
/// <item><b>controlled-retry</b>：先读 <see cref="SettingsService.GetRetryPathCertifiedAsync"/>，false 在零写入前抛
/// <see cref="RetryPathNotCertifiedException"/>；true 时仅 Failed 分片 ControlledRetryApproved → RetryWait，
/// 复位 <c>upload-dispatch</c> 作业（PENDING/attempt_count=0/next_run_at=now/清租约），绝不改写 upload_attempt 历史。</item>
/// </list>
/// 所有写入都在调用方事务内完成（端点经 IdempotencyExecutor），审计与业务同事务；不触网。
/// </summary>
public sealed class UploadManualService
{
    public const string DecisionAccepted = "ACCEPTED";
    public const string DecisionProcessing = "PROCESSING";
    public const string DecisionNotAccepted = "NOT_ACCEPTED";
    public const string ActionManualVerify = "task.upload.manual_verified";
    public const string ActionControlledRetry = "task.upload.controlled_retry";
    public const string ErrorEvidenceRequired = "EVIDENCE_REQUIRED";
    public const string ErrorEvidenceMismatch = "EVIDENCE_MISMATCH";
    public const string ErrorConsistencyWindow = "CONSISTENCY_WINDOW_NOT_MET";
    public const string ErrorNotManualAction = "NOT_MANUAL_ACTION";
    public const string ErrorNotFailed = "NOT_FAILED";
    public const string ErrorTransition = "TRANSITION_NOT_ALLOWED";
    public const string ErrorVersionConflict = "VERSION_CONFLICT";

    private const string DispatchJobKind = "upload-dispatch";
    private const string DispatchIntentPrefix = "part:";

    private readonly SettingsService _settings;
    private readonly IJobQueue _queue;
    private readonly IAuditLog _audit;

    public UploadManualService(SettingsService settings, IJobQueue queue, IAuditLog audit)
    {
        _settings = settings;
        _queue = queue;
        _audit = audit;
    }

    // ------------------------------------------------------------------ manual-verify

    public async Task<ManualVerifyResult> ManualVerifyAsync(SqlConnection conn, SqlTransaction tx, long taskId,
        ManualVerifyCommand cmd, string actor, string requestId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        var decision = (cmd.Decision ?? "").Trim().ToUpperInvariant();
        if (decision is not (DecisionAccepted or DecisionProcessing or DecisionNotAccepted))
            throw new AllocationValidationException("VALIDATION", "decision 只能为 ACCEPTED、PROCESSING 或 NOT_ACCEPTED");

        // 所有校验都在首个写入之前完成：拒绝路径事务回滚，幂等记录也不会落库（拒绝即零写入）。
        var task = await LoadTaskAsync(conn, tx, taskId, ct);
        if (task is null)
            throw new ResourceNotFoundException("上传任务不存在");
        if (!string.Equals(task.state, nameof(UploadState.ManualAction), StringComparison.Ordinal))
            throw new AllocationValidationException(ErrorNotManualAction,
                $"任务处于 {task.state}，仅 ManualAction 可人工核对");
        if (cmd.ExpectedVersion is int expected && expected != task.version)
            throw new AllocationConflictException(ErrorVersionConflict, "任务版本已变化，请刷新后重试");

        var parts = await LoadPartsAsync(conn, tx, taskId, ct);
        if (parts.Count == 0)
            throw new ResourceNotFoundException("上传任务没有分片");

        UploadEvent evt;
        string evidenceJson;
        IReadOnlyList<PartRow> targets;
        if (decision == DecisionNotAccepted)
        {
            if (!IsMeaningful(cmd.ContractEvidence))
                throw new AllocationValidationException(ErrorEvidenceRequired,
                    "NOT_ACCEPTED 需要非空 contractEvidence（合同/平台协作证据）");
            var policy = await _settings.GetQueryPolicyAsync(ct);
            var window = TimeSpan.FromSeconds(Math.Max(0, policy.ConsistencyWindowSeconds));
            // 平台侧受理存在一致性延迟：以任务内最早一次发送时间为基准，窗口未过不得判定「平台未受理」。
            var earliest = parts.Min(p => p.referenceAt);
            var notBefore = earliest + window;
            if (DateTime.UtcNow < notBefore)
                throw new AllocationValidationException(ErrorConsistencyWindow,
                    $"受理一致性窗口未到期（最早发送 {earliest:O} + {window.TotalSeconds:F0}s），不得判定平台未受理");
            evt = UploadEvent.QueryConfirmedNotAccepted;
            evidenceJson = BuildEvidence(decision, null, cmd.ContractEvidence);
            targets = parts.Where(p => p.state == nameof(UploadState.ManualAction)).ToList();
            if (targets.Count == 0)
                throw new AllocationValidationException(ErrorTransition, "任务没有可推进的 ManualAction 分片");
        }
        else
        {
            var billCode = cmd.PlatformBillCode?.Trim();
            if (string.IsNullOrEmpty(billCode))
                throw new AllocationValidationException(ErrorEvidenceRequired,
                    "ACCEPTED/PROCESSING 需要 platformBillCode（平台受理单号）");
            var matched = parts.Where(p => string.Equals(p.billCode, billCode, StringComparison.Ordinal)).ToList();
            if (matched.Count == 0)
                throw new AllocationValidationException(ErrorEvidenceMismatch,
                    "platformBillCode 不属于该任务的分片单号");
            evt = decision == DecisionAccepted ? UploadEvent.EvidenceVerifiedAccepted
                : UploadEvent.EvidenceVerifiedProcessing;
            evidenceJson = BuildEvidence(decision, billCode, cmd.ContractEvidence);
            targets = matched;
        }

        var before = Digest(task.state, task.version, parts);
        var moved = new List<long>(targets.Count);
        foreach (var part in targets)
        {
            moved.Add(part.id);
            // 受理侧结论仍需回查收敛：把 next_query_at 置为当前，交给 query:scan 兜底回查；RetryWait 不排回查。
            await AdvancePartAsync(conn, tx, part, evt,
                decision == DecisionNotAccepted ? null : DateTime.UtcNow, ct);
        }

        // NOT_ACCEPTED → RetryWait 必须复位发送作业，否则分片可发送但无人认领（RetryWait 是发送单元状态）。
        var jobsReset = decision == DecisionNotAccepted
            ? await ResetDispatchJobsAsync(conn, tx, task, moved, "manual-not-accepted", ct)
            : 0;

        await UploadDispatchService.RecomputeTaskStateAsync(conn, tx, taskId, ct);
        var (state, version) = await ReadStateAsync(conn, tx, taskId, ct);
        await _audit.AppendAsync(conn, tx, new AuditEntry(actor, "User", ActionManualVerify, "upload_task",
            taskId.ToString(CultureInfo.InvariantCulture),
            $"人工核对 {decision}（{moved.Count} 个分片，复位发送作业 {jobsReset}）",
            before, DigestWithEvidence(state, version, moved, evidenceJson), requestId, null, task.env));

        return new ManualVerifyResult(taskId, state, version, decision, moved);
    }

    // ------------------------------------------------------------------ controlled-retry

    public async Task<ControlledRetryResult> ControlledRetryAsync(SqlConnection conn, SqlTransaction tx, long taskId,
        int? expectedVersion, string actor, string requestId, CancellationToken ct)
    {
        // 合同门禁在读任务/写任何行之前判定（fail-closed）：false → 422 且事务内零写入。
        if (!await _settings.GetRetryPathCertifiedAsync(ct))
            throw new RetryPathNotCertifiedException(
                "受控重试通道未取证（top.retryPathCertified=false），禁止重发；请等待回查或联系平台");

        var task = await LoadTaskAsync(conn, tx, taskId, ct);
        if (task is null)
            throw new ResourceNotFoundException("上传任务不存在");
        if (!string.Equals(task.state, nameof(UploadState.Failed), StringComparison.Ordinal))
            throw new AllocationValidationException(ErrorNotFailed,
                $"任务处于 {task.state}，仅 Failed 可受控重试");
        if (expectedVersion is int expected && expected != task.version)
            throw new AllocationConflictException(ErrorVersionConflict, "任务版本已变化，请刷新后重试");

        var parts = await LoadPartsAsync(conn, tx, taskId, ct);
        var failedParts = parts.Where(p => p.state == nameof(UploadState.Failed)).ToList();
        if (failedParts.Count == 0)
            throw new AllocationValidationException(ErrorNotFailed, "任务没有 Failed 分片");

        var before = Digest(task.state, task.version, parts);
        var moved = new List<long>(failedParts.Count);
        foreach (var part in failedParts)
        {
            moved.Add(part.id);
            await AdvancePartAsync(conn, tx, part, UploadEvent.ControlledRetryApproved, null, ct);
        }

        // 只复位失败分片对应的发送作业（成功分片保持终态，绝不重发）；upload_attempt 历史不改写。
        var jobsReset = await ResetDispatchJobsAsync(conn, tx, task, moved, "controlled-retry", ct);
        await UploadDispatchService.RecomputeTaskStateAsync(conn, tx, taskId, ct);
        var (state, version) = await ReadStateAsync(conn, tx, taskId, ct);
        await _audit.AppendAsync(conn, tx, new AuditEntry(actor, "User", ActionControlledRetry, "upload_task",
            taskId.ToString(CultureInfo.InvariantCulture),
            $"受控重试（top.retryPathCertified=true，{moved.Count} 个失败分片，复位发送作业 {jobsReset}）",
            before, Digest(state, version, moved), requestId, null, task.env));

        return new ControlledRetryResult(taskId, state, version, moved, jobsReset);
    }

    // ------------------------------------------------------------------ 内部

    /// <summary>状态机推进（写前校验）：当前状态与事件必须可达，UPDATE 以 state 乐观守卫防并发覆盖。</summary>
    private static async Task<string> AdvancePartAsync(SqlConnection conn, SqlTransaction tx, PartRow part,
        UploadEvent evt, DateTime? nextQueryAt, CancellationToken ct)
    {
        if (!Enum.TryParse<UploadState>(part.state, ignoreCase: false, out var current)
            || !UploadStateMachine.TryGetNext(current, evt, out var next))
            throw new AllocationValidationException(ErrorTransition,
                $"分片处于 {part.state}，状态机不允许 {evt} 转换");
        var rows = await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.upload_part SET state=@next, next_query_at=@nextQueryAt WHERE id=@id AND state=@current",
            new { next = next.ToString(), nextQueryAt, id = part.id, current = part.state }, tx,
            cancellationToken: ct));
        if (rows != 1)
            throw new AllocationConflictException(ErrorVersionConflict, "分片状态已变化，请刷新后重试");
        return next.ToString();
    }

    /// <summary>
    /// 复位分片发送作业：存在则 PENDING/attempt_count=0/next_run_at=now/清租约与收尾字段；
    /// 不存在则新建（同事务，同意图幂等）。取消态作业不复位（行政取消优先）。
    /// </summary>
    private async Task<int> ResetDispatchJobsAsync(SqlConnection conn, SqlTransaction tx, TaskRow task,
        IReadOnlyList<long> partIds, string trigger, CancellationToken ct)
    {
        var reset = 0;
        foreach (var partId in partIds)
        {
            var intent = DispatchIntentPrefix + partId.ToString(CultureInfo.InvariantCulture);
            var payload = JsonSerializer.Serialize(new
            {
                orgId = task.orgId, env = task.env, taskId = task.id, partId, trigger,
            });
            var jobId = await _queue.EnqueueAsync(conn, tx, task.orgId, task.env, DispatchJobKind, intent,
                payload, DateTime.UtcNow, ct);
            reset += await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.outbox_job
SET job_state=N'PENDING', attempt_count=0, next_run_at=SYSUTCDATETIME(),
    lease_owner=NULL, lease_until=NULL, finished_at=NULL, last_error=NULL
WHERE id=@id AND job_state <> N'CANCELLED'", new { id = jobId }, tx, cancellationToken: ct));
        }
        return reset;
    }

    private static async Task<TaskRow?> LoadTaskAsync(SqlConnection conn, SqlTransaction tx, long taskId,
        CancellationToken ct)
        => await conn.QuerySingleOrDefaultAsync<TaskRow>(new CommandDefinition(@"
SELECT t.id, t.org_id AS orgId, t.env, t.state, t.version, t.task_type AS taskType
FROM dbo.upload_task t WITH (UPDLOCK, HOLDLOCK)
WHERE t.id=@id", new { id = taskId }, tx, cancellationToken: ct));

    /// <summary>
    /// 分片与「最后一次发送参考时间」：无 last_sent_at 列，取 UPLOAD 尝试的 MAX(started_at)，
    /// 退化到 first_sent_at，再退化到任务创建时间（从未发送的盲区也必须给出确定基准）。
    /// </summary>
    private static async Task<IReadOnlyList<PartRow>> LoadPartsAsync(SqlConnection conn, SqlTransaction tx,
        long taskId, CancellationToken ct)
        => (await conn.QueryAsync<PartRow>(new CommandDefinition(@"
SELECT p.id, p.part_index AS partIndex, p.bill_code AS billCode, p.state,
       CASE WHEN a.last_sent IS NOT NULL THEN a.last_sent
            WHEN p.first_sent_at IS NOT NULL THEN p.first_sent_at
            ELSE t.created_at END AS referenceAt
FROM dbo.upload_part p
JOIN dbo.upload_task t ON t.id=p.task_id
LEFT JOIN (
    SELECT part_id, MAX(started_at) AS last_sent
    FROM dbo.upload_attempt WHERE attempt_kind=N'UPLOAD' GROUP BY part_id
) a ON a.part_id=p.id
WHERE p.task_id=@taskId
ORDER BY p.part_index", new { taskId }, tx, cancellationToken: ct))).ToList();

    private static async Task<(string State, int Version)> ReadStateAsync(SqlConnection conn, SqlTransaction tx,
        long taskId, CancellationToken ct)
    {
        var row = await conn.QuerySingleAsync<StateRow>(new CommandDefinition(
            "SELECT state, version FROM dbo.upload_task WHERE id=@id", new { id = taskId }, tx,
            cancellationToken: ct));
        return (row.state, row.version);
    }

    private static string BuildEvidence(string decision, string? billCode, JsonElement? contract)
    {
        var evidence = new JsonObject { ["decision"] = decision };
        if (!string.IsNullOrEmpty(billCode)) evidence["platformBillCode"] = billCode;
        if (IsMeaningful(contract)) evidence["contractEvidence"] = JsonNode.Parse(contract!.Value.GetRawText());
        return evidence.ToJsonString();
    }

    /// <summary>证据「非空」判定：字符串去空白非空；对象/数组至少一个成员；其余（null/数字/布尔）不算证据。</summary>
    private static bool IsMeaningful(JsonElement? element)
    {
        if (element is not JsonElement value) return false;
        return value.ValueKind switch
        {
            JsonValueKind.String => !string.IsNullOrWhiteSpace(value.GetString()),
            JsonValueKind.Object => value.EnumerateObject().Any(),
            JsonValueKind.Array => value.GetArrayLength() > 0,
            _ => false,
        };
    }

    private static string Digest(string state, int version, IReadOnlyList<PartRow> parts)
        => JsonSerializer.Serialize(new
        {
            state,
            version,
            parts = parts.Select(p => new { id = p.id, state = p.state }).ToArray(),
        });

    private static string Digest(string state, int version, IReadOnlyList<long> partIds)
        => JsonSerializer.Serialize(new
        {
            state,
            version,
            parts = partIds.Select(id => new { id }).ToArray(),
        });

    private static string DigestWithEvidence(string state, int version, IReadOnlyList<long> partIds, string evidenceJson)
        => JsonSerializer.Serialize(new
        {
            state,
            version,
            parts = partIds.Select(id => new { id }).ToArray(),
            evidence = JsonNode.Parse(evidenceJson),
        });

    private sealed class TaskRow
    {
        public long id { get; set; }
        public long orgId { get; set; }
        public string env { get; set; } = "";
        public string state { get; set; } = "";
        public int version { get; set; }
        public string taskType { get; set; } = "";
    }

    private sealed class PartRow
    {
        public long id { get; set; }
        public int partIndex { get; set; }
        public string billCode { get; set; } = "";
        public string state { get; set; } = "";
        public DateTime referenceAt { get; set; }
    }

    private sealed class StateRow
    {
        public string state { get; set; } = "";
        public int version { get; set; }
    }
}