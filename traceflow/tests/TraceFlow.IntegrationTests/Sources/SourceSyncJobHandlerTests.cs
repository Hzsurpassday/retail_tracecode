using System.Runtime.Versioning; // brief 缺失此 using（SupportedOSPlatform 所需）；与 SourceSyncTests/ApiGateTests 标注模式一致（T6 偏差 1 同型）
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Infrastructure.Jobs;
using TraceFlow.Infrastructure.Security;
using TraceFlow.Infrastructure.Settings;
using TraceFlow.Infrastructure.Sources;

namespace TraceFlow.IntegrationTests.Sources;

[SupportedOSPlatform("windows")]
public class SourceSyncJobHandlerTests : IAsyncLifetime
{
    private Sql2008Fixture _fx = null!;
    public async Task InitializeAsync() { _fx = new Sql2008Fixture(); await _fx.InitializeAsync(); }
    public Task DisposeAsync() => _fx.DisposeAsync();

    private static string FixtureDir => Path.Combine(AppContext.BaseDirectory, "fixtures", "ksoa");

    private async Task<(long OrgId, long SysId)> SeedAsync()
    {
        await using var c = new SqlConnection(_fx.ConnectionString);
        await c.OpenAsync();
        return await TestSeed.EnsureAsync(c);
    }

    private async Task<ClaimedJob> EnqueueAndClaimAsync(long orgId, string payload, string intent)
    {
        var queue = new JobQueue(_fx.ConnectionString, new AuditLog());
        await using var c = new SqlConnection(_fx.ConnectionString);
        await c.OpenAsync();
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
        await queue.EnqueueAsync(c, tx, orgId, "MOCK", "source-sync", intent, payload, DateTime.UtcNow, CancellationToken.None);
        await tx.CommitAsync();
        var job = await queue.ClaimAsync("test-worker", "MOCK", new[] { "source-sync" }, TimeSpan.FromMinutes(5), CancellationToken.None);
        return job!;
    }

    private SourceSyncJobHandler NewHandler()
        => new(_fx.ConnectionString, new FixtureKsoaAdapter(FixtureDir),
               new SettingsService(_fx.ConnectionString, new DpapiProtector()), new AuditLog());

