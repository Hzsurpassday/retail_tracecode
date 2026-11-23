using System.Text.Json;
using TraceFlow.Application.Abstractions;
using TraceFlow.Infrastructure.Master;
using TraceFlow.Infrastructure.Settings;

namespace TraceFlow.Infrastructure.Jobs;

/// <summary>master-match：周期/每日/导入提交/人工重匹配共用（payload.entity 可为 null）。</summary>
public sealed class MasterMatchJobHandler : IJobHandler
{
    private readonly string _cs;
    private readonly SettingsService _settings;
    private readonly IAuditLog _audit;

    public MasterMatchJobHandler(string connectionString, SettingsService settings, IAuditLog audit)
    { _cs = connectionString; _settings = settings; _audit = audit; }

    public string Kind => "master-match";

    public async Task HandleAsync(ClaimedJob job, CancellationToken ct)
    {
        using var payload = JsonDocument.Parse(job.PayloadJson!);
        var orgId = payload.RootElement.GetProperty("orgId").GetInt64();
        var env = payload.RootElement.GetProperty("env").GetString()!;
        await new MatchingService(_cs, _settings, _audit).RunAsync(orgId, env, ct);
    }
}
