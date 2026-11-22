using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using TraceFlow.Infrastructure.Security;

namespace TraceFlow.Host.Security;

public static class IdempotencyExecutor
{
    public static async Task<IResult> RunAsync(HttpContext ctx, string connectionString,
        Func<SqlConnection, SqlTransaction, CancellationToken, Task<(int Status, string Body)>> action)
    {
        var key = ctx.Request.Headers["Idempotency-Key"].ToString();
        if (key.Length is 0 or > 128)
            return Results.Json(new { code = "VALIDATION", message = "缺少或超长 Idempotency-Key", retryable = false, requestId = ctx.TraceIdentifier }, statusCode: 422);
        ctx.Request.EnableBuffering();
        using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        ctx.Request.Body.Position = 0;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ctx.Request.Method + "|" + ctx.Request.Path + "|" + body)));
        var userId = long.Parse(ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value);

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        var store = new IdempotencyStore();
        var hit = await store.TryGetAsync(conn, tx, key, hash, userId, ctx.RequestAborted);
        if (hit is not null)
        {
            await tx.CommitAsync();
            return Results.Content(hit.Body, "application/json", statusCode: hit.StatusCode);
        }
        var (status, resultBody) = await action(conn, tx, ctx.RequestAborted);
        // 业务函数内部自行回滚并返回错误元组时（tx.Connection is null），跳过 Save/Commit 直接返回（brief Step 3  prose 约定）。
        if (tx.Connection is null)
            return Results.Content(resultBody, "application/json", statusCode: status);
        await store.SaveAsync(conn, tx, key, hash, userId, status, resultBody, ctx.RequestAborted);
        await tx.CommitAsync(); // 幂等记录与业务同事务（API-02）
        return Results.Content(resultBody, "application/json", statusCode: status);
    }
}