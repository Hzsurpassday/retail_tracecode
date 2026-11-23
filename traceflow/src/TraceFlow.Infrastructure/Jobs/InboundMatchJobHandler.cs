using System.Text.Json;
using TraceFlow.Application.Abstractions;
using TraceFlow.Infrastructure.Inbound;

namespace TraceFlow.Infrastructure.Jobs;

/// <summary>inbound-match：up:/map:/periodic 触发走待匹配扫描；payload.snapshotId 非空时定向单快照（T10 重匹配）。</summary>
public sealed class InboundMatchJobHandler : IJobHandler
{
    private readonly InboundMatchService _svc;
    public InboundMatchJobHandler(InboundMatchService svc) => _svc = svc;
    public string Kind => "inbound-match";

    public async Task HandleAsync(ClaimedJob job, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(job.PayloadJson ?? "{}");
        var r = doc.RootElement;
        var orgId = r.GetProperty("orgId").GetInt64();
        var env = r.GetProperty("env").GetString()!;
        long? snapshotId = r.TryGetProperty("snapshotId", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : null;
        await _svc.RunAsync(orgId, env, snapshotId, ct);
    }
}
