using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Domain.Sources;
using TraceFlow.Infrastructure.Sales;
using TraceFlow.Infrastructure.Settings;

namespace TraceFlow.Infrastructure.Sources;

/// <summary>
/// source-sync 作业：窗口三态——manual 用 payload 显式窗口；daily 全量回扫；
/// periodic 增量（检查点终点−回扫窗；首次 now−每日回扫天数）。停机缺口绝不截断（AT-007）。
/// 同步后为新晋升 COMPLETE 的 SALE 快照入队 sale-verify（SALE-05；入队参数可选，测试可省略）。
/// 失败抛异常→WorkerHostedService 指数退避重排（JOB-03）。
/// </summary>
public sealed class SourceSyncJobHandler : IJobHandler
{
    private readonly string _cs;
    private readonly ISourceAdapter _adapter;
    private readonly SettingsService _settings;
    private readonly IAuditLog _audit;
    private readonly IJobQueue? _queue;

    public SourceSyncJobHandler(string connectionString, ISourceAdapter adapter, SettingsService settings, IAuditLog audit,
        IJobQueue? queue = null)
    {
        _cs = connectionString;
        _adapter = adapter;
        _settings = settings;
        _audit = audit;
        _queue = queue;
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
        // 入队失败不得让已落库的同步失败（HIGH 6）：吞掉并留审计，漏入队由 SyncScheduler 兜底补发。
        try
        {
            await EnqueueSaleVerificationsAsync(orgId, env, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await TryAuditEnqueueFailureAsync(orgId, env, ex, ct);
        }
    }

    /// <summary>入队失败审计（best-effort）：审计自身失败也绝不把异常抛回 source-sync，调度兜底仍会补入队。</summary>
    private async Task TryAuditEnqueueFailureAsync(long orgId, string env, Exception error, CancellationToken ct)
    {
        try
        {
            await using var c = new SqlConnection(_cs);
            await c.OpenAsync(ct);
            await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
            await _audit.AppendAsync(c, tx, new AuditEntry("SYSTEM", "Worker", "sale.verify.enqueue.failed",
                "organization", orgId.ToString(), error.Message, null,
                JsonSerializer.Serialize(new { error = error.GetType().Name, message = error.Message }),
                null, null, env));
            await tx.CommitAsync(ct);
        }
        catch (Exception auditError) when (auditError is not OperationCanceledException)
        {
            // 审计写入同样失败（库不可用等）：仅吞掉，交给调度兜底与日志观测。
        }
    }

    /// <summary>
    /// 快照晋升 COMPLETE 后的核验入队（SALE-05）：扫近 30 天「COMPLETE SALE 且无当前 PASSED/FAILED 结论」的快照，
    /// 每快照入队 sale-verify（intent snapshot:{id}，outbox UQ 幂等）。
    /// 偏差 D1：SourceSyncService 的写入事务未对外暴露，无法严格同事务入队；本方法紧随其后用独立短事务，
    /// 窗口由 SyncScheduler 的 5 分钟兜底覆盖（见 task-2-report 偏差记录）。
    /// </summary>
    private async Task EnqueueSaleVerificationsAsync(long orgId, string env, CancellationToken ct)
    {
        var queue = _queue;
        if (queue is null) return;
        await using var c = new SqlConnection(_cs);
        await c.OpenAsync(ct);
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
        var ids = (await c.QueryAsync<long>(new CommandDefinition(@"
SELECT s.id FROM dbo.source_snapshot s
WHERE s.org_id=@orgId AND s.document_type=N'SALE' AND s.completeness_status=N'COMPLETE'
  AND s.observed_at >= DATEADD(day,-30,SYSUTCDATETIME())
  AND NOT EXISTS (SELECT 1 FROM dbo.verification v
                  WHERE v.org_id=s.org_id AND v.env=@env AND v.source_snapshot_id=s.id AND v.is_current=1
                    AND v.state IN (N'PASSED',N'FAILED'))
  AND NOT EXISTS (SELECT 1 FROM dbo.source_snapshot n
                  WHERE n.org_id=s.org_id AND n.source_system_id=s.source_system_id
                    AND n.source_document_key=s.source_document_key AND n.version>s.version)
ORDER BY s.id", new { orgId, env }, tx, cancellationToken: ct))).ToList();
        foreach (var snapshotId in ids)
        {
            var payload = JsonSerializer.Serialize(new { orgId, env, snapshotId });
            await queue.EnqueueAsync(c, tx, orgId, env, SaleVerificationService.JobKind, $"snapshot:{snapshotId}",
                payload, DateTime.UtcNow, ct);
        }
        await tx.CommitAsync(ct);
    }
}
