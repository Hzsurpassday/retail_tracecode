using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Domain.Top;
using TraceFlow.Domain.Uploads;
using TraceFlow.Infrastructure.Top;

namespace TraceFlow.Infrastructure.Uploads;

/// <summary>单次发送的结论（Brief Produces 契约）。</summary>
public enum DispatchOutcome { Accepted, AcceptanceUnknown, Failed, RateLimited, AuthPaused, StaleRejected }

/// <summary>
/// T3 回执输入：发送尝试行 + 守卫要素（租约持有者 + fencing token）+ 平台结论。
/// 抽出为记录类型，便于把"写回"作为独立可调用步骤（迟到写回/故障注入只调这一步）。
/// </summary>
public sealed record AttemptReceipt(long AttemptId, long PartId, long TaskId, string Env, string WorkerId,
    long FencingToken, TopOutcome Outcome, int? HttpStatus);

/// <summary>
/// 发送协议 upload-dispatch（IDEM-04 / §3.2 / §2.7 / §2.9）：
/// <list type="number">
/// <item>读 part/task 做发送前置校验（分片 Ready/RetryWait 且任务未落定），否则 StaleRejected（不发 HTTP、不写尝试）；</item>
/// <item>短事务（T2 尾段）：同一事务内以 UPDLOCK+HOLDLOCK 重读任务与分片复核（并发取消/重算 → StaleRejected），
/// 写 upload_attempt(max+1) 并把分片置 Dispatching，提交；</item>
/// <item>事务外调用 <see cref="TopClientService"/>（HTTP 不得在事务内）；</item>
/// <item>T3：新连接 + 事务，校验租约持有者 + fencing token + 分片仍 Dispatching，再写回执/推进状态/重算任务/入队回查。</item>
/// </list>
/// 旧 Worker 迟到写回只追加只读证据（<c>is_stale_evidence=1</c>），绝不推进任何状态。
/// 网关异常（含 TOP 未取证）一律外抛：分片保持 Dispatching，由发送恢复走"受理未知→先回查"。
/// </summary>
public sealed class UploadDispatchService
{
    public const string JobKind = "upload-dispatch";
    public const string QueryJobKind = "bill-query";

    /// <summary>候选方法名（G-TOP 取证前仅 MOCK 生效，见 docs/contracts/top-contract.md）。</summary>
    public const string UploadRetailMethod = "alibaba.alihealth.drugtrace.top.lsyd.uploadretail";
    public const string UploadInOutBillMethod = "alibaba.alihealth.drugtrace.top.lsyd.uploadinoutbill";

    private static readonly TimeSpan QueryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AuthPauseDelay = TimeSpan.FromMinutes(5);

    private readonly string _conn;
    private readonly TopClientService _top;
    private readonly IJobQueue _queue;
    private readonly ISendRecoveryService _recovery;

    public UploadDispatchService(string connectionString, TopClientService top, IJobQueue queue, ISendRecoveryService recovery)
    {
        _conn = connectionString;
        _top = top;
        _queue = queue;
        _recovery = recovery;
    }

    // ---------------------------------------------------------------- 故障注入钩子（仅测试可见）

    /// <summary>
    /// 故障注入钩子（internal，仅测试程序集可见）：在"尝试行已提交 + 分片已置 Dispatching"之后、
    /// 调网关之前执行，用于模拟进程在此刻消失（请求从未发出）。生产路径从不赋值。
    /// </summary>
    internal Func<Task>? BeforeSend { get; set; }

    /// <summary>
    /// 故障注入钩子：网关调用已返回（含限流/暂停归一）之后、进入 T3 写回之前执行，
    /// 用于模拟"请求可能已发出、回执尚未落库"的崩溃。生产路径从不赋值。
    /// </summary>
    internal Func<Task>? AfterSend { get; set; }

    /// <summary>
    /// 故障注入钩子：T3 内回执/状态已写入事务、但提交之前执行；抛出的异常令整个 T3 回滚，
    /// 使尝试行保持未收尾（模拟提交前进程消失）。生产路径从不赋值。
    /// 注意：钩子在 T3 尚未提交的事务内触发；钩子实现不得在另一连接上读取或等待同一任务/分片行，
    /// 否则会与事务内尚未释放的写锁互相等待。
    /// </summary>
    internal Func<Task>? BeforeCommit { get; set; }

