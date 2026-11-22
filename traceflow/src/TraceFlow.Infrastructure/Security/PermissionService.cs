using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;

namespace TraceFlow.Infrastructure.Security;

public sealed class PermissionService : IPermissionService
{
    private readonly string _conn;
    public PermissionService(string connectionString) => _conn = connectionString;

    public async Task<bool> HasAsync(long userId, string permission)
    {
        await using var c = new SqlConnection(_conn);
        return await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.user_permission WHERE user_id=@userId AND permission=@permission",
            new { userId, permission }) > 0;
    }

    public async Task<IReadOnlySet<string>> ForUserAsync(long userId)
    {
        await using var c = new SqlConnection(_conn);
        var rows = await c.QueryAsync<string>(
            "SELECT permission FROM dbo.user_permission WHERE user_id=@userId", new { userId });
        return rows.ToHashSet();
    }

    public async Task GrantAsync(SqlConnection conn, SqlTransaction tx, long userId, string permission, string grantedBy)
        => await conn.ExecuteAsync(@"
IF NOT EXISTS (SELECT 1 FROM dbo.user_permission WHERE user_id=@userId AND permission=@permission)
INSERT INTO dbo.user_permission(user_id,permission,granted_by) VALUES(@userId,@permission,@grantedBy)",
            new { userId, permission, grantedBy }, tx);

    public async Task RevokeAsync(SqlConnection conn, SqlTransaction tx, long userId, string permission, string grantedBy)
        => await conn.ExecuteAsync(
            "DELETE FROM dbo.user_permission WHERE user_id=@userId AND permission=@permission",
            new { userId, permission }, tx);
}