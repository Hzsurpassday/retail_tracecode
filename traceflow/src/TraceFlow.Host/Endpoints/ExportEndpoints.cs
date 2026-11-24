using System.Globalization;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Application.Export;
using TraceFlow.Application.Security;
using TraceFlow.Host.Security;

namespace TraceFlow.Host.Endpoints;

/// <summary>
/// CSV 导出（AT-074 / DEF-07）：码台账、对账单差异、审计日志三分面，GET 只读。
/// 统一约束：
/// - Dapper <c>QueryUnbufferedAsync</c> + <see cref="StreamWriter"/> 直接写 <see cref="HttpResponse.Body"/>，不落临时明文文件；
/// - 先 COUNT 后写：预计超过 <see cref="ExportPolicy.MaxExportRows"/> 行 → 422 <c>EXPORT_TOO_LARGE</c>（一个字节都不写）；
/// - UTF-8 BOM 开头 + 表头 + 数据行，行尾 CRLF；文本列一律经 <see cref="Csv.Cell"/> 防公式注入；
/// - 成功导出后写审计 <c>data.exported</c>（actor=当前用户，env 恒 LIVE，object_type=export）；
/// - GET 无需 CSRF；需登录 + 对应权限（401/403 由权限策略统一处理）。
/// 口径约束：码导出的筛选与排序复用台账列表同一聚合 SQL（<see cref="CodesEndpoints.BaseCte"/>），不做分页。
/// </summary>
public static class ExportEndpoints
{
    private const int AuditReasonMaxChars = 200;
    private const int MaxNameChars = 128;
    private const string TooLargeMessage = "导出行数超过上限（50,000 行），请缩小筛选范围";

    public static void MapExportEndpoints(this WebApplication app, string connectionString, string env)
    {
        // ---------------------------------------------------------------- 码台账导出
        app.MapGet("/api/v1/exports/codes.csv", async (HttpContext ctx, IAuditLog audit, ILoggerFactory logs,
            string? q, string? eventType, string? from, string? to, string? state,
            [FromQuery(Name = "env")] string? envFilter) =>
        {
            var targetEnv = CodesEndpoints.ResolveEnv(envFilter, env);
            if (targetEnv is null) return Err(ctx, 400, "VALIDATION", "env 只能为 LIVE 或 MOCK");
            var prefix = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
            if (prefix is not null && prefix.Length < CodesEndpoints.MinPrefixLength)
                return Err(ctx, 400, "VALIDATION", $"q 至少 {CodesEndpoints.MinPrefixLength} 个字符（码前缀搜索）");
            string? typeFilter = null;
            if (!string.IsNullOrEmpty(eventType))
            {
                typeFilter = eventType.Trim().ToUpperInvariant();
                if (Array.IndexOf(CodesEndpoints.EventTypes, typeFilter) < 0)
                    return Err(ctx, 400, "VALIDATION",
                        "eventType 只能为 PURCHASE_IN、SALE、CONSUMER_RETURN 或 PURCHASE_RETURN");
            }
            string? stateFilter = null;
            if (!string.IsNullOrEmpty(state))
            {
                stateFilter = state.Trim().ToUpperInvariant();
                if (Array.IndexOf(CodesEndpoints.ClaimStateFilters, stateFilter) < 0)
                    return Err(ctx, 400, "VALIDATION", "state 只能为 ACTIVE、RELEASED 或 NONE");
            }
            if (!CodesEndpoints.TryParseDateFrom(from, out var fromUtc) ||
                !CodesEndpoints.TryParseDateTo(to, out var toUtc, out var toExclusive))
                return Err(ctx, 400, "VALIDATION", "from/to 须为 ISO-8601 日期或时间");

            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ctx.RequestAborted);
            var orgId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT id FROM dbo.organization WHERE is_active=1", cancellationToken: ctx.RequestAborted));
            var args = new DynamicParameters();
            args.Add("orgId", orgId);
            args.Add("env", targetEnv);
            if (prefix is not null) args.Add("prefix", CodesEndpoints.EscapeLikePrefix(prefix));
            if (typeFilter is not null) args.Add("eventType", typeFilter);
            if (fromUtc is DateTime f) args.Add("from", f);
            if (toUtc is DateTime t) args.Add("to", t);

            var baseCte = CodesEndpoints.BaseCte(prefix is not null, typeFilter is not null,
                fromUtc is not null, toUtc is not null, toExclusive);
            var filtersLatest = CodesEndpoints.EventFilters("e2", prefix is not null, typeFilter is not null,
                fromUtc is not null, toUtc is not null, toExclusive);
            var statePredicate = CodesEndpoints.StatePredicate(stateFilter);
            var total = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                $"{baseCte} SELECT COUNT(*) FROM ev WHERE {statePredicate}",
                args, cancellationToken: ctx.RequestAborted));
            if (ExportPolicy.ExceedsLimit(total))
                return Err(ctx, 422, "EXPORT_TOO_LARGE", TooLargeMessage);

