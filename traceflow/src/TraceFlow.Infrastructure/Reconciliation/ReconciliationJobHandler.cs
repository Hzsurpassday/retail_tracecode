using System.Globalization;
using System.Text.Json;
using TraceFlow.Application.Abstractions;

namespace TraceFlow.Infrastructure.Reconciliation;

/// <summary>
/// <c>reconciliation</c> 作业处理器（REC-01/02/03）：认领后从 payload 取窗口与业务类型，执行
/// <see cref="ReconciliationService.RunAsync"/>。payload 由 <see cref="ReconciliationService.EnqueueAsync"/> /
/// <see cref="ReconciliationScheduler"/> 写入（windowStart/windowEnd ISO8601Z、businessType、env）。
/// 作业本身不调用 TOP：平台侧只用已持久化的 upload_attempt/code_result 证据。
/// </summary>
public sealed class ReconciliationJobHandler : IJobHandler
{
    private readonly ReconciliationService _service;

    public ReconciliationJobHandler(ReconciliationService service) => _service = service;

    public string Kind => ReconciliationService.JobKind;

    public async Task HandleAsync(ClaimedJob job, CancellationToken ct)
    {
        var payload = ParsePayload(job.PayloadJson);
        await _service.RunAsync(payload.Env, payload.WindowStartUtc, payload.WindowEndUtc, payload.BusinessType, ct);
    }

    private static Payload ParsePayload(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
            throw new InvalidOperationException("reconciliation 作业缺少 payload（窗口/业务类型）");
        using var doc = JsonDocument.Parse(payloadJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("reconciliation 作业 payload 不是 JSON 对象");
        var root = doc.RootElement;
        return new Payload(
            ReadTime(root, "windowStart"),
            ReadTime(root, "windowEnd"),
            ReadString(root, "businessType"),
            ReadString(root, "env"));
    }

    private static DateTime ReadTime(JsonElement root, string name)
    {
        var raw = ReadString(root, name);
        if (!DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var value))
            throw new InvalidOperationException($"reconciliation 作业 payload 字段 {name} 不是合法 ISO-8601 时间");
        return DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }

    private static string ReadString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException($"reconciliation 作业 payload 缺少字符串字段 {name}");
        return value.GetString() ?? "";
    }

    private sealed record Payload(DateTime WindowStartUtc, DateTime WindowEndUtc, string BusinessType, string Env);
}