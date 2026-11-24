using System.Globalization;
using System.Text;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Security;
using TraceFlow.Host.Security;

namespace TraceFlow.Host.Endpoints;

/// <summary>
/// 追溯码台账（UI-08，只读）：码列表 + 单码钻取。权限 <c>trace:read</c>（逐请求查库，匿名 401）。
/// 沿用既有约定：camelCase、API-01 <c>{data,requestId}</c> 包裹、env 可选（缺省=宿主 env，非法 400）、
/// ROW_NUMBER 区间分页（2008 R2，禁用跳行/取行分页子句）、错误经 ApiErrors 统一映射。
/// 只读面：仅 GET；不提供改结果/释放占用入口（释放属 code:release 的另一能力）。
/// 脱敏：响应只取白名单列，绝不回显 raw_response / platform_evidence / evidence_json。
/// </summary>
public static class CodesEndpoints
{
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 100;
    internal const int MinPrefixLength = 4;
    private const int MaxEvents = 500;
    private const int MaxClaims = 200;
    private const int MaxResults = 200;
    private const int MaxConfirmations = 50;
    private const int MaxRelations = 100;

    /// <summary>事件类型取值（与 0005 CK_event_type 一致）。</summary>
    internal static readonly string[] EventTypes =
        { "PURCHASE_IN", "SALE", "CONSUMER_RETURN", "PURCHASE_RETURN" };

    /// <summary>占用状态筛选取值：ACTIVE=存在激活占用；RELEASED=仅曾释放；NONE=无任何占用。</summary>
    internal static readonly string[] ClaimStateFilters = { "ACTIVE", "RELEASED", "NONE" };

