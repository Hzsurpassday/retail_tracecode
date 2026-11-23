using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Security;
using TraceFlow.Host.Security;

namespace TraceFlow.Host.Endpoints;

public static class DashboardEndpoints
{
    public static void MapDashboardEndpoints(this WebApplication app, string connectionString, string env)
    {
        app.MapGet("/api/v1/dashboard", async (HttpContext ctx) =>
        {
            await using var conn = new SqlConnection(connectionString);
            var openExceptions = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.ingest_exception WHERE status=N'OPEN'");
            var pendingJobs = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.outbox_job WHERE job_state=N'PENDING' AND env=@env", new { env });
            var taskStates = (await conn.QueryAsync<(string State, int N)>(
                "SELECT state AS State, COUNT(*) AS N FROM dbo.upload_task WHERE env=@env GROUP BY state", new { env }))
                .ToDictionary(x => x.State, x => x.N);
            var lastAudit = await conn.ExecuteScalarAsync<DateTime?>("SELECT MAX(occurred_at) FROM dbo.audit_log");
            return Results.Ok(new { data = new { env, openIngestExceptions = openExceptions, pendingJobs,
                uploadTasksByState = taskStates, lastActivityUtc = lastAudit }, requestId = ctx.TraceIdentifier });
        }).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.TraceRead));
    }
}
