using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Domain.Codes;
using TraceFlow.Domain.Top;
using TraceFlow.Domain.Uploads;
using TraceFlow.Infrastructure.Codes;
using TraceFlow.Infrastructure.Sales;
using TraceFlow.Infrastructure.Settings;
using TraceFlow.Infrastructure.Top;

namespace TraceFlow.Infrastructure.Uploads;

/// <summary>单次回查的结论（处理器只做完成/不改期判定，改期一律在服务内落库）。</summary>
public enum QueryOutcome { Skipped, Processing, Succeeded, Partial, Failed, Unresolved, Escalated, RateLimited, AuthPaused }

/// <summary>
/// billstatus 回查（TOP-02/03/04、JOB-04）：按分片回查平台处理结论，逐码写 <c>code_result</c>（SUCCESS/FAILED/UNKNOWN），
/// 分片状态只经 <see cref="UploadStateMachine"/> 推进，任务状态复用发送侧的聚合重算。
/// <list type="number">
/// <item>读分片：只有 Dispatching/Accepted/Processing/AcceptanceUnknown/PartialSuccess/ResultUnresolved 才回查；
/// 终态/取消直接返回（≤1 次读，零写入）。</item>
/// <item>短事务：写 <c>upload_attempt(attempt_kind='QUERY', attempt_no=max+1)</c> 并提交——查询超时只影响查询，
/// 不改变受理状态（HTTP 在事务外）。</item>
/// <item>调 <c>query.billstatus</c>；平台限流/通道暂停不改受理状态，只按退避重排回查作业。</item>
/// <item>T3 守卫（同发送侧）：任务非 Cancelled、分片仍可回查、回查作业仍由本 Worker 持租且 fencing token 未变。</item>
/// <item>写逐码结论（分片载荷内无平台状态的码一律 UNKNOWN，绝不静默成功）→ <c>query_count+1</c> → 状态机推进 →
/// 任务聚合重算；全部成功且任务 Succeeded 时唤醒依赖该码的 sale-verify。</item>
/// <item>结论先行：先应用任何合法结论转换（AllSuccess/Partial/Failed/Processing/Unresolved），再判升级——
/// 迟到的成功结论永远胜过人工升级。仅当结论仍把分片留在等待态（处理中/未决/状态未变）且达到
/// <c>top.query.maxAttempts</c> 或 <c>top.query.manualAfterHours</c> → ManualAction +
/// <c>reconciliation_difference(ACCEPTANCE_UNKNOWN)</c>；状态机无升级边时停回查（不再重排）但仍落差异行。</item>
/// </list>
/// </summary>
public sealed class BillQueryService
{
    public const string JobKind = UploadDispatchService.QueryJobKind;
    /// <summary>TOP billstatus 方法名（G-TOP 前为候选合同，MOCK 由 Fake 网关应答）。</summary>
    public const string BillStatusMethod = "alibaba.alihealth.drugtrace.top.lsyd.query.billstatus";
    public const string PartIntentPrefix = "query:part:";
    public const string ScanIntentPrefix = "query:scan:";
    /// <summary>
    /// 周期扫描单批上限。扫描作业租约 5 分钟且不续期：每个分片要串行完成一次 TOP HTTP 回查，
    /// 50 片会在 <c>RecoverExpiredLeasesAsync</c> 收租后中途被抢走，故一批最多回查 10 个分片
    /// （每片一次同步 HTTP，10 片留足余量；不足量由下一轮扫描兜底，绝不扩租约）。
    /// </summary>
    public const int ScanLimit = 10;

    private const int ReasonMaxChars = 500;
    private const int EvidenceMaxChars = 4000;
    private const int BackoffCapSeconds = 600;
    private static readonly TimeSpan AuthPauseDelay = TimeSpan.FromMinutes(5);

    private static readonly string[] QueryableStates =
    {
        nameof(UploadState.Dispatching), nameof(UploadState.Accepted), nameof(UploadState.Processing),
        nameof(UploadState.AcceptanceUnknown), nameof(UploadState.PartialSuccess), nameof(UploadState.ResultUnresolved),
    };

    private readonly string _cs;
    private readonly SettingsService _settings;
    private readonly TopClientService _top;
    private readonly IJobQueue _queue;
    private readonly SaleVerificationService _sales;
    private readonly IAuditLog _audit;

    public BillQueryService(string connectionString, SettingsService settings, TopClientService top, IJobQueue queue,
        SaleVerificationService sales, IAuditLog audit)
    {
        _cs = connectionString;
        _settings = settings;
        _top = top;
        _queue = queue;
        _sales = sales;
        _audit = audit;
    }

    /// <summary>分片回查作业意图键（与发送侧入队键一致：<c>query:part:{id}</c>）。</summary>
    public static string PartIntent(long partId) => PartIntentPrefix + partId.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// 退避：<c>min(backoffSeconds × 2^min(priorQueryCount,5), 600)</c> + 0..5s 抖动（策略值由 settings 提供，不硬编码）。
    /// </summary>
    public static TimeSpan Backoff(int priorQueryCount, int baseSeconds, int jitterSeconds)
        => TimeSpan.FromSeconds(Math.Min(BackoffCapSeconds,
            baseSeconds * Math.Pow(2, Math.Min(Math.Max(priorQueryCount, 0), 5))) + jitterSeconds);