    private static async Task RunHookAsync(Func<Task>? hook)
    {
        if (hook is not null) await hook();
    }

    /// <summary>发送一个分片。任何异常都不得在此吞掉（尤其网关/未取证异常）。</summary>
    public async Task<DispatchOutcome> DispatchAsync(long partId, string workerId, long fencingToken, CancellationToken ct)
    {
        var head = await ReadHeadAsync(partId, ct);
        if (head is null) return DispatchOutcome.StaleRejected;

        // 硬前置（ledger carry）：分片可发送 + 任务未落定；不满足 → 不发 HTTP、不写尝试行。
        if (!IsSendablePart(head.PartState) || !IsDispatchableTask(head.TaskState))
            return DispatchOutcome.StaleRejected;

        // 参数组装放在短事务之前：身份/载荷缺失时零写入（不留"发送中"假象）。
        var call = BuildCall(head);

        var claim = await OpenAttemptAsync(head, workerId, fencingToken, ct);
        if (claim is null) return DispatchOutcome.StaleRejected;

        await RunHookAsync(BeforeSend); // 仅测试注入：此刻崩溃 = 尝试行已落、请求未发出

        TopOutcome outcome;
        try
        {
            outcome = await _top.CallAsync(head.Env, call.Method, call.Parameters, call.RefEntId, workerId, ct); // HTTP 在事务外（§2.9）
        }
        catch (TopRateLimitedException ex)
        {
            // 可能已发出（响应限流）或未发出（预算门禁）：一律按"请求可能已发出"处理，保守不丢受理。
            outcome = new TopOutcome(TopOutcomeKind.RateLimited, null, null, ex.Message, null, null,
                (int)Math.Ceiling(ex.Wait.TotalSeconds));
        }
        catch (ChannelPausedException ex)
        {
            outcome = new TopOutcome(TopOutcomeKind.AuthError, null, null, ex.Message, null, null, null);
        }

        await RunHookAsync(AfterSend); // 仅测试注入：此刻崩溃 = 请求可能已发出、回执未落库

        return await ApplyReceiptAsync(new AttemptReceipt(claim.Value.AttemptId, partId, head.TaskId, head.Env,
            workerId, fencingToken, outcome, HttpStatusFor(outcome.Kind)), ct);
    }

    /// <summary>
    /// T3：守卫 + 回执落库 + 状态推进 + 任务聚合 + 回查入队。守卫不满足 → 只留证（StaleRejected）。
    /// </summary>
    public async Task<DispatchOutcome> ApplyReceiptAsync(AttemptReceipt receipt, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_conn);
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        // 守卫（§2.7）：任务未被行政取消 → 分片仍 Dispatching → 作业仍由本 Worker 持租且 token 未变。
        // 加锁顺序统一为 任务→分片→作业（与任务工厂/核验重算一致，避免与并发重算形成锁环）。
        var taskState = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT state FROM dbo.upload_task WITH (UPDLOCK, HOLDLOCK) WHERE id=@id", new { id = receipt.TaskId },
            tx, cancellationToken: ct));
        var intent = IntentFor(receipt.PartId);
        var alive = taskState is null || taskState == nameof(UploadState.Cancelled)
            ? null
            : await conn.ExecuteScalarAsync<string?>(new CommandDefinition(@"
SELECT p.state
FROM dbo.upload_part p WITH (UPDLOCK)
JOIN dbo.outbox_job j ON j.job_kind=N'upload-dispatch' AND j.intent_key=@intent
WHERE p.id=@partId AND p.state=N'Dispatching'
  AND j.job_state=N'RUNNING' AND j.lease_owner=@worker AND j.fencing_token=@token",
                new { partId = receipt.PartId, intent, worker = receipt.WorkerId, token = receipt.FencingToken },
                tx, cancellationToken: ct));
        if (alive is null)
        {
            await tx.RollbackAsync(ct);
            await AppendStaleEvidenceAsync(receipt, ct);
            return DispatchOutcome.StaleRejected;
        }

        var mapped = Map(receipt.Outcome.Kind);
        var rawResponse = TopResponseSanitizer.Sanitize(SerializeOutcome(receipt.Outcome));

        await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.upload_attempt
