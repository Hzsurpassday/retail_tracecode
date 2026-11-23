using System.Text.Json;
using TraceFlow.Application.Abstractions;
using TraceFlow.Infrastructure.Upstream;

namespace TraceFlow.Infrastructure.Jobs;

public sealed class UpstreamDownloadJobHandler : IJobHandler
{
    private readonly UpstreamDownloadService _svc;
    public UpstreamDownloadJobHandler(UpstreamDownloadService svc) => _svc = svc;
    public string Kind => "upstream-download";

    public async Task HandleAsync(ClaimedJob job, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(job.PayloadJson ?? "{}");
        var r = doc.RootElement;
        var orgId = r.GetProperty("orgId").GetInt64();
        var env = r.GetProperty("env").GetString()!;
        var end = r.TryGetProperty("end", out var e) ? e.GetDateTime() : DateTime.UtcNow;
        var start = r.TryGetProperty("start", out var s) ? s.GetDateTime() : end.AddDays(-7);
        long? billId = r.TryGetProperty("billId", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetInt64() : null;
        await _svc.RunAsync(orgId, env, start, end, billId, ct);
    }
}
