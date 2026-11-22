using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;

namespace TraceFlow.Infrastructure.Jobs;

public sealed class JobQueue : IJobQueue
{
    private readonly string _conn;
    private readonly IAuditLog _audit;
    public JobQueue(string connectionString, IAuditLog audit)
    {
        _conn = connectionString;
        _audit = audit;
    }

    public async Task<long> EnqueueAsync(SqlConnection conn, SqlTransaction tx, long orgId, string env,
        string jobKind, string intentKey, string? payloadJson, DateTime? nextRunAtUtc, CancellationToken ct)
    {
        try
        {
            return await conn.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.outbox_job(org_id,env,job_kind,intent_key,payload_json,next_run_at)
OUTPUT inserted.id
VALUES(@orgId,@env,@jobKind,@intentKey,@payloadJson,COALESCE(@nextRunAt,SYSUTCDATETIME()))",
                // 与 brief 的最小偏差：匿名对象属性名 nextRunAtUtc 改为 nextRunAt 以匹配 SQL 参数 @nextRunAt（否则 Dapper 报“必须声明标量变量”）
                new { orgId, env, jobKind, intentKey, payloadJson, nextRunAt = nextRunAtUtc }, tx);
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            return await conn.ExecuteScalarAsync<long>(
                "SELECT id FROM dbo.outbox_job WHERE org_id=@orgId AND env=@env AND job_kind=@jobKind AND intent_key=@intentKey",
                new { orgId, env, jobKind, intentKey }, tx);
        }
    }

    public async Task<ClaimedJob?> ClaimAsync(string workerId, string env, IReadOnlyCollection<string> jobKinds,
        TimeSpan lease, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_conn);
        await conn.OpenAsync(ct);
        // 2008 R2 可更新 CTE + UPDLOCK/READPAST：原子认领，网络调用不持锁（JOB-01）
        return await conn.QuerySingleOrDefaultAsync<ClaimedJob>(@"
WITH candidate AS (
  SELECT TOP (1) id, job_kind, intent_key, payload_json, job_state, lease_owner, lease_until, fencing_token, attempt_count
  FROM dbo.outbox_job WITH (UPDLOCK, READPAST)
  WHERE job_state = N'PENDING' AND env = @env AND next_run_at <= SYSUTCDATETIME() AND job_kind IN @jobKinds
  ORDER BY next_run_at, id
)
UPDATE candidate
SET job_state = N'RUNNING',
    lease_owner = @workerId,
    lease_until = DATEADD(millisecond, @leaseMs, SYSUTCDATETIME()),
    fencing_token = fencing_token + 1,
    attempt_count = attempt_count + 1
OUTPUT inserted.id AS Id, inserted.job_kind AS JobKind, inserted.intent_key AS IntentKey,
       inserted.payload_json AS PayloadJson, inserted.fencing_token AS FencingToken,
       inserted.lease_until AS LeaseUntilUtc, inserted.attempt_count AS AttemptCount;",
            new { env, jobKinds, workerId, leaseMs = (long)lease.TotalMilliseconds });
    }

    public async Task<bool> CompleteAsync(long jobId, string workerId, long fencingToken, CancellationToken ct)
        => await GuardedUpdateAsync(jobId, workerId, fencingToken,
            "job_state=N'DONE', finished_at=SYSUTCDATETIME(), lease_owner=NULL, lease_until=NULL", null, ct);

    public async Task<bool> RescheduleAsync(long jobId, string workerId, long fencingToken,
        DateTime nextRunAtUtc, string? error, CancellationToken ct)
        => await GuardedUpdateAsync(jobId, workerId, fencingToken,
            "job_state=N'PENDING', lease_owner=NULL, lease_until=NULL, next_run_at=@nextRunAt, last_error=@error",
            // 与 brief 的最小偏差：属性名 nextRunAtUtc 改为 nextRunAt 以匹配 SQL 参数 @nextRunAt
            new { nextRunAt = nextRunAtUtc, error }, ct);

    public async Task<bool> FailAsync(long jobId, string workerId, long fencingToken, string error, CancellationToken ct)
        => await GuardedUpdateAsync(jobId, workerId, fencingToken,
            "job_state=N'FAILED', finished_at=SYSUTCDATETIME(), lease_owner=NULL, lease_until=NULL, last_error=@error",
            new { error }, ct);

    private async Task<bool> GuardedUpdateAsync(long jobId, string workerId, long fencingToken,
        string setClause, object? extra, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_conn);
        await conn.OpenAsync(ct);
        var p = new DynamicParameters(extra);
        p.Add("id", jobId); p.Add("workerId", workerId); p.Add("fencingToken", fencingToken);
        var rows = await conn.ExecuteAsync(
            $"UPDATE dbo.outbox_job SET {setClause} WHERE id=@id AND lease_owner=@workerId AND fencing_token=@fencingToken AND job_state=N'RUNNING'", p);
        return rows == 1;
    }

    public async Task<int> RecoverExpiredLeasesAsync(CancellationToken ct)
    {
        await using var conn = new SqlConnection(_conn);
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        // upload-dispatch 失租不在此回收：必须走发送恢复→受理未知→回查（JOB-02）
        var expired = (await conn.QueryAsync<(long Id, string JobKind, string IntentKey, string Env, string? LeaseOwner)>(@"
SELECT id AS Id, job_kind AS JobKind, intent_key AS IntentKey, env AS Env, lease_owner AS LeaseOwner
FROM dbo.outbox_job WITH (UPDLOCK, READPAST)
WHERE job_state = N'RUNNING' AND lease_until < SYSUTCDATETIME() AND job_kind <> N'upload-dispatch'",
            transaction: tx)).ToList();
        if (expired.Count == 0)
        {
            await tx.CommitAsync(ct);
            return 0;
        }
        var rows = await conn.ExecuteAsync(@"
UPDATE dbo.outbox_job
SET job_state=N'PENDING', lease_owner=NULL, lease_until=NULL, next_run_at=SYSUTCDATETIME()
WHERE job_state=N'RUNNING' AND lease_until < SYSUTCDATETIME() AND job_kind <> N'upload-dispatch'",
            transaction: tx);
        // 设计 §4.8：租约回收事件只读留痕（M0 携带 5）
        foreach (var j in expired)
            await _audit.AppendAsync(conn, tx, new AuditEntry("SYSTEM", "Worker", "job.lease.recovered", "outbox_job",
                j.Id.ToString(), null, null,
                System.Text.Json.JsonSerializer.Serialize(new { j.JobKind, j.IntentKey, prevOwner = j.LeaseOwner }),
                null, null, j.Env));
        await tx.CommitAsync(ct);
        return rows;
    }
}
