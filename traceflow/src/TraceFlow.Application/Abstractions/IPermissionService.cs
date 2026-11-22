using Microsoft.Data.SqlClient;
namespace TraceFlow.Application.Abstractions;
public interface IPermissionService
{
    Task<bool> HasAsync(long userId, string permission);
    Task<IReadOnlySet<string>> ForUserAsync(long userId);
    Task GrantAsync(SqlConnection conn, SqlTransaction tx, long userId, string permission, string grantedBy);
    Task RevokeAsync(SqlConnection conn, SqlTransaction tx, long userId, string permission, string grantedBy);
}