SET finished_at=SYSUTCDATETIME(), http_status=@httpStatus, request_id=@requestId,
    platform_task_id=@platformTaskId, raw_response=@rawResponse, error_kind=@errorKind
WHERE id=@attemptId AND is_stale_evidence=0",
            new
            {
                attemptId = receipt.AttemptId, httpStatus = receipt.HttpStatus, requestId = receipt.Outcome.RequestId,
                platformTaskId = ExtractPlatformTaskId(receipt.Outcome.ResultJson),
                rawResponse, errorKind = ErrorKind(receipt.Outcome.Kind),
            }, tx, cancellationToken: ct));

        if (!UploadStateMachine.TryGetNext(UploadState.Dispatching, mapped.Event, out var next))
            throw new InvalidOperationException($"状态机缺少 {UploadState.Dispatching}→{mapped.Event} 的边");

        var advanced = await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.upload_part SET state=@next WHERE id=@partId AND state=N'Dispatching'",
            new { next = next.ToString(), partId = receipt.PartId }, tx, cancellationToken: ct));
        if (advanced != 1)
        {
            await tx.RollbackAsync(ct);
            await AppendStaleEvidenceAsync(receipt, ct);
            return DispatchOutcome.StaleRejected;
        }

        await RecomputeTaskStateAsync(conn, tx, receipt.TaskId, ct);

        // 受理成功/部分成功/受理未知 → 先回查再决策（禁止凭一次上传结论收尾）。
        if (next is UploadState.Accepted or UploadState.PartialSuccess or UploadState.AcceptanceUnknown)
        {
            var orgId = await OrgIdAsync(conn, tx, receipt.TaskId, ct);
            var queryIntent = "query:" + intent;
            await _queue.EnqueueAsync(conn, tx, orgId, receipt.Env, QueryJobKind, queryIntent, null,
                DateTime.UtcNow.Add(QueryDelay), ct);
            // 重新发送（RetryWait）后必须重新回查：已收尾/失败的旧回查作业复位为待执行。
            await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.outbox_job SET job_state=N'PENDING', lease_owner=NULL, lease_until=NULL,
       next_run_at=@nextRunAt, finished_at=NULL, last_error=NULL
WHERE org_id=@orgId AND env=@env AND job_kind=@kind AND intent_key=@intent AND job_state IN (N'DONE',N'FAILED')",
                new { orgId, env = receipt.Env, kind = QueryJobKind, intent = queryIntent,
                      nextRunAt = DateTime.UtcNow.Add(QueryDelay) }, tx, cancellationToken: ct));
        }

        // 限流/授权暂停：作业退避改期（保守，避免丢受理），分片已落受理未知由回查收敛。
        var reschedule = RescheduleDelay(receipt.Outcome);
        if (reschedule is TimeSpan wait)
            await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.outbox_job
