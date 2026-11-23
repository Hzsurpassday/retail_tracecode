using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Infrastructure.Security;

namespace TraceFlow.Infrastructure.Inbound;

/// <summary>
/// 发送前取消（IN-09/AT-034）：仅 Blocked/Ready 且无 UPLOAD 尝试、outbox 非 RUNNING 可取消；
/// 任务/分片/队列/分配/占用/匹配同事务原子回退。无任务返回 false。
/// auditEnv：人工取消="LIVE"（控制面），作业自动取消=作业 env。
/// </summary>
public static class InboundCancelService
{
    public static async Task<bool> CancelPreSendAsync(SqlConnection conn, SqlTransaction tx, long orgId, string env,
        long snapshotId, string actor, string reason, string auditEnv, CancellationToken ct)
    {
        var task = await conn.QuerySingleOrDefaultAsync<TaskRow>(new CommandDefinition(@"
SELECT id,state FROM dbo.upload_task WITH (UPDLOCK, HOLDLOCK)
WHERE org_id=@orgId AND env=@env AND source_snapshot_id=@snap AND task_type=N'PURCHASE_IN_102' AND state<>N'Cancelled'",
            new { orgId, env, snap = snapshotId }, tx, cancellationToken: ct));
        if (task is null) return false;
        if (task.state is not ("Blocked" or "Ready"))
            throw new AllocationConflictException("CANCEL_REFUSED", $"任务状态 {task.state} 已进入发送生命周期，禁止发送前取消");
        var attempts = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM dbo.upload_attempt WHERE task_id=@id AND attempt_kind=N'UPLOAD'",
            new { id = task.id }, tx, cancellationToken: ct));
        if (attempts > 0)
            throw new AllocationConflictException("CANCEL_REFUSED", "任务已有 UPLOAD 尝试，禁止取消（走 M3 结果处置流程）");
        var partIds = (await conn.QueryAsync<long>(new CommandDefinition(
            "SELECT id FROM dbo.upload_part WHERE task_id=@id", new { id = task.id }, tx, cancellationToken: ct))).ToList();
        foreach (var partId in partIds)
        {
            var running = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM dbo.outbox_job WHERE org_id=@orgId AND env=@env AND job_kind=N'upload-dispatch' AND intent_key=@intent AND job_state=N'RUNNING'",
                new { orgId, env, intent = $"part:{partId}" }, tx, cancellationToken: ct));
            if (running > 0)
                throw new AllocationConflictException("CANCEL_REFUSED", "发送作业正在 RUNNING，禁止取消");
        }
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.upload_task SET state=N'Cancelled', state_reason=@reason, version=version+1 WHERE id=@id",
            new { id = task.id, reason }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.upload_part SET state=N'Cancelled' WHERE task_id=@id", new { id = task.id }, tx, cancellationToken: ct));
        foreach (var partId in partIds)
            await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.outbox_job SET job_state=N'CANCELLED', finished_at=SYSUTCDATETIME()
WHERE org_id=@orgId AND env=@env AND job_kind=N'upload-dispatch' AND intent_key=@intent AND job_state IN (N'PENDING',N'FAILED')",
                new { orgId, env, intent = $"part:{partId}" }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.inbound_allocation SET state=N'CANCELLED', version=version+1
WHERE org_id=@orgId AND env=@env AND state IN (N'RESERVED',N'COMMITTED')
  AND source_line_id IN (SELECT id FROM dbo.source_line WHERE snapshot_id=@snap)",
            new { orgId, env, snap = snapshotId }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.code_claim SET claim_state=N'RELEASED', released_at=SYSUTCDATETIME(), released_by=@by, release_evidence=@reason
WHERE claim_state=N'ACTIVE' AND (upload_task_id=@taskId OR allocation_id IN (
    SELECT a.id FROM dbo.inbound_allocation a JOIN dbo.source_line l ON l.id=a.source_line_id WHERE l.snapshot_id=@snap))",
            new { by = actor, reason, taskId = task.id, snap = snapshotId }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.inbound_match SET match_state=N'UNMATCHED', reason=@reason, matched_at=NULL, version=version+1
WHERE org_id=@orgId AND env=@env AND source_snapshot_id=@snap",
            new { orgId, env, snap = snapshotId, reason }, tx, cancellationToken: ct));
        await new AuditLog().AppendAsync(conn, tx, new AuditEntry(actor, "User", "inbound.cancel", "upload_task",
            task.id.ToString(), reason, null, null, null, null, auditEnv));
        return true;
    }

    private sealed class TaskRow
    {
        public long id { get; set; }
        public string state { get; set; } = "";
    }
}
