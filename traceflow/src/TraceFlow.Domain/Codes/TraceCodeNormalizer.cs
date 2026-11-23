namespace TraceFlow.Domain.Codes;

/// <summary>
/// SRC-04：raw 永不改写；规范化仅移除首尾空白与已验证扫码控制字符（C0 控制符/DEL/GS）。
/// 不截断为 20 位、不转数值；内部控制符属未验证情形→Unsupported 异常保存。
/// 码制支持范围以 G-TOP 合同验证为准，当前仅 8 开头 20 位数字。
/// </summary>
public static class TraceCodeNormalizer
{
    public static NormalizedCode Normalize(string? raw)
    {
        raw ??= "";
        if (raw.Length == 0) return new NormalizedCode(raw, null, CodeFormat.Empty);

        var normalized = raw.Trim().Trim(EdgeControlChars);
        if (normalized.Length == 0) return new NormalizedCode(raw, null, CodeFormat.Empty);

        var format = IsSupported(normalized) ? CodeFormat.Supported8Prefix20 : CodeFormat.Unsupported;
        return new NormalizedCode(raw, normalized, format);
    }

    private static readonly char[] EdgeControlChars =
        Enumerable.Range(0, 32).Select(i => (char)i).Append('\u007f').ToArray();

    private static bool IsSupported(string s)
        => s.Length == 20 && s[0] == '8' && s.All(char.IsAsciiDigit);
}
