using System.Globalization;

namespace TraceFlow.Domain.Sources;

/// <summary>
/// DB-02：KSOA rq(日期)+ontime(时刻) 按 Asia/Shanghai 解析为 UTC。
/// 部署目标 Windows（ADR-0001），用 Windows 时区 ID "China Standard Time"。
/// 解析失败返回 null（调用方记 BAD_BUSINESS_TIME 异常，不猜时间）。
/// </summary>
public static class BusinessTimeParser
{
    public static readonly TimeZoneInfo Shanghai = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
    private static readonly string[] DateFormats = { "yyyy-MM-dd", "yyyy/M/d", "yyyy/MM/dd", "yyyy-M-d" };
    // TimeSpan 自定义格式区分大小写且冒号须转义：小时用小写 h/hh、冒号写 \:（大写 HH 与裸冒号均被 TryParseExact 拒绝）。
    private static readonly string[] TimeFormats = { @"hh\:mm\:ss", @"h\:mm\:ss", @"hh\:mm" };

    public static DateTime? ParseUtc(string? dateRaw, string? timeRaw)
    {
        if (string.IsNullOrWhiteSpace(dateRaw)) return null;
        if (!DateTime.TryParseExact(dateRaw.Trim(), DateFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date))
            return null;
        var time = TimeSpan.Zero;
        if (!string.IsNullOrWhiteSpace(timeRaw) &&
            !TimeSpan.TryParseExact(timeRaw.Trim(), TimeFormats, CultureInfo.InvariantCulture, out time))
            return null;
        var local = date.Date + time;
        // datetime2 域守卫（1753-9999）。不可用 DateTime.MinValue/MaxValue 做 ConvertTimeToUtc 边界比较——会溢出。
        if (local.Year < 1753 || local.Year > 9999) return null;
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), Shanghai);
    }
}
