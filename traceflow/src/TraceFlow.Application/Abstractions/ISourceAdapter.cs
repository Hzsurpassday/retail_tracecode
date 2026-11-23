namespace TraceFlow.Application.Abstractions;

/// <summary>来源行 DTO：raw 字段原样携带（SRC-04 raw 永不改写）；SourceLineKey 为 ERP 真实行键字符串（SRC-02）。</summary>
public sealed record SourceLineDto(
    string? SourceLineKey, string ProductSourceId, decimal QuantityOriginal,
    string? UnitOriginal, string? BatchRaw, string? ExpiryRaw, string? TraceCodeRaw,
    string EventKind, string RawLineJson);

/// <summary>键/时间异常：raw 行完整保留在 RawLineJson，供接入异常台账展示（不丢行、不造行号）。</summary>
public sealed record SourceKeyIssue(string? SourceLineKey, string IssueKind, string Detail, string RawLineJson);

/// <summary>来源单据 DTO：BusinessDateTimeUtc 已由适配器按来源时区换算；KeyIssues 非空→服务侧判 INVALID。</summary>
public sealed record SourceDocumentDto(
    string SourceDocumentKey, string DocumentType, DateTime? BusinessDateTimeUtc,
    string? SourceTimeRaw, string? SourceTimeZone, string RawHeaderJson,
    IReadOnlyList<SourceLineDto> Lines, IReadOnlyList<SourceKeyIssue> KeyIssues);

/// <summary>
/// 来源适配器端口（DB-01 只读）。entity ∈ sale/purchase-in。
/// 真实 KSOA 适配器在 G-ERP 取证门禁通过前禁止实现查询（AdapterNotCertifiedException）。
/// </summary>
public interface ISourceAdapter
{
    string ContractVersion { get; }
    Task<IReadOnlyList<SourceDocumentDto>> FetchAsync(string entity, DateTime windowStartUtc, DateTime windowEndUtc, CancellationToken ct);
}