    public static TimeSpan Backoff(int priorQueryCount, int baseSeconds)
        => Backoff(priorQueryCount, baseSeconds, Random.Shared.Next(0, 6));

    /// <summary>回查一个分片（per-part 作业入口）。</summary>
    public Task<QueryOutcome> QueryPartAsync(long partId, string workerId, long fencingToken, CancellationToken ct)
        => QueryPartCoreAsync(partId, workerId, fencingToken, PartIntent(partId), ct);

    /// <summary>周期扫描兜底：以扫描作业的租约身份回查到期分片（逐分片仍走同一路径）。</summary>
    internal Task<QueryOutcome> QueryPartFromScanAsync(long partId, string workerId, long fencingToken,
        string scanIntent, CancellationToken ct)
        => QueryPartCoreAsync(partId, workerId, fencingToken, scanIntent, ct);

    /// <summary>
    /// 扫描选片（兜底，只挑没有待执行 per-part 回查作业的分片，避免双查）：
    /// <list type="number">
    /// <item>到期分片：<c>next_query_at &lt;= now</c>。</item>
    /// <item>从未排期的盲区分片：<c>next_query_at IS NULL</c> 且处于受理侧状态（Accepted/Dispatching/AcceptanceUnknown）
    /// 且首次发送已超过 5 分钟——受理后回查作业丢失（如入队失败/租约回收丢意图）时由扫描接管（M2）。</item>
    /// </list>
    /// 到期分片优先；批次同样受 <paramref name="limit"/> 约束。
    /// </summary>
    public async Task<IReadOnlyList<long>> SelectDuePartIdsAsync(string? env, int limit, CancellationToken ct)
    {
        if (limit <= 0) return Array.Empty<long>();
        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        return (await conn.QueryAsync<long>(new CommandDefinition(@"
SELECT TOP (@limit) p.id
FROM dbo.upload_part p
JOIN dbo.upload_task t ON t.id = p.task_id
WHERE p.state IN (N'Dispatching',N'Accepted',N'Processing',N'AcceptanceUnknown',N'PartialSuccess',N'ResultUnresolved')
  AND (
    (p.next_query_at IS NOT NULL AND p.next_query_at <= SYSUTCDATETIME())
    OR (p.next_query_at IS NULL
        AND p.state IN (N'Accepted',N'Dispatching',N'AcceptanceUnknown')
        AND p.first_sent_at IS NOT NULL
        AND p.first_sent_at < DATEADD(minute,-5,SYSUTCDATETIME()))
  )
  AND t.state <> N'Cancelled'
  AND (@env IS NULL OR p.env = @env)
  AND NOT EXISTS (
    SELECT 1 FROM dbo.outbox_job j
    WHERE j.job_kind = N'bill-query' AND j.job_state IN (N'PENDING',N'RUNNING')
      AND j.intent_key = N'query:part:' + CAST(p.id AS nvarchar(20)))
ORDER BY CASE WHEN p.next_query_at IS NULL THEN 1 ELSE 0 END, p.next_query_at, p.id",
            new { limit, env }, cancellationToken: ct))).ToList();
    }

    // ---------------------------------------------------------------- 主体

    private async Task<QueryOutcome> QueryPartCoreAsync(long partId, string workerId, long fencingToken,
        string guardIntent, CancellationToken ct)
    {
        var policy = await _settings.GetQueryPolicyAsync(ct);
        var head = await ReadHeadAsync(partId, ct);
        if (head is null || !IsQueryablePart(head.PartState) || head.TaskState == nameof(UploadState.Cancelled))
            return QueryOutcome.Skipped;

        var attempt = await OpenQueryAttemptAsync(head, workerId, fencingToken, guardIntent, ct);
        if (attempt is null) return QueryOutcome.Skipped;

        TopOutcome outcome;
        try
        {
            outcome = await _top.CallAsync(head.Env, BillStatusMethod, QueryParameters(head), head.OrgRefEntId, workerId, ct);
        }
        catch (TopRateLimitedException ex)
        {
            outcome = new TopOutcome(TopOutcomeKind.RateLimited, null, null, ex.Message, null, null,
                (int)Math.Ceiling(ex.Wait.TotalSeconds));
        }
        catch (ChannelPausedException ex)
        {
            outcome = new TopOutcome(TopOutcomeKind.AuthError, null, null, ex.Message, null, null, null);
        }
        catch (TopNotCertifiedException ex)
        {
            // LIVE fail-closed：真实通道未取证时网关直接抛错。先收尾本次查询尝试再抛出（作业由 Worker 退避重排），
            // 绝不留下 finished_at 为空的尝试行在循环里反复累积。
            await CloseAttemptOnExceptionAsync(attempt.AttemptId, "NOT_CERTIFIED", ex);
            throw;
        }
        catch (OperationCanceledException ex)
        {
            await CloseAttemptOnExceptionAsync(attempt.AttemptId, "CANCELLED", ex);
            throw;
        }
        catch (Exception ex)
        {
            await CloseAttemptOnExceptionAsync(attempt.AttemptId, ex is SqlException ? "DB_ERROR" : "UNEXPECTED_ERROR", ex);
            throw;
        }

        return await ApplyAsync(head, attempt, outcome, workerId, fencingToken, guardIntent, policy, ct);
    }

