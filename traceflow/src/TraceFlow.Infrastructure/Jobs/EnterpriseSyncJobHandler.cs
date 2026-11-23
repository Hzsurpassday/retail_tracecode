using System.Text.Json;
using TraceFlow.Application.Abstractions;
using TraceFlow.Infrastructure.Upstream;

namespace TraceFlow.Infrastructure.Jobs;

public sealed class EnterpriseSyncJobHandler : IJobHandler
{
    private readonly EnterpriseSyncService _svc;
    public EnterpriseSyncJobHandler(EnterpriseSyncService svc) => _svc = svc;
    public string Kind => "enterprise-sync";

    public async Task HandleAsync(ClaimedJob job, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(job.PayloadJson ?? "{}");
        var r = doc.RootElement;
        await _svc.RunAsync(r.GetProperty("orgId").GetInt64(), r.GetProperty("env").GetString()!, ct);
    }
}
