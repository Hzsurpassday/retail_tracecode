using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Application.Security;
using TraceFlow.Host.Security;
using TraceFlow.Infrastructure.Settings;

namespace TraceFlow.Host.Endpoints;

public static class OpsEndpoints
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static void MapOpsEndpoints(this WebApplication app, string connectionString, string env)
    {
        app.MapPost("/api/v1/sync-jobs", (HttpContext ctx, IdempotencyExecutor idem, IJobQueue queue, SettingsService settings, IAuditLog audit) =>
            idem.RunAsync(ctx, async (conn, tx, ct) =>
            {
                ctx.Request.Body.Position = 0;
                var req = await JsonSerializer.DeserializeAsync<SyncJobRequest>(ctx.Request.Body, Web, ct);
                if (req is null || req.Entity is not ("sale" or "purchase-in" or "upstream" or "enterprise"))
                    return Err(ctx, 422, "VALIDATION", "entity 须为 sale/purchase-in/upstream/enterprise");
                var start = NormalizeUtc(req.Start);
                var end = NormalizeUtc(req.End);
                var now = DateTime.UtcNow;
                if (end <= start)
                    return Err(ctx, 422, "VALIDATION", "end 必须晚于 start");
                if (end > now.AddMinutes(5))
                    return Err(ctx, 422, "VALIDATION", "end 不得超过当前时间 +5 分钟");
                if ((end - start).TotalDays > 90)
                    return Err(ctx, 422, "VALIDATION", "手动窗口不得超过 90 天");
                if (env != "MOCK")
                    return Err(ctx, 422, req.Entity is "upstream" or "enterprise" ? "TOP_NOT_CERTIFIED" : "ERP_NOT_CERTIFIED",
                        req.Entity is "upstream" or "enterprise" ? "LIVE 通道须 G-TOP 取证门禁通过后开启" : "LIVE 通道须 G-ERP 取证门禁通过后开启");
                if (await settings.IsChannelPausedAsync(ct))
                    return Err(ctx, 422, "CHANNEL_PAUSED", "同步通道已暂停，请先恢复");
                var orgId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                    "SELECT id FROM dbo.organization WHERE is_active=1", transaction: tx, cancellationToken: ct));
                var jobKind = req.Entity switch { "upstream" => "upstream-download", "enterprise" => "enterprise-sync", _ => "source-sync" };
                // upstream/enterprise 手动意图含 nonce：同窗口补拉不被 DONE 终态挡死（幂等由 Idempotency-Key 层保证）
                var intent = req.Entity is "upstream" or "enterprise"
                    ? $"manual:{req.Entity}:{start:yyyyMMddHHmmss}:{end:yyyyMMddHHmmss}:{Guid.NewGuid():N}"
                    : $"manual:{req.Entity}:{start:yyyyMMddHHmmss}:{end:yyyyMMddHHmmss}";
                var payload = JsonSerializer.Serialize(new { orgId, env, entity = req.Entity, trigger = "manual", startUtc = start, endUtc = end, start, end });
                var jobId = await queue.EnqueueAsync(conn, tx, orgId, env, jobKind, intent, payload, now, ct);
                await audit.AppendAsync(conn, tx, new AuditEntry(ctx.User.Identity!.Name!, "User", "sync.manual",
                    "outbox_job", jobId.ToString(), null, null,
                    JsonSerializer.Serialize(new { entity = req.Entity, start, end }), ctx.TraceIdentifier, null, "LIVE"));
                return (202, JsonSerializer.Serialize(new { data = new { jobId = jobId.ToString() }, requestId = ctx.TraceIdentifier }));
            })).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.JobRematch));

        app.MapGet("/api/v1/jobs/{id:long}", async (HttpContext ctx, long id) =>
        {
            await using var conn = new SqlConnection(connectionString);
            var row = await conn.QuerySingleOrDefaultAsync<JobRow>(
                "SELECT id,job_kind,intent_key,job_state,attempt_count,next_run_at,last_error,lease_owner,fencing_token FROM dbo.outbox_job WHERE id=@id",
                new { id });
            if (row is null)
                return Results.Json(new { code = "NOT_FOUND", message = "作业不存在", retryable = false, requestId = ctx.TraceIdentifier }, statusCode: 404);
            return Results.Ok(new { data = new { jobId = row.id.ToString(), jobKind = row.job_kind, intentKey = row.intent_key,
                jobState = row.job_state, attemptCount = row.attempt_count, nextRunAtUtc = row.next_run_at,
                lastError = row.last_error, leaseOwner = row.lease_owner, fencingToken = row.fencing_token }, requestId = ctx.TraceIdentifier });
        }).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.TraceRead));

        app.MapGet("/api/v1/audit-logs", async (HttpContext ctx, int? page, int? pageSize,
            string? action, string? objectType, string? objectId, DateTime? dateStart, DateTime? dateEnd) =>
        {
            var p = Math.Max(1, page ?? 1);
            var size = Math.Clamp(pageSize ?? 50, 1, 200);
            var where = "WHERE 1=1";
            var args = new DynamicParameters();
            if (!string.IsNullOrEmpty(action)) { where += " AND action=@action"; args.Add("action", action); }
            if (!string.IsNullOrEmpty(objectType)) { where += " AND object_type=@objectType"; args.Add("objectType", objectType); }
            if (!string.IsNullOrEmpty(objectId)) { where += " AND object_id=@objectId"; args.Add("objectId", objectId); }
            if (dateStart is not null) { where += " AND occurred_at>=@dateStart"; args.Add("dateStart", NormalizeUtc(dateStart.Value)); }
            if (dateEnd is not null) { where += " AND occurred_at<=@dateEnd"; args.Add("dateEnd", NormalizeUtc(dateEnd.Value)); }
            await using var conn = new SqlConnection(connectionString);
            var total = await conn.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM dbo.audit_log {where}", args);
            args.Add("lo", (p - 1) * size + 1);
            args.Add("hi", p * size);
            var rows = await conn.QueryAsync<AuditRow>($@"
SELECT id,occurred_at,actor,actor_kind,action,object_type,object_id,reason,before_digest,after_digest,request_id,rule_version,env FROM (
    SELECT ROW_NUMBER() OVER (ORDER BY occurred_at DESC, id DESC) AS rn,
           id,occurred_at,actor,actor_kind,action,object_type,object_id,reason,before_digest,after_digest,request_id,rule_version,env
    FROM dbo.audit_log {where}
) t WHERE rn BETWEEN @lo AND @hi ORDER BY rn", args);
            return Results.Ok(new { data = new {
                records = rows.Select(r => new { id = r.id, occurredAtUtc = r.occurred_at, actor = r.actor, actorKind = r.actor_kind,
                    action = r.action, objectType = r.object_type, objectId = r.object_id, reason = r.reason,
                    beforeDigest = r.before_digest, afterDigest = r.after_digest, requestId = r.request_id,
                    ruleVersion = r.rule_version, env = r.env }),
                total, page = p, pageSize = size }, requestId = ctx.TraceIdentifier });
        }).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.AuditRead));
    }

    private static DateTime NormalizeUtc(DateTime d)
        => d.Kind == DateTimeKind.Local ? d.ToUniversalTime() : DateTime.SpecifyKind(d, DateTimeKind.Utc);

    private static (int, string) Err(HttpContext ctx, int status, string code, string message)
        => (status, JsonSerializer.Serialize(new { code, message, retryable = false, requestId = ctx.TraceIdentifier }));

    public sealed record SyncJobRequest(string Entity, DateTime Start, DateTime End, string? Filters);

    private sealed class JobRow
    {
        public long id { get; set; }
        public string job_kind { get; set; } = "";
        public string intent_key { get; set; } = "";
        public string job_state { get; set; } = "";
        public int attempt_count { get; set; }
        public DateTime next_run_at { get; set; }
        public string? last_error { get; set; }
        public string? lease_owner { get; set; }
        public long fencing_token { get; set; }
    }

    private sealed class AuditRow
    {
        public long id { get; set; }
        public DateTime occurred_at { get; set; }
        public string actor { get; set; } = "";
        public string actor_kind { get; set; } = "";
        public string action { get; set; } = "";
        public string object_type { get; set; } = "";
        public string? object_id { get; set; }
        public string? reason { get; set; }
        public string? before_digest { get; set; }
        public string? after_digest { get; set; }
        public string? request_id { get; set; }
        public string? rule_version { get; set; }
        public string env { get; set; } = "";
    }
}