    private static IReadOnlyDictionary<string, string> QueryParameters(HeadRow head)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal) { ["bill_code"] = head.BillCode };
        if (!string.IsNullOrWhiteSpace(head.OrgRefEntId)) parameters["ref_ent_id"] = head.OrgRefEntId!;
        return parameters;
    }

    private async Task<HeadRow?> ReadHeadAsync(long partId, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<HeadRow>(new CommandDefinition(@"
SELECT p.id AS PartId, p.task_id AS TaskId, p.org_id AS OrgId, p.env AS Env, p.bill_code AS BillCode,
       p.payload_json AS PartPayloadJson, p.state AS PartState, p.query_count AS QueryCount,
       p.first_sent_at AS FirstSentAt, t.state AS TaskState, t.task_type AS TaskType,
       o.ref_ent_id AS OrgRefEntId
FROM dbo.upload_part p
JOIN dbo.upload_task t ON t.id = p.task_id
LEFT JOIN dbo.organization o ON o.id = p.org_id
WHERE p.id = @partId", new { partId }, cancellationToken: ct));
    }

    // ---------------------------------------------------------------- 短事务：先落查询尝试

    /// <summary>查询尝试先记账再发 HTTP（查询超时只影响查询，不改变受理状态）。</summary>
    private async Task<AttemptRow?> OpenQueryAttemptAsync(HeadRow head, string workerId, long fencingToken,
        string guardIntent, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        // 与发送/重算同一加锁顺序：任务 → 分片；再复核作业租约。
        var taskState = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT state FROM dbo.upload_task WITH (UPDLOCK, HOLDLOCK) WHERE id=@id", new { id = head.TaskId },
            tx, cancellationToken: ct));
        var partState = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT state FROM dbo.upload_part WITH (UPDLOCK, HOLDLOCK) WHERE id=@id", new { id = head.PartId },
            tx, cancellationToken: ct));
        var leaseAlive = await conn.ExecuteScalarAsync<int>(new CommandDefinition(@"
SELECT CASE WHEN EXISTS (
  SELECT 1 FROM dbo.outbox_job WITH (UPDLOCK, HOLDLOCK)
  WHERE job_kind=N'bill-query' AND intent_key=@intent AND job_state=N'RUNNING'
    AND lease_owner=@worker AND fencing_token=@token
) THEN 1 ELSE 0 END", new { intent = guardIntent, worker = workerId, token = fencingToken },
            tx, cancellationToken: ct));

        if (taskState is null || partState is null || taskState == nameof(UploadState.Cancelled)
            || !IsQueryablePart(partState) || leaseAlive != 1)
        {
            await tx.RollbackAsync(ct);
            return null;
        }

        var attempt = await conn.QuerySingleAsync<AttemptRow>(new CommandDefinition(@"
INSERT INTO dbo.upload_attempt(task_id,part_id,attempt_no,attempt_kind,env,lease_owner,fencing_token)
OUTPUT inserted.id AS AttemptId, inserted.attempt_no AS AttemptNo
SELECT @taskId,@partId,ISNULL(MAX(attempt_no),0)+1,N'QUERY',@env,@workerId,@fencingToken
FROM dbo.upload_attempt WITH (UPDLOCK, HOLDLOCK)
WHERE task_id=@taskId AND attempt_kind=N'QUERY'",
            new { taskId = head.TaskId, partId = head.PartId, env = head.Env, workerId, fencingToken },
            tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return attempt;
    }

    // ---------------------------------------------------------------- T3 守卫写回

    private async Task<QueryOutcome> ApplyAsync(HeadRow head, AttemptRow attempt, TopOutcome outcome,
        string workerId, long fencingToken, string guardIntent, QueryPolicy policy, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var rateLimited = outcome.Kind == TopOutcomeKind.RateLimited;
        var authPaused = outcome.Kind == TopOutcomeKind.AuthError;
        var parsed = outcome.Kind is TopOutcomeKind.Success or TopOutcomeKind.PartialEvidence
            ? BillStatusParser.Parse(outcome.ResultJson)
            : null;
        var platformError = parsed is null && !rateLimited && !authPaused ? outcome.Kind.ToString() : null;
        // 限流/授权暂停：请求可能未发出，不写逐码结论、不消耗查询次数（只重排回查）。
        var counted = !rateLimited && !authPaused;
        var priorCount = head.QueryCount;
        var attemptCount = counted ? priorCount + 1 : priorCount;

        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        var taskState = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT state FROM dbo.upload_task WITH (UPDLOCK) WHERE id=@id", new { id = head.TaskId },
            tx, cancellationToken: ct));
        var partState = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT state FROM dbo.upload_part WITH (UPDLOCK) WHERE id=@id", new { id = head.PartId },
            tx, cancellationToken: ct));
        var leaseAlive = await conn.ExecuteScalarAsync<int>(new CommandDefinition(@"
SELECT CASE WHEN EXISTS (
  SELECT 1 FROM dbo.outbox_job WITH (UPDLOCK, HOLDLOCK)
  WHERE job_kind=N'bill-query' AND intent_key=@intent AND job_state=N'RUNNING'
    AND lease_owner=@worker AND fencing_token=@token
) THEN 1 ELSE 0 END", new { intent = guardIntent, worker = workerId, token = fencingToken },
            tx, cancellationToken: ct));

        if (taskState is null || partState is null || taskState == nameof(UploadState.Cancelled)
            || !IsQueryablePart(partState) || leaseAlive != 1)
        {
            // 迟到/失租写回：只收尾本行查询尝试，不推进任何状态、不写逐码结论。
            await tx.RollbackAsync(ct);
            await FinishStaleAttemptAsync(attempt.AttemptId, outcome, ct);
            return QueryOutcome.Skipped;
        }

        var current = Enum.Parse<UploadState>(partState, ignoreCase: false);
        var (codeGroups, payloadIncomplete) = BuildCodeStatuses(head, parsed, platformError);
        // result_type=2 只代表平台整单判成功；分片载荷里若有平台未给结论的码，证据不足不得升级（绝不静默成功）。
        if (parsed is not null && parsed.Kind == BillStatusKind.AllSuccess && payloadIncomplete)
            parsed = parsed with
            {
                Kind = BillStatusKind.Partial,
                Note = "result_type=2 但分片载荷存在无平台结论的码：按部分成功处理，绝不升级成功",
            };

        await FinishAttemptAsync(conn, tx, attempt.AttemptId, outcome, parsed, ct);

        var codeIds = new List<long>();
        if (counted)
        {
            foreach (var (code, status) in codeGroups)
            {
                var stored = await TraceCodeStore.EnsureAsync(conn, tx, code, ct);
                codeIds.Add(stored.Id);
                await conn.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.code_result(task_id,part_id,code_id,result,reason,platform_evidence,attempt_id)
VALUES(@taskId,@partId,@codeId,@result,@reason,@evidence,@attemptId)",
                    new
                    {
                        taskId = head.TaskId, partId = head.PartId, codeId = stored.Id, result = status.Result,
                        reason = Truncate(status.Reason, ReasonMaxChars), evidence = CodeEvidence(parsed),
                        attemptId = attempt.AttemptId,
                    }, tx, cancellationToken: ct));
            }
        }

        var isSettled = parsed?.Kind is BillStatusKind.AllSuccess or BillStatusKind.Partial or BillStatusKind.Failed;
        var retryDelay = RetryDelay(outcome, priorCount, policy.BackoffSeconds);
        var nextQueryAt = isSettled ? (DateTime?)null : now.Add(retryDelay);

        if (counted)
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE dbo.upload_part SET query_count=query_count+1, next_query_at=@nextQueryAt WHERE id=@partId",
                new { partId = head.PartId, nextQueryAt }, tx, cancellationToken: ct));

        // 先应用任何合法结论转换——无论累计查询次数是否已到上限：迟到的 AllSuccess/Partial/Failed/Processing
        // 都必须先落地（否则一旦 attempts≥max 后续所有结论都会被丢弃，分片永久冻结在原状态）。H1。
        var expired = counted && IsObservationExpired(head, attemptCount, policy, now);
        var evt = parsed?.Kind switch
        {
            BillStatusKind.AllSuccess => UploadEvent.QueryAllSuccess,
            BillStatusKind.Partial => UploadEvent.QueryPartial,
            BillStatusKind.Failed => UploadEvent.QueryFailed,
            BillStatusKind.Processing => UploadEvent.QueryProcessing,
            BillStatusKind.Unresolved => UploadEvent.QueryUnresolved,
            _ => (UploadEvent?)null,
        };

        var applied = false;
        var next = current;
        if (evt is UploadEvent e)
        {
            if (UploadStateMachine.TryGetNext(current, e, out var candidate))
            {
                var rows = await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE dbo.upload_part SET state=@next WHERE id=@partId AND state=@current",
                    new { next = candidate.ToString(), partId = head.PartId, current = current.ToString() },
                    tx, cancellationToken: ct));
                applied = rows == 1;
                if (applied) next = candidate;
            }
            else if (!expired)
            {
                // 非法转换只告警不改状态（如 Dispatching 分片：先由发送恢复收敛为受理未知）。
                // 已决定升级人工时不再报"非法转换"，避免把正常升级误标为异常。
                await _audit.AppendAsync(conn, tx, new AuditEntry("SYSTEM", "Worker", "bill.query.illegal_transition",
                    "upload_part", head.PartId.ToString(CultureInfo.InvariantCulture), $"{current}->{e}", null,
                    JsonSerializer.Serialize(new { from = current.ToString(), attempt = e.ToString(), kind = parsed?.Kind }),
                    outcome.RequestId, null, head.Env));
            }
        }

        if (applied)
        {
            await UploadDispatchService.RecomputeTaskStateAsync(conn, tx, head.TaskId, ct);
            if (next == UploadState.Succeeded)
                await EnqueueDependentVerificationsAsync(conn, tx, head, codeIds, ct);
        }

        // 只有当结论把分片留在非终态等待（处理中/未决/状态未变）且观察窗已过期时才升级人工：
        // 已落地的成功/部分/失败结论永远优先于升级。H1。
        if (!isSettled && expired)
            return await EscalateAsync(conn, tx, head, next, attempt.AttemptId, parsed, platformError,
                attemptCount, policy, codeIds, ct);

        if (!isSettled)
            await ScheduleRetryAsync(conn, tx, head, guardIntent, workerId, fencingToken, nextQueryAt!.Value,
                parsed?.Note ?? platformError, ct);

        await tx.CommitAsync(ct);
        return QueryOutcomeFor(parsed);
    }

    // ---------------------------------------------------------------- 人工升级

    private async Task<QueryOutcome> EscalateAsync(SqlConnection conn, SqlTransaction tx, HeadRow head,
        UploadState current, long attemptId, BillStatusParseResult? parsed, string? platformError, int attemptCount,
        QueryPolicy policy, IReadOnlyList<long> codeIds, CancellationToken ct)
    {
        // 观察窗到期优先走 ObservationExpired；状态机无该边的中间态（AcceptanceUnknown/PartialSuccess）
        // 走 ManualEscalate——两条边都只通向 ManualAction，状态机仍是唯一权威。
        UploadEvent? escalateEvent = null;
        var next = current;
        if (UploadStateMachine.TryGetNext(current, UploadEvent.ObservationExpired, out var viaExpired))
        {
            escalateEvent = UploadEvent.ObservationExpired;
            next = viaExpired;
        }
        else if (UploadStateMachine.TryGetNext(current, UploadEvent.ManualEscalate, out var viaManual))
        {
            escalateEvent = UploadEvent.ManualEscalate;
            next = viaManual;
        }

        if (escalateEvent is null)
        {
            // 兜底（正常不可达：所有可回查状态都已有到 ManualAction 的边）：绝不无限重排——
            // 停回查（next_query_at=NULL）、撤掉待执行的 per-part 回查作业，但仍落复核对账差异，让分片浮到人工面前。
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE dbo.upload_part SET next_query_at=NULL WHERE id=@partId",
                new { partId = head.PartId }, tx, cancellationToken: ct));
            await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.outbox_job
