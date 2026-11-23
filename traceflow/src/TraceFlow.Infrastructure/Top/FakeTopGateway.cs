using TraceFlow.Application.Abstractions;

namespace TraceFlow.Infrastructure.Top;

/// <summary>Fixture 网关只服务 MOCK；LIVE 一律拒绝（硬隔离，G-TOP 前不演示真实通道）。</summary>
public sealed class FakeTopGateway : ITopGateway
{
    private readonly Func<TopCall, TopRawResponse> _handler;
    public FakeTopGateway(Func<TopCall, TopRawResponse> handler) => _handler = handler;

    public Task<TopRawResponse> CallAsync(TopCall call, CancellationToken ct)
    {
        if (call.Env != "MOCK")
            throw new TopNotCertifiedException("G-TOP 未取证：Fake 网关只服务 MOCK，真实通道禁用");
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_handler(call));
    }
}
