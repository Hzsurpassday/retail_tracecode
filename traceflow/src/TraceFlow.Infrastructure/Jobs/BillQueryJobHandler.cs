using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Infrastructure.Uploads;

namespace TraceFlow.Infrastructure.Jobs;

/// <summary>
/// bill-query 作业（TOP-02..04、JOB-04）：认领后按意图键分派——
/// <c>query:part:{id}</c> 回查单个分片；<c>query:scan:{bucket}</c> 作为周期兜底挑出到期分片逐片回查（同一路径）。
/// 租约/fencing 由 <see cref="BillQueryService"/> 的 T3 守卫复核；返回 Skipped 表示失租或分片已终态，
/// 服务内已按需改期，处理器不再干预（Worker 的 Complete 对已改期作业幂等失败）。
/// </summary>
public sealed class BillQueryJobHandler : IJobHandler
{
    private readonly BillQueryService _service;
    private readonly string _connectionString;

    public BillQueryJobHandler(BillQueryService service, string connectionString)
    {
        _service = service;
        _connectionString = connectionString;
    }

    public string Kind => BillQueryService.JobKind;

    public async Task HandleAsync(ClaimedJob job, CancellationToken ct)
    {
        var owner = await ReadLeaseOwnerAsync(job.Id, job.FencingToken, ct);
        if (owner is null) return; // 租约已失效：不做任何回查/写回

        if (job.IntentKey.StartsWith(BillQueryService.PartIntentPrefix, StringComparison.Ordinal))
        {
            var partId = ParsePartId(job.IntentKey.Substring(BillQueryService.PartIntentPrefix.Length), job.IntentKey);
            await _service.QueryPartAsync(partId, owner, job.FencingToken, ct);
            return;
        }

        if (job.IntentKey.StartsWith(BillQueryService.ScanIntentPrefix, StringComparison.Ordinal))
        {
            var env = ReadEnv(job.PayloadJson);
            var partIds = await _service.SelectDuePartIdsAsync(env, BillQueryService.ScanLimit, ct);
            foreach (var partId in partIds)
            {
                ct.ThrowIfCancellationRequested();
                await _service.QueryPartFromScanAsync(partId, owner, job.FencingToken, job.IntentKey, ct);
            }
            return;
        }

        // 兼容仅带 payload 的作业（partId）。
        if (ReadPartId(job.PayloadJson) is long fromPayload)
        {
            await _service.QueryPartAsync(fromPayload, owner, job.FencingToken, ct);
            return;
        }

        throw new InvalidOperationException($"bill-query 作业缺少分片标识（payload/intent: {job.IntentKey}）");
    }

    private static long ParsePartId(string raw, string intentKey)
    {
        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var partId)) return partId;
        throw new InvalidOperationException($"bill-query 意图键非法（{intentKey}）");
    }

    private static long? ReadPartId(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        using var doc = JsonDocument.Parse(payloadJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
        return doc.RootElement.TryGetProperty("partId", out var partId) && partId.TryGetInt64(out var value)
            ? value
            : null;
    }

    private static string? ReadEnv(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        using var doc = JsonDocument.Parse(payloadJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
        return doc.RootElement.TryGetProperty("env", out var env) && env.ValueKind == JsonValueKind.String
            ? env.GetString()
            : null;
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