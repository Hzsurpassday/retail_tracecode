using TraceFlow.Infrastructure.Top;
using Xunit;

namespace TraceFlow.IntegrationTests.Top;

public class ChannelPauseTests : IClassFixture<Sql2008Fixture>
{
    private readonly Sql2008Fixture _fx;
    public ChannelPauseTests(Sql2008Fixture fx) => _fx = fx;

    [Fact]
    public async Task Pause_idempotent_scope_isolated_resume_and_repause()
    {
        var svc = new ChannelPauseService();
        var scope = "method:t3pause." + Guid.NewGuid().ToString("N")[..8];
        var keys = new[] { scope, "credential:top" };
        await using var c = await _fx.OpenAsync();
        await using (var tx = (Microsoft.Data.SqlClient.SqlTransaction)c.BeginTransaction())
        {
            await svc.PauseAsync(c, tx, "MOCK", keys, "FAIL_BIZ_AUTH_ERROR", "test", default);
            await svc.PauseAsync(c, tx, "MOCK", keys, "again", "test", default); // 幂等
            tx.Commit();
        }
        Assert.True(await svc.IsPausedAsync(c, null, "MOCK", new[] { scope }, default));
        Assert.False(await svc.IsPausedAsync(c, null, "LIVE", new[] { scope }, default)); // env 隔离
        Assert.False(await svc.IsPausedAsync(c, null, "MOCK", new[] { "method:other" }, default)); // 只影响相关管道
        var list = await svc.ListAsync(c, "MOCK", default);
        Assert.Contains(list, p => p.ScopeKey == scope && p.IsActive);
        await using (var tx = (Microsoft.Data.SqlClient.SqlTransaction)c.BeginTransaction())
        {
            Assert.Equal(1, await svc.ResumeAsync(c, tx, "MOCK", scope, "admin", default));
            tx.Commit();
        }
        Assert.False(await svc.IsPausedAsync(c, null, "MOCK", new[] { scope }, default));
        await using (var tx = (Microsoft.Data.SqlClient.SqlTransaction)c.BeginTransaction())
        {
            await svc.PauseAsync(c, tx, "MOCK", new[] { scope }, "repause", "test", default); // 解除后可再暂停
            tx.Commit();
        }
        Assert.True(await svc.IsPausedAsync(c, null, "MOCK", new[] { scope }, default));
    }
}
