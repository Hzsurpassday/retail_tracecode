using Dapper;
using TraceFlow.Application.Abstractions;
using TraceFlow.Domain.Top;
using TraceFlow.Infrastructure.Security;
using TraceFlow.Infrastructure.Top;
using Xunit;

namespace TraceFlow.IntegrationTests.Top;

public class TopClientServiceTests : IClassFixture<Sql2008Fixture>
{
    private readonly Sql2008Fixture _fx;
    public TopClientServiceTests(Sql2008Fixture fx) => _fx = fx;

    private TopClientService Build(Func<TopCall, TopRawResponse> handler)
        => new(_fx.ConnectionString, new FakeTopGateway(handler), new RateBudgetService(), new ChannelPauseService(), new AuditLog());

    private static IReadOnlyDictionary<string, string> NoParams => new Dictionary<string, string>();

    [Fact]
    public async Task Success_increments_budget_and_returns_outcome()
    {
        var method = "t4ok." + Guid.NewGuid().ToString("N")[..8];
        var svc = Build(_ => new TopRawResponse(true, 200, null, """{"response_success":true,"request_id":"r-ok","result":{}}""", null));
        var o = await svc.CallAsync("MOCK", method, NoParams, null, "test", default);
        Assert.Equal(TopOutcomeKind.Success, o.Kind);
        await using var c = await _fx.OpenAsync();
        var n = await c.ExecuteScalarAsync<int>("SELECT day_count FROM dbo.top_rate_budget WHERE env=N'MOCK' AND budget_key=@k", new { k = "method:" + method });
        Assert.Equal(1, n);
    }

    [Fact]
    public async Task Auth_error_pauses_scopes_audits_and_blocks_next_call_without_http()
    {
        var method = "t4auth." + Guid.NewGuid().ToString("N")[..8];
        var calls = 0;
        var svc = Build(call =>
        {
            if (call.Method != method)
                return new TopRawResponse(true, 200, null, """{"response_success":true,"result":{}}""", null);
            calls++;
            return new TopRawResponse(true, 200, """{"code":15,"sub_code":"FAIL_BIZ_AUTH_ERROR","sub_msg":"无权限","request_id":"r-auth"}""", null, null);
        });
        var ex = await Assert.ThrowsAsync<ChannelPausedException>(() => svc.CallAsync("MOCK", method, NoParams, "REF-T4", "test", default));
        Assert.Equal("method:" + method, ex.ScopeKey);
        Assert.Equal(1, calls);
        // 第二次调用：暂停门禁在 HTTP 之前→不再触达网关
        await Assert.ThrowsAsync<ChannelPausedException>(() => svc.CallAsync("MOCK", method, NoParams, "REF-T4", "test", default));
        Assert.Equal(1, calls);
        await using var c = await _fx.OpenAsync();
        Assert.Equal(1, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.top_channel_pause WHERE env=N'MOCK' AND scope_key=@k AND is_active=1", new { k = "ent:REF-T4" }));
        Assert.Equal(1, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.audit_log WHERE action='top.channel.pause' AND object_id=@oid AND env=N'MOCK'", new { oid = "method:" + method }));
        // 其他方法不受影响（只暂停相关管道）
        var other = await svc.CallAsync("MOCK", method + ".other", NoParams, null, "test", default);
        Assert.Equal(TopOutcomeKind.Success, other.Kind);
    }

    [Fact]
    public async Task Rate_limit_registers_platform_wait_seconds_priority()
    {
        var method = "t4rate." + Guid.NewGuid().ToString("N")[..8];
        var svc = Build(_ => new TopRawResponse(true, 200, """{"code":7,"sub_code":"FAIL_BIZ_RATE_LIMIT"}""", null, 42));
        var ex = await Assert.ThrowsAsync<TopRateLimitedException>(() => svc.CallAsync("MOCK", method, NoParams, null, "test", default));
        Assert.Equal(42, (int)ex.Wait.TotalSeconds); // 平台明确等待秒数优先于退避
        await using var c = await _fx.OpenAsync();
        var next = await c.ExecuteScalarAsync<DateTime>("SELECT next_allowed_at FROM dbo.top_rate_budget WHERE env=N'MOCK' AND budget_key=@k", new { k = "method:" + method });
        Assert.True(next > DateTime.UtcNow.AddSeconds(30));
    }

    [Fact]
    public async Task Live_env_through_fake_gateway_is_not_certified()
    {
        var svc = Build(_ => new TopRawResponse(true, 200, null, """{"response_success":true}""", null));
        await Assert.ThrowsAsync<TopNotCertifiedException>(() => svc.CallAsync("LIVE", "t4live", NoParams, null, "test", default));
    }
}
