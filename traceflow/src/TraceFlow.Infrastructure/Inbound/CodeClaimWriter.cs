using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;

namespace TraceFlow.Infrastructure.Inbound;

/// <summary>DB-05 占用原语：UX_claim_active 过滤唯一索引保证一码不被两个有效入库消费；争用→409 CODE_CONTENDED。</summary>
public static class CodeClaimWriter
{
    public static async Task<long> InsertActiveAsync(SqlConnection conn, SqlTransaction tx, long orgId, string env,
        long codeId, string eventScope, long? allocationId, long? uploadTaskId, CancellationToken ct)
    {
        try
        {
            return await conn.ExecuteScalarAsync<long>(new CommandDefinition(@"
INSERT INTO dbo.code_claim(org_id,env,code_id,event_scope,upload_task_id,allocation_id,claim_state)
OUTPUT INSERTED.id
VALUES(@orgId,@env,@codeId,@scope,@taskId,@allocId,N'ACTIVE')",
                new { orgId, env, codeId, scope = eventScope, taskId = uploadTaskId, allocId = allocationId }, tx, cancellationToken: ct));
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            throw new AllocationConflictException("CODE_CONTENDED", $"码 {codeId} 在 {eventScope} 范围已被其他有效入库占用");
        }
    }

    public static Task<int> ReleaseByAllocationAsync(SqlConnection conn, SqlTransaction tx, long allocationId,
        string releasedBy, string evidence, CancellationToken ct)
        => conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.code_claim SET claim_state=N'RELEASED', released_at=SYSUTCDATETIME(), released_by=@by, release_evidence=@ev
WHERE allocation_id=@allocId AND claim_state=N'ACTIVE'",
            new { allocId = allocationId, by = releasedBy, ev = evidence }, tx, cancellationToken: ct));

    public static Task BindTaskAsync(SqlConnection conn, SqlTransaction tx, long allocationId, long taskId, CancellationToken ct)
        => conn.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.code_claim SET upload_task_id=@taskId WHERE allocation_id=@allocId AND claim_state=N'ACTIVE'",
            new { taskId, allocId = allocationId }, tx, cancellationToken: ct));
}
