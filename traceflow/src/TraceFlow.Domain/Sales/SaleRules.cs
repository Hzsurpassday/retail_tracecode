using TraceFlow.Domain.Codes;

namespace TraceFlow.Domain.Sales;

/// <summary>
/// 销售核验纯规则输入行：来源行 + 商品追溯分类（经 product_mapping 推导）。
/// CodeNormalized 已按 TraceCodeNormalizer 归一；Emptied 行为 null。
/// </summary>
public sealed record SaleLineInput(
    long SourceLineId,
    string SourceLineKey,
    string ProductSourceId,
    decimal Quantity,
    string? CodeNormalized,
    string EventKind,
    string Classification,
    string? ClassificationReason);

/// <summary>
/// 规则结论：失败码（首个触发的规则）+ 三个诊断清单 + 正负行分桶。
/// Ok=true 时 FailCode 必为 null；Ok=false 时 SaleLines/ReturnLines 不含参与上传的行。
/// </summary>
public sealed record SaleRuleResult(
    bool Ok,
    string? FailCode,
    IReadOnlyList<SaleLineInput> SaleLines,
    IReadOnlyList<SaleLineInput> ReturnLines,
    IReadOnlyList<string> DuplicateCodes,
    IReadOnlyList<SaleLineInput> MissingCodeLines,
    IReadOnlyList<SaleLineInput> UnknownClassLines);

/// <summary>
/// SALE-01..04 纯函数（无 IO，可在单测内穷举）。规则优先级即 FailCode 的判定顺序：
/// 1) 分类不明确（UNKNOWN / 非 REQUIRED|EXEMPT / EXEMPT 缺依据）→ UNKNOWN_CLASSIFICATION；
/// 2) REQUIRED 行码缺失 → MISSING_CODE，随后码格式非「8 前缀 20 位数字」→ CODE_FORMAT；
/// 3) 同单据内归一码重复 → DUPLICATE_CODE（跨全单据，含正负行）；
/// 4) 分桶：SALE 且数量&gt;0 → 销售桶；CONSUMER_RETURN 或数量&lt;0 → 退货桶；EXEMPT 行永不参与上传。
/// 任一失败即整单阻断，绝不「尽量上传一部分」（SALE-04）。
/// </summary>
public static class SaleRules
{
    public const string ClassificationRequired = "REQUIRED";
    public const string ClassificationExempt = "EXEMPT";
    public const string ClassificationUnknown = "UNKNOWN";

    public const string FailMissingCode = "MISSING_CODE";
    public const string FailCodeFormat = "CODE_FORMAT";
    public const string FailDuplicateCode = "DUPLICATE_CODE";
    public const string FailUnknownClassification = "UNKNOWN_CLASSIFICATION";

    public const string EventKindSale = "SALE";
    public const string EventKindConsumerReturn = "CONSUMER_RETURN";

    private static readonly IReadOnlyList<SaleLineInput> NoLines = Array.Empty<SaleLineInput>();
    private static readonly IReadOnlyList<string> NoCodes = Array.Empty<string>();

    public static SaleRuleResult Evaluate(IReadOnlyList<SaleLineInput> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        // 1) 分类门禁：分类缺失/未知/非受控值、EXEMPT 无依据一律阻断；不得默认为可上传或豁免。
        var unknown = lines.Where(l => !IsTraceable(l)).ToList();
        if (unknown.Count > 0)
            return Failure(FailUnknownClassification, NoLines, unknown, NoCodes);

        // EXEMPT 行带依据 → 直接从上传与码校验中移除（SALE-03）。
        var participating = lines.Where(l => l.Classification == ClassificationRequired).ToList();

        // 2) 码门禁：缺失优先于格式，两者都先于重复（便于运维按最可执行的错误修复）。
        var missing = participating.Where(l => string.IsNullOrWhiteSpace(l.CodeNormalized)).ToList();
        if (missing.Count > 0)
            return Failure(FailMissingCode, missing, NoLines, NoCodes);

        var badFormat = participating
            .Where(l => TraceCodeNormalizer.Normalize(l.CodeNormalized).Format != CodeFormat.Supported8Prefix20)
            .ToList();
        if (badFormat.Count > 0)
            return Failure(FailCodeFormat, badFormat, NoLines, NoCodes);

        // 此处所有码均为受支持格式 → Normalized 必非 null。
        var duplicate = participating
            .Select(l => TraceCodeNormalizer.Normalize(l.CodeNormalized).Normalized!)
            .GroupBy(c => c, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();
        if (duplicate.Count > 0)
            return Failure(FailDuplicateCode, NoLines, NoLines, duplicate);

        // 3) 分桶：正行入销售桶，负行/退货行入退货桶；其余（NO_EVENT、PURCHASE_IN、数量 0）不参与本次上传。
        var sale = new List<SaleLineInput>();
        var returns = new List<SaleLineInput>();
        foreach (var line in participating)
        {
            if (IsReturnLine(line))
                returns.Add(line);
            else if (IsSaleLine(line))
                sale.Add(line);
        }

        return new SaleRuleResult(true, null, sale, returns, NoCodes, NoLines, NoLines);
    }

    public static SaleRuleResult Failure(string failCode, IReadOnlyList<SaleLineInput> missing,
        IReadOnlyList<SaleLineInput> unknown, IReadOnlyList<string> duplicate)
        => new(false, failCode, NoLines, NoLines, duplicate, missing, unknown);

    private static bool IsTraceable(SaleLineInput line)
        => line.Classification switch
        {
            ClassificationRequired => true,
            ClassificationExempt => !string.IsNullOrWhiteSpace(line.ClassificationReason),
            _ => false,
        };

    private static bool IsReturnLine(SaleLineInput line)
        => line.EventKind == EventKindConsumerReturn || line.Quantity < 0;

    private static bool IsSaleLine(SaleLineInput line)
        => line.EventKind == EventKindSale && line.Quantity > 0;
}
