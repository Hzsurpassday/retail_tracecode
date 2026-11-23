using TraceFlow.Application.Abstractions;

namespace TraceFlow.Infrastructure.Sources;

/// <summary>
/// 真实 KSOA 直连适配器占位（DB-01 只读）。G-ERP 取证门禁（真实库结构/键稳定性/时区/换算方向）
/// 通过并更新 ksoa-adapter-contract.md 之前，禁止写任何 SQL——一律抛 AdapterNotCertifiedException。
/// </summary>
public sealed class RealKsoaAdapter : ISourceAdapter
{
    public string ContractVersion => "ksoa-real-0";

    public Task<IReadOnlyList<SourceDocumentDto>> FetchAsync(string entity, DateTime windowStartUtc, DateTime windowEndUtc, CancellationToken ct)
        => throw new AdapterNotCertifiedException("KSOA 真实适配器未取证（G-ERP 未通过）；当前仅允许 Mock fixture 通道");
}
