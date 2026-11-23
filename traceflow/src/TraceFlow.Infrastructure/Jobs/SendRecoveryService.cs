using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Domain.Uploads;
using TraceFlow.Infrastructure.Uploads;

namespace TraceFlow.Infrastructure.Jobs;

/// <summary>
/// 发送恢复（JOB-02）：只处理本机环境（<c>RecoverAsync(env)</c>）的 Dispatching 分片，
/// 失租 → AcceptanceUnknown → 只回查；审计/入队 env 与被处理分片一致。
/// </summary>
public sealed class SendRecoveryService : ISendRecoveryService
{
    private readonly string _conn;
    private readonly IJobQueue _queue;
    public SendRecoveryService(string connectionString, IJobQueue queue) { _conn = connectionString; _queue = queue; }

    public async Task<int> RecoverAsync(string env, CancellationToken ct)
    {
        if (!UploadStateMachine.TryGetNext(UploadState.Dispatching, UploadEvent.LeaseLost, out var next) ||
            next != UploadState.AcceptanceUnknown)
            throw new InvalidOperationException("状态机缺少 LeaseLost 恢复边");

        // 环境定界（与 Worker 认领同一口径）：MOCK worker 绝不能把 LIVE 分片改成受理未知、
        // 也不能失败 LIVE 派发作业或入队 LIVE 回查。空/非法 env → 零候选（fail-closed，不跨环境兜底）。
        var scope = (env ?? "").Trim().ToUpperInvariant();
        if (scope is not ("MOCK" or "LIVE"))
            return 0;

        await using var conn = new SqlConnection(_conn);
        await conn.OpenAsync(ct);
        var candidates = (await conn.QueryAsync<(long PartId, long TaskId, string Env, long OrgId)>(@"
SELECT p.id AS PartId, p.task_id AS TaskId, p.env AS Env, p.org_id AS OrgId
FROM dbo.upload_part p
WHERE p.state = N'Dispatching' AND p.env = @env
  AND NOT EXISTS (
    SELECT 1 FROM dbo.outbox_job j
    WHERE j.env = @env AND j.job_kind = N'upload-dispatch' AND j.job_state = N'RUNNING'
      AND j.lease_until >= SYSUTCDATETIME()
      AND j.intent_key = N'part:' + CAST(p.id AS nvarchar(20)))", new { env = scope })).ToList();

        var recovered = 0;
        foreach (var c in candidates)
        {
            ct.ThrowIfCancellationRequested();
            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
            // 加锁顺序与发送写回一致（任务 → 分片）：下方重算会更新任务行，先锁任务避免与并发发送形成反向顺序。
            await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT state FROM dbo.upload_task WITH (UPDLOCK, HOLDLOCK) WHERE id=@id",
                new { id = c.TaskId }, tx, cancellationToken: ct));
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
                // 恢复即结算：复用发送写回的同一聚合重算，使任务状态在恢复当刻即为真实聚合，而非滞留 Dispatching。
                await UploadDispatchService.RecomputeTaskStateAsync(conn, tx, c.TaskId, ct);
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
