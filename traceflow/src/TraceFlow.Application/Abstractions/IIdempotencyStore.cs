using Microsoft.Data.SqlClient;
namespace TraceFlow.Application.Abstractions;
public sealed record IdempotencyHit(int StatusCode, string Body);
public sealed class IdempotencyConflictException : Exception
{
    public IdempotencyConflictException() : base("同幂等键不同请求内容") { }
}
public interface IIdempotencyStore
{
    Task<IdempotencyHit?> TryGetAsync(SqlConnection conn, SqlTransaction tx, string key, string requestHash, long userId, CancellationToken ct);
    Task SaveAsync(SqlConnection conn, SqlTransaction tx, string key, string requestHash, long userId, int status, string body, CancellationToken ct);
}