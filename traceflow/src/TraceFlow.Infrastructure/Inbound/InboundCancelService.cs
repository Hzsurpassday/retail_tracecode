using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Infrastructure.Security;

namespace TraceFlow.Infrastructure.Inbound;

public sealed record StaleRevokeResult(int OlderSnapshots, int AllocationsRevoked, int ClaimsReleased, int TasksCancelled);

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

    /// <summary>
    /// 版本推进回收（review fix 1 / T10 §8 陈旧占用）：同一 (org, source_system, source_document_key) 下比
    /// newestSnapshotId 更旧的快照上，RESERVED/TENTATIVE 分配会永久占用码（UX_claim_active 使新版本自动匹配/保存
    /// 撞 CODE_CONTENDED），而 UI 只列最新版本、旧版本没有任何取消入口。因此在新版本匹配/保存前显式回收：
    /// 先按发送前取消撤掉旧版本任务（只碰 Blocked/Ready 且无 UPLOAD 尝试的，绝不触碰已确认/已发送），
    /// 再撤销残留分配并释放占用（COMMITTED 跳过，随任务一起处置），并留一条汇总审计。
    /// 幂等：无可撤销对象时零写入；整段在调用方事务内。
    /// </summary>
    public static async Task<StaleRevokeResult> RevokeStaleDocKeyAllocationsAsync(SqlConnection conn, SqlTransaction tx,
        long orgId, string env, string docKey, long newestSnapshotId, CancellationToken ct)
    {
        const string reason = "旧版本被新版本取代，撤销补配";
        var newest = await conn.QuerySingleOrDefaultAsync<VersionRow>(new CommandDefinition(@"
SELECT version, source_system_id FROM dbo.source_snapshot WHERE id=@id AND org_id=@orgId",
            new { id = newestSnapshotId, orgId }, tx, cancellationToken: ct));
        if (newest is null) return new StaleRevokeResult(0, 0, 0, 0);
        var older = (await conn.QueryAsync<long>(new CommandDefinition(@"
SELECT id FROM dbo.source_snapshot
WHERE org_id=@orgId AND source_system_id=@sys AND source_document_key=@docKey AND document_type=N'PURCHASE_IN'
  AND version<@ver AND id<>@id",
            new { orgId, sys = newest.source_system_id, docKey, ver = newest.version, id = newestSnapshotId }, tx, cancellationToken: ct))).ToList();
        if (older.Count == 0) return new StaleRevokeResult(0, 0, 0, 0);

        // 1) 旧版本的发送前任务先撤（CancelPreSendAsync 连带回退其分配/占用/匹配）。
        var taskSnaps = (await conn.QueryAsync<long>(new CommandDefinition(@"
SELECT t.source_snapshot_id FROM dbo.upload_task t
WHERE t.org_id=@orgId AND t.env=@env AND t.task_type=N'PURCHASE_IN_102' AND t.source_snapshot_id IN @snaps
  AND t.state IN (N'Blocked',N'Ready')
  AND NOT EXISTS (SELECT 1 FROM dbo.upload_attempt ua WHERE ua.task_id=t.id AND ua.attempt_kind=N'UPLOAD')",
            new { orgId, env, snaps = older }, tx, cancellationToken: ct))).ToList();
        var tasks = 0;
        foreach (var snap in taskSnaps)
            if (await CancelPreSendAsync(conn, tx, orgId, env, snap, "SYSTEM", reason, env, ct)) tasks++;

        // 2) 残留 RESERVED/TENTATIVE 撤销并释放占用；COMMITTED 已确认，随任务处置，不在此处动。
        var allocIds = (await conn.QueryAsync<long>(new CommandDefinition(@"
SELECT a.id FROM dbo.inbound_allocation a JOIN dbo.source_line l ON l.id=a.source_line_id
WHERE a.org_id=@orgId AND a.env=@env AND l.snapshot_id IN @snaps AND a.state IN (N'RESERVED',N'TENTATIVE')",
            new { orgId, env, snaps = older }, tx, cancellationToken: ct))).ToList();
        var revoked = 0;
        var claims = 0;
        foreach (var id in allocIds)
        {
            var n = await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.inbound_allocation SET state=N'CANCELLED', version=version+1
WHERE id=@id AND state IN (N'RESERVED',N'TENTATIVE')", new { id }, tx, cancellationToken: ct));
            if (n == 0) continue;
            revoked++;
            claims += await CodeClaimWriter.ReleaseByAllocationAsync(conn, tx, id, "SYSTEM", reason, ct);
        }
        if (tasks == 0 && revoked == 0) return new StaleRevokeResult(older.Count, 0, 0, 0);

        // 旧版本已无可发送内容：匹配态回到 UNMATCHED，避免陈旧 COMPLETE 误导（列表只暴露最新版本）。
        await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.inbound_match SET match_state=N'UNMATCHED', reason=@reason, matched_at=NULL, version=version+1
WHERE org_id=@orgId AND env=@env AND source_snapshot_id IN @snaps AND match_state<>N'UNMATCHED'",
            new { orgId, env, snaps = older, reason }, tx, cancellationToken: ct));
        await new AuditLog().AppendAsync(conn, tx, new AuditEntry("SYSTEM", "System", "inbound.allocate.revoke.stale",
            "source_snapshot", newestSnapshotId.ToString(),
            $"撤销旧版本补配：旧快照 {older.Count} 个，分配 {revoked} 条，占用 {claims} 个，任务 {tasks} 个",
            null, null, null, null, env));
        return new StaleRevokeResult(older.Count, revoked, claims, tasks);
    }

    private sealed class TaskRow
    {
        public long id { get; set; }
        public string state { get; set; } = "";
    }

    private sealed class VersionRow
    {
        public int version { get; set; }
        public long source_system_id { get; set; }
    }
}
