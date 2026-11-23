using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Infrastructure.Sales;
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
            // 核验兜底（SALE-05）：近 24h COMPLETE SALE 且当前结论非 PASSED/FAILED 的快照（漏入队/依赖等待/未知重核），
            // 按 5 分钟桶重入队；无候选时零入队（既有桶计数不变）。
            var verifyIds = (await c.QueryAsync<long>(new CommandDefinition(@"
SELECT s.id FROM dbo.source_snapshot s
WHERE s.org_id=@orgId AND s.document_type=N'SALE' AND s.completeness_status=N'COMPLETE'
  AND s.observed_at >= DATEADD(hour,-24,@now)
  AND NOT EXISTS (SELECT 1 FROM dbo.verification v
                  WHERE v.org_id=s.org_id AND v.env=@env AND v.source_snapshot_id=s.id AND v.is_current=1
                    AND v.state IN (N'PASSED',N'FAILED'))
  AND NOT EXISTS (SELECT 1 FROM dbo.source_snapshot n
                  WHERE n.org_id=s.org_id AND n.source_system_id=s.source_system_id
                    AND n.source_document_key=s.source_document_key AND n.version>s.version)
ORDER BY s.id", new { orgId = orgId.Value, env = _env, now = nowUtc }, tx, cancellationToken: ct))).ToList();
            foreach (var snapshotId in verifyIds)
            {
                var verifyPayload = JsonSerializer.Serialize(new { orgId = orgId.Value, env = _env, snapshotId });
                await _queue.EnqueueAsync(c, tx, orgId.Value, _env, SaleVerificationService.JobKind,
                    $"snapshot:{snapshotId}:retry:{unix / 300}", verifyPayload, nowUtc, ct);
                n++;
            }
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
