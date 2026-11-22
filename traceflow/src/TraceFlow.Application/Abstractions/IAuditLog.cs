using Microsoft.Data.SqlClient;
namespace TraceFlow.Application.Abstractions;
public sealed record AuditEntry(string Actor, string ActorKind, string Action, string ObjectType,
    string? ObjectId, string? Reason, string? BeforeDigest, string? AfterDigest,
    string? RequestId, string? RuleVersion, string Env);
public interface IAuditLog
{
    Task AppendAsync(SqlConnection conn, SqlTransaction? tx, AuditEntry entry);
}