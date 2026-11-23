using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Infrastructure.Settings;

namespace TraceFlow.Infrastructure.Sources;

/// <summary>
/// 周期调度（SYNC-04）：30 秒轮询桶 + 每日全量回扫 + 5 分钟匹配桶。
/// 意图键含时间桶→outbox UQ(org,env,kind,intent) 天然去重，多实例并发安全。
/// 仅 MOCK（M1）；暂停通道跳过 source-sync/upstream-download/enterprise-sync（master-match 为本地计算，继续）。
/// </summary>
public sealed class SyncScheduler
{
    private readonly string _cs;
    private readonly IJobQueue _queue;
    private readonly SettingsService _settings;
    private readonly string _env;

    public SyncScheduler(string connectionString, IJobQueue queue, SettingsService settings, string env)
    {
        _cs = connectionString;
        _queue = queue;
        _settings = settings;
        _env = env;
    }

    public async Task<int> TickAsync(DateTime nowUtc, CancellationToken ct)
    {
        if (_env != "MOCK") return 0;
        await using var c = new SqlConnection(_cs);
        await c.OpenAsync(ct);
        var orgId = await c.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT id FROM dbo.organization WHERE is_active=1", cancellationToken: ct));
        if (orgId is null) return 0;
        var policy = await _settings.GetSyncPolicyAsync(ct);
        var upstreamPoll = await _settings.GetUpstreamPollSecondsAsync(ct);
        var paused = await _settings.IsChannelPausedAsync(ct);
        var unix = new DateTimeOffset(nowUtc).ToUnixTimeSeconds();
        var n = 0;
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
        if (!paused)
        {
            foreach (var entity in new[] { "sale", "purchase-in" })
            {
                var periodicPayload = JsonSerializer.Serialize(new { orgId = orgId.Value, env = _env, entity, trigger = "periodic" });
                await _queue.EnqueueAsync(c, tx, orgId.Value, _env, "source-sync",
                    $"periodic:{entity}:{unix / policy.PollSeconds}", periodicPayload, nowUtc, ct);
                n++;
                var dailyPayload = JsonSerializer.Serialize(new { orgId = orgId.Value, env = _env, entity, trigger = "daily" });
                await _queue.EnqueueAsync(c, tx, orgId.Value, _env, "source-sync",
                    $"daily:{entity}:{nowUtc:yyyyMMdd}", dailyPayload, nowUtc, ct);
                n++;
            }
            var upPayload = JsonSerializer.Serialize(new { orgId = orgId.Value, env = _env, trigger = "periodic", start = nowUtc.AddDays(-7), end = nowUtc });
            await _queue.EnqueueAsync(c, tx, orgId.Value, _env, "upstream-download",
                $"periodic:{unix / upstreamPoll}", upPayload, nowUtc, ct);
            n++;
            var entPayload = JsonSerializer.Serialize(new { orgId = orgId.Value, env = _env, trigger = "daily" });
            await _queue.EnqueueAsync(c, tx, orgId.Value, _env, "enterprise-sync",
                $"daily:{nowUtc:yyyyMMdd}", entPayload, nowUtc, ct);
            n++;
        }
        var matchPayload = JsonSerializer.Serialize(new { orgId = orgId.Value, env = _env, entity = (string?)null, trigger = "periodic" });
        await _queue.EnqueueAsync(c, tx, orgId.Value, _env, "master-match",
            $"match:periodic:{unix / 300}", matchPayload, nowUtc, ct);
        n++;
        // 入库自动匹配为本地计算（不入渠道），暂停通道时仍须周期兜底新 COMPLETE 快照（R8b/R8c）。
        var inboundPayload = JsonSerializer.Serialize(new { orgId = orgId.Value, env = _env, snapshotId = (long?)null, trigger = "periodic" });
        await _queue.EnqueueAsync(c, tx, orgId.Value, _env, "inbound-match",
            $"inbound:periodic:{unix / 300}", inboundPayload, nowUtc, ct);
        n++;
        await tx.CommitAsync(ct);
        return n;
    }
}
