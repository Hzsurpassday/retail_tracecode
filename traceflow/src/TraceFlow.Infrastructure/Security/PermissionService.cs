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
        // 停用用户即时失权（fail-closed，I-4）：授权行保留但 is_active=0 时一律判无权限
        return await c.ExecuteScalarAsync<int>(@"
SELECT COUNT(*) FROM dbo.user_permission up
WHERE up.user_id=@userId AND up.permission=@permission
  AND EXISTS (SELECT 1 FROM dbo.app_user u WHERE u.id = up.user_id AND u.is_active = 1)",
            new { userId, permission }) > 0;
    }

    public async Task<IReadOnlySet<string>> ForUserAsync(long userId)
    {
        await using var c = new SqlConnection(_conn);
        var rows = await c.QueryAsync<string>(@"
SELECT up.permission FROM dbo.user_permission up
WHERE up.user_id=@userId
  AND EXISTS (SELECT 1 FROM dbo.app_user u WHERE u.id = up.user_id AND u.is_active = 1)", new { userId });
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