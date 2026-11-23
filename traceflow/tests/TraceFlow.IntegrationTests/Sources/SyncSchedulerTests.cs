using System.Runtime.Versioning; // brief 缺失此 using（SupportedOSPlatform 所需）；与 SourceSyncTests/ApiGateTests 标注模式一致（T6 偏差 1 同型）
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Infrastructure.Jobs;
using TraceFlow.Infrastructure.Security;
using TraceFlow.Infrastructure.Settings;
using TraceFlow.Infrastructure.Sources;

namespace TraceFlow.IntegrationTests.Sources;

[SupportedOSPlatform("windows")]
// 每测试独立测试库：Paused 测试改写共享设置、桶计数断言要求完全隔离。
public class SyncSchedulerTests : IAsyncLifetime
{
    private Sql2008Fixture _fx = null!;
    public async Task InitializeAsync() { _fx = new Sql2008Fixture(); await _fx.InitializeAsync(); }
    public Task DisposeAsync() => _fx.DisposeAsync();

    private SyncScheduler NewSvc(string env = "MOCK")
        => new(_fx.ConnectionString, new JobQueue(_fx.ConnectionString, new AuditLog()),
               new SettingsService(_fx.ConnectionString, new DpapiProtector()), env);

    private async Task SeedAsync()
    {
        await using var c = new SqlConnection(_fx.ConnectionString);
        await c.OpenAsync();
        await TestSeed.EnsureAsync(c);
    }

    private async Task<int> JobCountAsync()
    {
        await using var c = new SqlConnection(_fx.ConnectionString);
        return await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.outbox_job");
    }

    [Fact]
    public async Task Tick_enqueues_periodic_daily_and_match_intents_idempotent()
    {
        await SeedAsync();
        var now = new DateTime(2026, 11, 22, 8, 0, 0, DateTimeKind.Utc);
        var n = await NewSvc().TickAsync(now, CancellationToken.None);
        Assert.Equal(8, n); // 2 实体 × (periodic+daily) + 1 match + 1 upstream + 1 enterprise + 1 inbound
        Assert.Equal(8, await JobCountAsync());
        await NewSvc().TickAsync(now, CancellationToken.None); // 同意图桶→幂等
        Assert.Equal(8, await JobCountAsync());
        await using var c = new SqlConnection(_fx.ConnectionString);
        // unix(2026-11-22T08:00Z)/300 动态计算，不硬编码桶号
        var unix = new DateTimeOffset(now).ToUnixTimeSeconds();
        Assert.Equal(1, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.outbox_job WHERE intent_key=@k", new { k = $"match:periodic:{unix / 300}" }));
        Assert.Equal(1, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.outbox_job WHERE intent_key=@k", new { k = $"daily:sale:{now:yyyyMMdd}" }));
    }

    [Fact]
    public async Task Next_poll_bucket_creates_new_periodic_intents()
    {
        await SeedAsync();
        var now = new DateTime(2026, 11, 22, 8, 0, 0, DateTimeKind.Utc);
        await NewSvc().TickAsync(now, CancellationToken.None);
        await NewSvc().TickAsync(now.AddSeconds(30), CancellationToken.None); // 默认 pollSeconds=30 → 新桶
        Assert.Equal(10, await JobCountAsync()); // +2 periodic（其余桶未变）
    }

    [Fact]
    public async Task Live_env_tick_enqueues_nothing()
    {
        await SeedAsync();
        var n = await NewSvc("LIVE").TickAsync(DateTime.UtcNow, CancellationToken.None);
        Assert.Equal(0, n);
        Assert.Equal(0, await JobCountAsync());
    }

    [Fact]
    public async Task Paused_channel_skips_source_sync_but_keeps_match()
    {
        await SeedAsync();
        var settings = new SettingsService(_fx.ConnectionString, new DpapiProtector());
        await using (var c = new SqlConnection(_fx.ConnectionString))
        {
            await c.OpenAsync();
            await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
            await settings.UpdateAsync(c, tx, "sync.paused", "true", null, "0", "暂停测试", "tester", CancellationToken.None);
            await tx.CommitAsync();
        }
        await NewSvc().TickAsync(new DateTime(2026, 11, 22, 9, 0, 0, DateTimeKind.Utc), CancellationToken.None);
        await using var c2 = new SqlConnection(_fx.ConnectionString);
        Assert.Equal(0, await c2.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=N'source-sync'"));
        Assert.Equal(1, await c2.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=N'master-match'"));
        Assert.Equal(0, await c2.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=N'upstream-download'"));
        Assert.Equal(0, await c2.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=N'enterprise-sync'"));
        Assert.Equal(1, await c2.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=N'inbound-match'"));
    }
}
