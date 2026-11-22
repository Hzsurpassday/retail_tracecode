using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Domain.Uploads;

namespace TraceFlow.Infrastructure.Jobs;

public sealed class SendRecoveryService : ISendRecoveryService
{
    private readonly string _conn;
    private readonly IJobQueue _queue;
    public SendRecoveryService(string connectionString, IJobQueue queue) { _conn = connectionString; _queue = queue; }

    public async Task<int> RecoverAsync(CancellationToken ct)
    {
        if (!UploadStateMachine.TryGetNext(UploadState.Dispatching, UploadEvent.LeaseLost, out var next) ||
            next != UploadState.AcceptanceUnknown)
            throw new InvalidOperationException("状态机缺少 LeaseLost 恢复边");

        await using var conn = new SqlConnection(_conn);
        await conn.OpenAsync(ct);
        var candidates = (await conn.QueryAsync<(long PartId, long TaskId, string Env, long OrgId)>(@"
SELECT p.id AS PartId, p.task_id AS TaskId, p.env AS Env, p.org_id AS OrgId
FROM dbo.upload_part p
WHERE p.state = N'Dispatching'
  AND NOT EXISTS (
    SELECT 1 FROM dbo.outbox_job j
    WHERE j.job_kind = N'upload-dispatch' AND j.job_state = N'RUNNING'
      AND j.lease_until >= SYSUTCDATETIME()
      AND j.intent_key = N'part:' + CAST(p.id AS nvarchar(20)))")).ToList();

        var recovered = 0;
        foreach (var c in candidates)
        {
            ct.ThrowIfCancellationRequested();
            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
            // 版本守卫：仅当仍是 Dispatching 才推进（防止与新认领者竞争）
            var rows = await conn.ExecuteAsync(
                "UPDATE dbo.upload_part SET state=@next WHERE id=@id AND state=N'Dispatching'",
                new { id = c.PartId, next = next.ToString() }, tx);
            if (rows == 1)
            {
                await conn.ExecuteAsync(@"
UPDATE dbo.outbox_job SET job_state=N'FAILED', finished_at=SYSUTCDATETIME(), lease_owner=NULL, lease_until=NULL,
       last_error=N'lease expired; part moved to AcceptanceUnknown; query-first'
WHERE job_kind=N'upload-dispatch' AND intent_key=@k AND job_state IN (N'RUNNING', N'PENDING')",
                    new { k = $"part:{c.PartId}" }, tx);
                await _queue.EnqueueAsync(conn, tx, c.OrgId, c.Env, "bill-query", $"query:part:{c.PartId}", null, null, ct);
                await conn.ExecuteAsync(@"
INSERT INTO dbo.audit_log(actor,actor_kind,action,object_type,object_id,reason,env)
VALUES(N'SYSTEM',N'Worker',N'send-recovery',N'upload_part',@obj,N'失租→受理未知，先回查',@env)",
                    new { obj = c.PartId.ToString(), env = c.Env }, tx);
                recovered++;
            }
            await tx.CommitAsync(ct);
        }
        return recovered;
    }

    public async Task AppendStaleEvidenceAsync(long taskId, long partId, int attemptNo, string attemptKind,
        long fencingToken, string? rawResponse, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_conn);
        await conn.OpenAsync(ct);
        // 只读证据：不触碰任务/分片/占用状态（设计 §2.7）
        await conn.ExecuteAsync(@"
INSERT INTO dbo.upload_attempt(task_id,part_id,attempt_no,attempt_kind,env,fencing_token,raw_response,is_stale_evidence,finished_at)
SELECT @taskId,@partId,@attemptNo,@attemptKind,t.env,@fencingToken,@rawResponse,1,SYSUTCDATETIME()
FROM dbo.upload_task t WHERE t.id=@taskId",
            new { taskId, partId, attemptNo, attemptKind, fencingToken, rawResponse });
    }
}
