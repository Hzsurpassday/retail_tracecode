using System.Text.Json;
using TraceFlow.Application.Abstractions;
using TraceFlow.Domain.Sources;

namespace TraceFlow.Infrastructure.Sources;

/// <summary>
/// Mock 适配器：读 docs/contracts/fixtures/ksoa 的候选结构 JSON（G-ERP 前仅 Mock 使用）。
/// 静态 fixture 不按窗口过滤——窗口语义属真实适配器职责；重复读取由服务侧 content_hash 去重。
/// 键规则（SRC-02）：sale=(lshh,plh)，purchase-in=(djbh,dj_sn)，全程字符串；
/// plh 空→MISSING_LINE_KEY；单内重复→DUPLICATE_LINE_KEY（首条保留）；无单头行→ORPHAN_LINE；
/// 同 docKey 跨业务日→CROSS_DAY_REUSE；rq+ontime 解析失败→BAD_BUSINESS_TIME。
/// </summary>
public sealed class FixtureKsoaAdapter : ISourceAdapter
{
    private readonly string _fixtureDir;
    public FixtureKsoaAdapter(string fixtureDir) => _fixtureDir = fixtureDir;
    public string ContractVersion => "ksoa-fixture-1";

    public Task<IReadOnlyList<SourceDocumentDto>> FetchAsync(string entity, DateTime windowStartUtc, DateTime windowEndUtc, CancellationToken ct)
    {
        if (entity is not ("sale" or "purchase-in"))
            throw new ArgumentException($"未知来源实体: {entity}", nameof(entity));
        var docs = new List<SourceDocumentDto>();
        var seenDays = new Dictionary<string, DateOnly>(StringComparer.Ordinal);
        if (!Directory.Exists(_fixtureDir))
            return Task.FromResult<IReadOnlyList<SourceDocumentDto>>(docs);
        foreach (var file in Directory.GetFiles(_fixtureDir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            using var j = JsonDocument.Parse(File.ReadAllText(file));
            var root = j.RootElement;
            if (entity == "sale" && root.TryGetProperty("salemast", out var sm))
                docs.AddRange(ParseDocuments(sm, root.GetProperty("salemxls"), "lshh", "plh", "SALE",
                    qty => qty > 0 ? "SALE" : qty < 0 ? "CONSUMER_RETURN" : "NO_EVENT", seenDays));
            else if (entity == "purchase-in" && root.TryGetProperty("jh_rkhz", out var ph))
                docs.AddRange(ParseDocuments(ph, root.GetProperty("jh_rkmx"), "djbh", "dj_sn", "PURCHASE_IN",
                    _ => "PURCHASE_IN", seenDays));
        }
        return Task.FromResult<IReadOnlyList<SourceDocumentDto>>(docs);
    }

    private static IEnumerable<SourceDocumentDto> ParseDocuments(
        JsonElement headers, JsonElement lines, string keyProp, string lineKeyProp, string docType,
        Func<decimal, string> eventKindOf, Dictionary<string, DateOnly> seenDays)
    {
        var headerMap = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var missingKeyIssues = new List<SourceKeyIssue>();
        foreach (var h in headers.EnumerateArray())
        {
            var key = Str(h, keyProp);
            if (string.IsNullOrWhiteSpace(key))
            {
                missingKeyIssues.Add(new SourceKeyIssue(null, "MISSING_DOC_KEY", "单头单据键为空", h.GetRawText()));
                continue;
            }
            headerMap[key] = h;
        }
        var byDoc = new Dictionary<string, List<JsonElement>>(StringComparer.Ordinal);
        var orphans = new Dictionary<string, List<SourceKeyIssue>>(StringComparer.Ordinal);
        foreach (var l in lines.EnumerateArray())
        {
            var key = Str(l, keyProp);
            if (key is null || !headerMap.ContainsKey(key))
            {
                var orphanKey = key ?? "(missing-doc-key)";
                var issue = new SourceKeyIssue(Str(l, lineKeyProp), "ORPHAN_LINE",
                    $"行的 {keyProp}={key ?? "(null)"} 无对应单头", l.GetRawText());
                (orphans.TryGetValue(orphanKey, out var orphanList) ? orphanList : orphans[orphanKey] = new List<SourceKeyIssue>()).Add(issue);
                continue;
            }
            (byDoc.TryGetValue(key, out var lineList) ? lineList : byDoc[key] = new List<JsonElement>()).Add(l);
        }
        var result = new List<SourceDocumentDto>();
        foreach (var (docKey, header) in headerMap)
        {
            var docIssues = new List<SourceKeyIssue>();
            var rq = Str(header, "rq");
            var ontime = Str(header, "ontime");
            var businessUtc = BusinessTimeParser.ParseUtc(rq, ontime);
            if (businessUtc is null)
                docIssues.Add(new SourceKeyIssue(null, "BAD_BUSINESS_TIME",
                    $"rq={rq ?? "(null)"} ontime={ontime ?? "(null)"} 无法按 Asia/Shanghai 解析", header.GetRawText()));
            else
            {
                var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(businessUtc.Value, BusinessTimeParser.Shanghai));
                if (seenDays.TryGetValue(docKey, out var prev) && prev != day)
                    docIssues.Add(new SourceKeyIssue(null, "CROSS_DAY_REUSE",
                        $"docKey={docKey} 跨业务日复用 {prev:yyyy-MM-dd}→{day:yyyy-MM-dd}", header.GetRawText()));
                seenDays[docKey] = day;
            }
            var docLines = new List<SourceLineDto>();
            var seenLineKeys = new HashSet<string>(StringComparer.Ordinal);
            if (byDoc.TryGetValue(docKey, out var rawLines))
                foreach (var l in rawLines)
                {
                    var lineKey = Str(l, lineKeyProp);
                    if (string.IsNullOrWhiteSpace(lineKey))
                    {
                        docIssues.Add(new SourceKeyIssue(null, "MISSING_LINE_KEY",
                            $"{lineKeyProp} 为空，无法构成稳定行键；不造行号（SRC-02）", l.GetRawText()));
                        continue;
                    }
                    if (!seenLineKeys.Add(lineKey))
                    {
                        docIssues.Add(new SourceKeyIssue(lineKey, "DUPLICATE_LINE_KEY",
                            $"{lineKeyProp}={lineKey} 单内重复，保留首条", l.GetRawText()));
                        continue;
                    }
                    var qty = l.TryGetProperty("shl", out var shl) && shl.ValueKind == JsonValueKind.Number ? shl.GetDecimal() : 0m;
                    docLines.Add(new SourceLineDto(lineKey, Str(l, "spid") ?? "", qty,
                        Str(l, "dw"), Str(l, "pihao"), Str(l, "sxrq"), Str(l, "TraceCode"),
                        eventKindOf(qty), l.GetRawText()));
                }
            result.Add(new SourceDocumentDto(docKey, docType, businessUtc,
                rq is null && ontime is null ? null : $"{rq} {ontime}".Trim(), "Asia/Shanghai",
                header.GetRawText(), docLines, docIssues));
        }
        foreach (var (orphanKey, issues) in orphans)
            result.Add(new SourceDocumentDto(orphanKey, docType, null, null, "Asia/Shanghai", "{}",
                Array.Empty<SourceLineDto>(), issues));
        if (missingKeyIssues.Count > 0)
            result.Add(new SourceDocumentDto("(missing-doc-key)", docType, null, null, "Asia/Shanghai", "{}",
                Array.Empty<SourceLineDto>(), missingKeyIssues));
        return result;
    }

    private static string? Str(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
