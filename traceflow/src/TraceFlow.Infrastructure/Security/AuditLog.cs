using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;

namespace TraceFlow.Infrastructure.Security;

public sealed class AuditLog : IAuditLog
{
    public async Task AppendAsync(SqlConnection conn, SqlTransaction? tx, AuditEntry e)
        => await conn.ExecuteAsync(@"
INSERT INTO dbo.audit_log(actor,actor_kind,action,object_type,object_id,reason,before_digest,after_digest,request_id,rule_version,env)
VALUES(@Actor,@ActorKind,@Action,@ObjectType,@ObjectId,@Reason,@BeforeDigest,@AfterDigest,@RequestId,@RuleVersion,@Env)", e, tx);
}
