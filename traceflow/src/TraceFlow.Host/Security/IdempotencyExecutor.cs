using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;

namespace TraceFlow.Host.Security;

public sealed class IdempotencyExecutor
{
    private readonly string _connectionString;
    private readonly IIdempotencyStore _store;
    public IdempotencyExecutor(string connectionString, IIdempotencyStore store)
    {
        _connectionString = connectionString;
        _store = store;
    }

    public async Task<IResult> RunAsync(HttpContext ctx,
        Func<SqlConnection, SqlTransaction, CancellationToken, Task<(int Status, string Body)>> action)
    {
        var key = ctx.Request.Headers["Idempotency-Key"].ToString();
        if (key.Length is 0 or > 128)
            return Results.Json(new { code = "VALIDATION", message = "缺少或超长 Idempotency-Key", retryable = false, requestId = ctx.TraceIdentifier }, statusCode: 422);
        ctx.Request.EnableBuffering();
        using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        ctx.Request.Body.Position = 0;
        // M0 携带 3：hash 纳入 QueryString——同键同 body 不同 query 视为不同请求（409）
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            ctx.Request.Method + "|" + ctx.Request.Path + "|" + ctx.Request.QueryString + "|" + body)));
        var userId = long.Parse(ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value);

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        var hit = await _store.TryGetAsync(conn, tx, key, hash, userId, ctx.RequestAborted);
        if (hit is not null)
        {
            await tx.CommitAsync();
            return Results.Content(hit.Body, "application/json", statusCode: hit.StatusCode);
        }
        var (status, resultBody) = await action(conn, tx, ctx.RequestAborted);
        // 业务函数内部自行回滚并返回错误元组时（tx.Connection is null），跳过 Save/Commit 直接返回。
        if (tx.Connection is null)
            return Results.Content(resultBody, "application/json", statusCode: status);
        try
        {
            await _store.SaveAsync(conn, tx, key, hash, userId, status, resultBody, ctx.RequestAborted);
            await tx.CommitAsync(); // 幂等记录与业务同事务（API-02）
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            // 并发同键竞态败者（M0 携带 3 裁定）：业务+幂等整体回滚，重读胜者记录。
            // 同 hash→重放胜者响应；异 hash→TryGet 抛 IdempotencyConflictException→409。
            await tx.RollbackAsync();
            var winner = await _store.TryGetAsync(conn, null!, key, hash, userId, ctx.RequestAborted);
            if (winner is null)
                throw new IdempotencyConflictException();
            return Results.Content(winner.Body, "application/json", statusCode: winner.StatusCode);
        }
        return Results.Content(resultBody, "application/json", statusCode: status);
    }
}