SET job_state=N'DONE', finished_at=SYSUTCDATETIME(), lease_owner=NULL, lease_until=NULL
WHERE org_id=@orgId AND env=@env AND job_kind=N'bill-query' AND intent_key=@intent AND job_state=N'PENDING'",
                new { orgId = head.OrgId, env = head.Env, intent = PartIntent(head.PartId) }, tx, cancellationToken: ct));

            var fallbackRunId = await InsertEscalationRunAsync(conn, tx, head, attemptCount, policy, parsed,
                platformError, ct);
            await InsertEscalationDifferenceAsync(conn, tx, head, fallbackRunId, attemptCount, policy, parsed,
                platformError, ct);
            await _audit.AppendAsync(conn, tx, new AuditEntry("SYSTEM", "Worker", "bill.query.escalation_unavailable",
                "upload_part", head.PartId.ToString(CultureInfo.InvariantCulture), "OBSERVATION_EXPIRED", null,
                JsonSerializer.Serialize(new
                {
                    from = current.ToString(), attemptCount, maxAttempts = policy.MaxAttempts,
                    outcome = "STOPPED_RETRYING_DIFFERENCE_RAISED",
                }), null, null, head.Env));
            await tx.CommitAsync(ct);
            return QueryOutcome.Unresolved;
        }

        var rows = await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.upload_part SET state=@next, next_query_at=NULL WHERE id=@partId AND state=@current",
            new { next = next.ToString(), partId = head.PartId, current = current.ToString() }, tx, cancellationToken: ct));
        if (rows != 1)
        {
            await tx.RollbackAsync(ct);
            return QueryOutcome.Skipped;
        }

        await UploadDispatchService.RecomputeTaskStateAsync(conn, tx, head.TaskId, ct);
        var runId = await InsertEscalationRunAsync(conn, tx, head, attemptCount, policy, parsed, platformError, ct);
        await InsertEscalationDifferenceAsync(conn, tx, head, runId, attemptCount, policy, parsed, platformError, ct);

        if (codeIds.Count > 0)
            await _sales.EnqueueDependentVerificationsAsync(conn, tx, codeIds, head.OrgId, head.Env, ct);

        await _audit.AppendAsync(conn, tx, new AuditEntry("SYSTEM", "Worker", "bill.query.manual_escalated", "upload_part",
            head.PartId.ToString(CultureInfo.InvariantCulture), "OBSERVATION_EXPIRED", null,
            JsonSerializer.Serialize(new
            {
                from = current.ToString(), to = next.ToString(), attempt = escalateEvent.ToString(), attemptId,
                attemptCount, maxAttempts = policy.MaxAttempts, manualAfterHours = policy.ManualAfterHours,
                kind = parsed?.Kind.ToString(), platformError,
            }), null, null, head.Env));

        await tx.CommitAsync(ct);
        return QueryOutcome.Escalated;
    }

    private static async Task<long> InsertEscalationRunAsync(SqlConnection conn, SqlTransaction tx, HeadRow head,
        int attemptCount, QueryPolicy policy, BillStatusParseResult? parsed, string? platformError, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var start = head.FirstSentAt ?? now;
        var summary = TopResponseSanitizer.Sanitize(JsonSerializer.Serialize(new
        {
            reason = "ACCEPTANCE_UNKNOWN",
            partId = head.PartId, taskId = head.TaskId, billCode = head.BillCode,
            queryCount = attemptCount, maxAttempts = policy.MaxAttempts, manualAfterHours = policy.ManualAfterHours,
            firstSentAt = head.FirstSentAt, elapsedHours = Math.Round((now - start).TotalHours, 3),
            lastKind = parsed?.Kind.ToString(), note = parsed?.Note, platformError,
        }))!;
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition(@"
INSERT INTO dbo.reconciliation_run(org_id,env,window_start,window_end,business_type,status,local_summary,finished_at)
OUTPUT inserted.id
VALUES(@orgId,@env,@start,@end,@businessType,N'COMPLETE',@summary,SYSUTCDATETIME())",
            new
            {
                orgId = head.OrgId, env = head.Env, start, end = now,
                businessType = BusinessType(head.TaskType), summary,
            }, tx, cancellationToken: ct));
    }

    private static Task<int> InsertEscalationDifferenceAsync(SqlConnection conn, SqlTransaction tx, HeadRow head,
        long runId, int attemptCount, QueryPolicy policy, BillStatusParseResult? parsed, string? platformError,
        CancellationToken ct)
    {
        var evidence = TopResponseSanitizer.Sanitize(JsonSerializer.Serialize(new
        {
            partId = head.PartId, taskId = head.TaskId, billCode = head.BillCode, queryCount = attemptCount,
            maxAttempts = policy.MaxAttempts, manualAfterHours = policy.ManualAfterHours,
            lastKind = parsed?.Kind.ToString(), note = parsed?.Note, platformError,
        }))!;
        return conn.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.reconciliation_difference(run_id,diff_type,severity,object_ref,evidence_json,status,owner_role,next_action)
VALUES(@runId,N'ACCEPTANCE_UNKNOWN',N'HIGH',@objectRef,@evidence,N'OPEN',N'OPERATOR',N'MANUAL_VERIFY')",
            new
            {
                runId, objectRef = "part:" + head.PartId.ToString(CultureInfo.InvariantCulture), evidence,
            }, tx, cancellationToken: ct));
    }

    /// <summary>reconciliation_run.business_type（nvarchar(16)）：零售/消费者退货记 SALE，采购按方向记。</summary>
    private static string BusinessType(string taskType) => taskType switch
    {
        "PURCHASE_IN_102" => "PURCHASE_IN",
        "PURCHASE_RETURN_202" => "PURCHASE_RETURN",
        _ => "SALE",
    };

    // ---------------------------------------------------------------- 写入细节

    private static async Task FinishAttemptAsync(SqlConnection conn, SqlTransaction tx, long attemptId,
        TopOutcome outcome, BillStatusParseResult? parsed, CancellationToken ct)
        => await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.upload_attempt