    [Fact]
    public async Task First_periodic_run_uses_daily_rescan_window()
    {
        var (orgId, sysId) = await SeedAsync();
        var before = DateTime.UtcNow;
        var job = await EnqueueAndClaimAsync(orgId,
            $$"""{"orgId":{{orgId}},"env":"MOCK","entity":"sale","trigger":"periodic"}""", "h-first-" + Guid.NewGuid().ToString("N"));
        await NewHandler().HandleAsync(job, CancellationToken.None);
        await using var c = new SqlConnection(_fx.ConnectionString);
        var start = await c.ExecuteScalarAsync<DateTime>(
            "SELECT window_start FROM dbo.sync_checkpoint WHERE source_system_id=@s AND entity=N'sale' AND checkpoint_kind=N'WINDOW'", new { s = sysId });
        // 首次无检查点：now−30 天（允许测试执行秒级误差）
        Assert.True((before.AddDays(-30) - start).Duration() < TimeSpan.FromMinutes(1));
        Assert.True(await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.source_snapshot") >= 3);
    }

    [Fact]
    public async Task Stale_checkpoint_gap_never_truncated() // AT-007
    {
        var (orgId, sysId) = await SeedAsync();
        var oldEnd = DateTime.UtcNow.AddDays(-10); // 停机 10 天
        await using (var c = new SqlConnection(_fx.ConnectionString))
        {
            await c.OpenAsync();
            await c.ExecuteAsync(@"
INSERT INTO dbo.sync_checkpoint(source_system_id,entity,checkpoint_kind,window_start,window_end,last_success_at)
VALUES(@s,N'sale',N'WINDOW',@ws,@we,@we)", new { s = sysId, ws = oldEnd.AddDays(-1), we = oldEnd });
        }
        var job = await EnqueueAndClaimAsync(orgId,
            $$"""{"orgId":{{orgId}},"env":"MOCK","entity":"sale","trigger":"periodic"}""", "h-gap-" + Guid.NewGuid().ToString("N"));
        await NewHandler().HandleAsync(job, CancellationToken.None);
        await using var c2 = new SqlConnection(_fx.ConnectionString);
        var start = await c2.ExecuteScalarAsync<DateTime>(
            "SELECT window_start FROM dbo.sync_checkpoint WHERE source_system_id=@s AND entity=N'sale' AND checkpoint_kind=N'WINDOW'", new { s = sysId });
        // 起点=旧检查点终点−72h 回扫，而不是 now−72h（缺口全覆盖）
        Assert.Equal(oldEnd.AddHours(-72), start, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Manual_trigger_honors_explicit_window()
    {
        var (orgId, sysId) = await SeedAsync();
        var start = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc);
        var job = await EnqueueAndClaimAsync(orgId,
            $$"""{"orgId":{{orgId}},"env":"MOCK","entity":"purchase-in","trigger":"manual","startUtc":"{{start:O}}","endUtc":"{{end:O}}"}""",
            "h-manual-" + Guid.NewGuid().ToString("N"));
        await NewHandler().HandleAsync(job, CancellationToken.None);
        await using var c = new SqlConnection(_fx.ConnectionString);
        Assert.Equal(start, await c.ExecuteScalarAsync<DateTime>(
            "SELECT window_start FROM dbo.sync_checkpoint WHERE source_system_id=@s AND entity=N'purchase-in' AND checkpoint_kind=N'WINDOW'", new { s = sysId }));
        Assert.Equal(end, await c.ExecuteScalarAsync<DateTime>(
            "SELECT window_end FROM dbo.sync_checkpoint WHERE source_system_id=@s AND entity=N'purchase-in' AND checkpoint_kind=N'WINDOW'", new { s = sysId }));
    }

    /// <summary>
    /// F7：核验入队失败不得让已落库的 source-sync 失败（异常被吞并按 sale.verify.enqueue.failed 审计），
    /// 漏入队由 SyncScheduler 兜底。注入抛异常的 IJobQueue 使用构造器已有的可选 queue 缝，不改 handler 结构。
    /// </summary>
    [Fact]
    public async Task Enqueue_failure_is_audited_and_sync_job_still_completes()
    {
        var (orgId, sysId) = await SeedAsync();
        // 需至少一个 COMPLETE SALE 快照，核验入队查询才会真正调用 EnqueueAsync（否则空转不触发故障）。
        await using (var seedConn = new SqlConnection(_fx.ConnectionString))
        {
            await seedConn.OpenAsync();
            await seedConn.ExecuteAsync(@"
INSERT INTO dbo.source_snapshot(org_id,source_system_id,source_document_key,document_type,version,completeness_status,content_hash)
VALUES(@org,@sys,N'SALE-ENQFAIL',N'SALE',1,N'COMPLETE',REPLICATE('a',64))", new { org = orgId, sys = sysId });
        }
        var job = await EnqueueAndClaimAsync(orgId,
            $$"""{"orgId":{{orgId}},"env":"MOCK","entity":"sale","trigger":"periodic"}""",
            "h-enqfail-" + Guid.NewGuid().ToString("N"));
        var handler = new SourceSyncJobHandler(_fx.ConnectionString, new FixtureKsoaAdapter(FixtureDir),
            new SettingsService(_fx.ConnectionString, new DpapiProtector()), new AuditLog(), new ThrowingJobQueue());

        // 不抛异常 = 作业成功完成（WorkerHostedService 会 Complete 而不是退避重排）
        await handler.HandleAsync(job, CancellationToken.None);

        await using var c = new SqlConnection(_fx.ConnectionString);
        Assert.True(await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.source_snapshot") >= 3,
            "同步本身必须已落库");
        Assert.Equal(1, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.audit_log WHERE action=N'sale.verify.enqueue.failed'"));
        // 没有 sale-verify 入队行（入队整体失败回滚），由调度兜底补发
        Assert.Equal(0, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=N'sale-verify'"));
    }

    /// <summary>仅 EnqueueAsync 抛错的队列替身：模拟源同步后的核验入队失败。</summary>
    private sealed class ThrowingJobQueue : IJobQueue
    {
        public Task<long> EnqueueAsync(SqlConnection conn, SqlTransaction tx, long orgId, string env, string jobKind,
            string intentKey, string? payloadJson, DateTime? nextRunAtUtc, CancellationToken ct)
            => throw new InvalidOperationException("模拟核验入队失败");

        public Task<ClaimedJob?> ClaimAsync(string workerId, string env, IReadOnlyCollection<string> jobKinds,
            TimeSpan lease, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> CompleteAsync(long jobId, string workerId, long fencingToken, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<bool> RescheduleAsync(long jobId, string workerId, long fencingToken, DateTime nextRunAtUtc,
            string? error, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> FailAsync(long jobId, string workerId, long fencingToken, string error, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<int> RecoverExpiredLeasesAsync(CancellationToken ct) => throw new NotSupportedException();
    }
}
