using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Infrastructure.Sales;
using TraceFlow.Infrastructure.Top;

namespace TraceFlow.Infrastructure.Jobs;

/// <summary>
/// sale-verify 作业：payload {orgId,env,snapshotId} → 核验服务。平台限流/暂停（verification=WAIT_RETRY）时
/// 用指数退避 <see cref="IJobQueue.RescheduleAsync"/> 重排；其余状态正常返回由 Worker 完成（JOB-03）。
/// </summary>
public sealed class SaleVerifyJobHandler : IJobHandler
{
    private readonly SaleVerificationService _service;
    private readonly IJobQueue _queue;
    private readonly string _cs;

    public SaleVerifyJobHandler(SaleVerificationService service, IJobQueue queue, string connectionString)
    {
        _service = service;
        _queue = queue;
        _cs = connectionString;
    }

    public string Kind => SaleVerificationService.JobKind;

    public async Task HandleAsync(ClaimedJob job, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(job.PayloadJson ?? "{}");
        var root = doc.RootElement;
        if (!root.TryGetProperty("snapshotId", out var snapshotIdElement) || !snapshotIdElement.TryGetInt64(out var snapshotId))
            throw new InvalidOperationException("sale-verify payload 缺少 snapshotId");

        var verificationId = await _service.VerifySnapshotAsync(snapshotId, ct);
        if (verificationId is null) return; // 非 COMPLETE SALE 快照：无事可做，正常完成
        if (await _service.GetVerificationStateAsync(verificationId.Value, ct) != SaleVerificationService.StateWaitRetry)
            return;

        // WAIT_RETRY：不得 Complete，按限流退避重排（租约持有者为 Worker，经 fencing token 守卫）。
        var owner = await ReadLeaseOwnerAsync(job.Id, job.FencingToken, ct);
        if (owner is null) return; // 租约已失效：交租约回收/重认领处理
        await _queue.RescheduleAsync(job.Id, owner, job.FencingToken,
            DateTime.UtcNow.Add(RateBudgetService.Backoff(job.AttemptCount)), SaleVerificationService.StateWaitRetry, ct);
    }

    private async Task<string?> ReadLeaseOwnerAsync(long jobId, long fencingToken, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<string?>(new CommandDefinition(@"
SELECT lease_owner FROM dbo.outbox_job
WHERE id=@id AND fencing_token=@token AND job_state=N'RUNNING'",
            new { id = jobId, token = fencingToken }, cancellationToken: ct));
    }
}
