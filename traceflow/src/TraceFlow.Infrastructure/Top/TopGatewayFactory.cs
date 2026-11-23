using TraceFlow.Application.Abstractions;

namespace TraceFlow.Infrastructure.Top;

public static class TopGatewayFactory
{
    public static ITopGateway Create(string mode, string fixtureDir) => mode switch
    {
        "fixture" => new FakeTopGateway(new TopFixtureHandler(fixtureDir).Handle),
        "real" => new RealTopGateway(),
        _ => throw new ArgumentException($"未知 TOP 网关模式: {mode}（仅 fixture/real）", nameof(mode))
    };
}
