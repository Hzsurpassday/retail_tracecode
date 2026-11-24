using System.Globalization;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Application.Security;
using TraceFlow.Host.Security;
using TraceFlow.Infrastructure.Reconciliation;

namespace TraceFlow.Host.Endpoints;

/// <summary>
/// 日对账 API（REC-01/02/03、AT-062/063/064）：触发需 <c>system:manage</c>，查询需 <c>audit:read</c>，
/// 差异解决需 <c>system:manage</c>。沿用既有端点约定：<c>{data,requestId}</c>、外部/内部标识按契约
/// （jobId/runId/differenceId 为字符串）、ROW_NUMBER 区间分页（2008 R2）、CSRF 由全局中间件校验、
/// 变更接口经 <see cref="IdempotencyExecutor"/> 幂等（Idempotency-Key 头）。
/// 审计口径：触发/解决为控制面人工动作 → env 恒 LIVE；run 完成由作业自体写 env=作业 env。
/// </summary>
public static class ReconciliationEndpoints
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;
    private const int MaxDifferences = 2000;
    private const int ResolutionEvidenceMaxChars = 4000;
    private const int AuditReasonMaxChars = 200;

    public static void MapReconciliationEndpoints(this WebApplication app, string connectionString, string env)
    {
        // ---------------------------------------------------------------- 触发对账
        app.MapPost("/api/v1/reconciliation-runs",
            (HttpContext ctx, IdempotencyExecutor idem, ReconciliationService svc) =>
            idem.RunAsync(ctx, async (conn, tx, ct) =>
            {
                var body = await ReadBodyAsync(ctx, ct);
                ReconciliationRequestBody? req;
                try
                {
                    req = string.IsNullOrWhiteSpace(body) ? null : JsonSerializer.Deserialize<ReconciliationRequestBody>(body, Web);
                }
                catch (JsonException)
                {
                    return ErrTuple(ctx, 400, "BAD_REQUEST", "请求体不是合法 JSON");
                }
                if (req is null)
                    return ErrTuple(ctx, 422, "VALIDATION", "请求体必填");
                if (!TryParseUtc(req.WindowStart, out var windowStart) || !TryParseUtc(req.WindowEnd, out var windowEnd))
                    return ErrTuple(ctx, 422, "VALIDATION", "windowStart/windowEnd 须为 ISO-8601（含时区或 Z）");
                // 参数与权限门禁均在服务层校验/抛错（AllocationValidationException → 422 统一映射，事务回滚不落幂等记录）。
                var jobId = await svc.EnqueueAsync(env, windowStart, windowEnd, req.BusinessType ?? "",
                    ctx.User.Identity!.Name!, ctx.TraceIdentifier, ct);
                return (202, JsonSerializer.Serialize(new
                {
                    data = new { jobId = jobId.ToString(CultureInfo.InvariantCulture) },
                    requestId = ctx.TraceIdentifier,
                }, Web));
            })).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.SystemManage));

        // ---------------------------------------------------------------- 对账运行列表
        app.MapGet("/api/v1/reconciliation-runs", async (HttpContext ctx, int? page, int? pageSize,
            string? status, string? from, string? to) =>
        {
            if (!TryPaging(page, pageSize, out var p, out var size, out var pagingError))
                return Err(ctx, 400, "VALIDATION", pagingError!);
            if (!string.IsNullOrEmpty(status) && status is not ("RUNNING" or "COMPLETE" or "INCOMPLETE"))
                return Err(ctx, 422, "VALIDATION", "status 只能为 RUNNING/COMPLETE/INCOMPLETE");
            DateTime? fromUtc = null;
            DateTime? toUtc = null;
            if (!string.IsNullOrWhiteSpace(from))
            {
                if (!TryParseUtc(from, out var parsedFrom)) return Err(ctx, 400, "VALIDATION", "from 须为 ISO-8601");
                fromUtc = parsedFrom;
            }
            if (!string.IsNullOrWhiteSpace(to))
            {
                if (!TryParseUtc(to, out var parsedTo)) return Err(ctx, 400, "VALIDATION", "to 须为 ISO-8601");
                toUtc = parsedTo;
            }

            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ctx.RequestAborted);
            var orgId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT id FROM dbo.organization WHERE is_active=1", cancellationToken: ctx.RequestAborted));
            var args = new DynamicParameters();
            args.Add("orgId", orgId);
            args.Add("env", env);
            var filters = "";
            if (!string.IsNullOrEmpty(status)) { filters += " AND r.status=@status"; args.Add("status", status); }
            if (fromUtc is DateTime f) { filters += " AND r.window_start>=@from"; args.Add("from", f); }
            if (toUtc is DateTime t) { filters += " AND r.window_start<@to"; args.Add("to", t); }
            var where = "WHERE r.org_id=@orgId AND r.env=@env" + filters;
            var total = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                $"SELECT COUNT(*) FROM dbo.reconciliation_run r {where}", args, cancellationToken: ctx.RequestAborted));

            args.Add("lo", (p - 1) * size + 1);
            args.Add("hi", p * size);
            // 2008 R2：ROW_NUMBER 区间分页（不使用 2012+ 的跳行/取行分页子句）。
            var rows = (await conn.QueryAsync<RunListRow>(new CommandDefinition($@"
SELECT x.id AS RunId, x.window_start AS WindowStart, x.window_end AS WindowEnd,
       x.business_type AS BusinessType, x.status AS Status, x.started_at AS StartedAt, x.finished_at AS FinishedAt,
       (SELECT COUNT(*) FROM dbo.reconciliation_difference d WHERE d.run_id=x.id) AS DifferenceCount
FROM (
    SELECT r.id, r.window_start, r.window_end, r.business_type, r.status, r.started_at, r.finished_at,
           ROW_NUMBER() OVER (ORDER BY r.started_at DESC, r.id DESC) AS rn
    FROM dbo.reconciliation_run r {where}
) x WHERE x.rn BETWEEN @lo AND @hi
ORDER BY x.rn", args, cancellationToken: ctx.RequestAborted))).ToList();
            var items = rows.Select(r => new
            {
                runId = r.RunId.ToString(CultureInfo.InvariantCulture), windowStart = r.WindowStart,
                windowEnd = r.WindowEnd, r.BusinessType, r.Status, startedAt = r.StartedAt,
                finishedAt = r.FinishedAt, r.DifferenceCount,
            }).ToList();
            return Results.Ok(new { data = new { items, page = p, pageSize = size, total }, requestId = ctx.TraceIdentifier });
        }).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.AuditRead));

        // ---------------------------------------------------------------- 对账运行详情
        app.MapGet("/api/v1/reconciliation-runs/{id:long}", async (HttpContext ctx, long id) =>
        {
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ctx.RequestAborted);
            var orgId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT id FROM dbo.organization WHERE is_active=1", cancellationToken: ctx.RequestAborted));
            // 跨 env / 不存在一律 404（不暴露另一环境的 run 是否存在）。
            var run = await conn.QuerySingleOrDefaultAsync<RunDetailRow>(new CommandDefinition(@"
SELECT r.id AS RunId, r.env AS Env, r.window_start AS WindowStart, r.window_end AS WindowEnd,
       r.business_type AS BusinessType, r.status AS Status, r.local_summary AS LocalSummary,
       r.platform_summary AS PlatformSummary, r.started_at AS StartedAt, r.finished_at AS FinishedAt,
       (SELECT COUNT(*) FROM dbo.reconciliation_difference d WHERE d.run_id=r.id) AS DifferenceCount
FROM dbo.reconciliation_run r
WHERE r.id=@id AND r.org_id=@orgId AND r.env=@env",
                new { id, orgId, env }, cancellationToken: ctx.RequestAborted));
            if (run is null)
                return Err(ctx, 404, "NOT_FOUND", "对账运行不存在");
            var differences = (await conn.QueryAsync<DifferenceListRow>(new CommandDefinition($@"
SELECT TOP ({MaxDifferences}) id AS DifferenceId, diff_type AS DiffType, severity AS Severity,
       object_ref AS ObjectRef, status AS Status, owner_role AS OwnerRole, next_action AS NextAction,
       found_at AS FoundAt, evidence_json AS EvidenceJson, resolution_evidence AS ResolutionEvidence,
       resolved_by AS ResolvedBy, resolved_at AS ResolvedAt
FROM dbo.reconciliation_difference
WHERE run_id=@id
ORDER BY found_at, id", new { id }, cancellationToken: ctx.RequestAborted))).ToList();

            return Results.Ok(new { data = new
            {
                run = new
                {
                    runId = run.RunId.ToString(CultureInfo.InvariantCulture), run.Env, windowStart = run.WindowStart,
                    windowEnd = run.WindowEnd, run.BusinessType, run.Status, startedAt = run.StartedAt,
                    finishedAt = run.FinishedAt, run.DifferenceCount,
                    localSummary = ParseJson(run.LocalSummary), platformSummary = ParseJson(run.PlatformSummary),
                },
                differences = differences.Select(d => new
                {
                    differenceId = d.DifferenceId.ToString(CultureInfo.InvariantCulture), d.DiffType, d.Severity,
                    d.ObjectRef, d.Status, d.OwnerRole, d.NextAction, foundAt = d.FoundAt,
                    evidence = ParseJson(d.EvidenceJson), resolutionEvidence = d.ResolutionEvidence,
                    d.ResolvedBy, resolvedAt = d.ResolvedAt,
                }),
            }, requestId = ctx.TraceIdentifier });
        }).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.AuditRead));

        // ---------------------------------------------------------------- 差异解决（留证）
        app.MapPost("/api/v1/reconciliation-differences/{id:long}/resolve",
            (HttpContext ctx, long id, IdempotencyExecutor idem, IAuditLog audit) =>
            idem.RunAsync(ctx, async (conn, tx, ct) =>
            {
                var body = await ReadBodyAsync(ctx, ct);
                ResolveRequestBody? req;
                try
                {
                    req = string.IsNullOrWhiteSpace(body) ? null : JsonSerializer.Deserialize<ResolveRequestBody>(body, Web);
                }
                catch (JsonException)
                {
                    return ErrTuple(ctx, 400, "BAD_REQUEST", "请求体不是合法 JSON");
                }
                var evidence = req?.ResolutionEvidence?.Trim();
                if (string.IsNullOrEmpty(evidence))
                    return ErrTuple(ctx, 422, "VALIDATION", "resolutionEvidence 必填");
                if (evidence.Length > ResolutionEvidenceMaxChars)
                    return ErrTuple(ctx, 422, "VALIDATION",
                        $"resolutionEvidence 不得超过 {ResolutionEvidenceMaxChars} 字");

                var orgId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                    "SELECT id FROM dbo.organization WHERE is_active=1", transaction: tx, cancellationToken: ct));
                var actor = ctx.User.Identity!.Name!;
                // 仅 OPEN→RESOLVED；跨 env/不存在不暴露（与 GET 详情同口径按宿主 env 定界）。
                var rows = await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE d SET d.status=N'RESOLVED', d.resolution_evidence=@evidence, d.resolved_by=@actor,
       d.resolved_at=SYSUTCDATETIME()
FROM dbo.reconciliation_difference d
JOIN dbo.reconciliation_run r ON r.id=d.run_id
WHERE d.id=@id AND d.status=N'OPEN' AND r.org_id=@orgId AND r.env=@env",
                    new { id, orgId, env, evidence, actor }, tx, cancellationToken: ct));
                if (rows == 0)
                {
                    var exists = await conn.ExecuteScalarAsync<int>(new CommandDefinition(@"
SELECT COUNT(*) FROM dbo.reconciliation_difference d
JOIN dbo.reconciliation_run r ON r.id=d.run_id
WHERE d.id=@id AND r.org_id=@orgId AND r.env=@env", new { id, orgId, env }, tx, cancellationToken: ct));
                    return exists == 0
                        ? ErrTuple(ctx, 404, "NOT_FOUND", "对账差异不存在")
                        : ErrTuple(ctx, 409, "DIFFERENCE_ALREADY_RESOLVED", "差异已解决，不能重复处理");
                }
                var resolvedAt = await conn.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
                    "SELECT resolved_at FROM dbo.reconciliation_difference WHERE id=@id", new { id }, tx,
                    cancellationToken: ct));
                // 控制面人工动作：审计 env 恒 LIVE；reason 为证据摘要（≤200）。
                await audit.AppendAsync(conn, tx, new AuditEntry(actor, "User", "recon.difference.resolved",
                    "reconciliation_difference", id.ToString(CultureInfo.InvariantCulture),
                    evidence.Length <= AuditReasonMaxChars ? evidence : evidence.Substring(0, AuditReasonMaxChars),
                    null, JsonSerializer.Serialize(new { status = "RESOLVED" }), ctx.TraceIdentifier, null, "LIVE"));
                return (200, JsonSerializer.Serialize(new
                {
                    data = new
                    {
                        differenceId = id.ToString(CultureInfo.InvariantCulture), status = "RESOLVED",
                        resolvedBy = actor, resolvedAt,
                    },
                    requestId = ctx.TraceIdentifier,
                }, Web));
            })).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.SystemManage));
    }

    // ---------------------------------------------------------------- 辅助

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

    private static bool TryParseUtc(string? raw, out DateTime value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        if (!DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)) return false;
        value = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
        return true;
    }

    /// <summary>summary/evidence 以 JSON 对象回显；历史行损坏（非 JSON）时返回 null 而不是 500。</summary>
    private static JsonElement? ParseJson(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
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

    /// <summary>与全局 <c>ApiErrors</c> 同形的标准错误体（code/message/fieldErrors/retryable/retryAfter/requestId 六字段）。</summary>
    private static IResult Err(HttpContext ctx, int status, string code, string message)
        => Results.Json(new
        {
            code, message, fieldErrors = Array.Empty<object>(), retryable = false, retryAfter = (int?)null,
            requestId = ctx.TraceIdentifier,
        }, statusCode: status);

    private static (int Status, string Body) ErrTuple(HttpContext ctx, int status, string code, string message)
        => (status, JsonSerializer.Serialize(new
        {
            code, message, fieldErrors = Array.Empty<object>(), retryable = false, retryAfter = (int?)null,
            requestId = ctx.TraceIdentifier,
        }));

    public sealed record ReconciliationRequestBody(string? WindowStart, string? WindowEnd, string? BusinessType);
    public sealed record ResolveRequestBody(string? ResolutionEvidence);

    private sealed class RunListRow
    {
        public long RunId { get; set; }
        public DateTime WindowStart { get; set; }
        public DateTime WindowEnd { get; set; }
        public string BusinessType { get; set; } = "";
        public string Status { get; set; } = "";
        public DateTime StartedAt { get; set; }
        public DateTime? FinishedAt { get; set; }
        public int DifferenceCount { get; set; }
    }

    private sealed class RunDetailRow
    {
        public long RunId { get; set; }
        public string Env { get; set; } = "";
        public DateTime WindowStart { get; set; }
        public DateTime WindowEnd { get; set; }
        public string BusinessType { get; set; } = "";
        public string Status { get; set; } = "";
        public string? LocalSummary { get; set; }
        public string? PlatformSummary { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime? FinishedAt { get; set; }
        public int DifferenceCount { get; set; }
    }

    private sealed class DifferenceListRow
    {
        public long DifferenceId { get; set; }
        public string DiffType { get; set; } = "";
        public string Severity { get; set; } = "";
        public string ObjectRef { get; set; } = "";
        public string Status { get; set; } = "";
        public string? OwnerRole { get; set; }
        public string? NextAction { get; set; }
        public DateTime FoundAt { get; set; }
        public string? EvidenceJson { get; set; }
        public string? ResolutionEvidence { get; set; }
        public string? ResolvedBy { get; set; }
        public DateTime? ResolvedAt { get; set; }
    }
}