            // 与台账列表同一口径（排序也一致：最近事件时间倒序、code_id 倒序），仅去掉分页以流式全量输出。
            var rows = conn.QueryUnbufferedAsync<CodeExportRow>($@"
{baseCte}
SELECT ev.code_id AS codeId, tc.code_normalized AS code, tc.format AS format,
       ev.lastEventAt AS lastEventAt, ev.eventCount AS eventCount,
       latest.event_type AS latestEventType,
       claim.claim_state AS claimState, claim.event_scope AS claimScope, part.bill_code AS billCode
FROM ev
JOIN dbo.trace_code tc ON tc.id=ev.code_id
OUTER APPLY (
    SELECT TOP 1 e2.event_type
    FROM dbo.code_event e2
    WHERE e2.code_id=ev.code_id AND e2.org_id=@orgId AND e2.env=@env{filtersLatest}
    ORDER BY e2.occurred_at DESC, e2.id DESC
) latest
OUTER APPLY (
    SELECT TOP 1 c.claim_state, c.event_scope, c.upload_task_id
    FROM dbo.code_claim c
    WHERE c.code_id=ev.code_id AND c.org_id=@orgId AND c.env=@env
    ORDER BY CASE WHEN c.claim_state=N'ACTIVE' THEN 0 ELSE 1 END, c.claimed_at DESC, c.id DESC
) claim
OUTER APPLY (
    SELECT TOP 1 p.bill_code
    FROM dbo.upload_part p
    WHERE claim.claim_state=N'ACTIVE' AND p.task_id=claim.upload_task_id
      AND p.org_id=@orgId AND p.env=@env
    ORDER BY p.part_index, p.id
) part
WHERE {statePredicate}
ORDER BY ev.lastEventAt DESC, ev.code_id DESC", args);

            var summary = Summary($"env={targetEnv}",
                prefix is null ? null : $"q={prefix}",
                typeFilter is null ? null : $"eventType={typeFilter}",
                stateFilter is null ? null : $"state={stateFilter}",
                fromUtc is DateTime ff ? $"from={ExportPolicy.UtcIso(ff)}" : null,
                toUtc is DateTime tt ? $"to={ExportPolicy.UtcIso(tt)}" : null);
            return await StreamCsvAsync(ctx, conn, audit, logs, "codes", summary, total, rows,
                r => new[]
                {
                    Csv.Cell(r.code), Csv.Cell(r.format), Csv.Cell(r.latestEventType),
                    Csv.Number(r.eventCount), Csv.Cell(ExportPolicy.UtcIso(r.lastEventAt)),
                    Csv.Cell(r.claimState ?? "NONE"), Csv.Cell(r.claimScope), Csv.Cell(r.billCode),
                },
                "码", "格式", "最近事件类型", "事件数", "最近事件时间", "占用状态", "占用范围", "单号");
        }).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.DataExport));

        // ---------------------------------------------------------------- 对账单差异导出
        app.MapGet("/api/v1/exports/reconciliation-runs/{id:long}/differences.csv",
            async (HttpContext ctx, long id, IAuditLog audit, ILoggerFactory logs) =>
        {
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ctx.RequestAborted);
            var orgId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT id FROM dbo.organization WHERE is_active=1", cancellationToken: ctx.RequestAborted));
            // 宿主 env 范围：跨 env / 不存在一律 404（不暴露另一环境的 run 是否存在），与 run 详情同口径。
            var runExists = await conn.ExecuteScalarAsync<int>(new CommandDefinition(@"
SELECT COUNT(*) FROM dbo.reconciliation_run r
WHERE r.id=@id AND r.org_id=@orgId AND r.env=@env",
                new { id, orgId, env }, cancellationToken: ctx.RequestAborted));
            if (runExists == 0)
                return Err(ctx, 404, "NOT_FOUND", "对账运行不存在");

            var total = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM dbo.reconciliation_difference WHERE run_id=@id",
                new { id }, cancellationToken: ctx.RequestAborted));
            if (ExportPolicy.ExceedsLimit(total))
                return Err(ctx, 422, "EXPORT_TOO_LARGE", TooLargeMessage);

            // 排序与 run 详情差异列表一致（found_at, id 升序）；evidence_json 不入导出（原始证据不外泄）。
            var rows = conn.QueryUnbufferedAsync<DifferenceExportRow>($@"
SELECT diff_type AS diffType, severity AS severity, object_ref AS objectRef, status AS status,
       owner_role AS ownerRole, next_action AS nextAction, found_at AS foundAt,
       resolution_evidence AS resolutionEvidence, resolved_by AS resolvedBy, resolved_at AS resolvedAt
FROM dbo.reconciliation_difference
WHERE run_id=@id
ORDER BY found_at, id", new { id });

            var summary = Summary($"env={env}", $"runId={id}");
            return await StreamCsvAsync(ctx, conn, audit, logs, $"reconciliation-run-{id}-differences",
                summary, total, rows,
                r => new[]
                {
                    Csv.Cell(r.diffType), Csv.Cell(r.severity), Csv.Cell(r.objectRef), Csv.Cell(r.status),
                    Csv.Cell(r.ownerRole), Csv.Cell(r.nextAction), Csv.Cell(ExportPolicy.UtcIso(r.foundAt)),
                    Csv.Cell(r.resolutionEvidence), Csv.Cell(r.resolvedBy),
                    Csv.Cell(r.resolvedAt is DateTime at ? ExportPolicy.UtcIso(at) : null),
                },
                "差异类型", "严重级", "对象", "状态", "责任角色", "下一步", "发现时间",
                "解决证据", "解决人", "解决时间");
        }).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.DataExport));

        // ---------------------------------------------------------------- 审计日志导出
        app.MapGet("/api/v1/exports/audit-logs.csv", async (HttpContext ctx, IAuditLog audit,
            ILoggerFactory logs, string? action, string? objectType, string? objectId,
            DateTime? dateStart, DateTime? dateEnd) =>
        {
            // 参数与既有 /api/v1/audit-logs 完全一致（action/objectType/objectId/dateStart/dateEnd），全量导出。
            var where = "WHERE 1=1";
            var args = new DynamicParameters();
            if (!string.IsNullOrEmpty(action)) { where += " AND action=@action"; args.Add("action", action); }
            if (!string.IsNullOrEmpty(objectType)) { where += " AND object_type=@objectType"; args.Add("objectType", objectType); }
            if (!string.IsNullOrEmpty(objectId)) { where += " AND object_id=@objectId"; args.Add("objectId", objectId); }
            if (dateStart is not null) { where += " AND occurred_at>=@dateStart"; args.Add("dateStart", NormalizeUtc(dateStart.Value)); }
            if (dateEnd is not null) { where += " AND occurred_at<=@dateEnd"; args.Add("dateEnd", NormalizeUtc(dateEnd.Value)); }

            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ctx.RequestAborted);
            var total = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                $"SELECT COUNT(*) FROM dbo.audit_log {where}", args, cancellationToken: ctx.RequestAborted));
            if (ExportPolicy.ExceedsLimit(total))
                return Err(ctx, 422, "EXPORT_TOO_LARGE", TooLargeMessage);

            var rows = conn.QueryUnbufferedAsync<AuditExportRow>($@"
SELECT occurred_at AS occurredAt, env AS env, actor AS actor, action AS action,
       object_type AS objectType, object_id AS objectId, reason AS reason,
       before_digest AS beforeDigest, after_digest AS afterDigest
FROM dbo.audit_log {where}
ORDER BY occurred_at DESC, id DESC", args);

            var summary = Summary(action is null ? null : $"action={action}",
                objectType is null ? null : $"objectType={objectType}",
                objectId is null ? null : $"objectId={objectId}",
                dateStart is null ? null : $"dateStart={ExportPolicy.UtcIso(NormalizeUtc(dateStart.Value))}",
                dateEnd is null ? null : $"dateEnd={ExportPolicy.UtcIso(NormalizeUtc(dateEnd.Value))}");
            return await StreamCsvAsync(ctx, conn, audit, logs, "audit-logs",
                string.IsNullOrEmpty(summary) ? "全量" : summary, total, rows,
                r => new[]
                {
                    Csv.Cell(ExportPolicy.UtcIso(r.occurredAt)), Csv.Cell(r.env), Csv.Cell(r.actor), Csv.Cell(r.action),
                    Csv.Cell(r.objectType), Csv.Cell(r.objectId), Csv.Cell(r.reason),
                    Csv.Cell(r.beforeDigest), Csv.Cell(r.afterDigest),
                },
                "时间", "环境", "操作人", "动作", "对象类型", "对象ID", "原因", "变更前", "变更后");
        }).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.AuditExport));
    }

    // ---------------------------------------------------------------- 辅助

    /// <summary>
    /// 流式写 CSV：BOM + 表头 + 行；写完（客户端完整收到）才写 <c>data.exported</c> 审计。
    /// 审计失败只记日志，不影响已交付的下载（响应体已写出，不能再改状态码）。
    /// </summary>
    private static async Task<IResult> StreamCsvAsync<T>(HttpContext ctx, SqlConnection conn, IAuditLog audit,
        ILoggerFactory loggerFactory, string exportName, string auditSummary, long count,
        IAsyncEnumerable<T> rows, Func<T, string[]> toCells, params string[] header)
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.ContentType = "text/csv; charset=utf-8";
        ctx.Response.Headers.ContentDisposition =
            $"attachment; filename=\"{exportName}-{stamp}.csv\"";
        ctx.Response.Headers.CacheControl = "no-store";

        // 显式写 BOM 字符，编码器本身不再发 BOM（避免双 BOM）。
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        try
        {
            await using (var writer = new StreamWriter(ctx.Response.Body, encoding, 16 * 1024, leaveOpen: true))
            {
                await writer.WriteAsync(Csv.Bom);
                await writer.WriteAsync(Csv.Line(header.Select(Csv.Cell).ToArray()));
                await foreach (var row in rows.WithCancellation(ctx.RequestAborted))
                    await writer.WriteAsync(Csv.Line(toCells(row)));
                await writer.FlushAsync(ctx.RequestAborted);
            }
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            // 客户端中途断开：不写审计（导出未完成）。
            return Results.Empty;
        }
        catch (Exception ex)
        {
            // 200/BOM 已写出后取数失败（如连接中断）：中断响应（客户端看到的是被 abort 的截断下载，
            // 不是看似完整的 CSV），并且不写 data.exported（导出未完成）。
            loggerFactory.CreateLogger("TraceFlow.Host.Endpoints.ExportEndpoints")
                .LogWarning(ex, "导出流中断，未写审计：{Export}", exportName);
            ctx.Abort();
            return Results.Empty;
        }

        try
        {
            var actor = ctx.User.Identity?.Name ?? "unknown";
            await audit.AppendAsync(conn, null, new AuditEntry(actor, "User", "data.exported", "export",
                Truncate(exportName, MaxNameChars),
                Truncate($"{auditSummary}; rows={count}", AuditReasonMaxChars),
                null, JsonSerializer.Serialize(new { rows = count, filter = auditSummary }),
                ctx.TraceIdentifier, null, "LIVE"));
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger("TraceFlow.Host.Endpoints.ExportEndpoints")
                .LogWarning(ex, "导出审计写入失败：{Export}", exportName);
        }
        return Results.Empty;
    }

    private static string Summary(params string?[] parts)
        => string.Join("; ", parts.Where(p => !string.IsNullOrEmpty(p)));

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max];

    private static DateTime NormalizeUtc(DateTime d)
        => d.Kind == DateTimeKind.Local ? d.ToUniversalTime() : DateTime.SpecifyKind(d, DateTimeKind.Utc);

    private static IResult Err(HttpContext ctx, int status, string code, string message)
        => Results.Json(new { code, message, retryable = false, requestId = ctx.TraceIdentifier }, statusCode: status);

    private sealed class CodeExportRow
    {
        public long codeId { get; set; }
        public string code { get; set; } = "";
        public string format { get; set; } = "";
        public DateTime lastEventAt { get; set; }
        public int eventCount { get; set; }
        public string? latestEventType { get; set; }
        public string? claimState { get; set; }
        public string? claimScope { get; set; }
        public string? billCode { get; set; }
    }

    private sealed class DifferenceExportRow
    {
        public string diffType { get; set; } = "";
        public string severity { get; set; } = "";
        public string objectRef { get; set; } = "";
        public string status { get; set; } = "";
        public string? ownerRole { get; set; }
        public string? nextAction { get; set; }
        public DateTime foundAt { get; set; }
        public string? resolutionEvidence { get; set; }
        public string? resolvedBy { get; set; }
        public DateTime? resolvedAt { get; set; }
    }

    private sealed class AuditExportRow
    {
        public DateTime occurredAt { get; set; }
        public string env { get; set; } = "";
        public string actor { get; set; } = "";
        public string action { get; set; } = "";
        public string objectType { get; set; } = "";
        public string? objectId { get; set; }
        public string? reason { get; set; }
        public string? beforeDigest { get; set; }
        public string? afterDigest { get; set; }
    }
}