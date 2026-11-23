using TraceFlow.Application.Abstractions;

namespace TraceFlow.Infrastructure.Top;

/// <summary>G-TOP 取证前占位：不写签名/HTTP 代码，恒拒绝（附录 B 门禁）。</summary>
public sealed class RealTopGateway : ITopGateway
{
    public Task<TopRawResponse> CallAsync(TopCall call, CancellationToken ct)
        => throw new TopNotCertifiedException("G-TOP 未取证：TOP 签名与协议未实现，真实通道禁用");
}
