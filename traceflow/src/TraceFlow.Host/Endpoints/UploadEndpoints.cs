using System.Globalization;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Security;
using TraceFlow.Domain.Uploads;
using TraceFlow.Host.Security;
using TraceFlow.Infrastructure.Settings;
using TraceFlow.Infrastructure.Uploads;

namespace TraceFlow.Host.Endpoints;

/// <summary>
/// 上传中心 API（UI-09）：列表/详情只读（登录）；受理未知人工核对需 <c>upload:verify</c>，
/// 受控重试需 <c>upload:retry</c>。成功率口径=最终全部成功（Succeeded / (Succeeded+Failed) 任务数），
/// PartialSuccess/Processing/AcceptanceUnknown/ResultUnresolved/ManualAction 单列计数，绝不混入分子/分母。
/// 列表/详情只出白名单字段：raw_response 为入库前脱敏证据，设置项与密钥一律不返回。
/// </summary>
public static class UploadEndpoints
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;

    /// <summary>逐码最新结论（同码多次尝试取 id 最大的一行）：分页与汇总共用。</summary>
    private const string LatestResults = @"
        SELECT c.task_id, c.code_id, c.result,
               ROW_NUMBER() OVER (PARTITION BY c.task_id, c.code_id ORDER BY c.id DESC) AS rn
        FROM dbo.code_result c JOIN filtered f ON f.id=c.task_id";

    public static void MapUploadEndpoints(this WebApplication app, string connectionString, string env)
    {
        // ---------------------------------------------------------------- 上传任务列表
        app.MapGet("/api/v1/upload-tasks", async (HttpContext ctx, int? page, int? pageSize, string? state,
            string? taskType, [FromQuery(Name = "env")] string? envFilter) =>
        {
            if (!TryPaging(page, pageSize, out var p, out var size, out var pagingError))
                return PagingError(ctx, pagingError!);
            var targetEnv = ResolveEnv(envFilter, env);
            if (targetEnv is null) return Err(ctx, 400, "VALIDATION", "env 只能为 LIVE 或 MOCK");
            if (!string.IsNullOrEmpty(state) && !Enum.TryParse<UploadState>(state, ignoreCase: false, out _))
                return Err(ctx, 422, "VALIDATION", "state 不是合法的上传状态");

            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ctx.RequestAborted);
            var orgId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT id FROM dbo.organization WHERE is_active=1", cancellationToken: ctx.RequestAborted));
            var args = new DynamicParameters();
            args.Add("orgId", orgId);
            args.Add("env", targetEnv);
            var filters = "";
            if (!string.IsNullOrEmpty(state)) { filters += " AND t.state=@state"; args.Add("state", state); }
            if (!string.IsNullOrEmpty(taskType)) { filters += " AND t.task_type=@taskType"; args.Add("taskType", taskType); }
            var where = $"WHERE t.org_id=@orgId AND t.env=@env{filters}";

            var summary = (await conn.QueryAsync<StateCountRow>(new CommandDefinition(
                $"SELECT t.state, COUNT(*) AS n FROM dbo.upload_task t {where} GROUP BY t.state", args,
                cancellationToken: ctx.RequestAborted))).ToList();
            var total = summary.Sum(s => s.n);
            var byState = summary.ToDictionary(s => s.state, s => s.n, StringComparer.Ordinal);
            int Count(string name) => byState.TryGetValue(name, out var n) ? n : 0;
            var succeeded = Count(nameof(UploadState.Succeeded));
            var failed = Count(nameof(UploadState.Failed));
            var settled = succeeded + failed;

            args.Add("lo", (p - 1) * size + 1);
            args.Add("hi", p * size);
            var rows = (await conn.QueryAsync<UploadListRow>(new CommandDefinition($@"
;WITH filtered AS (
    SELECT t.id, t.event_key, t.task_type, t.state, t.state_reason, t.verification_state, t.created_at
    FROM dbo.upload_task t {where}
), latest AS ({LatestResults}
), code_agg AS (
    SELECT task_id, COUNT(*) AS codeCount,
           SUM(CASE WHEN result=N'SUCCESS' THEN 1 ELSE 0 END) AS succeededCodes,
           SUM(CASE WHEN result=N'FAILED' THEN 1 ELSE 0 END) AS failedCodes,
           SUM(CASE WHEN result=N'UNKNOWN' THEN 1 ELSE 0 END) AS unknownCodes
    FROM latest WHERE rn=1 GROUP BY task_id
), part_agg AS (
    SELECT p.task_id, COUNT(*) AS partCount,
           SUM(CASE WHEN p.state=N'Succeeded' THEN 1 ELSE 0 END) AS succeededParts,
           SUM(CASE WHEN p.state=N'Failed' THEN 1 ELSE 0 END) AS failedParts,
           SUM(CASE WHEN p.state=N'PartialSuccess' THEN 1 ELSE 0 END) AS partialSuccessParts,
           SUM(CASE WHEN p.state=N'Processing' THEN 1 ELSE 0 END) AS processingParts,
           SUM(CASE WHEN p.state=N'AcceptanceUnknown' THEN 1 ELSE 0 END) AS acceptanceUnknownParts,
           SUM(CASE WHEN p.state=N'ResultUnresolved' THEN 1 ELSE 0 END) AS resultUnresolvedParts,
           SUM(CASE WHEN p.state=N'ManualAction' THEN 1 ELSE 0 END) AS manualActionParts
    FROM dbo.upload_part p JOIN filtered f ON f.id=p.task_id
    GROUP BY p.task_id
), paged AS (
    SELECT ROW_NUMBER() OVER (ORDER BY f.created_at DESC, f.id DESC) AS rn2, f.* FROM filtered f
)
SELECT p.id AS taskId, p.event_key AS eventKey, p.task_type AS taskType, p.state,
       p.state_reason AS stateReason, p.verification_state AS verificationState, p.created_at AS createdAt,
       ISNULL(pa.partCount,0) AS partCount, ISNULL(pa.succeededParts,0) AS succeededParts,
       ISNULL(pa.failedParts,0) AS failedParts, ISNULL(pa.partialSuccessParts,0) AS partialSuccessParts,
       ISNULL(pa.processingParts,0) AS processingParts, ISNULL(pa.acceptanceUnknownParts,0) AS acceptanceUnknownParts,
       ISNULL(pa.resultUnresolvedParts,0) AS resultUnresolvedParts, ISNULL(pa.manualActionParts,0) AS manualActionParts,
       ISNULL(ca.codeCount,0) AS codeCount, ISNULL(ca.succeededCodes,0) AS succeededCodes,
       ISNULL(ca.failedCodes,0) AS failedCodes, ISNULL(ca.unknownCodes,0) AS unknownCodes
FROM paged p
LEFT JOIN part_agg pa ON pa.task_id=p.id
LEFT JOIN code_agg ca ON ca.task_id=p.id
WHERE p.rn2 BETWEEN @lo AND @hi
ORDER BY p.rn2", args, cancellationToken: ctx.RequestAborted))).ToList();

            var items = rows.Select(r => new
            {
                id = r.taskId.ToString(CultureInfo.InvariantCulture), r.eventKey, r.taskType, r.state,
                stateReason = r.stateReason, env = targetEnv, r.verificationState,
                r.partCount, r.codeCount, r.succeededCodes, r.failedCodes, r.unknownCodes,
                r.succeededParts, r.failedParts, r.partialSuccessParts, r.processingParts,
                r.acceptanceUnknownParts, r.resultUnresolvedParts, r.manualActionParts,
                r.createdAt,
            }).ToList();
            // 成功率=最终全部成功：只把 Succeeded 记为成功、Succeeded+Failed 为分母；
            // 中间态/部分成功单列，绝不混入分子或分母。
            var rate = settled == 0 ? (decimal?)null : Math.Round((decimal)succeeded / settled, 4);
            return Results.Ok(new { data = new
            {
                items,
                page = p,
                pageSize = size,
                total,
                summary = new
                {
                    total, succeeded, failed, settled,
                    partialSuccess = Count(nameof(UploadState.PartialSuccess)),
                    processing = Count(nameof(UploadState.Processing)),
                    acceptanceUnknown = Count(nameof(UploadState.AcceptanceUnknown)),
                    resultUnresolved = Count(nameof(UploadState.ResultUnresolved)),
                    manualAction = Count(nameof(UploadState.ManualAction)),
                    finalSuccessRate = rate,
                },
            }, requestId = ctx.TraceIdentifier });
        }).RequireAuthorization();

        // ---------------------------------------------------------------- 上传任务钻取
        app.MapGet("/api/v1/upload-tasks/{id:long}", async (HttpContext ctx, long id,
            [FromQuery(Name = "env")] string? envFilter, SettingsService settings) =>
        {
            // 与列表同一口径：env 可选，非法值 400；任务须属于「活动机构 + 目标环境」，范围外一律 404。
            var targetEnv = ResolveEnv(envFilter, env);
            if (targetEnv is null) return Err(ctx, 400, "VALIDATION", "env 只能为 LIVE 或 MOCK");

            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ctx.RequestAborted);
            var orgId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT id FROM dbo.organization WHERE is_active=1", cancellationToken: ctx.RequestAborted));
            var task = await conn.QuerySingleOrDefaultAsync<UploadTaskRow>(new CommandDefinition(@"
SELECT id, event_key, task_type, env, source_snapshot_id, predecessor_task_id, state, state_reason,
       version, created_at, verification_state, dependency_state, match_state, source_integrity, payload_hash
FROM dbo.upload_task WHERE id=@id AND org_id=@orgId AND env=@env",
                new { id, orgId, env = targetEnv }, cancellationToken: ctx.RequestAborted));
            if (task is null)
                return Err(ctx, 404, "NOT_FOUND", "上传任务不存在");
            var parts = (await conn.QueryAsync<UploadPartRow>(new CommandDefinition(@"
SELECT p.id, p.part_index, p.bill_code, p.state, p.query_count, p.next_query_at, p.first_sent_at,
       (SELECT COUNT(*) FROM dbo.code_result c WHERE c.part_id=p.id) AS resultRows
FROM dbo.upload_part p WHERE p.task_id=@id ORDER BY p.part_index",
                new { id }, cancellationToken: ctx.RequestAborted))).ToList();
            var attempts = (await conn.QueryAsync<UploadAttemptRow>(new CommandDefinition(@"
SELECT id, part_id, attempt_no, attempt_kind, started_at, finished_at, http_status, request_id,
       platform_task_id, error_kind, is_stale_evidence, raw_response
FROM dbo.upload_attempt WHERE task_id=@id ORDER BY attempt_kind, attempt_no, id",
                new { id }, cancellationToken: ctx.RequestAborted))).ToList();
            var results = (await conn.QueryAsync<CodeResultRow>(new CommandDefinition(@"
SELECT c.id, c.part_id, c.code_id, tc.code_normalized, c.result, c.reason, c.attempt_id, c.recorded_at
FROM dbo.code_result c JOIN dbo.trace_code tc ON tc.id=c.code_id
WHERE c.task_id=@id ORDER BY c.id",
                new { id }, cancellationToken: ctx.RequestAborted))).ToList();
            var audits = (await conn.QueryAsync<AuditTimelineRow>(new CommandDefinition(@"
SELECT occurred_at, actor, action, reason FROM dbo.audit_log
WHERE object_type=N'upload_task' AND object_id=@objectId AND env=@env ORDER BY occurred_at, id",
                new { objectId = id.ToString(CultureInfo.InvariantCulture), env = task.env },
                cancellationToken: ctx.RequestAborted))).ToList();

            var retryPathCertified = await settings.GetRetryPathCertifiedAsync(ctx.RequestAborted);

            var timeline = new List<TimelineItem> { new(task.created_at, "TASK_CREATED", null, null) };
            foreach (var attempt in attempts)
            {
                timeline.Add(new TimelineItem(attempt.started_at,
                    "ATTEMPT_" + attempt.attempt_kind, $"attemptNo={attempt.attempt_no}", null));
                if (attempt.finished_at is DateTime finished)
                    timeline.Add(new TimelineItem(finished, "ATTEMPT_" + attempt.attempt_kind + "_FINISHED",
                        attempt.http_status is null ? attempt.error_kind : "HTTP " + attempt.http_status, null));
            }
            foreach (var audit in audits)
                timeline.Add(new TimelineItem(audit.occurred_at, audit.action, audit.reason, audit.actor));
            var orderedTimeline = timeline.OrderBy(t => t.At).ThenBy(t => t.Kind, StringComparer.Ordinal)
                .Select(t => new { at = t.At, kind = t.Kind, detail = t.Detail, actor = t.Actor }).ToList();

            return Results.Ok(new { data = new
            {
                // 受控重试合同门禁：false 时前端必须禁用重试入口（fail-closed）。
                retryPathCertified,
                task = new
                {
                    taskId = task.id.ToString(CultureInfo.InvariantCulture), eventKey = task.event_key,
                    taskType = task.task_type, task.env,
                    sourceSnapshotId = task.source_snapshot_id?.ToString(CultureInfo.InvariantCulture),
                    predecessorTaskId = task.predecessor_task_id?.ToString(CultureInfo.InvariantCulture),
                    task.state, stateReason = task.state_reason, task.version, createdAt = task.created_at,
                    verificationState = task.verification_state, dependencyState = task.dependency_state,
                    matchState = task.match_state, sourceIntegrity = task.source_integrity,
                    payloadHash = task.payload_hash,
                },
                parts = parts.Select(part => new
                {
                    partId = part.id.ToString(CultureInfo.InvariantCulture), partIndex = part.part_index,
                    billCode = part.bill_code, part.state, queryCount = part.query_count,
                    nextQueryAt = part.next_query_at, firstSentAt = part.first_sent_at,
                    resultRows = part.resultRows,
                }),
                attempts = attempts.Select(a => new
                {
                    attemptId = a.id.ToString(CultureInfo.InvariantCulture),
                    partId = a.part_id?.ToString(CultureInfo.InvariantCulture), attemptNo = a.attempt_no,
                    attemptKind = a.attempt_kind, startedAt = a.started_at, finishedAt = a.finished_at,
                    httpStatus = a.http_status, requestId = a.request_id, platformTaskId = a.platform_task_id,
                    errorKind = a.error_kind, isStaleEvidence = a.is_stale_evidence,
                    // raw_response 在写库前已脱敏（不含 AppSecret/签名/请求参数），此处只读回显。
                    rawResponse = a.raw_response,
                }),
                codeResults = results.Select(r => new
                {
                    resultId = r.id.ToString(CultureInfo.InvariantCulture),
                    partId = r.part_id.ToString(CultureInfo.InvariantCulture),
                    codeId = r.code_id.ToString(CultureInfo.InvariantCulture), codeNormalized = r.code_normalized,
                    r.result, r.reason,
                    attemptId = r.attempt_id.ToString(CultureInfo.InvariantCulture), recordedAt = r.recorded_at,
                }),
                timeline = orderedTimeline,
            }, requestId = ctx.TraceIdentifier });
        }).RequireAuthorization();

        // ---------------------------------------------------------------- 受理未知人工核对
        app.MapPost("/api/v1/upload-tasks/{id:long}/manual-verify",
            (HttpContext ctx, long id, IdempotencyExecutor idem, UploadManualService svc) =>
            idem.RunAsync(ctx, async (conn, tx, ct) =>
            {
                var body = await ReadBodyAsync(ctx, ct);
                ManualVerifyRequest? req;
                try
                {
                    req = string.IsNullOrWhiteSpace(body) ? null : JsonSerializer.Deserialize<ManualVerifyRequest>(body, Web);
                }
                catch (JsonException)
                {
                    return ErrTuple(ctx, 400, "BAD_REQUEST", "请求体不是合法 JSON");
                }
                if (req is null)
                    return ErrTuple(ctx, 422, "VALIDATION", "请求体必填");
                var cmd = new ManualVerifyCommand(req.Decision ?? "", req.Evidence?.PlatformBillCode,
                    req.Evidence?.ContractEvidence, req.ExpectedVersion);
                var result = await svc.ManualVerifyAsync(conn, tx, id, env, cmd, ctx.User.Identity!.Name!,
                    ctx.TraceIdentifier, ct);
                return (200, JsonSerializer.Serialize(new { data = new
                {
                    taskId = result.TaskId.ToString(CultureInfo.InvariantCulture), state = result.State,
                    version = result.Version, decision = result.Decision,
                    partIds = result.PartIds.Select(x => x.ToString(CultureInfo.InvariantCulture)).ToArray(),
                }, requestId = ctx.TraceIdentifier }, Web));
            })).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.UploadVerify));

        // ---------------------------------------------------------------- 受控重试
        app.MapPost("/api/v1/upload-tasks/{id:long}/controlled-retry",
            (HttpContext ctx, long id, IdempotencyExecutor idem, UploadManualService svc) =>
            idem.RunAsync(ctx, async (conn, tx, ct) =>
            {
                var body = await ReadBodyAsync(ctx, ct);
                ControlledRetryRequest? req;
                try
                {
                    req = string.IsNullOrWhiteSpace(body) ? null : JsonSerializer.Deserialize<ControlledRetryRequest>(body, Web);
                }
                catch (JsonException)
                {
                    return ErrTuple(ctx, 400, "BAD_REQUEST", "请求体不是合法 JSON");
                }
                var result = await svc.ControlledRetryAsync(conn, tx, id, env, req?.ExpectedVersion,
                    ctx.User.Identity!.Name!, ctx.TraceIdentifier, ct);
                return (200, JsonSerializer.Serialize(new { data = new
                {
                    taskId = result.TaskId.ToString(CultureInfo.InvariantCulture), state = result.State,
                    version = result.Version, retryAllowed = true, jobsReset = result.JobsReset,
                    partIds = result.PartIds.Select(x => x.ToString(CultureInfo.InvariantCulture)).ToArray(),
                }, requestId = ctx.TraceIdentifier }, Web));
            })).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.UploadRetry));
    }

    // ---------------------------------------------------------------- 辅助

    /// <summary>分页口径：page&lt;1 或 pageSize&lt;1 → 400；pageSize&gt;100 收敛到 100；默认 20。</summary>
    private static bool TryPaging(int? page, int? pageSize, out int p, out int size, out string? error)
    {
        p = 1;
        size = DefaultPageSize;
        error = null;
        if (page is int rawPage)
        {
            if (rawPage < 1) { error = "page 必须 ≥ 1"; return false; }
            p = rawPage;
        }
        if (pageSize is int rawSize)
        {
            if (rawSize < 1) { error = "pageSize 必须 ≥ 1"; return false; }
            size = Math.Min(rawSize, MaxPageSize);
        }
        return true;
    }

    private static string? ResolveEnv(string? requested, string current)
    {
        if (string.IsNullOrWhiteSpace(requested)) return current;
        var value = requested.Trim().ToUpperInvariant();
        return value is "LIVE" or "MOCK" ? value : null;
    }

    private static async Task<string> ReadBodyAsync(HttpContext ctx, CancellationToken ct)
    {
        ctx.Request.EnableBuffering();
        ctx.Request.Body.Position = 0;
        using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync(ct);
        ctx.Request.Body.Position = 0;
        return body;
    }

    private static IResult PagingError(HttpContext ctx, string message) => Err(ctx, 400, "VALIDATION", message);

    private static IResult Err(HttpContext ctx, int status, string code, string message)
        => Results.Json(new { code, message, retryable = false, requestId = ctx.TraceIdentifier }, statusCode: status);

    /// <summary>IdempotencyExecutor 动作体只接受 (status, body) 元组（错误响应与成功共用同一条序列化路径）。</summary>
    private static (int Status, string Body) ErrTuple(HttpContext ctx, int status, string code, string message)
        => (status, JsonSerializer.Serialize(new { code, message, retryable = false, requestId = ctx.TraceIdentifier }));

    public sealed record ManualVerifyRequest(string? Decision, ManualEvidence? Evidence, int? ExpectedVersion);
    public sealed record ManualEvidence(string? PlatformBillCode, JsonElement? ContractEvidence);
    public sealed record ControlledRetryRequest(int? ExpectedVersion);

    private sealed record TimelineItem(DateTime At, string Kind, string? Detail, string? Actor);

    private sealed class StateCountRow
    {
        public string state { get; set; } = "";
        public int n { get; set; }
    }

    private sealed class UploadListRow
    {
        public long taskId { get; set; }
        public string eventKey { get; set; } = "";
        public string taskType { get; set; } = "";
        public string state { get; set; } = "";
        public string? stateReason { get; set; }
        public string verificationState { get; set; } = "";
        public DateTime createdAt { get; set; }
        public int partCount { get; set; }
        public int succeededParts { get; set; }
        public int failedParts { get; set; }
        public int partialSuccessParts { get; set; }
        public int processingParts { get; set; }
        public int acceptanceUnknownParts { get; set; }
        public int resultUnresolvedParts { get; set; }
        public int manualActionParts { get; set; }
        public int codeCount { get; set; }
        public int succeededCodes { get; set; }
        public int failedCodes { get; set; }
        public int unknownCodes { get; set; }
    }

    private sealed class UploadTaskRow
    {
        public long id { get; set; }
        public string event_key { get; set; } = "";
        public string task_type { get; set; } = "";
        public string env { get; set; } = "";
        public long? source_snapshot_id { get; set; }
        public long? predecessor_task_id { get; set; }
        public string state { get; set; } = "";
        public string? state_reason { get; set; }
        public int version { get; set; }
        public DateTime created_at { get; set; }
        public string verification_state { get; set; } = "";
        public string dependency_state { get; set; } = "";
        public string match_state { get; set; } = "";
        public string source_integrity { get; set; } = "";
        public string payload_hash { get; set; } = "";
    }

    private sealed class UploadPartRow
    {
        public long id { get; set; }
        public int part_index { get; set; }
        public string bill_code { get; set; } = "";
        public string state { get; set; } = "";
        public int query_count { get; set; }
        public DateTime? next_query_at { get; set; }
        public DateTime? first_sent_at { get; set; }
        public int resultRows { get; set; }
    }

    private sealed class UploadAttemptRow
    {
        public long id { get; set; }
        public long? part_id { get; set; }
        public int attempt_no { get; set; }
        public string attempt_kind { get; set; } = "";
        public DateTime started_at { get; set; }
        public DateTime? finished_at { get; set; }
        public int? http_status { get; set; }
        public string? request_id { get; set; }
        public string? platform_task_id { get; set; }
        public string? error_kind { get; set; }
        public bool is_stale_evidence { get; set; }
        public string? raw_response { get; set; }
    }

    private sealed class CodeResultRow
    {
        public long id { get; set; }
        public long part_id { get; set; }
        public long code_id { get; set; }
        public string code_normalized { get; set; } = "";
        public string result { get; set; } = "";
        public string? reason { get; set; }
        public long attempt_id { get; set; }
        public DateTime recorded_at { get; set; }
    }

    private sealed class AuditTimelineRow
    {
        public DateTime occurred_at { get; set; }
        public string actor { get; set; } = "";
        public string action { get; set; } = "";
        public string? reason { get; set; }
    }
}