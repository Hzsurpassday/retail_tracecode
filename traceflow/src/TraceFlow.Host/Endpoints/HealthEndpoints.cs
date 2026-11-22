using Dapper;
using Microsoft.Data.SqlClient;

namespace TraceFlow.Host.Endpoints;

public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this WebApplication app, string connectionString, string env)
    {
        app.MapGet("/api/v1/health", async (HttpContext ctx) =>
        {
            string db;
            try
            {
                await using var conn = new SqlConnection(connectionString);
                db = await conn.ExecuteScalarAsync<string?>("SELECT N'OK'") ?? "UNAVAILABLE";
            }
            catch { db = "UNAVAILABLE"; } // 不回显连接细节
            return Results.Ok(new { data = new { env, db, serverTimeUtc = DateTime.UtcNow }, requestId = ctx.TraceIdentifier });
        });
    }
}