    public static void MapCodesEndpoints(this WebApplication app, string connectionString, string env)
    {
        // ---------------------------------------------------------------- 码台账列表
        app.MapGet("/api/v1/codes", async (HttpContext ctx, string? q, string? eventType, string? from,
            string? to, string? state, int? page, int? pageSize,
            [FromQuery(Name = "env")] string? envFilter) =>
        {
            if (!TryPaging(page, pageSize, out var p, out var size, out var pagingError))
                return Err(ctx, 400, "VALIDATION", pagingError!);
            var targetEnv = ResolveEnv(envFilter, env);
            if (targetEnv is null) return Err(ctx, 400, "VALIDATION", "env 只能为 LIVE 或 MOCK");
            // 码前缀：走 BIN2 列 code_normalized LIKE 前缀（不走全表模糊），长度 <4 拒绝以免命中过宽。
            var prefix = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
            if (prefix is not null && prefix.Length < MinPrefixLength)
                return Err(ctx, 400, "VALIDATION", $"q 至少 {MinPrefixLength} 个字符（码前缀搜索）");
            string? typeFilter = null;
            if (!string.IsNullOrEmpty(eventType))
            {
                typeFilter = eventType.Trim().ToUpperInvariant();
                if (Array.IndexOf(EventTypes, typeFilter) < 0)
                    return Err(ctx, 400, "VALIDATION",
                        "eventType 只能为 PURCHASE_IN、SALE、CONSUMER_RETURN 或 PURCHASE_RETURN");
            }
            string? stateFilter = null;
            if (!string.IsNullOrEmpty(state))
            {
                stateFilter = state.Trim().ToUpperInvariant();
                if (Array.IndexOf(ClaimStateFilters, stateFilter) < 0)
                    return Err(ctx, 400, "VALIDATION", "state 只能为 ACTIVE、RELEASED 或 NONE");
            }
            if (!TryParseDateFrom(from, out var fromUtc) || !TryParseDateTo(to, out var toUtc, out var toExclusive))
                return Err(ctx, 400, "VALIDATION", "from/to 须为 ISO-8601 日期或时间");

            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ctx.RequestAborted);
            var orgId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT id FROM dbo.organization WHERE is_active=1", cancellationToken: ctx.RequestAborted));
            var args = new DynamicParameters();
            args.Add("orgId", orgId);
            args.Add("env", targetEnv);
            if (prefix is not null) args.Add("prefix", EscapeLikePrefix(prefix));
            if (typeFilter is not null) args.Add("eventType", typeFilter);
            if (fromUtc is DateTime f) args.Add("from", f);
            if (toUtc is DateTime t) args.Add("to", t);

            // 基础集合 = 满足 q/eventType/from/to 的事件所属 distinct code_id（无事件的码不入列表）。
            // firstEventAt/lastEventAt/eventCount/latestEventType 均基于同一筛选后事件集合。
            var baseCte = BaseCte(prefix is not null, typeFilter is not null, fromUtc is not null,
                toUtc is not null, toExclusive);
            var filtersLatest = EventFilters("e2", prefix is not null, typeFilter is not null,
                fromUtc is not null, toUtc is not null, toExclusive);
            var statePredicate = StatePredicate(stateFilter);
            var total = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                $"{baseCte} SELECT COUNT(*) FROM ev WHERE {statePredicate}",
                args, cancellationToken: ctx.RequestAborted));
            args.Add("lo", (p - 1) * size + 1);
            args.Add("hi", p * size);
            // 2008 R2：ROW_NUMBER 区间分页；最新事件类型/占用/单号用 OUTER APPLY TOP 1（禁 2012+ 分页子句）。
            var rows = (await conn.QueryAsync<CodeListRow>(new CommandDefinition($@"
{baseCte},
paged AS (
    SELECT ev.code_id, ev.firstEventAt, ev.lastEventAt, ev.eventCount,
           ROW_NUMBER() OVER (ORDER BY ev.lastEventAt DESC, ev.code_id DESC) AS rn
    FROM ev
    WHERE {statePredicate}
)
SELECT f.code_id AS codeId, tc.code_normalized AS code, tc.code_raw AS codeRaw, tc.format AS format,
       tc.first_seen_at AS firstSeenAt, f.firstEventAt, f.lastEventAt, f.eventCount,
       latest.event_type AS latestEventType,
       claim.claim_state AS claimState, claim.event_scope AS claimScope, part.bill_code AS billCode
FROM paged f
JOIN dbo.trace_code tc ON tc.id=f.code_id
OUTER APPLY (
    SELECT TOP 1 e2.event_type
    FROM dbo.code_event e2
    WHERE e2.code_id=f.code_id AND e2.org_id=@orgId AND e2.env=@env{filtersLatest}
    ORDER BY e2.occurred_at DESC, e2.id DESC
) latest
OUTER APPLY (
    -- 当前激活占用优先；否则取最近一次已释放占用（历史只读展示）。
    SELECT TOP 1 c.claim_state, c.event_scope, c.upload_task_id
    FROM dbo.code_claim c
    WHERE c.code_id=f.code_id AND c.org_id=@orgId AND c.env=@env
    ORDER BY CASE WHEN c.claim_state=N'ACTIVE' THEN 0 ELSE 1 END, c.claimed_at DESC, c.id DESC
) claim
OUTER APPLY (
    -- 单号只取激活占用任务的分片（已释放占用不回单号，避免把历史单当当前占用）。
    SELECT TOP 1 p.bill_code
    FROM dbo.upload_part p
    WHERE claim.claim_state=N'ACTIVE' AND p.task_id=claim.upload_task_id
      AND p.org_id=@orgId AND p.env=@env
    ORDER BY p.part_index, p.id
) part
WHERE f.rn BETWEEN @lo AND @hi
ORDER BY f.rn", args, cancellationToken: ctx.RequestAborted))).ToList();
            var items = rows.Select(r => new
            {
                code = r.code, codeRaw = r.codeRaw, format = r.format,
                firstSeenAt = r.firstSeenAt, firstEventAt = r.firstEventAt, lastEventAt = r.lastEventAt,
                r.eventCount, latestEventType = r.latestEventType,
                claimState = r.claimState ?? "NONE", claimScope = r.claimScope, billCode = r.billCode,
            }).ToList();
            return Results.Ok(new
            {
                data = new { items, page = p, pageSize = size, total, env = targetEnv },
                requestId = ctx.TraceIdentifier,
            });
        }).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.TraceRead));

        // ---------------------------------------------------------------- 单码钻取
        app.MapGet("/api/v1/codes/{code}", async (HttpContext ctx, string code,
            [FromQuery(Name = "env")] string? envFilter) =>
        {
            var targetEnv = ResolveEnv(envFilter, env);
            if (targetEnv is null) return Err(ctx, 400, "VALIDATION", "env 只能为 LIVE 或 MOCK");

            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ctx.RequestAborted);
            var orgId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT id FROM dbo.organization WHERE is_active=1", cancellationToken: ctx.RequestAborted));
            // 码身份全局唯一（trace_code 无 org 列）；可见性由「本 org+env 存在该码事件」定界：
            // 只在 MOCK 有事件的码在 LIVE 视图中 404（与列表「无事件不入列表」同一口径）。
            var codeRow = await conn.QuerySingleOrDefaultAsync<CodeRow>(new CommandDefinition(@"
SELECT tc.id, tc.code_normalized AS code, tc.code_raw AS codeRaw, tc.format AS format,
       tc.first_seen_at AS firstSeenAt
FROM dbo.trace_code tc
WHERE tc.code_normalized=@code
  AND EXISTS (SELECT 1 FROM dbo.code_event e
              WHERE e.code_id=tc.id AND e.org_id=@orgId AND e.env=@env)",
                new { code, orgId, env = targetEnv }, cancellationToken: ctx.RequestAborted));
            if (codeRow is null)
                return Err(ctx, 404, "NOT_FOUND", "追溯码不存在");

            var events = (await conn.QueryAsync<EventRow>(new CommandDefinition($@"
SELECT TOP ({MaxEvents}) id AS eventId, event_type AS eventType, direction, occurred_at AS occurredAt,
       business_datetime AS businessDatetime, source_snapshot_id AS sourceSnapshotId,
       source_line_id AS sourceLineId, upload_task_id AS uploadTaskId,
       predecessor_event_id AS predecessorEventId
FROM dbo.code_event
WHERE code_id=@id AND org_id=@orgId AND env=@env
ORDER BY occurred_at, id",
                new { id = codeRow.id, orgId, env = targetEnv }, cancellationToken: ctx.RequestAborted))).ToList();
            var claims = (await conn.QueryAsync<ClaimRow>(new CommandDefinition($@"
SELECT TOP ({MaxClaims}) claim_state AS claimState, event_scope AS eventScope,
       upload_task_id AS uploadTaskId, claimed_at AS claimedAt, released_at AS releasedAt,
       released_by AS releasedBy
FROM dbo.code_claim
WHERE code_id=@id AND org_id=@orgId AND env=@env
ORDER BY claimed_at, id",
                new { id = codeRow.id, orgId, env = targetEnv }, cancellationToken: ctx.RequestAborted))).ToList();
            // 逐码平台结果：分片按同 task+env 定界（code_result 自身无 env 列）；证据列一律不取。
            var results = (await conn.QueryAsync<ResultRow>(new CommandDefinition($@"
SELECT TOP ({MaxResults}) r.task_id AS taskId, r.result, r.reason, r.recorded_at AS recordedAt,
       p.bill_code AS billCode, p.state AS partState, t.state AS taskState,
       a.request_id AS requestId, a.http_status AS httpStatus
FROM dbo.code_result r
JOIN dbo.upload_part p ON p.id=r.part_id AND p.org_id=@orgId AND p.env=@env
JOIN dbo.upload_task t ON t.id=r.task_id AND t.org_id=@orgId AND t.env=@env
JOIN dbo.upload_attempt a ON a.id=r.attempt_id AND a.env=@env
WHERE r.code_id=@id
ORDER BY r.recorded_at DESC, r.id DESC",
                new { id = codeRow.id, orgId, env = targetEnv }, cancellationToken: ctx.RequestAborted))).ToList();
            // 退货确认：码关联任务（逐码结果任务 ∪ 事件任务）的确认记录，同 env。
            var confirmations = (await conn.QueryAsync<ConfirmationRow>(new CommandDefinition($@"
SELECT TOP ({MaxConfirmations}) rc.task_id AS taskId, rc.decision, rc.relation_kind AS relationKind,
       rc.predecessor_task_id AS predecessorTaskId, rc.confirmed_by AS confirmedBy,
       rc.confirmed_at AS confirmedAt
FROM dbo.return_confirmation rc
WHERE rc.org_id=@orgId AND rc.env=@env
  AND rc.task_id IN (
      SELECT r.task_id FROM dbo.code_result r
      JOIN dbo.upload_part p ON p.id=r.part_id AND p.org_id=@orgId AND p.env=@env
      WHERE r.code_id=@id
      UNION
      SELECT e.upload_task_id FROM dbo.code_event e
      WHERE e.code_id=@id AND e.org_id=@orgId AND e.env=@env AND e.upload_task_id IS NOT NULL)
ORDER BY rc.confirmed_at DESC, rc.id DESC",
                new { id = codeRow.id, orgId, env = targetEnv }, cancellationToken: ctx.RequestAborted))).ToList();
            var parents = (await conn.QueryAsync<RelationRow>(new CommandDefinition($@"
SELECT TOP ({MaxRelations}) parent.code_normalized AS code, r.relation_source AS relationSource,
       r.observed_at AS observedAt
FROM dbo.code_relation r
JOIN dbo.trace_code parent ON parent.id=r.parent_code_id
WHERE r.child_code_id=@id
ORDER BY r.observed_at DESC, r.id DESC",
                new { id = codeRow.id }, cancellationToken: ctx.RequestAborted))).ToList();
            var children = (await conn.QueryAsync<RelationRow>(new CommandDefinition($@"
SELECT TOP ({MaxRelations}) child.code_normalized AS code, r.relation_source AS relationSource,
       r.observed_at AS observedAt
FROM dbo.code_relation r
JOIN dbo.trace_code child ON child.id=r.child_code_id
WHERE r.parent_code_id=@id
ORDER BY r.observed_at DESC, r.id DESC",
                new { id = codeRow.id }, cancellationToken: ctx.RequestAborted))).ToList();

            return Results.Ok(new
            {
                data = new
                {
                    code = new
                    {
                        code = codeRow.code, codeRaw = codeRow.codeRaw, format = codeRow.format,
                        firstSeenAt = codeRow.firstSeenAt, env = targetEnv,
                    },
                    // eventId/taskId 等对象标识为字符串（API-01）；sourceSnapshotId/uploadTaskId 与 M2/M3 一致为数字。
                    events = events.Select(e => new
                    {
                        eventId = e.eventId.ToString(CultureInfo.InvariantCulture), e.eventType, e.direction,
                        e.occurredAt, e.businessDatetime, e.sourceSnapshotId, e.sourceLineId, e.uploadTaskId,
                        predecessorEventId = e.predecessorEventId?.ToString(CultureInfo.InvariantCulture),
                    }),
                    claims = claims.Select(c => new
                    {
                        c.claimState, c.eventScope, c.uploadTaskId, c.claimedAt, c.releasedAt, c.releasedBy,
                    }),
                    results = results.Select(r => new
                    {
                        taskId = r.taskId.ToString(CultureInfo.InvariantCulture), r.result, r.reason, r.recordedAt,
                        r.billCode, r.partState, r.taskState, r.requestId, r.httpStatus,
                    }),
                    confirmations = confirmations.Select(c => new
                    {
                        taskId = c.taskId.ToString(CultureInfo.InvariantCulture), c.decision, c.relationKind,
                        predecessorTaskId = c.predecessorTaskId?.ToString(CultureInfo.InvariantCulture),
                        c.confirmedBy, c.confirmedAt,
                    }),
                    relations = new
                    {
                        parents = parents.Select(r => new { r.code, r.relationSource, r.observedAt }),
                        children = children.Select(r => new { r.code, r.relationSource, r.observedAt }),
                    },
                },
                requestId = ctx.TraceIdentifier,
            });
        }).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.TraceRead));
    }

    // ---------------------------------------------------------------- 辅助

    /// <summary>
    /// 基础集合 CTE（列表与 CSV 导出共用，口径一致）：满足筛选的事件所属 distinct code_id 及聚合。
    /// </summary>
    internal static string BaseCte(bool hasPrefix, bool hasType, bool hasFrom, bool hasTo, bool toExclusive)
    {
        var filters = EventFilters("e", hasPrefix, hasType, hasFrom, hasTo, toExclusive);
        return $@"
;WITH ev AS (
    SELECT e.code_id,
           MIN(e.occurred_at) AS firstEventAt,
           MAX(e.occurred_at) AS lastEventAt,
           COUNT(*) AS eventCount
    FROM dbo.code_event e
    WHERE e.org_id=@orgId AND e.env=@env{filters}
    GROUP BY e.code_id
)";
    }

    /// <summary>事件筛选片段（按别名复用：基础集合用 e，最新事件 APPLY 用 e2）。</summary>
    internal static string EventFilters(string alias, bool hasPrefix, bool hasType, bool hasFrom, bool hasTo,
        bool toExclusive)
    {
        var sb = new StringBuilder();
        if (hasPrefix)
            // @prefix 已由 EscapeLikePrefix 转成字面量模式（含尾部 %）：用户输入的 %/_/[ 不得当通配符。
            sb.Append($" AND {alias}.code_id IN (SELECT tc.id FROM dbo.trace_code tc ")
              .Append("WHERE tc.code_normalized LIKE @prefix ESCAPE '\\')");
        if (hasType) sb.Append($" AND {alias}.event_type=@eventType");
        if (hasFrom) sb.Append($" AND {alias}.occurred_at>=@from");
        if (hasTo) sb.Append(toExclusive ? $" AND {alias}.occurred_at<@to" : $" AND {alias}.occurred_at<=@to");
        return sb.ToString();
    }

    /// <summary>
    /// 用户码前缀 → LIKE 字面量模式（尾部追加 <c>%</c>）：先按 <c>\ % _ [</c> 顺序转义，
    /// 配合 SQL 的 <c>ESCAPE '\'</c>（SQL Server 2008 R2 支持）让 <c>69_0</c>/<c>69%</c> 只匹配字面量，
    /// 不再被当成通配符。列表与 CSV 导出共用同一模式（同一取数口径）。
    /// </summary>
    internal static string EscapeLikePrefix(string q)
        => q.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[") + "%";

    /// <summary>占用状态谓词（作用于 CTE ev）：ACTIVE / RELEASED（无激活且有释放）/ NONE（无任何占用）。</summary>
    internal static string StatePredicate(string? state)
    {
        const string any = "SELECT 1 FROM dbo.code_claim c " +
                           "WHERE c.code_id=ev.code_id AND c.org_id=@orgId AND c.env=@env";
        return state switch
        {
            "ACTIVE" => $"EXISTS ({any} AND c.claim_state=N'ACTIVE')",
            "RELEASED" => $"NOT EXISTS ({any} AND c.claim_state=N'ACTIVE') " +
                          $"AND EXISTS ({any} AND c.claim_state=N'RELEASED')",
            "NONE" => $"NOT EXISTS ({any})",
            _ => "1=1",
        };
    }

    /// <summary>分页口径：page&lt;1 或 pageSize&lt;1 → 400；pageSize&gt;100 收敛到 100；默认 50。</summary>
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

    internal static string? ResolveEnv(string? requested, string current)
    {
        if (string.IsNullOrWhiteSpace(requested)) return current;
        var value = requested.Trim().ToUpperInvariant();
        return value is "LIVE" or "MOCK" ? value : null;
    }

    internal static bool TryParseDateFrom(string? raw, out DateTime? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(raw)) return true;
        if (!DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)) return false;
        value = raw.Trim().Length <= 10 ? parsed.Date : parsed;
        return true;
    }

    /// <summary>to 仅日期时按「整日包含」转成次日 0 点排他；带时间时按闭区间。</summary>
    internal static bool TryParseDateTo(string? raw, out DateTime? value, out bool exclusive)
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

    private static IResult Err(HttpContext ctx, int status, string code, string message)
        => Results.Json(new { code, message, retryable = false, requestId = ctx.TraceIdentifier }, statusCode: status);

    private sealed class CodeListRow
    {
        public long codeId { get; set; }
        public string code { get; set; } = "";
        public string codeRaw { get; set; } = "";
        public string format { get; set; } = "";
        public DateTime firstSeenAt { get; set; }
        public DateTime firstEventAt { get; set; }
        public DateTime lastEventAt { get; set; }
        public int eventCount { get; set; }
        public string? latestEventType { get; set; }
        public string? claimState { get; set; }
        public string? claimScope { get; set; }
        public string? billCode { get; set; }
    }

    private sealed class CodeRow
    {
        public long id { get; set; }
        public string code { get; set; } = "";
        public string codeRaw { get; set; } = "";
        public string format { get; set; } = "";
        public DateTime firstSeenAt { get; set; }
    }

    private sealed class EventRow
    {
        public long eventId { get; set; }
        public string eventType { get; set; } = "";
        public string direction { get; set; } = "";
        public DateTime occurredAt { get; set; }
        public DateTime? businessDatetime { get; set; }
        public long? sourceSnapshotId { get; set; }
        public long sourceLineId { get; set; }
        public long? uploadTaskId { get; set; }
        public long? predecessorEventId { get; set; }
    }

    private sealed class ClaimRow
    {
        public string claimState { get; set; } = "";
        public string eventScope { get; set; } = "";
        public long? uploadTaskId { get; set; }
        public DateTime claimedAt { get; set; }
        public DateTime? releasedAt { get; set; }
        public string? releasedBy { get; set; }
    }

    private sealed class ResultRow
    {
        public long taskId { get; set; }
        public string result { get; set; } = "";
        public string? reason { get; set; }
        public DateTime recordedAt { get; set; }
        public string billCode { get; set; } = "";
        public string partState { get; set; } = "";
        public string taskState { get; set; } = "";
        public string? requestId { get; set; }
        public int? httpStatus { get; set; }
    }

    private sealed class ConfirmationRow
    {
        public long taskId { get; set; }
        public string decision { get; set; } = "";
        public string relationKind { get; set; } = "";
        public long? predecessorTaskId { get; set; }
        public string confirmedBy { get; set; } = "";
        public DateTime confirmedAt { get; set; }
    }

    private sealed class RelationRow
    {
        public string code { get; set; } = "";
        public string relationSource { get; set; } = "";
        public DateTime observedAt { get; set; }
    }
}