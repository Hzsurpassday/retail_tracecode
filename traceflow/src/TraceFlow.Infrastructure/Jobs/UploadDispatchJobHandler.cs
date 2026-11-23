using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Infrastructure.Uploads;

namespace TraceFlow.Infrastructure.Jobs;

/// <summary>
/// upload-dispatch 作业（§3.2）：认领后按 payload/intent 解析分片 → 调 <see cref="UploadDispatchService"/>。
/// <c>StaleRejected</c> 表示分片/任务已不可发送或写回被 fencing 拒绝（迟到写回），作业以 "stale" 收尾；
/// 限流/授权暂停已由服务在 T3 内按平台等待重排，本处理器不再干预；其余结论由 Worker 正常完成（JOB-01/JOB-02）。
/// </summary>
public sealed class UploadDispatchJobHandler : IJobHandler
{
    private const string StaleError = "stale";

    private readonly UploadDispatchService _service;
    private readonly IJobQueue _queue;
    private readonly string _connectionString;

    public UploadDispatchJobHandler(UploadDispatchService service, IJobQueue queue, string connectionString)
    {
        _service = service;
        _queue = queue;
        _connectionString = connectionString;
    }

    public string Kind => UploadDispatchService.JobKind;

    public async Task HandleAsync(ClaimedJob job, CancellationToken ct)
    {
        var partId = ResolvePartId(job);
        // 处理器拿不到 workerId（租约持有者是认领方），从作业行读取并在 token 仍匹配时使用。
        var owner = await ReadLeaseOwnerAsync(job.Id, job.FencingToken, ct);
        if (owner is null) return; // 租约已失效：交给发送恢复（受理未知→先回查），不得再次上传

        var outcome = await _service.DispatchAsync(partId, owner, job.FencingToken, ct);
        if (outcome == DispatchOutcome.StaleRejected)
            await _queue.FailAsync(job.Id, owner, job.FencingToken, StaleError, ct);
    }

    private static long ResolvePartId(ClaimedJob job)
    {
        if (!string.IsNullOrWhiteSpace(job.PayloadJson))
        {
            using var doc = JsonDocument.Parse(job.PayloadJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("partId", out var partId)
                && partId.TryGetInt64(out var value))
                return value;
        }
        // 兼容仅带 intent 的作业（part:{id}）
        const string prefix = "part:";
        if (job.IntentKey.StartsWith(prefix, StringComparison.Ordinal)
            && long.TryParse(job.IntentKey.Substring(prefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var fromIntent))
            return fromIntent;
        throw new InvalidOperationException($"upload-dispatch 作业缺少分片标识（payload/intent: {job.IntentKey}）");
    }

    private async Task<string?> ReadLeaseOwnerAsync(long jobId, long fencingToken, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<string?>(new CommandDefinition(@"
SELECT lease_owner FROM dbo.outbox_job
WHERE id=@id AND fencing_token=@token AND job_state=N'RUNNING'",
            new { id = jobId, token = fencingToken }, cancellationToken: ct));
    }
}