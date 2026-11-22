namespace TraceFlow.Domain.Versioning;

/// <summary>API-01：rowversion 序列化为 16 位大写十六进制字符串（无连字符）。</summary>
public static class RowVer
{
    public static string ToHex(byte[] ver) => Convert.ToHexString(ver);

    public static byte[] FromHex(string hex)
    {
        if (string.IsNullOrEmpty(hex) || hex.Length != 16)
            throw new FormatException("rowversion 须为 16 位十六进制字符串");
        foreach (var c in hex)
            if (!Uri.IsHexDigit(c))
                throw new FormatException("rowversion 含非十六进制字符");
        return Convert.FromHexString(hex);
    }
}