SET job_state=N'PENDING', lease_owner=NULL, lease_until=NULL, next_run_at=@nextRunAt, last_error=@error
WHERE job_kind=N'upload-dispatch' AND intent_key=@intent AND job_state=N'RUNNING' AND fencing_token=@token",
                new
                {
                    nextRunAt = DateTime.UtcNow.Add(wait), intent, token = receipt.FencingToken,
                    error = ErrorKind(receipt.Outcome.Kind) + ": " + (receipt.Outcome.Message ?? ""),
                }, tx, cancellationToken: ct));

        await RunHookAsync(BeforeCommit); // 仅测试注入：此刻崩溃 = 回执已写入事务但未提交，整个 T3 回滚
        await tx.CommitAsync(ct);
        return mapped.Outcome;
    }

    // ---------------------------------------------------------------- 读前置

    private async Task<HeadRow?> ReadHeadAsync(long partId, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_conn);
        await conn.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<HeadRow>(new CommandDefinition(@"
SELECT p.id AS PartId, p.task_id AS TaskId, p.org_id AS OrgId, p.env AS Env, p.part_index AS PartIndex,
       p.bill_code AS BillCode, p.payload_json AS PartPayloadJson, p.state AS PartState,
       t.task_type AS TaskType, t.state AS TaskState, t.payload_json AS TaskPayloadJson,
       o.ref_ent_id AS OrgRefEntId, o.ent_id AS OrgEntId
FROM dbo.upload_part p
JOIN dbo.upload_task t ON t.id = p.task_id
LEFT JOIN dbo.organization o ON o.id = p.org_id
WHERE p.id = @partId", new { partId }, cancellationToken: ct));
    }

    private (string Method, IReadOnlyDictionary<string, string> Parameters, string? RefEntId) BuildCall(HeadRow head)
    {
        var (method, billType) = ResolveMethod(head.TaskType);
        var (refEntId, entId) = ResolveSubmitter(head, billType);
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["bill_code"] = head.BillCode,
            ["ref_ent_id"] = refEntId,
            ["ent_id"] = entId,
            ["bill_type"] = billType,
            ["bill_time"] = BillTime(head),
            ["detail"] = head.PartPayloadJson,
        };
        return (method, parameters, refEntId);
    }

    /// <summary>任务类型 →（方法, bill_type）。202 仅服务/测试用，不伪造 KSOA 采集（R-M3-4）。</summary>
    private static (string Method, string BillType) ResolveMethod(string taskType) => taskType switch
    {
        "RETAIL_321" => (UploadRetailMethod, "321"),
        "CONSUMER_RETURN_116" => (UploadRetailMethod, "116"),
        "PURCHASE_IN_102" => (UploadInOutBillMethod, "102"),
        "PURCHASE_RETURN_202" => (UploadInOutBillMethod, "202"),
        _ => throw new InvalidOperationException($"未知上传任务类型 {taskType}"),
    };

    /// <summary>零售单据由门店主体提交；采购单据由任务载荷中的供应商主体提交（缺失时回退门店）。</summary>
    private static (string RefEntId, string EntId) ResolveSubmitter(HeadRow head, string billType)
    {
        var (refEntId, entId) = (head.OrgRefEntId, head.OrgEntId);
        if (billType is "102" or "202")
        {
            var (supRef, supEnt) = ReadSupplier(head.TaskPayloadJson);
            if (!string.IsNullOrWhiteSpace(supRef) && !string.IsNullOrWhiteSpace(supEnt))
                return (supRef, supEnt);
        }
        if (string.IsNullOrWhiteSpace(refEntId) || string.IsNullOrWhiteSpace(entId))
            throw new InvalidOperationException("平台主体身份未配置（organization.ref_ent_id / ent_id），拒绝发送");
        return (refEntId, entId);
    }

    private static (string? RefEntId, string? EntId) ReadSupplier(string? taskPayloadJson)
    {
        if (string.IsNullOrWhiteSpace(taskPayloadJson)) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(taskPayloadJson);
            if (!doc.RootElement.TryGetProperty("supplier", out var supplier) || supplier.ValueKind != JsonValueKind.Object)
                return (null, null);
            return (Str(supplier, "refEntId"), Str(supplier, "entId"));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static string BillTime(HeadRow head)
    {
        var raw = ReadString(head.PartPayloadJson, "businessDateTimeUtc") ?? ReadString(head.TaskPayloadJson, "businessDateTimeUtc");
        if (raw is not null && DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            return parsed.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        return DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    // ---------------------------------------------------------------- 短事务（HTTP 之前）

    private async Task<(long AttemptId, int AttemptNo)?> OpenAttemptAsync(HeadRow head, string workerId,
        long fencingToken, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_conn);
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        // 竞态守卫（与核验重算/行政取消并发）：同一事务内重读任务与分片并复核同一条件。
        var taskState = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(@"
SELECT state FROM dbo.upload_task WITH (UPDLOCK, HOLDLOCK) WHERE id=@id", new { id = head.TaskId }, tx, cancellationToken: ct));
        var partState = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(@"
SELECT state FROM dbo.upload_part WITH (UPDLOCK, HOLDLOCK) WHERE id=@id", new { id = head.PartId }, tx, cancellationToken: ct));
        if (taskState is null || partState is null
            || taskState == nameof(UploadState.Cancelled) || partState == nameof(UploadState.Cancelled)
            || !IsSendablePart(partState) || !IsDispatchableTask(taskState))
        {
            await tx.RollbackAsync(ct);
            return null;
        }

        var attempt = await conn.QuerySingleAsync<AttemptRow>(new CommandDefinition(@"
INSERT INTO dbo.upload_attempt(task_id,part_id,attempt_no,attempt_kind,env,lease_owner,fencing_token)
OUTPUT inserted.id AS Id, inserted.attempt_no AS AttemptNo
SELECT @taskId,@partId,ISNULL(MAX(attempt_no),0)+1,N'UPLOAD',@env,@workerId,@fencingToken
FROM dbo.upload_attempt WITH (UPDLOCK, HOLDLOCK)
WHERE task_id=@taskId AND attempt_kind=N'UPLOAD'",
            new { taskId = head.TaskId, partId = head.PartId, env = head.Env, workerId, fencingToken },
            tx, cancellationToken: ct));

        var moved = await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.upload_part
SET state=N'Dispatching', first_sent_at=COALESCE(first_sent_at,SYSUTCDATETIME())
WHERE id=@partId AND state IN (N'Ready',N'RetryWait')", new { partId = head.PartId }, tx, cancellationToken: ct));
        if (moved != 1)
        {
            await tx.RollbackAsync(ct);
            return null;
        }
        await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.upload_task SET state=N'Dispatching', version=version+1
WHERE id=@taskId AND state IN (N'Ready',N'RetryWait',N'Dispatching')", new { taskId = head.TaskId }, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return (attempt.Id, attempt.AttemptNo);
    }

    // ---------------------------------------------------------------- 迟到写回：只留证

    private async Task AppendStaleEvidenceAsync(AttemptReceipt receipt, CancellationToken ct)
    {
        var evidence = TopResponseSanitizer.Sanitize(SerializeOutcome(receipt.Outcome));
        var attemptNo = await NextAttemptNoAsync(receipt.TaskId, ct);
        try
        {
            await _recovery.AppendStaleEvidenceAsync(receipt.TaskId, receipt.PartId, attemptNo, "UPLOAD",
                receipt.FencingToken, evidence, ct);
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            // 与并发留证撞 attempt_no：顺延一号重试一次（证据不得因唯一约束丢失）。
            await _recovery.AppendStaleEvidenceAsync(receipt.TaskId, receipt.PartId, attemptNo + 1, "UPLOAD",
                receipt.FencingToken, evidence, ct);
        }
    }

    private async Task<int> NextAttemptNoAsync(long taskId, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_conn);
        await conn.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(@"
SELECT ISNULL(MAX(attempt_no),0)+1 FROM dbo.upload_attempt WHERE task_id=@taskId AND attempt_kind=N'UPLOAD'",
            new { taskId }, cancellationToken: ct));
    }

    // ---------------------------------------------------------------- 任务级聚合

    /// <summary>可结算分片态：这些态已表达"该分片本轮发送的最终结论"，可直接参与严重度聚合。</summary>
    private static bool IsSettleable(UploadState state)
        => state is UploadState.Succeeded or UploadState.Failed or UploadState.PartialSuccess
            or UploadState.ResultUnresolved or UploadState.AcceptanceUnknown or UploadState.ManualAction;

    /// <summary>
    /// 分片状态 → 任务状态的唯一规则（严重度表本身仍由 <see cref="TaskStateAggregator"/> 持有，顺序不变）。
    /// <list type="number">
    /// <item>Cancelled 分片不参与聚合；全 Cancelled → 任务 Cancelled。</item>
    /// <item>任一非 Cancelled 分片不可结算时先归一：<c>Succeeded</c> → <c>Accepted</c>（回执受理≠逐码成功），
    /// <c>Ready/RetryWait/Blocked</c> → <c>Dispatching</c>（仍在推进，任务不得提前落定），再聚合。</item>
    /// <item>全部可结算时按原状态聚合。</item>
    /// </list>
    /// </summary>
    public static UploadState AggregateTaskState(IReadOnlyCollection<UploadState> partStates)
    {
        ArgumentNullException.ThrowIfNull(partStates);
        if (partStates.Count == 0)
            throw new ArgumentException("分片状态集合不能为空", nameof(partStates));

        var active = partStates.Where(s => s != UploadState.Cancelled).ToList();
        if (active.Count == 0) return UploadState.Cancelled;

        if (active.Any(s => !IsSettleable(s)))
            active = active.Select(s => s switch
            {
                UploadState.Succeeded => UploadState.Accepted,
                UploadState.Ready or UploadState.RetryWait or UploadState.Blocked => UploadState.Dispatching,
                _ => s,
            }).ToList();

        return TaskStateAggregator.Aggregate(active);
    }

    /// <summary>
    /// 任务状态重算：绝不跳过写入（否则更坏的分片结论已知后任务会滞留在 Dispatching）。
    /// 守卫：除行政取消（Cancelled）外一律写入——分片独立收尾，任务不得因停留在 AcceptanceUnknown/
    /// ManualAction 等中间态而永远无法被聚合结果更新；全 Cancelled 的取消结论始终可写（终态幂等）。
    /// 并发取消已提交时，UPDATE 在守卫内重读 state，不会把已取消任务复活。
    /// 供回查（<see cref="BillQueryService"/>）复用，不得复制粘贴第二份聚合写入逻辑。
    /// </summary>
    internal static async Task<int> RecomputeTaskStateAsync(SqlConnection conn, SqlTransaction tx, long taskId, CancellationToken ct)
    {
        var states = (await conn.QueryAsync<string>(new CommandDefinition(
            "SELECT state FROM dbo.upload_part WHERE task_id=@taskId", new { taskId }, tx, cancellationToken: ct)))
            .Select(s => Enum.Parse<UploadState>(s, ignoreCase: false)).ToList();
        if (states.Count == 0) return 0;

        var aggregate = AggregateTaskState(states);
        return await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.upload_task SET state=@state, version=version+1
WHERE id=@taskId
  AND (state <> N'Cancelled' OR @state = N'Cancelled')",
            new { taskId, state = aggregate.ToString() }, tx, cancellationToken: ct));
    }

    private static Task<long> OrgIdAsync(SqlConnection conn, SqlTransaction tx, long taskId, CancellationToken ct)
        => conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT org_id FROM dbo.upload_task WHERE id=@taskId", new { taskId }, tx, cancellationToken: ct));

    // ---------------------------------------------------------------- 映射与证据

    private static (UploadEvent Event, DispatchOutcome Outcome) Map(TopOutcomeKind kind) => kind switch
    {
        TopOutcomeKind.Success => (UploadEvent.ReceiptAccepted, DispatchOutcome.Accepted),
        TopOutcomeKind.PartialEvidence => (UploadEvent.PartialEvidence, DispatchOutcome.Accepted),
        TopOutcomeKind.BusinessRejected => (UploadEvent.Rejected, DispatchOutcome.Failed),
        TopOutcomeKind.DuplicateBillCode => (UploadEvent.DuplicateBillCode, DispatchOutcome.AcceptanceUnknown),
        TopOutcomeKind.RateLimited => (UploadEvent.TransportUncertain, DispatchOutcome.RateLimited),
        TopOutcomeKind.AuthError => (UploadEvent.TransportUncertain, DispatchOutcome.AuthPaused),
        // TransportUncertain / SystemError：受理状态未知，绝不当成功或失败。
        _ => (UploadEvent.TransportUncertain, DispatchOutcome.AcceptanceUnknown),
    };

    private static TimeSpan? RescheduleDelay(TopOutcome outcome) => outcome.Kind switch
    {
        // 平台明确等待秒数优先；否则 30s×2^n 退避（JOB-03）。
        TopOutcomeKind.RateLimited => TimeSpan.FromSeconds(
            Math.Max(outcome.RetryAfterSeconds ?? 0, (int)RateBudgetService.Backoff(0).TotalSeconds)),
        TopOutcomeKind.AuthError => AuthPauseDelay,
        _ => null,
    };

    private static int? HttpStatusFor(TopOutcomeKind kind) => kind switch
    {
        // 传输失败与本地限流/暂停未取得任何 HTTP 状态；其余（含解析失败）均已收到 HTTP 200 信封。
        TopOutcomeKind.TransportUncertain or TopOutcomeKind.RateLimited or TopOutcomeKind.AuthError => null,
        _ => 200,
    };

    private static string? ErrorKind(TopOutcomeKind kind) => kind switch
    {
        TopOutcomeKind.Success or TopOutcomeKind.PartialEvidence => null,
        TopOutcomeKind.BusinessRejected => "BUSINESS_REJECTED",
        TopOutcomeKind.DuplicateBillCode => "DUPLICATE_BILL_CODE",
        TopOutcomeKind.RateLimited => "RATE_LIMITED",
        TopOutcomeKind.AuthError => "AUTH_ERROR",
        TopOutcomeKind.SystemError => "SYSTEM_ERROR",
        _ => "TRANSPORT_UNCERTAIN",
    };

    /// <summary>回执证据摘要（脱敏后落 raw_response）：只保存结论要素与 resultJson，不保存请求参数/凭证。</summary>
    private static string SerializeOutcome(TopOutcome outcome)
    {
        object? result = null;
        if (outcome.ResultJson is not null)
        {
            try { result = JsonSerializer.Deserialize<JsonElement>(outcome.ResultJson); }
            catch (JsonException) { result = outcome.ResultJson; }
        }
        return JsonSerializer.Serialize(new
        {
            kind = outcome.Kind.ToString(),
            code = outcome.Code,
            subCode = outcome.SubCode,
            message = outcome.Message,
            requestId = outcome.RequestId,
            retryAfterSeconds = outcome.RetryAfterSeconds,
            result,
        });
    }

    private static string? ExtractPlatformTaskId(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(resultJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var direct = Str(root, "task_id");
            if (direct is not null) return direct;
            foreach (var name in new[] { "model", "result" })
                if (root.TryGetProperty(name, out var nested) && nested.ValueKind == JsonValueKind.Object
                    && Str(nested, "task_id") is string nestedId)
                    return nestedId;
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(string? payloadJson, string name)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? Str(doc.RootElement, name) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement element, string name)
        => element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool IsSendablePart(string state) => state is "Ready" or "RetryWait";

    /// <summary>
    /// 任务级可发送判定：只拒绝已落定/需人工/已取消的任务（Cancelled/Blocked/Succeeded/Failed/ManualAction）；
    /// 其余中间态（含 AcceptanceUnknown/Processing/PartialSuccess/ResultUnresolved）不阻断无关分片发送——
    /// 分片是独立发送单元（§2.6/§3.2），真正的逐分片闸门是 <see cref="IsSendablePart"/> 与短事务内的重读复核。
    /// </summary>
    private static bool IsDispatchableTask(string state)
        => state is not ("Cancelled" or "Blocked" or "Succeeded" or "Failed" or "ManualAction");

    private static string IntentFor(long partId) => "part:" + partId.ToString(CultureInfo.InvariantCulture);

    private sealed class HeadRow
    {
        public long PartId { get; set; }
        public long TaskId { get; set; }
        public long OrgId { get; set; }
        public string Env { get; set; } = "";
        public int PartIndex { get; set; }
        public string BillCode { get; set; } = "";
        public string PartPayloadJson { get; set; } = "{}";
        public string PartState { get; set; } = "";
        public string TaskType { get; set; } = "";
        public string TaskState { get; set; } = "";
        public string? TaskPayloadJson { get; set; }
        public string? OrgRefEntId { get; set; }
        public string? OrgEntId { get; set; }
    }

    private sealed class AttemptRow
    {
        public long Id { get; set; }
        public int AttemptNo { get; set; }
    }
}