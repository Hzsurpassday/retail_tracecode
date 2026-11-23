using System.Text.RegularExpressions;

namespace TraceFlow.Domain.Sources;

/// <summary>SRC-05：效期精度 DAY/MONTH/UNKNOWN；不把月效期虚构成具体日，未验证格式不猜。</summary>
public static class ExpiryParser
{
    private static readonly Regex Day = new(@"^\d{4}[-/]\d{1,2}[-/]\d{1,2}$", RegexOptions.Compiled);
    private static readonly Regex Month = new(@"^\d{4}[-/]\d{1,2}$", RegexOptions.Compiled);

    public static string ParsePrecision(string? raw)
    {
        var s = raw?.Trim();
        if (string.IsNullOrEmpty(s)) return "UNKNOWN";
        if (Day.IsMatch(s)) return "DAY";
        if (Month.IsMatch(s)) return "MONTH";
        return "UNKNOWN";
    }
}
