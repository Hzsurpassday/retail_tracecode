using System.Text.Json;

namespace TraceFlow.Domain.Top;

/// <summary>TOP-02/03/04：billstatus 的平台处理结论（上传成功≠处理成功，缺失证据绝不升级为成功）。</summary>
public enum BillStatusKind { Processing, AllSuccess, Partial, Failed, Unresolved }

/// <summary>逐码结论：<see cref="Result"/> 只允许 SUCCESS/FAILED/UNKNOWN（与 code_result 检查约束一致）。</summary>
public sealed record CodeStatus(string Code, string Result, string? Reason)
{
    public const string Success = "SUCCESS";
    public const string Failed = "FAILED";
    public const string Unknown = "UNKNOWN";
}

/// <summary>解析结论 + 证据说明（Note 只描述结构异常，不含平台凭证）。</summary>
public sealed record BillStatusParseResult(BillStatusKind Kind, IReadOnlyList<CodeStatus> Codes, string? Note);

/// <summary>
/// billstatus 候选映射（G-TOP 取证前，见 docs/contracts/top-contract.md 与计划 Global Constraints）：
/// <list type="bullet">
/// <item><c>result_type="1"</c> → Processing（上传成功≠处理成功）；</item>
/// <item><c>result_type="2"</c> → AllSuccess；若逐码证据含 FAILED/UNKNOWN 则降为 Partial（证据不足不得升级成功）；</item>
/// <item><c>result_type="3"</c> 或 <c>sub_process_flag="52"</c> → Partial（逐码证据缺失/为空同样是 Partial）；</item>
/// <item><c>result_type="4"</c> → Failed；</item>
/// <item>无 <c>result_type</c>/<c>model</c> 为空/JSON 不可解析 → Unresolved（继续回查）。</item>
/// </list>
/// 逐码 <c>code_list[].status</c>：<c>"1"</c>=SUCCESS、<c>"2"</c>=FAILED、缺失/其他=UNKNOWN。
/// 入参既接受内部业务体（<c>{result_type,...}</c>），也接受完整信封（回退 <c>model</c> 节点，R-M3-2 同型兼容）。
/// </summary>
public static class BillStatusParser
{
    private const string ProcessingType = "1";
    private const string AllSuccessType = "2";
    private const string PartialType = "3";
    private const string FailedType = "4";
    private const string PartialProcessFlag = "52";

    public static BillStatusParseResult Parse(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson))
            return Unresolved("缺少 billstatus 响应体");

        try
        {
            using var doc = JsonDocument.Parse(resultJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Unresolved("billstatus 响应体不是对象");

            // 完整信封（含 model）时下钻业务体；业务体本身无 result_type 且无 model → Unresolved。
            if (!root.TryGetProperty("result_type", out _)
                && root.TryGetProperty("model", out var model))
            {
                if (model.ValueKind != JsonValueKind.Object)
                    return Unresolved("model 节点不是对象");
                root = model;
            }

            var resultType = GetText(root, "result_type");
            var subProcessFlag = GetText(root, "sub_process_flag");
            var codes = ParseCodes(root);

            if (resultType == FailedType)
                return new BillStatusParseResult(BillStatusKind.Failed, codes, null);

            if (resultType == PartialType || subProcessFlag == PartialProcessFlag)
            {
                var note = codes.Count == 0 ? "部分成功但逐码证据为空：仅保留缺失证据，不升级成功" : null;
                return new BillStatusParseResult(BillStatusKind.Partial, codes, note);
            }

            if (resultType == AllSuccessType)
            {
                var notSuccess = codes.Where(c => c.Result != CodeStatus.Success).ToList();
                if (codes.Count > 0 && notSuccess.Count == 0)
                    return new BillStatusParseResult(BillStatusKind.AllSuccess, codes, null);
                if (codes.Count == 0)
                    return new BillStatusParseResult(BillStatusKind.AllSuccess, codes,
                        "result_type=2 但无逐码证据：整单按全部成功处理，逐码结论记为 UNKNOWN");
                return new BillStatusParseResult(BillStatusKind.Partial, codes,
                    "result_type=2 与逐码证据不一致：按部分成功处理，绝不升级成功");
            }

            if (resultType == ProcessingType)
                return new BillStatusParseResult(BillStatusKind.Processing, codes, null);

            return Unresolved("缺少 result_type（或无 model 节点）");
        }
        catch (JsonException)
        {
            return Unresolved("billstatus 响应不可解析");
        }
    }

    private static List<CodeStatus> ParseCodes(JsonElement root)
    {
        var list = new List<CodeStatus>();
        if (!root.TryGetProperty("code_list", out var codeList) || codeList.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var entry in codeList.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            var code = GetText(entry, "code");
            if (string.IsNullOrWhiteSpace(code)) continue;
            if (list.Any(c => string.Equals(c.Code, code, StringComparison.Ordinal))) continue;
            list.Add(new CodeStatus(code, MapStatus(GetText(entry, "status")),
                GetText(entry, "reason") ?? GetText(entry, "msg") ?? GetText(entry, "message")));
        }
        return list;
    }

    /// <summary>"1"=SUCCESS、"2"=FAILED、缺失/其他=UNKNOWN（绝不把缺失码当成功）。</summary>
    private static string MapStatus(string? status) => status switch
    {
        "1" => CodeStatus.Success,
        "2" => CodeStatus.Failed,
        _ => CodeStatus.Unknown,
    };

    private static BillStatusParseResult Unresolved(string note) => new(BillStatusKind.Unresolved, Array.Empty<CodeStatus>(), note);

    private static string? GetText(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()
         : e.TryGetProperty(name, out var n) && n.ValueKind == JsonValueKind.Number ? n.GetRawText()
         : null;
}