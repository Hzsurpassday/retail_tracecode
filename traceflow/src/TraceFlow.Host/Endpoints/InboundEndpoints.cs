using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Application.Security;
using TraceFlow.Host.Security;
using TraceFlow.Infrastructure.Inbound;

namespace TraceFlow.Host.Endpoints;

/// <summary>
/// 入库关联 API（IN-03/04/06/07/08）：列表/详情只读（trace:read）；重匹配（job:rematch）；
/// 保存/确认/取消补配（inbound:allocate）全部走 Idempotency-Key + 服务层异常→ApiErrors 统一映射。
/// </summary>
public static class InboundEndpoints
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static void MapInboundEndpoints(this WebApplication app, string connectionString, string env)
    {
        app.MapGet("/api/v1/inbounds", async (HttpContext ctx, int? page, int? pageSize, string? matchState, string? docKey) =>
        {
            var p = Math.Max(1, page ?? 1);
            var size = Math.Clamp(pageSize ?? 50, 1, 200);
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ctx.RequestAborted);
            var orgId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT id FROM dbo.organization WHERE is_active=1", cancellationToken: ctx.RequestAborted));
            var where = "WHERE s.rn=1";
            var args = new DynamicParameters();
            args.Add("orgId", orgId);
            args.Add("env", env);
            if (!string.IsNullOrEmpty(matchState)) { where += " AND m.match_state=@matchState"; args.Add("matchState", matchState); }
            if (!string.IsNullOrEmpty(docKey)) { where += " AND s.source_document_key=@docKey"; args.Add("docKey", docKey); }
            // 每个 docKey 只回最新版本（ROW_NUMBER）；分页用 rn2 区间而非 2012 版分页关键字（2008 R2）
            var baseSql = $@"
FROM (
    SELECT ROW_NUMBER() OVER (PARTITION BY source_document_key ORDER BY version DESC, id DESC) AS rn,
           id, source_document_key, version, business_datetime, completeness_status
    FROM dbo.source_snapshot WHERE org_id=@orgId AND document_type=N'PURCHASE_IN'
) s
LEFT JOIN dbo.inbound_match m ON m.source_snapshot_id=s.id AND m.org_id=@orgId AND m.env=@env
LEFT JOIN dbo.upload_task t ON t.source_snapshot_id=s.id AND t.org_id=@orgId AND t.env=@env AND t.task_type=N'PURCHASE_IN_102'
{where}";
            var total = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                $"SELECT COUNT(*) {baseSql}", args, cancellationToken: ctx.RequestAborted));
            args.Add("lo", (p - 1) * size + 1);
            args.Add("hi", p * size);
            var rows = await conn.QueryAsync<InboundListRow>(new CommandDefinition($@"
SELECT snapshotId,docKey,version,businessDatetimeUtc,completeness,matchState,matchVersion,reason,supplierDwbh,taskId,taskState FROM (
    SELECT ROW_NUMBER() OVER (ORDER BY s.id DESC) AS rn2,
           s.id AS snapshotId, s.source_document_key AS docKey, s.version, s.business_datetime AS businessDatetimeUtc,
           s.completeness_status AS completeness, m.match_state AS matchState, m.version AS matchVersion, m.reason,
           m.supplier_dwbh AS supplierDwbh, t.id AS taskId, t.state AS taskState
    {baseSql}
) x WHERE rn2 BETWEEN @lo AND @hi ORDER BY rn2", args, cancellationToken: ctx.RequestAborted));
            return Results.Ok(new { data = new { records = rows, total, page = p, pageSize = size }, requestId = ctx.TraceIdentifier });
        }).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.TraceRead));

        app.MapGet("/api/v1/inbounds/{id:long}", async (HttpContext ctx, long id) =>
        {
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ctx.RequestAborted);
            var orgId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT id FROM dbo.organization WHERE is_active=1", cancellationToken: ctx.RequestAborted));
            var snap = await conn.QuerySingleOrDefaultAsync<SnapDetailRow>(new CommandDefinition(
                "SELECT id, source_document_key, version, business_datetime, completeness_status, content_hash FROM dbo.source_snapshot WHERE id=@id AND org_id=@orgId AND document_type=N'PURCHASE_IN'",
                new { id, orgId }, cancellationToken: ctx.RequestAborted));
            if (snap is null)
                return Results.Json(new { code = "NOT_FOUND", message = "入库快照不存在", retryable = false, requestId = ctx.TraceIdentifier }, statusCode: 404);
            var lines = await conn.QueryAsync<LineDetailRow>(new CommandDefinition(@"
SELECT l.id AS lineId, l.source_line_key AS sourceLineKey, l.product_source_id AS productSourceId, l.quantity_original AS quantity,
       l.unit_original AS unit, l.batch_raw AS batchRaw, p.trace_classification AS classification
FROM dbo.source_line l
LEFT JOIN dbo.product p ON p.org_id=@orgId AND p.source_system_id=(SELECT source_system_id FROM dbo.source_snapshot WHERE id=@id) AND p.spid=l.product_source_id
WHERE l.snapshot_id=@id AND l.event_kind=N'PURCHASE_IN' ORDER BY l.id", new { id, orgId }, cancellationToken: ctx.RequestAborted));
            var match = await conn.QuerySingleOrDefaultAsync<MatchDetailRow>(new CommandDefinition(
                "SELECT match_state AS matchState, version, reason FROM dbo.inbound_match WHERE org_id=@orgId AND env=@env AND source_snapshot_id=@id",
                new { orgId, env, id }, cancellationToken: ctx.RequestAborted));
            var allocs = (await conn.QueryAsync<AllocDetailRow>(new CommandDefinition(@"
SELECT a.id AS allocationId, a.source_line_id AS sourceLineId, a.upstream_line_id AS upstreamLineId, a.quantity,
       a.unit_ratio_numerator AS num, a.unit_ratio_denominator AS den, a.state
FROM dbo.inbound_allocation a JOIN dbo.source_line l ON l.id=a.source_line_id
WHERE a.org_id=@orgId AND a.env=@env AND l.snapshot_id=@id AND a.state IN (N'RESERVED',N'COMMITTED') ORDER BY a.id",
                new { orgId, env, id }, cancellationToken: ctx.RequestAborted))).ToList();
            var allocCodes = allocs.Count == 0 ? new List<AllocCodeDetailRow>() : (await conn.QueryAsync<AllocCodeDetailRow>(new CommandDefinition(@"
SELECT ac.allocation_id AS allocationId, ac.code_id AS codeId, tc.code_normalized AS codeNormalized
FROM dbo.allocation_code ac JOIN dbo.trace_code tc ON tc.id=ac.code_id WHERE ac.allocation_id IN @ids",
                new { ids = allocs.Select(a => a.allocationId).ToList() }, cancellationToken: ctx.RequestAborted))).ToList();
            var task = await conn.QuerySingleOrDefaultAsync<TaskDetailRow>(new CommandDefinition(@"
SELECT t.id AS taskId, t.state, p.bill_code AS billCode
FROM dbo.upload_task t LEFT JOIN dbo.upload_part p ON p.task_id=t.id AND p.part_index=0
WHERE t.org_id=@orgId AND t.env=@env AND t.source_snapshot_id=@id AND t.task_type=N'PURCHASE_IN_102'",
                new { orgId, env, id }, cancellationToken: ctx.RequestAborted));
            return Results.Ok(new { data = new {
                snapshot = new { snapshotId = snap.id, docKey = snap.source_document_key, snap.version,
                    businessDatetimeUtc = snap.business_datetime, completeness = snap.completeness_status, contentHash = snap.content_hash },
                lines,
                match = match is null ? null : new { match.matchState, match.version, match.reason },
                allocations = allocs.Select(a => new { a.allocationId, a.sourceLineId, a.upstreamLineId, a.quantity,
                    unitRatio = $"{a.num}/{a.den}", a.state,
                    codes = allocCodes.Where(k => k.allocationId == a.allocationId).Select(k => new { k.codeId, k.codeNormalized }) }),
                task = task is null ? null : new { task.taskId, task.state, task.billCode } }, requestId = ctx.TraceIdentifier });
        }).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.TraceRead));

        app.MapPost("/api/v1/inbounds/{id:long}/rematch", (HttpContext ctx, long id, IdempotencyExecutor idem, IJobQueue queue, IAuditLog audit) =>
            idem.RunAsync(ctx, async (conn, tx, ct) =>
            {
                var orgId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                    "SELECT id FROM dbo.organization WHERE is_active=1", transaction: tx, cancellationToken: ct));
                var exists = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                    "SELECT COUNT(*) FROM dbo.source_snapshot WHERE id=@id AND org_id=@orgId AND document_type=N'PURCHASE_IN'",
                    new { id, orgId }, tx, cancellationToken: ct));
                if (exists == 0) return Err(ctx, 404, "NOT_FOUND", "入库快照不存在");
                var payload = JsonSerializer.Serialize(new { orgId, env, snapshotId = id, trigger = "rematch" });
                var jobId = await queue.EnqueueAsync(conn, tx, orgId, env, "inbound-match", $"rematch:{id}:{Guid.NewGuid():N}", payload, DateTime.UtcNow, ct);
                await audit.AppendAsync(conn, tx, new AuditEntry(ctx.User.Identity!.Name!, "User", "inbound.rematch",
                    "source_snapshot", id.ToString(), null, null, null, ctx.TraceIdentifier, null, "LIVE"));
                return (202, JsonSerializer.Serialize(new { data = new { jobId = jobId.ToString() }, requestId = ctx.TraceIdentifier }));
            })).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.JobRematch));

        app.MapPost("/api/v1/inbounds/{id:long}/allocations", (HttpContext ctx, long id, IdempotencyExecutor idem, AllocationService svc) =>
            idem.RunAsync(ctx, async (conn, tx, ct) =>
            {
                ctx.Request.Body.Position = 0;
                var req = await JsonSerializer.DeserializeAsync<AllocationRequest>(ctx.Request.Body, Web, ct);
                if (req is null || req.Lines is null || req.Lines.Count == 0)
                    return Err(ctx, 422, "VALIDATION", "lines 不能为空");
                var orgId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                    "SELECT id FROM dbo.organization WHERE is_active=1", transaction: tx, cancellationToken: ct));
                var r = await svc.SaveAsync(conn, tx, orgId, env, id, req, ctx.User.Identity!.Name!, ct);
                return (200, JsonSerializer.Serialize(new { data = new { matchVersion = r.MatchVersion, allocationsSaved = r.AllocationsSaved,
                    allRequiredCovered = r.AllRequiredCovered }, requestId = ctx.TraceIdentifier }));
            })).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.InboundAllocate));

        app.MapPost("/api/v1/inbounds/{id:long}/confirm-allocation", (HttpContext ctx, long id, IdempotencyExecutor idem, AllocationService svc, InboundTaskFactory factory) =>
            idem.RunAsync(ctx, async (conn, tx, ct) =>
            {
                ctx.Request.Body.Position = 0;
                var req = await JsonSerializer.DeserializeAsync<ConfirmRequest>(ctx.Request.Body, Web, ct);
                if (req is null || string.IsNullOrEmpty(req.ExpectedMatchVersion))
                    return Err(ctx, 422, "VALIDATION", "expectedMatchVersion 必填");
                var orgId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                    "SELECT id FROM dbo.organization WHERE is_active=1", transaction: tx, cancellationToken: ct));
                var r = await svc.ConfirmAsync(conn, tx, orgId, env, id, req.ExpectedMatchVersion, ctx.User.Identity!.Name!, factory, ct);
                return (200, JsonSerializer.Serialize(new { data = new { taskId = r.TaskId.ToString(), billCode = r.BillCode,
                    matchVersion = r.MatchVersion, reused = r.Reused, message = "符合资格后将自动上传" }, requestId = ctx.TraceIdentifier }));
            })).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.InboundAllocate));

        app.MapPost("/api/v1/inbounds/{id:long}/cancel-allocation", (HttpContext ctx, long id, IdempotencyExecutor idem, AllocationService svc) =>
            idem.RunAsync(ctx, async (conn, tx, ct) =>
            {
                ctx.Request.Body.Position = 0;
                var req = await JsonSerializer.DeserializeAsync<CancelRequest>(ctx.Request.Body, Web, ct);
                if (req is null || string.IsNullOrWhiteSpace(req.Reason))
                    return Err(ctx, 422, "VALIDATION", "reason 必填");
                var orgId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                    "SELECT id FROM dbo.organization WHERE is_active=1", transaction: tx, cancellationToken: ct));
                var n = await svc.CancelAsync(conn, tx, orgId, env, id, req.AllocationId, ctx.User.Identity!.Name!, req.Reason, ct);
                return (200, JsonSerializer.Serialize(new { data = new { cancelled = n }, requestId = ctx.TraceIdentifier }));
            })).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.InboundAllocate));
    }

    private static (int, string) Err(HttpContext ctx, int status, string code, string message)
        => (status, JsonSerializer.Serialize(new { code, message, retryable = false, requestId = ctx.TraceIdentifier }));

    public sealed record ConfirmRequest(string ExpectedMatchVersion);
    public sealed record CancelRequest(long? AllocationId, string Reason);

    private sealed class InboundListRow
    {
        public long snapshotId { get; set; }
        public string docKey { get; set; } = "";
        public int version { get; set; }
        public DateTime? businessDatetimeUtc { get; set; }
        public string completeness { get; set; } = "";
        public string? matchState { get; set; }
        public int? matchVersion { get; set; }
        public string? reason { get; set; }
        public string? supplierDwbh { get; set; }
        public long? taskId { get; set; }
        public string? taskState { get; set; }
    }
    private sealed class SnapDetailRow
    {
        public long id { get; set; }
        public string source_document_key { get; set; } = "";
        public int version { get; set; }
        public DateTime? business_datetime { get; set; }
        public string completeness_status { get; set; } = "";
        public string content_hash { get; set; } = "";
    }
    private sealed class LineDetailRow
    {
        public long lineId { get; set; }
        public string sourceLineKey { get; set; } = "";
        public string productSourceId { get; set; } = "";
        public decimal quantity { get; set; }
        public string? unit { get; set; }
        public string? batchRaw { get; set; }
        public string? classification { get; set; }
    }
    private sealed class MatchDetailRow
    {
        public string matchState { get; set; } = "";
        public int version { get; set; }
        public string? reason { get; set; }
    }
    private sealed class AllocDetailRow
    {
        public long allocationId { get; set; }
        public long sourceLineId { get; set; }
        public long upstreamLineId { get; set; }
        public decimal quantity { get; set; }
        public long num { get; set; }
        public long den { get; set; }
        public string state { get; set; } = "";
    }
    private sealed class AllocCodeDetailRow
    {
        public long allocationId { get; set; }
        public long codeId { get; set; }
        public string codeNormalized { get; set; } = "";
    }
    private sealed class TaskDetailRow
    {
        public long taskId { get; set; }
        public string state { get; set; } = "";
        public string? billCode { get; set; }
    }
}