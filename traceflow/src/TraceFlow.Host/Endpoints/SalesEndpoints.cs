using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Security;
using TraceFlow.Domain.Uploads;
using TraceFlow.Host.Security;
using TraceFlow.Infrastructure.Sales;

namespace TraceFlow.Host.Endpoints;

/// <summary>
/// POS 销售 / 退货 API（UI-06/07）：只读列表与钻取需登录；退货首次人工确认需 <c>return:confirm</c>。
/// 复用 InboundEndpoints 约定：camelCase、API-01 <c>{data,requestId}</c> 包裹、外部 ID 字符串、
/// ROW_NUMBER 区间分页（2008 R2）；错误经 ApiErrors 统一映射（VERSION_CONFLICT→409、RETURN_*/PREDECESSOR_*→422）。
/// </summary>
public static class SalesEndpoints
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;

    public static void MapSalesEndpoints(this WebApplication app, string connectionString, string env)
    {
        // ---------------------------------------------------------------- 销售快照列表
        app.MapGet("/api/v1/pos-sales", async (HttpContext ctx, int? page, int? pageSize, string? dateFrom,
            string? dateTo, [FromQuery(Name = "env")] string? envFilter) =>
        {
            if (!TryPaging(page, pageSize, out var p, out var size, out var pagingError))
                return PagingError(ctx, pagingError!);
            var targetEnv = ResolveEnv(envFilter, env);
            if (targetEnv is null) return Err(ctx, 400, "VALIDATION", "env 只能为 LIVE 或 MOCK");
            if (!TryParseDateFrom(dateFrom, out var from) || !TryParseDateTo(dateTo, out var to, out var toExclusive))
                return Err(ctx, 400, "VALIDATION", "dateFrom/dateTo 须为 ISO-8601 日期或时间");

            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ctx.RequestAborted);
            var orgId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT id FROM dbo.organization WHERE is_active=1", cancellationToken: ctx.RequestAborted));
            var args = new DynamicParameters();
            args.Add("orgId", orgId);
            args.Add("env", targetEnv);
            var filters = "";
            if (from is DateTime f) { filters += " AND s.business_datetime>=@from"; args.Add("from", f); }
            if (to is DateTime t)
            {
                filters += toExclusive ? " AND s.business_datetime<@to" : " AND s.business_datetime<=@to";
                args.Add("to", t);
            }
            // 快照本身无 env 列：归属由当前核验结论的 env 决定（MOCK 与 LIVE 分开计数）；
            // 尚未核验的快照不偏向任一环境，两个环境都可见，避免新单在列表里凭空消失。
            var envScope = @"
  AND (EXISTS (SELECT 1 FROM dbo.verification ve
               WHERE ve.source_snapshot_id=s.id AND ve.org_id=@orgId AND ve.env=@env AND ve.is_current=1)
       OR NOT EXISTS (SELECT 1 FROM dbo.verification ve
                      WHERE ve.source_snapshot_id=s.id AND ve.org_id=@orgId AND ve.is_current=1))";
            // 同一来源单只回最新版本；分页用 ROW_NUMBER 区间（2008 R2），不用 2012+ 分页子句。
            var baseSql = $@"
FROM (
    SELECT ROW_NUMBER() OVER (PARTITION BY s.source_document_key ORDER BY s.version DESC, s.id DESC) AS rn,
           s.id, s.source_document_key, s.version, s.business_datetime, s.completeness_status, s.observed_at
    FROM dbo.source_snapshot s
    WHERE s.org_id=@orgId AND s.document_type=N'SALE'{filters}{envScope}
) s
LEFT JOIN dbo.verification v
       ON v.source_snapshot_id=s.id AND v.org_id=@orgId AND v.env=@env AND v.is_current=1
LEFT JOIN dbo.upload_task t ON t.id=v.upload_task_id
WHERE s.rn=1";
            var total = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                $"SELECT COUNT(*) {baseSql}", args, cancellationToken: ctx.RequestAborted));
            args.Add("lo", (p - 1) * size + 1);
            args.Add("hi", p * size);
            var rows = await conn.QueryAsync<SaleListRow>(new CommandDefinition($@"
SELECT snapshotId,docKey,version,businessDatetimeUtc,completeness,lineCount,codeCount,
       verificationState,taskId,taskState,createdAt FROM (
    SELECT ROW_NUMBER() OVER (ORDER BY s.id DESC) AS rn2,
           s.id AS snapshotId, s.source_document_key AS docKey, s.version,
           s.business_datetime AS businessDatetimeUtc, s.completeness_status AS completeness,
           (SELECT COUNT(*) FROM dbo.source_line l
            WHERE l.snapshot_id=s.id AND l.event_kind=N'SALE') AS lineCount,
           (SELECT COUNT(*) FROM dbo.source_line l
            WHERE l.snapshot_id=s.id AND l.event_kind=N'SALE' AND l.trace_code_normalized IS NOT NULL) AS codeCount,
           v.state AS verificationState, t.id AS taskId, t.state AS taskState,
           s.observed_at AS createdAt
    {baseSql}
) x WHERE rn2 BETWEEN @lo AND @hi ORDER BY rn2", args, cancellationToken: ctx.RequestAborted));
            // API-01：外部 ID 一律 JSON 字符串；响应只含白名单字段（raw_header/raw_line/payload 永不外泄）。
            // source_snapshot 无 env 列：join 已把 v.env 钉死为查询环境（@env），所以行 env 即查询环境本身；
            // 未核验快照没有 env 属性，按 R-M3-17 在两个环境视图中都可见，同样回显查询环境。
            var items = rows.Select(r => new
            {
                snapshotId = r.snapshotId.ToString(CultureInfo.InvariantCulture),
                r.docKey, r.version, businessDatetimeUtc = r.businessDatetimeUtc,
                completeness = r.completeness, lineCount = r.lineCount, codeCount = r.codeCount,
                env = targetEnv, r.verificationState,
                taskId = r.taskId?.ToString(CultureInfo.InvariantCulture), r.taskState,
            }).ToList();
            return Results.Ok(new { data = new { items, page = p, pageSize = size, total }, requestId = ctx.TraceIdentifier });
        }).RequireAuthorization();

        // ---------------------------------------------------------------- 销售快照钻取
        app.MapGet("/api/v1/pos-sales/{id:long}", async (HttpContext ctx, long id,
            [FromQuery(Name = "env")] string? envFilter) =>
        {
            // 解析规则与列表一致：env 可选，非法值 400；快照不属于该环境视图（范围外）→ 404。
            var targetEnv = ResolveEnv(envFilter, env);
            if (targetEnv is null) return Err(ctx, 400, "VALIDATION", "env 只能为 LIVE 或 MOCK");

            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ctx.RequestAborted);
            var orgId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT id FROM dbo.organization WHERE is_active=1", cancellationToken: ctx.RequestAborted));
            // 与列表同一可见性口径：该环境有当前核验，或快照尚无任何当前核验（R-M3-17：未核验快照两视图都可见）。
            var snap = await conn.QuerySingleOrDefaultAsync<SnapRow>(new CommandDefinition(@"
SELECT id, source_document_key, version, business_datetime, completeness_status, content_hash, observed_at
FROM dbo.source_snapshot s
WHERE s.id=@id AND s.org_id=@orgId AND s.document_type=N'SALE'
  AND (EXISTS (SELECT 1 FROM dbo.verification ve
               WHERE ve.source_snapshot_id=s.id AND ve.org_id=@orgId AND ve.env=@env AND ve.is_current=1)
       OR NOT EXISTS (SELECT 1 FROM dbo.verification ve
                      WHERE ve.source_snapshot_id=s.id AND ve.org_id=@orgId AND ve.is_current=1))",
                new { id, orgId, env = targetEnv }, cancellationToken: ctx.RequestAborted));
            if (snap is null)
                return Err(ctx, 404, "NOT_FOUND", "销售快照不存在");
            var lines = (await conn.QueryAsync<SaleLineRow>(new CommandDefinition(@"
SELECT l.id AS lineId, l.source_line_key, l.product_source_id, l.quantity_original AS quantity,
       l.unit_original AS unit, l.batch_raw, l.trace_code_normalized, l.code_format, l.event_kind
FROM dbo.source_line l WHERE l.snapshot_id=@id ORDER BY l.id",
                new { id }, cancellationToken: ctx.RequestAborted))).ToList();
            var verification = await conn.QuerySingleOrDefaultAsync<VerificationRow>(new CommandDefinition(@"
SELECT id, state, rule_version, result_json, verified_at, is_current, upload_task_id
FROM dbo.verification WHERE org_id=@orgId AND env=@env AND source_snapshot_id=@id AND is_current=1",
                new { orgId, env = targetEnv, id }, cancellationToken: ctx.RequestAborted));
            var codes = verification is null
                ? new List<VerificationCodeRow>()
                : (await conn.QueryAsync<VerificationCodeRow>(new CommandDefinition(@"
SELECT vc.code_id AS codeId, tc.code_normalized, vc.source_line_id AS sourceLineId, vc.result, vc.reason, vc.checked_at
FROM dbo.verification_code vc JOIN dbo.trace_code tc ON tc.id=vc.code_id
WHERE vc.verification_id=@vid ORDER BY vc.id",
                    new { vid = verification.id }, cancellationToken: ctx.RequestAborted))).ToList();
            TaskRow? task = null;
            List<PartRow> parts = new();
            if (verification?.upload_task_id is long taskId)
            {
                task = await conn.QuerySingleOrDefaultAsync<TaskRow>(new CommandDefinition(@"
SELECT id, event_key, task_type, state, state_reason, version, created_at
FROM dbo.upload_task WHERE id=@taskId", new { taskId }, cancellationToken: ctx.RequestAborted));
                parts = (await conn.QueryAsync<PartRow>(new CommandDefinition(@"
SELECT id, part_index, bill_code, state FROM dbo.upload_part WHERE task_id=@taskId ORDER BY part_index",
                    new { taskId }, cancellationToken: ctx.RequestAborted))).ToList();
            }
            // result_json 只在 C# 侧解析（取 reason/failCode；绝不把原始 JSON 直接回显）。
            var (verifyReason, failCode) = ParseVerificationSummary(verification?.result_json);
            return Results.Ok(new { data = new
            {
                snapshot = new
                {
                    snapshotId = snap.id.ToString(CultureInfo.InvariantCulture), docKey = snap.source_document_key,
                    snap.version, businessDatetimeUtc = snap.business_datetime,
                    completeness = snap.completeness_status, contentHash = snap.content_hash,
                    observedAt = snap.observed_at,
                },
                lines = lines.Select(l => new
                {
                    lineId = l.lineId.ToString(CultureInfo.InvariantCulture), sourceLineKey = l.source_line_key,
                    productSourceId = l.product_source_id, quantity = Qty(l.quantity), l.unit,
                    batchRaw = l.batch_raw, traceCodeNormalized = l.trace_code_normalized,
                    codeFormat = l.code_format, eventKind = l.event_kind,
                }),
                codes = codes.Select(c => new
                {
                    codeId = c.codeId.ToString(CultureInfo.InvariantCulture), codeNormalized = c.code_normalized,
                    sourceLineId = c.sourceLineId.ToString(CultureInfo.InvariantCulture), c.result, c.reason,
                    checkedAt = c.checked_at,
                }),
                verification = verification is null ? null : new
                {
                    id = verification.id.ToString(CultureInfo.InvariantCulture), verification.state,
                    ruleVersion = verification.rule_version, verifyReason, failCode,
                    verifiedAt = verification.verified_at, isCurrent = verification.is_current,
                },
                task = task is null ? null : new
                {
                    taskId = task.id.ToString(CultureInfo.InvariantCulture), eventKey = task.event_key,
                    taskType = task.task_type, task.state, stateReason = task.state_reason, task.version,
                    createdAt = task.created_at,
                    parts = parts.Select(part => new
                    {
                        partId = part.id.ToString(CultureInfo.InvariantCulture), partIndex = part.part_index,
                        billCode = part.bill_code, part.state,
                    }),
                },
            }, requestId = ctx.TraceIdentifier });
        }).RequireAuthorization();

        // ---------------------------------------------------------------- 退货任务列表
        app.MapGet("/api/v1/returns", async (HttpContext ctx, int? page, int? pageSize, string? state,
            string? confirmStatus) =>
        {
            if (!TryPaging(page, pageSize, out var p, out var size, out var pagingError))
                return PagingError(ctx, pagingError!);
            if (!string.IsNullOrEmpty(state)
                && !Enum.TryParse<UploadState>(state, ignoreCase: false, out _))
                return Err(ctx, 422, "VALIDATION", "state 不是合法的上传状态");
            // 页签分组以 return_confirmation 为准（稳定，不依赖 state_reason 留存）：大小写敏感，非法值 422，
            // 且在读写前拒绝（绝不静默返回空列表）。
            if (!TryParseConfirmStatus(confirmStatus, out var confirmGroup))
                return Err(ctx, 422, "VALIDATION", "confirmStatus 只能为 PENDING、CONFIRMED 或 REJECTED");

            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ctx.RequestAborted);
            var orgId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT id FROM dbo.organization WHERE is_active=1", cancellationToken: ctx.RequestAborted));
            var args = new DynamicParameters();
            args.Add("orgId", orgId);
            args.Add("env", env);
            var filters = "";
            if (!string.IsNullOrEmpty(state)) { filters += " AND t.state=@state"; args.Add("state", state); }
            if (confirmGroup is not null) filters += " AND " + ConfirmGroupPredicate(confirmGroup);
            var where = $@"FROM dbo.upload_task t
WHERE t.org_id=@orgId AND t.env=@env
  AND t.task_type IN (N'CONSUMER_RETURN_116',N'PURCHASE_RETURN_202'){filters}";
            var total = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                $"SELECT COUNT(*) {where}", args, cancellationToken: ctx.RequestAborted));
            args.Add("lo", (p - 1) * size + 1);
            args.Add("hi", p * size);
            var rows = (await conn.QueryAsync<ReturnListRow>(new CommandDefinition($@"
SELECT id,eventKey,taskType,state,stateReason,version,predecessorTaskId,sourceSnapshotId,partCount,codeCount,createdAt
FROM (
    SELECT ROW_NUMBER() OVER (ORDER BY t.id DESC) AS rn2,
           t.id, t.event_key AS eventKey, t.task_type AS taskType, t.state, t.state_reason AS stateReason,
           t.version, t.predecessor_task_id AS predecessorTaskId, t.source_snapshot_id AS sourceSnapshotId,
           (SELECT COUNT(*) FROM dbo.upload_part p WHERE p.task_id=t.id) AS partCount,
           (SELECT COUNT(*) FROM dbo.code_event e WHERE e.upload_task_id=t.id) AS codeCount,
           t.created_at AS createdAt
    {where}
) x WHERE rn2 BETWEEN @lo AND @hi ORDER BY rn2", args, cancellationToken: ctx.RequestAborted))).ToList();
            var candidates = await LoadReturnCandidatesAsync(conn, orgId, env, rows, ctx.RequestAborted);
            var items = rows.Select(r => new
            {
                id = r.id.ToString(CultureInfo.InvariantCulture), r.eventKey, r.taskType, r.state,
                stateReason = r.stateReason, r.version,
                predecessorTaskId = r.predecessorTaskId?.ToString(CultureInfo.InvariantCulture),
                r.partCount, r.codeCount, r.createdAt,
                // 前序候选由当前核验 result_json.returnCandidates 提供（C# 侧解析，不在 SQL 里拆 JSON）。
                predecessorCandidates = candidates.TryGetValue(r.id, out var found) ? found : new List<object>(),
            }).ToList();
            return Results.Ok(new { data = new { items, page = p, pageSize = size, total }, requestId = ctx.TraceIdentifier });
        }).RequireAuthorization();

        // ---------------------------------------------------------------- 退货首次人工确认
        app.MapPost("/api/v1/returns/{id:long}/confirm", async (HttpContext ctx, long id, ReturnConfirmService svc) =>
        {
            var body = await ReadBodyAsync(ctx, ctx.RequestAborted);
            ReturnConfirmRequest? req;
            try
            {
                req = string.IsNullOrWhiteSpace(body) ? null : JsonSerializer.Deserialize<ReturnConfirmRequest>(body, Web);
            }
            catch (JsonException)
            {
                return Err(ctx, 400, "BAD_REQUEST", "请求体不是合法 JSON");
            }
            if (req is null)
                return Err(ctx, 422, "VALIDATION", "请求体必填");
            var evidenceJson = req.Evidence is JsonElement evidence ? EvidenceText(evidence) : "";
            var cmd = new ReturnConfirmCommand(req.ExpectedVersion, req.Decision ?? "",
                req.PredecessorEventIds ?? Array.Empty<long>(), evidenceJson, req.IdemKey ?? "",
                ctx.User.Identity!.Name!);
            var result = await svc.ConfirmAsync(id, cmd, ctx.RequestAborted);
            return Results.Ok(new { data = new
            {
                taskId = result.TaskId.ToString(CultureInfo.InvariantCulture), result.State, result.Idempotent,
            }, requestId = ctx.TraceIdentifier });
        }).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.ReturnConfirm));
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

    /// <summary>退货首次确认分组取值（大小写敏感）：PENDING / CONFIRMED / REJECTED。</summary>
    private const string ConfirmStatusPending = "PENDING";
    private const string ConfirmStatusConfirmed = "CONFIRMED";
    private const string ConfirmStatusRejected = "REJECTED";

    // 页签分组以 dbo.return_confirmation（0009：task_id UNIQUE，decision 只能取 CONFIRMED/REJECTED）为唯一
    // 权威事实源：state_reason 会被 SaleVerificationService / RetailTaskFactory 改写，按文本推导会把
    // 「驳回后被新快照取代」（Cancelled + RETURN_CONFIRM_REJECTED_SUPERSEDED）错落到已确认页签。

    /// <summary>确认记录子查询的公共前缀（按任务 + 机构 + 环境定界，避免跨环境串组）。</summary>
    private const string ConfirmRowSelect =
        "SELECT 1 FROM dbo.return_confirmation rc WHERE rc.task_id=t.id AND rc.org_id=@orgId AND rc.env=@env";

    private const string ConfirmRowExists = "EXISTS (" + ConfirmRowSelect + ")";

    /// <summary>待确认：仍是 Blocked 且原因为 RETURN_CONFIRM_REQUIRED，且尚无任何确认记录。</summary>
    private const string ConfirmPendingPredicate =
        "(t.state=N'Blocked' AND t.state_reason=N'RETURN_CONFIRM_REQUIRED' AND NOT " + ConfirmRowExists + ")";

    /// <summary>已驳回：存在 REJECTED 确认记录（覆盖被取代的历史驳回，无论当前 state 是什么）。</summary>
    private const string ConfirmRejectedPredicate = "EXISTS (" + ConfirmRowSelect + " AND rc.decision=N'REJECTED')";

    /// <summary>已确认：存在 CONFIRMED 确认记录（确认后 state/state_reason 如何改写都不影响分组）。</summary>
    private const string ConfirmConfirmedPredicate = "EXISTS (" + ConfirmRowSelect + " AND rc.decision=N'CONFIRMED')";

    private static string ConfirmGroupPredicate(string group) => group switch
    {
        ConfirmStatusPending => ConfirmPendingPredicate,
        ConfirmStatusRejected => ConfirmRejectedPredicate,
        _ => ConfirmConfirmedPredicate,
    };

    /// <summary>解析 confirmStatus：空/缺省表示不过滤；大小写敏感（不做 Trim/大小写归一），未知值 → false（422）。</summary>
    private static bool TryParseConfirmStatus(string? raw, out string? group)
    {
        group = null;
        if (raw is null || raw.Length == 0) return true;
        if (raw is not (ConfirmStatusPending or ConfirmStatusConfirmed or ConfirmStatusRejected)) return false;
        group = raw;
        return true;
    }

    private static bool TryParseDateFrom(string? raw, out DateTime? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(raw)) return true;
        if (!DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)) return false;
        value = raw.Trim().Length <= 10 ? parsed.Date : parsed;
        return true;
    }

    /// <summary>dateTo 仅日期时按「整日包含」转成次日 0 点排他；带时间时按闭区间。</summary>
    private static bool TryParseDateTo(string? raw, out DateTime? value, out bool exclusive)
    {
        value = null;
        exclusive = false;
        if (string.IsNullOrWhiteSpace(raw)) return true;
        if (!DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)) return false;
        if (raw.Trim().Length <= 10) { value = parsed.Date.AddDays(1); exclusive = true; }
        else value = parsed;
        return true;
    }

    /// <summary>
    /// 批量读取退货任务的当前核验结论里的前序候选（每快照一次查询，C# 解析 JSON），并补齐展示字段：
    /// code（trace_code.code_normalized）、occurredAt（code_event.occurred_at）、docKey（前序事件来源单据键，
    /// 经 code_event.source_snapshot_id → source_snapshot，退化到 source_line.snapshot_id 链）。
    /// 任一联查缺行只把该字段置 null，绝不丢候选（候选列表与核验结论一一对应）。
    /// </summary>
    private static async Task<Dictionary<long, List<object>>> LoadReturnCandidatesAsync(SqlConnection conn,
        long orgId, string env, IReadOnlyList<ReturnListRow> rows, CancellationToken ct)
    {
        var result = new Dictionary<long, List<object>>();
        var snapshotIds = rows.Where(r => r.sourceSnapshotId is not null)
            .Select(r => r.sourceSnapshotId!.Value).Distinct().ToList();
        if (snapshotIds.Count == 0) return result;
        var verifications = (await conn.QueryAsync<VerificationJsonRow>(new CommandDefinition(@"
SELECT source_snapshot_id, result_json FROM dbo.verification
WHERE org_id=@orgId AND env=@env AND is_current=1 AND source_snapshot_id IN @ids",
            new { orgId, env, ids = snapshotIds }, cancellationToken: ct))).ToList();
        var candidatesBySnapshot = new Dictionary<long, List<CandidateRef>>();
        foreach (var v in verifications)
        {
            if (string.IsNullOrWhiteSpace(v.result_json)) continue;
            try
            {
                using var doc = JsonDocument.Parse(v.result_json);
                if (!doc.RootElement.TryGetProperty("returnCandidates", out var list)
                    || list.ValueKind != JsonValueKind.Array) continue;
                var parsed = new List<CandidateRef>();
                foreach (var item in list.EnumerateArray())
                {
                    parsed.Add(new CandidateRef(Str(item, "codeId"), Str(item, "sourceLineId"),
                        Str(item, "predecessorEventId"), Str(item, "error")));
                }
                candidatesBySnapshot[v.source_snapshot_id] = parsed;
            }
            catch (JsonException)
            {
                // 历史 result_json 非法不影响列表读取（候选留空）。
            }
        }
        if (candidatesBySnapshot.Count == 0) return result;

        var all = candidatesBySnapshot.Values.SelectMany(list => list).ToList();
        var codeTexts = await LoadCodeTextsAsync(conn, all.Select(c => c.CodeId), ct);
        var predecessorFacts = await LoadPredecessorFactsAsync(conn, orgId, all.Select(c => c.PredecessorEventId), ct);

        var enriched = new Dictionary<long, List<object>>(candidatesBySnapshot.Count);
        foreach (var (snapshotId, parsed) in candidatesBySnapshot)
        {
            var items = new List<object>(parsed.Count);
            foreach (var c in parsed)
            {
                var codeId = ParseId(c.CodeId);
                var eventId = ParseId(c.PredecessorEventId);
                var fact = eventId is long key && predecessorFacts.TryGetValue(key, out var found) ? found : null;
                items.Add(new
                {
                    codeId = c.CodeId,
                    sourceLineId = c.SourceLineId,
                    predecessorEventId = c.PredecessorEventId,
                    error = c.Error,
                    code = codeId is long id && codeTexts.TryGetValue(id, out var text) ? text : null,
                    occurredAt = fact?.OccurredAt,
                    docKey = fact?.DocKey,
                });
            }
            enriched[snapshotId] = items;
        }
        foreach (var row in rows)
            if (row.sourceSnapshotId is long snap && enriched.TryGetValue(snap, out var found))
                result[row.id] = found;
        return result;

        static string? Str(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out var value)) return null;
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            };
        }
    }

    /// <summary>候选展示字段的联查批量大小（避免单条语句参数超限）。</summary>
    private const int CandidateBatchSize = 500;

    /// <summary>
    /// 码文本（按 codeId）；缺行不返回，只让对应候选的 code 置 null。
    /// 不做机构作用域：trace_code 是全局码台账（无 org_id 列），码本身全局唯一；
    /// 调用侧的 codeId 来自活动机构快照的当前核验结论，故不存在跨机构越权读取面。
    /// </summary>
    private static async Task<Dictionary<long, string>> LoadCodeTextsAsync(SqlConnection conn,
        IEnumerable<string?> codeIdTexts, CancellationToken ct)
    {
        var ids = codeIdTexts.Select(ParseId).Where(id => id is not null).Select(id => id!.Value)
            .Distinct().ToList();
        var map = new Dictionary<long, string>();
        for (var start = 0; start < ids.Count; start += CandidateBatchSize)
        {
            var chunk = ids.Skip(start).Take(CandidateBatchSize).ToList();
            var rows = await conn.QueryAsync<CandidateCodeRow>(new CommandDefinition(
                "SELECT id, code_normalized FROM dbo.trace_code WHERE id IN @ids",
                new { ids = chunk }, cancellationToken: ct));
            foreach (var row in rows) map[row.id] = row.code_normalized;
        }
        return map;
    }

    /// <summary>
    /// 前序事件事实：occurredAt 取 code_event.occurred_at；docKey 取事件来源单据键——
    /// 事件自带 source_snapshot_id 时直连 source_snapshot，否则（外部证据事件/来源行事件）经
    /// source_line 的 snapshot 链回查；两处都缺行 → null（候选保留，仅字段为空）。
    /// 纵深防御：事件与两处快照联查都按活动机构 org_id 收窄（source_line 无 org 列，经快照归属）。
    /// </summary>
    private static async Task<Dictionary<long, CandidatePredecessorFact>> LoadPredecessorFactsAsync(
        SqlConnection conn, long orgId, IEnumerable<string?> eventIdTexts, CancellationToken ct)
    {
        var ids = eventIdTexts.Select(ParseId).Where(id => id is not null).Select(id => id!.Value)
            .Distinct().ToList();
        var map = new Dictionary<long, CandidatePredecessorFact>();
        for (var start = 0; start < ids.Count; start += CandidateBatchSize)
        {
            var chunk = ids.Skip(start).Take(CandidateBatchSize).ToList();
            var rows = await conn.QueryAsync<CandidatePredecessorFactRow>(new CommandDefinition(@"
SELECT e.id, e.occurred_at AS occurredAt,
       COALESCE(snap.source_document_key, line_snap.source_document_key) AS docKey
FROM dbo.code_event e
LEFT JOIN dbo.source_snapshot snap ON snap.id=e.source_snapshot_id AND snap.org_id=@orgId
LEFT JOIN dbo.source_line l ON l.id=e.source_line_id
LEFT JOIN dbo.source_snapshot line_snap ON line_snap.id=l.snapshot_id AND line_snap.org_id=@orgId
WHERE e.org_id=@orgId AND e.id IN @ids", new { orgId, ids = chunk }, cancellationToken: ct));
            foreach (var row in rows) map[row.id] = new CandidatePredecessorFact(row.occurredAt, row.docKey);
        }
        return map;
    }

    private static long? ParseId(string? raw)
        => long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>result_json 里的展示摘要（reason/failCode）；解析失败返回空，不影响钻取。</summary>
    private static (string? Reason, string? FailCode) ParseVerificationSummary(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson)) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(resultJson);
            string? reason = doc.RootElement.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString() : null;
            string? failCode = doc.RootElement.TryGetProperty("failCode", out var f) && f.ValueKind == JsonValueKind.String
                ? f.GetString() : null;
            return (reason, failCode);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static async Task<string> ReadBodyAsync(HttpContext ctx, CancellationToken ct)
    {
        ctx.Request.EnableBuffering();
        ctx.Request.Body.Position = 0;
        using var reader = new StreamReader(ctx.Request.Body, System.Text.Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync(ct);
        ctx.Request.Body.Position = 0;
        return body;
    }

    /// <summary>evidence 可为 JSON 对象/数组/字符串；字符串按原值，其余保留原始 JSON 文本（服务端会再校验合法性）。</summary>
    private static string EvidenceText(JsonElement evidence) => evidence.ValueKind switch
    {
        JsonValueKind.String => evidence.GetString() ?? "",
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        _ => evidence.GetRawText(),
    };

    private static IResult PagingError(HttpContext ctx, string message) => Err(ctx, 400, "VALIDATION", message);

    private static IResult Err(HttpContext ctx, int status, string code, string message)
        => Results.Json(new { code, message, retryable = false, requestId = ctx.TraceIdentifier }, statusCode: status);

    private static string Qty(decimal v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    public sealed record ReturnConfirmRequest(long ExpectedVersion, string? Decision, long[]? PredecessorEventIds,
        JsonElement? Evidence, string? IdemKey);

    private sealed class SaleListRow
    {
        public long snapshotId { get; set; }
        public string docKey { get; set; } = "";
        public int version { get; set; }
        public DateTime? businessDatetimeUtc { get; set; }
        public string completeness { get; set; } = "";
        public int lineCount { get; set; }
        public int codeCount { get; set; }
        public string? verificationState { get; set; }
        public long? taskId { get; set; }
        public string? taskState { get; set; }
        public DateTime? createdAt { get; set; }
    }

    private sealed class SnapRow
    {
        public long id { get; set; }
        public string source_document_key { get; set; } = "";
        public int version { get; set; }
        public DateTime? business_datetime { get; set; }
        public string completeness_status { get; set; } = "";
        public string content_hash { get; set; } = "";
        public DateTime observed_at { get; set; }
    }

    private sealed class SaleLineRow
    {
        public long lineId { get; set; }
        public string source_line_key { get; set; } = "";
        public string product_source_id { get; set; } = "";
        public decimal quantity { get; set; }
        public string? unit { get; set; }
        public string? batch_raw { get; set; }
        public string? trace_code_normalized { get; set; }
        public string code_format { get; set; } = "";
        public string event_kind { get; set; } = "";
    }

    private sealed class VerificationRow
    {
        public long id { get; set; }
        public string state { get; set; } = "";
        public string rule_version { get; set; } = "";
        public string? result_json { get; set; }
        public DateTime? verified_at { get; set; }
        public bool is_current { get; set; }
        public long? upload_task_id { get; set; }
    }

    private sealed class VerificationCodeRow
    {
        public long codeId { get; set; }
        public string code_normalized { get; set; } = "";
        public long sourceLineId { get; set; }
        public string result { get; set; } = "";
        public string? reason { get; set; }
        public DateTime checked_at { get; set; }
    }

    private sealed class TaskRow
    {
        public long id { get; set; }
        public string event_key { get; set; } = "";
        public string task_type { get; set; } = "";
        public string state { get; set; } = "";
        public string? state_reason { get; set; }
        public int version { get; set; }
        public DateTime created_at { get; set; }
    }

    private sealed class PartRow
    {
        public long id { get; set; }
        public int part_index { get; set; }
        public string bill_code { get; set; } = "";
        public string state { get; set; } = "";
    }

    private sealed class ReturnListRow
    {
        public long id { get; set; }
        public string eventKey { get; set; } = "";
        public string taskType { get; set; } = "";
        public string state { get; set; } = "";
        public string? stateReason { get; set; }
        public int version { get; set; }
        public long? predecessorTaskId { get; set; }
        public long? sourceSnapshotId { get; set; }
        public int partCount { get; set; }
        public int codeCount { get; set; }
        public DateTime createdAt { get; set; }
    }

    private sealed class VerificationJsonRow
    {
        public long source_snapshot_id { get; set; }
        public string? result_json { get; set; }
    }

    /// <summary>核验 result_json.returnCandidates 的原始（未富化）字段。</summary>
    private sealed record CandidateRef(string? CodeId, string? SourceLineId, string? PredecessorEventId, string? Error);

    /// <summary>前序事件的展示事实：发生时间与来源单据键（缺行时 DocKey 为 null）。</summary>
    private sealed record CandidatePredecessorFact(DateTime OccurredAt, string? DocKey);

    private sealed class CandidateCodeRow
    {
        public long id { get; set; }
        public string code_normalized { get; set; } = "";
    }

    private sealed class CandidatePredecessorFactRow
    {
        public long id { get; set; }
        public DateTime occurredAt { get; set; }
        public string? docKey { get; set; }
    }
}