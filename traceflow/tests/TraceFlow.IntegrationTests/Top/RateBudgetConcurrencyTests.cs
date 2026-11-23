using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Infrastructure.Top;
using Xunit;

namespace TraceFlow.IntegrationTests.Top;

public class RateBudgetConcurrencyTests : IClassFixture<Sql2008Fixture>
{
    private readonly Sql2008Fixture _fx;
    public RateBudgetConcurrencyTests(Sql2008Fixture fx) => _fx = fx;

    // 每测试唯一前缀（沿用 RateBudgetTests.NewKeys 的隔离思路），避免跨用例共享行污染。
    private static string[] NewKeys()
    {
        var prefix = Guid.NewGuid().ToString("N");
        return new[] { $"appkey{prefix}:total", $"method{prefix}:call" };
    }

    [Fact]
    public async Task Parallel_first_touch_acquires_all_succeed_single_row()
    {
        var svc = new RateBudgetService();
        var keys = NewKeys();
        var now = DateTime.UtcNow;

        // 8 个并发首触：每个 worker 独立 SqlConnection + BeginTransaction，争用同两把新键（Task.WhenAll）。
        var workers = Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var c = await _fx.OpenAsync();
            await using var tx = c.BeginTransaction();
            // limits 全 NULL（本地不设帽）→ 每个 worker 都应获取成功（返回 null）。
            Assert.Null(await svc.TryAcquireAsync(c, tx, "MOCK", keys, now, default));
            tx.Commit();
        }).ToArray();

        // 若发生 1205 死锁或其他 SqlException，Task.WhenAll 会抛出 → 测试失败（无 worker 吞异常）。
        await Task.WhenAll(workers);

        await using var verify = await _fx.OpenAsync();
        foreach (var key in keys)
        {
            var rows = (await verify.QueryAsync<BudgetCount>(
                "SELECT day_count AS DayCount FROM dbo.top_rate_budget WHERE env=N'MOCK' AND budget_key=@k",
                new { k = key })).ToList();
            Assert.Single(rows);              // 每键恰好 1 行（select-first 缺行范围锁串行 + 唯一索引去重）
            Assert.Equal(8, rows[0].DayCount); // 8 个 worker 全部获取，NULL 限值不设帽
        }
    }

    private sealed record BudgetCount(int DayCount);

    [Fact]
    public async Task Parallel_pause_same_scope_single_active()
    {
        var svc = new ChannelPauseService();
        var scope = "method:t3cpause." + Guid.NewGuid().ToString("N")[..8];

        var ready = 0;
        var start = new TaskCompletionSource();
        var workers = Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var c = await _fx.OpenAsync();
            await using var tx = c.BeginTransaction();
            if (Interlocked.Increment(ref ready) == 8) start.SetResult();
            await start.Task;
            await svc.PauseAsync(c, tx, "MOCK", new[] { scope }, "FAIL_BIZ_AUTH_ERROR", "test", default);
            tx.Commit();
        }).ToArray();

        await Task.WhenAll(workers);

        await using var verify = await _fx.OpenAsync();
        // 过滤唯一索引 UX_pause_active(WHERE is_active=1) 保证同范围恰好 1 活跃行（幂等）。
        var active = await verify.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.top_channel_pause WHERE env=N'MOCK' AND scope_key=@k AND is_active=1",
            new { k = scope });
        Assert.Equal(1, active);
    }
}
