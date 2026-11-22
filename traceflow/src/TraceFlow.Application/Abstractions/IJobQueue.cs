namespace TraceFlow.Application.Abstractions;

public sealed record ClaimedJob(long Id, string JobKind, string IntentKey, string? PayloadJson,
    long FencingToken, DateTime LeaseUntilUtc, int AttemptCount);

public interface IJobQueue
{
    // 在调用方事务内入队（T1/T2 同事务要求）；同意图幂等返回已有 id
    Task<long> EnqueueAsync(Microsoft.Data.SqlClient.SqlConnection conn, Microsoft.Data.SqlClient.SqlTransaction tx,
        long orgId, string env, string jobKind, string intentKey, string? payloadJson,
        DateTime? nextRunAtUtc, CancellationToken ct);
    Task<ClaimedJob?> ClaimAsync(string workerId, string env, IReadOnlyCollection<string> jobKinds, TimeSpan lease, CancellationToken ct);
    Task<bool> CompleteAsync(long jobId, string workerId, long fencingToken, CancellationToken ct);
    Task<bool> RescheduleAsync(long jobId, string workerId, long fencingToken, DateTime nextRunAtUtc, string? error, CancellationToken ct);
    Task<bool> FailAsync(long jobId, string workerId, long fencingToken, string error, CancellationToken ct);
    // 通用租约回收：upload-dispatch 除外（由 Task 10 发送恢复处理，不得重新直接上传）
    Task<int> RecoverExpiredLeasesAsync(CancellationToken ct);
}