SET finished_at=SYSUTCDATETIME(), http_status=@httpStatus, request_id=@requestId,
    raw_response=@rawResponse, error_kind=@errorKind
WHERE id=@attemptId AND is_stale_evidence=0",
            new
            {
                attemptId, httpStatus = HttpStatusFor(outcome.Kind), requestId = outcome.RequestId,
                rawResponse = TopResponseSanitizer.Sanitize(SerializeOutcome(outcome, parsed)),
                errorKind = ErrorKind(outcome.Kind, parsed?.Kind),
            }, tx, cancellationToken: ct));

    private async Task FinishStaleAttemptAsync(long attemptId, TopOutcome outcome, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.upload_attempt
SET finished_at=SYSUTCDATETIME(), request_id=@requestId, raw_response=@rawResponse, error_kind=N'FENCING_STALE'
WHERE id=@attemptId AND is_stale_evidence=0",
            new
            {
                attemptId, requestId = outcome.RequestId,
                rawResponse = TopResponseSanitizer.Sanitize(SerializeOutcome(outcome, null)),
            }, cancellationToken: ct));
    }

    /// <summary>
    /// 网关/数据库异常时收尾本次查询尝试（<c>finished_at</c> + <c>error_kind</c>）：异常照常上抛由 Worker 退避重排，
    /// 但尝试行不得留在未完成状态。收尾自身失败不掩盖原始异常（优先保住根因，行留给审计/人工发现）。
    /// </summary>
    private async Task CloseAttemptOnExceptionAsync(long attemptId, string errorKind, Exception cause)
    {
        try
        {
            var summary = TopResponseSanitizer.Sanitize(JsonSerializer.Serialize(new
            {
                kind = errorKind, message = cause.Message, exception = cause.GetType().Name,
            }));
            await using var conn = new SqlConnection(_cs);
            await conn.OpenAsync(CancellationToken.None);
            await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.upload_attempt
SET finished_at=SYSUTCDATETIME(), error_kind=@errorKind, raw_response=@rawResponse
WHERE id=@attemptId AND is_stale_evidence=0",
                new { attemptId, errorKind, rawResponse = summary }, cancellationToken: CancellationToken.None));
        }
        catch (Exception)
        {
            // 收尾写失败（如库瞬断）不能把原始异常换成二级异常：抛出原始异常，尝试行交由审计/人工确认。
        }
    }

    /// <summary>
    /// 逐码结论：分片载荷里的每个码都必须有结论；平台未返回状态的码一律 UNKNOWN（绝不静默成功）。
    /// 平台多返回的码也落库（同一 attempt 内按规范化码去重）。第二项表示载荷内是否存在非 SUCCESS 的码。
    /// </summary>
    private static (List<(string Code, CodeStatus Status)> Items, bool PayloadIncomplete) BuildCodeStatuses(
        HeadRow head, BillStatusParseResult? parsed, string? platformError)
    {
        var byNormalized = new Dictionary<string, CodeStatus>(StringComparer.Ordinal);
        if (parsed is not null)
            foreach (var status in parsed.Codes)
            {
                var normalized = TraceCodeNormalizer.Normalize(status.Code).Normalized;
                if (normalized is not null) byNormalized[normalized] = status;
            }

        var ordered = new List<(string Code, CodeStatus Status)>();
        var payloadIncomplete = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in ReadPayloadCodes(head.PartPayloadJson))
        {
            var normalized = TraceCodeNormalizer.Normalize(raw).Normalized;
            if (normalized is null || !seen.Add(normalized)) continue;
            var status = byNormalized.TryGetValue(normalized, out var evidence)
                ? new CodeStatus(normalized, evidence.Result, evidence.Reason)
                : new CodeStatus(normalized, CodeStatus.Unknown,
                    parsed?.Kind == BillStatusKind.Processing ? "平台处理中，逐码结论未出" : platformError ?? "平台未返回该码结论");
            if (status.Result != CodeStatus.Success) payloadIncomplete = true;
            ordered.Add((normalized, status));
        }
        foreach (var status in byNormalized.Values)
            if (seen.Add(TraceCodeNormalizer.Normalize(status.Code).Normalized!))
                ordered.Add((TraceCodeNormalizer.Normalize(status.Code).Normalized!, status));
        return (ordered, payloadIncomplete);
    }

    private static List<string> ReadPayloadCodes(string? payloadJson)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(payloadJson)) return result;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;
            if (!doc.RootElement.TryGetProperty("lines", out var lines) || lines.ValueKind != JsonValueKind.Array)
                return result;
            foreach (var line in lines.EnumerateArray())
            {
                if (line.ValueKind != JsonValueKind.Object) continue;
                if (!line.TryGetProperty("codes", out var codes) || codes.ValueKind != JsonValueKind.Array) continue;
                foreach (var code in codes.EnumerateArray())
                {
                    if (code.ValueKind != JsonValueKind.String) continue;
                    var value = code.GetString();
                    if (!string.IsNullOrWhiteSpace(value)) result.Add(value!);
                }
            }
        }
        catch (JsonException)
        {
            // 载荷不可解析：无逐码证据，全部码由平台 code_list 决定（缺失不写入，绝不当成功）。
        }
        return result;
    }

    private static string? CodeEvidence(BillStatusParseResult? parsed)
        => parsed?.Note is null ? null : Truncate(JsonSerializer.Serialize(new { note = parsed.Note }), EvidenceMaxChars);

    private async Task EnqueueDependentVerificationsAsync(SqlConnection conn, SqlTransaction tx, HeadRow head,
        IReadOnlyList<long> codeIds, CancellationToken ct)
    {
        if (codeIds.Count == 0) return;
        // 复用 Task 2 的确定性唤醒（只命中 dependency_state=WAIT 的 SALE 快照，不扫描非依赖任务）。
        await _sales.EnqueueDependentVerificationsAsync(conn, tx, codeIds, head.OrgId, head.Env, ct);
    }

    /// <summary>回查作业改期：per-part 作业退避重排；扫描路径不触碰扫描作业本身（否则会打断本批扫描的租约）。</summary>
    private async Task ScheduleRetryAsync(SqlConnection conn, SqlTransaction tx, HeadRow head, string guardIntent,
        string workerId, long fencingToken, DateTime nextRunAtUtc, string? lastError, CancellationToken ct)
    {
        var partIntent = PartIntent(head.PartId);
        var error = Truncate(lastError, ReasonMaxChars);
        if (string.Equals(guardIntent, partIntent, StringComparison.Ordinal))
            await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.outbox_job
SET job_state=N'PENDING', lease_owner=NULL, lease_until=NULL, next_run_at=@nextRunAt, last_error=@error
WHERE org_id=@orgId AND env=@env AND job_kind=N'bill-query' AND intent_key=@intent
  AND job_state=N'RUNNING' AND lease_owner=@worker AND fencing_token=@token",
                new
                {
                    orgId = head.OrgId, env = head.Env, intent = partIntent, nextRunAt = nextRunAtUtc,
                    error, worker = workerId, token = fencingToken,
                }, tx, cancellationToken: ct));

        // 已收尾/待执行的 per-part 作业复位（DONE/FAILED 归零次数，PENDING 取更早时间）。
        await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.outbox_job
