using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;

namespace TraceFlow.Infrastructure.Security;

public sealed class IdempotencyStore : IIdempotencyStore
{
    public async Task<IdempotencyHit?> TryGetAsync(SqlConnection conn, SqlTransaction tx, string key, string requestHash, long userId, CancellationToken ct)
    {
        var row = await conn.QuerySingleOrDefaultAsync<(string Hash, int Status, string Body)?>(
            "SELECT request_hash AS Hash, response_status AS Status, response_body AS Body FROM dbo.idempotency_record WHERE idem_key=@key AND user_id=@userId",
            new { key, userId }, tx);
        if (row is null) return null;
        if (!string.Equals(row.Value.Hash.Trim(), requestHash, StringComparison.OrdinalIgnoreCase))
            throw new IdempotencyConflictException();
        return new IdempotencyHit(row.Value.Status, row.Value.Body);
    }

    public async Task SaveAsync(SqlConnection conn, SqlTransaction tx, string key, string requestHash, long userId, int status, string body, CancellationToken ct)
        => await conn.ExecuteAsync(
            "INSERT INTO dbo.idempotency_record(idem_key,request_hash,user_id,response_status,response_body) VALUES(@key,@requestHash,@userId,@status,@body)",
            new { key, requestHash, userId, status, body }, tx);
}
