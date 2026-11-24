using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Security;
using TraceFlow.Host.Security;

namespace TraceFlow.Host.Endpoints;

/// <summary>
/// 工作台（UI-01、NFR-06）：真实 DB 计数，只读不触发平台请求。
/// 既有字段（env/openIngestExceptions/pendingJobs/uploadTasksByState/lastActivityUtc）名称与类型不变；
/// 本任务增量追加待办队列计数、开放差异、24h 失败作业、激活暂停、告警条数与今日上传成功率。
/// 队列计数与异常队列（Task 3）同口径；alertsCount 与 <c>/ops/alerts</c> 同源加载器，保证条数一致。
/// </summary>
public static class DashboardEndpoints
{
    public static void MapDashboardEndpoints(this WebApplication app, string connectionString, string env)
    {
        app.MapGet("/api/v1/dashboard", async (HttpContext ctx) =>
        {
            var ct = ctx.RequestAborted;
            var now = DateTime.UtcNow;
            var since = now.AddHours(-24);
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ct);

            var openExceptions = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM dbo.ingest_exception WHERE status=N'OPEN'", cancellationToken: ct));
            var pendingJobs = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM dbo.outbox_job WHERE job_state=N'PENDING' AND env=@env",
                new { env }, cancellationToken: ct));
            var taskStates = (await conn.QueryAsync<(string State, int N)>(new CommandDefinition(
                "SELECT state AS State, COUNT(*) AS N FROM dbo.upload_task WHERE env=@env GROUP BY state",
                new { env }, cancellationToken: ct))).ToDictionary(x => x.State, x => x.N);
            var lastAudit = await conn.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
                "SELECT MAX(occurred_at) FROM dbo.audit_log", cancellationToken: ct));

            var orgId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT id FROM dbo.organization WHERE is_active=1", cancellationToken: ct));
            // 队列计数与异常队列 counts 完全同谓词（INGEST 无 env；RECON/TASK 按宿主 env；MAPPING 两表 CONFLICT）。
            var queue = await conn.QuerySingleAsync<QueueCounts>(new CommandDefinition(@"
SELECT
    (SELECT COUNT(*) FROM dbo.ingest_exception e
      WHERE e.org_id=@orgId AND e.status=N'OPEN') AS IngestOpen,
    (SELECT COUNT(*) FROM dbo.reconciliation_difference d
      JOIN dbo.reconciliation_run r ON r.id=d.run_id
      WHERE d.status=N'OPEN' AND r.org_id=@orgId AND r.env=@env) AS ReconOpen,
    (SELECT COUNT(*) FROM dbo.upload_task t
      WHERE t.org_id=@orgId AND t.env=@env
        AND t.state IN (N'ManualAction', N'ResultUnresolved', N'AcceptanceUnknown')) AS TaskOpen,
    (SELECT COUNT(*) FROM dbo.product_mapping pm
      WHERE pm.org_id=@orgId AND pm.state=N'CONFLICT')
  + (SELECT COUNT(*) FROM dbo.supplier_mapping sm
      WHERE sm.org_id=@orgId AND sm.state=N'CONFLICT') AS MappingOpen",
                new { orgId, env }, cancellationToken: ct));

            var failedJobs24h = await conn.ExecuteScalarAsync<int>(new CommandDefinition(@"
SELECT COUNT(*) FROM dbo.outbox_job
WHERE env=@env AND job_state=N'FAILED' AND finished_at>=@since",
                new { env, since }, cancellationToken: ct));
            var activePauses = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM dbo.top_channel_pause WHERE env=@env AND is_active=1",
                new { env }, cancellationToken: ct));
            var today = await OpsStatusEndpoints.LoadUploadsTodayAsync(conn, env, now, ct);
            var alerts = await OpsStatusEndpoints.LoadAlertsAsync(conn, orgId, env, now, ct);

            return Results.Ok(new
            {
                data = new
                {
                    env,
                    openIngestExceptions = openExceptions,
                    pendingJobs,
                    uploadTasksByState = taskStates,
                    lastActivityUtc = lastAudit,
                    queue = new
                    {
                        queue.IngestOpen,
                        queue.ReconOpen,
                        queue.TaskOpen,
                        queue.MappingOpen,
                    },
                    openDifferences = queue.ReconOpen,
                    failedJobs24h,
                    activePauses,
                    alertsCount = alerts.Count,
                    todayUploads = new
                    {
                        today.Total,
                        today.Succeeded,
                        successRate = OpsStatusEndpoints.Ratio(today.Succeeded, today.Succeeded + today.Failed),
                    },
                },
                requestId = ctx.TraceIdentifier,
            });
        }).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.TraceRead));
    }

    private sealed class QueueCounts
    {
        public int IngestOpen { get; set; }
        public int ReconOpen { get; set; }
        public int TaskOpen { get; set; }
        public int MappingOpen { get; set; }
    }
}