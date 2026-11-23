using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Domain.Sources;
using TraceFlow.Infrastructure.Settings;

namespace TraceFlow.Infrastructure.Sources;

/// <summary>
/// source-sync 作业：窗口三态——manual 用 payload 显式窗口；daily 全量回扫；
/// periodic 增量（检查点终点−回扫窗；首次 now−每日回扫天数）。停机缺口绝不截断（AT-007）。
/// 失败抛异常→WorkerHostedService 指数退避重排（JOB-03）。
/// </summary>
public sealed class SourceSyncJobHandler : IJobHandler
{
    private readonly string _cs;
    private readonly ISourceAdapter _adapter;
    private readonly SettingsService _settings;
    private readonly IAuditLog _audit;

    public SourceSyncJobHandler(string connectionString, ISourceAdapter adapter, SettingsService settings, IAuditLog audit)
    {
        _cs = connectionString;
        _adapter = adapter;
        _settings = settings;
        _audit = audit;
    }

    public string Kind => "source-sync";

    public async Task HandleAsync(ClaimedJob job, CancellationToken ct)
    {
        using var j = JsonDocument.Parse(job.PayloadJson ?? "{}");
        var root = j.RootElement;
        var orgId = root.GetProperty("orgId").GetInt64();
        var env = root.GetProperty("env").GetString() ?? "MOCK";
        var entity = root.GetProperty("entity").GetString()
            ?? throw new InvalidOperationException("source-sync payload 缺少 entity");
        var trigger = root.TryGetProperty("trigger", out var t) ? t.GetString() ?? "periodic" : "periodic";

        var policy = await _settings.GetSyncPolicyAsync(ct);
        var now = DateTime.UtcNow;
        DateTime start, end;
        if (trigger == "manual")
        {
            start = root.GetProperty("startUtc").GetDateTime();
            end = root.GetProperty("endUtc").GetDateTime();
        }
        else if (trigger == "daily")
        {
            (start, end) = SyncWindowPolicy.Daily(now, policy);
        }
        else
        {
            await using var c = new SqlConnection(_cs);
            await c.OpenAsync(ct);
            var sysId = await c.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT id FROM dbo.source_system WHERE system_code=N'KSOA'", cancellationToken: ct));
            var cpEnd = await c.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
                "SELECT window_end FROM dbo.sync_checkpoint WHERE source_system_id=@s AND entity=@e AND checkpoint_kind=N'WINDOW'",
                new { s = sysId, e = entity }, cancellationToken: ct));
            (start, end) = SyncWindowPolicy.Incremental(cpEnd, now, policy);
        }
        var svc = new SourceSyncService(_cs, _adapter, _settings, _audit);
        await svc.RunAsync(orgId, env, entity, start, end, ct);
    }
}
