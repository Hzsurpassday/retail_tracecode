using Dapper;
using TraceFlow.Infrastructure.Top;
using Xunit;

namespace TraceFlow.IntegrationTests.Top;

public class RateBudgetTests : IClassFixture<Sql2008Fixture>
{
    private readonly Sql2008Fixture _fx;
    public RateBudgetTests(Sql2008Fixture fx) => _fx = fx;
    // 偏差说明（相对 brief 逐字稿）：appkey 键改为每测试唯一。brief 原稿共享字面量 "appkey:total"，
    // Next_allowed 测试会对该共享行提交 next_allowed_at=now+60，xUnit 非声明顺序执行时污染其余三个用例（实测 3 失败≈59.9s 等待）。
    // 唯一化仅消除跨用例共享行，所有断言与多键语义保持逐字不变。
    private static string[] NewKeys() => new[] { "appkey:t3." + Guid.NewGuid().ToString("N")[..8], "method:t3." + Guid.NewGuid().ToString("N")[..8] };

    [Fact]
    public async Task Acquire_increments_counters_and_persists_across_connections()
    {
        var svc = new RateBudgetService();
        var keys = NewKeys();
        var now = DateTime.UtcNow;
        await using (var c = await _fx.OpenAsync())
        await using (var tx = (Microsoft.Data.SqlClient.SqlTransaction)c.BeginTransaction())
        {
            Assert.Null(await svc.TryAcquireAsync(c, tx, "MOCK", keys, now, default));
            Assert.Null(await svc.TryAcquireAsync(c, tx, "MOCK", keys, now, default));
            tx.Commit();
        }
        // “重启”=新连接：计数仍在（JOB-03 持久化）
        await using var c2 = await _fx.OpenAsync();
        var counts = await c2.QuerySingleAsync<CountsRow>(
            "SELECT day_count AS Day, minute_count AS Minute, second_count AS Second FROM dbo.top_rate_budget WHERE env=N'MOCK' AND budget_key=@k",
            new { k = keys[0] });
        Assert.Equal(2, counts.Day);
        Assert.Equal(2, counts.Minute);
        Assert.Equal(2, counts.Second);
    }

    private sealed record CountsRow(int Day, int Minute, int Second);

    [Fact]
    public async Task Second_limit_exhaustion_returns_wait_without_consuming()
    {
        var svc = new RateBudgetService();
        var keys = NewKeys();
        var now = DateTime.UtcNow;
        await using var c = await _fx.OpenAsync();
        await using var tx = (Microsoft.Data.SqlClient.SqlTransaction)c.BeginTransaction();
        Assert.Null(await svc.TryAcquireAsync(c, tx, "MOCK", keys, now, default));
        await c.ExecuteAsync("UPDATE dbo.top_rate_budget SET second_limit=1 WHERE env=N'MOCK' AND budget_key=@k", new { k = keys[1] }, tx);
        var wait = await svc.TryAcquireAsync(c, tx, "MOCK", keys, now.AddMilliseconds(100), default);
        Assert.NotNull(wait);
        Assert.InRange(wait!.Value.TotalMilliseconds, 1, 1000);
        // 未消费：method 键 second_count 仍为 1
        var n = await c.ExecuteScalarAsync<int>("SELECT second_count FROM dbo.top_rate_budget WHERE env=N'MOCK' AND budget_key=@k", new { k = keys[1] }, tx);
        Assert.Equal(1, n);
        tx.Commit();
    }

    [Fact]
    public async Task Window_roll_resets_counter()
    {
        var svc = new RateBudgetService();
        var keys = NewKeys();
        await using var c = await _fx.OpenAsync();
        await using var tx = (Microsoft.Data.SqlClient.SqlTransaction)c.BeginTransaction();
        var past = DateTime.UtcNow.AddSeconds(-5);
        await c.ExecuteAsync(@"
INSERT INTO dbo.top_rate_budget(env,budget_key,second_count,second_window_start,second_limit)
VALUES(N'MOCK',@k,1,@past,1)", new { k = keys[1], past }, tx);
        // 窗口已过期→重置计数→获取成功
        Assert.Null(await svc.TryAcquireAsync(c, tx, "MOCK", keys, DateTime.UtcNow, default));
        var n = await c.ExecuteScalarAsync<int>("SELECT second_count FROM dbo.top_rate_budget WHERE env=N'MOCK' AND budget_key=@k", new { k = keys[1] }, tx);
        Assert.Equal(1, n); // 重置 0 后 +1
        tx.Commit();
    }

    [Fact]
    public async Task Next_allowed_wait_and_register_wait_max_semantics()
    {
        var svc = new RateBudgetService();
        var keys = NewKeys();
        var now = DateTime.UtcNow;
        await using var c = await _fx.OpenAsync();
        await using var tx = (Microsoft.Data.SqlClient.SqlTransaction)c.BeginTransaction();
        await svc.RegisterWaitAsync(c, tx, "MOCK", keys, now.AddSeconds(30), default);
        // RegisterWait 对不存在行是 UPDATE 0 行——先确保行存在再注册
        Assert.Null(await svc.TryAcquireAsync(c, tx, "MOCK", keys, now.AddSeconds(31), default)); // 创建行（等待已过）
        await svc.RegisterWaitAsync(c, tx, "MOCK", keys, now.AddSeconds(60), default);
        await svc.RegisterWaitAsync(c, tx, "MOCK", keys, now.AddSeconds(45), default); // 更早值不得回退
        var wait = await svc.TryAcquireAsync(c, tx, "MOCK", keys, now.AddSeconds(50), default);
        Assert.NotNull(wait);
        Assert.InRange(wait!.Value.TotalSeconds, 9, 11);
        tx.Commit();
    }

    [Fact]
    public async Task Live_and_mock_budgets_isolated()
    {
        var svc = new RateBudgetService();
        var key = "method:t3iso." + Guid.NewGuid().ToString("N")[..8];
        var now = DateTime.UtcNow;
        await using var c = await _fx.OpenAsync();
        await using var tx = (Microsoft.Data.SqlClient.SqlTransaction)c.BeginTransaction();
        await svc.RegisterWaitAsync(c, tx, "MOCK", new[] { key }, now, default);
        Assert.Null(await svc.TryAcquireAsync(c, tx, "MOCK", new[] { key }, now, default));
        await c.ExecuteAsync("UPDATE dbo.top_rate_budget SET next_allowed_at=@w WHERE env=N'MOCK' AND budget_key=@k", new { w = now.AddHours(1), k = key }, tx);
        // LIVE 同键不受 MOCK 暂停影响（硬隔离）
        Assert.Null(await svc.TryAcquireAsync(c, tx, "LIVE", new[] { key }, now, default));
        tx.Commit();
    }
}
