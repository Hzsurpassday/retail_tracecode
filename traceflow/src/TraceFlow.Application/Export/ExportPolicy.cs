using System.Globalization;

namespace TraceFlow.Application.Export;

/// <summary>
/// CSV 导出统一策略（AT-074 / DEF-07）：行数上限与时间文本格式。
/// 纯函数/常量集中在应用层，端点与测试共用同一份实现（不复制公式）。
/// </summary>
public static class ExportPolicy
{
    /// <summary>单次导出最大行数（超过则 422 EXPORT_TOO_LARGE；一个字节都不写）。</summary>
    public const int MaxExportRows = 50_000;

    /// <summary>行数是否超过上限（端点先 COUNT 后写，写响应前调用）。</summary>
    public static bool ExceedsLimit(long count) => count > MaxExportRows;

    /// <summary>UTC ISO 时间文本（数据库列存储即 UTC）：<c>yyyy-MM-ddTHH:mm:ss.fffZ</c>。</summary>
    public static string UtcIso(DateTime value)
        => value.ToString("yyyy-MM-ddTHH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}