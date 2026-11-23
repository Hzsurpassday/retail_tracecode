namespace TraceFlow.Domain.Codes;

/// <summary>
/// M0 携带项 1：CodeFormat 枚举名（Supported8Prefix20=18 字符）超出 M0 DDL nvarchar(16)。
/// 持久层一律经此映射为短 token；0007 迁移同时把列加宽到 nvarchar(32) 双保险。
/// 禁止 enum.ToString() 直接落库。
/// </summary>
public static class CodeFormatStorage
{
    public static string ToToken(CodeFormat format) => format switch
    {
        CodeFormat.Empty => "EMPTY",
        CodeFormat.Supported8Prefix20 => "S8P20",
        CodeFormat.Unsupported => "UNSUP",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "未知 CodeFormat"),
    };

    public static CodeFormat FromToken(string token) => token switch
    {
        "EMPTY" => CodeFormat.Empty,
        "S8P20" => CodeFormat.Supported8Prefix20,
        "UNSUP" => CodeFormat.Unsupported,
        _ => throw new ArgumentOutOfRangeException(nameof(token), token, "未知 format 存储 token"),
    };
}
