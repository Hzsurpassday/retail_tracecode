namespace TraceFlow.Domain.Matching;

/// <summary>MATCH-02：保留有意义字母、数字及版本信息；去空白/标点，大写。raw 另行保留。</summary>
public static class ApprovalNoNormalizer
{
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var kept = raw.Where(char.IsLetterOrDigit).ToArray();
        if (kept.Length == 0) return null;
        return new string(kept).ToUpperInvariant();
    }
}
