using TraceFlow.Application.Abstractions;

namespace TraceFlow.Infrastructure.Sources;

public static class SourceAdapterFactory
{
    public static ISourceAdapter Create(string mode, string fixtureDir) => mode switch
    {
        "fixture" => new FixtureKsoaAdapter(fixtureDir),
        "real" => new RealKsoaAdapter(),
        _ => throw new ArgumentException($"未知来源适配器模式: {mode}（仅 fixture/real）", nameof(mode))
    };
}
