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
}