SET job_state=N'PENDING', lease_owner=NULL, lease_until=NULL,
    next_run_at = CASE WHEN next_run_at > @nextRunAt THEN @nextRunAt ELSE next_run_at END,
    attempt_count = CASE WHEN job_state IN (N'DONE',N'FAILED') THEN 0 ELSE attempt_count END,
    finished_at=NULL, last_error=NULL
WHERE org_id=@orgId AND env=@env AND job_kind=N'bill-query' AND intent_key=@intent
  AND job_state IN (N'PENDING',N'DONE',N'FAILED')",
            new { orgId = head.OrgId, env = head.Env, intent = partIntent, nextRunAt = nextRunAtUtc },
            tx, cancellationToken: ct));

        await _queue.EnqueueAsync(conn, tx, head.OrgId, head.Env, JobKind, partIntent, null, nextRunAtUtc, ct);
    }

    // ---------------------------------------------------------------- 判定与映射

    private static bool IsQueryablePart(string state) => QueryableStates.Contains(state, StringComparer.Ordinal);

    /// <summary>观察窗：达到最大回查次数，或自首次发送起超过转人工小时数。</summary>
    private static bool IsObservationExpired(HeadRow head, int attemptCount, QueryPolicy policy, DateTime now)
    {
        if (attemptCount >= policy.MaxAttempts) return true;
        return head.FirstSentAt is DateTime sent && now - sent >= TimeSpan.FromHours(policy.ManualAfterHours);
    }

    /// <summary>
    /// 退避：平台明确等待秒数优先，其次 <c>top.query.backoffSeconds × 2^min(query_count,5)</c>（上限 600s）+ 抖动；
    /// 授权暂停 5 分钟后重试（请求未受理，交回查收敛）。
    /// </summary>
    private static TimeSpan RetryDelay(TopOutcome outcome, int priorCount, int baseSeconds) => outcome.Kind switch
    {
        TopOutcomeKind.RateLimited => TimeSpan.FromSeconds(
            Math.Max(outcome.RetryAfterSeconds ?? 0, (int)Backoff(0, baseSeconds, 0).TotalSeconds)),
        TopOutcomeKind.AuthError => AuthPauseDelay,
        _ => Backoff(priorCount, baseSeconds),
    };

    private static QueryOutcome QueryOutcomeFor(BillStatusParseResult? parsed) => parsed?.Kind switch
    {
        BillStatusKind.AllSuccess => QueryOutcome.Succeeded,
        BillStatusKind.Partial => QueryOutcome.Partial,
        BillStatusKind.Failed => QueryOutcome.Failed,
        BillStatusKind.Processing => QueryOutcome.Processing,
        _ => QueryOutcome.Unresolved,
    };

    private static int? HttpStatusFor(TopOutcomeKind kind) => kind switch
    {
        // 传输失败/本地限流/暂停未取得任何 HTTP 状态；其余（含解析失败）均已收到 HTTP 200 信封。
        TopOutcomeKind.TransportUncertain or TopOutcomeKind.RateLimited or TopOutcomeKind.AuthError => null,
        _ => 200,
    };

    private static string? ErrorKind(TopOutcomeKind kind, BillStatusKind? parsedKind) => parsedKind switch
    {
        BillStatusKind.Unresolved => "RESULT_UNRESOLVED",
        _ => kind switch
        {
            TopOutcomeKind.Success or TopOutcomeKind.PartialEvidence => null,
            TopOutcomeKind.BusinessRejected => "BUSINESS_REJECTED",
            TopOutcomeKind.DuplicateBillCode => "DUPLICATE_BILL_CODE",
            TopOutcomeKind.RateLimited => "RATE_LIMITED",
            TopOutcomeKind.AuthError => "AUTH_ERROR",
            TopOutcomeKind.SystemError => "SYSTEM_ERROR",
            _ => "TRANSPORT_UNCERTAIN",
        },
    };

    /// <summary>响应摘要（脱敏后落 upload_attempt.raw_response）：只保留结论要素，不保存请求参数/凭证。</summary>
    private static string SerializeOutcome(TopOutcome outcome, BillStatusParseResult? parsed)
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
            message = outcome.Message,
            requestId = outcome.RequestId,
            retryAfterSeconds = outcome.RetryAfterSeconds,
            billStatus = parsed?.Kind.ToString(),
            note = parsed?.Note,
            result,
        });
    }

    private static string? Truncate(string? value, int max)
        => value is null || value.Length <= max ? value : value[..max];

    // ---------------------------------------------------------------- 行类型

    private sealed class HeadRow
    {
        public long PartId { get; set; }
        public long TaskId { get; set; }
        public long OrgId { get; set; }
        public string Env { get; set; } = "";
        public string BillCode { get; set; } = "";
        public string PartPayloadJson { get; set; } = "{}";
        public string PartState { get; set; } = "";
        public int QueryCount { get; set; }
        public DateTime? FirstSentAt { get; set; }
        public string TaskState { get; set; } = "";
        public string TaskType { get; set; } = "";
        public string? OrgRefEntId { get; set; }
    }

    private sealed class AttemptRow
    {
        public long AttemptId { get; set; }
        public int AttemptNo { get; set; }
    }
}