namespace TraceFlow.Application.Ops;

/// <summary>
/// 百分位计算（NFR-03 性能门禁用）：nearest-rank 口径，纯函数、无 IO，
/// 便于单元测试与 OpsTool 复用。nearest-rank：第 p 百分位 = sorted[ceil(p/100*n)-1]。
/// 不使用插值（避免小样本下的"平滑"掩盖尾部慢请求）。
/// </summary>
public static class Percentile
{
    /// <summary>nearest-rank 百分位；<paramref name="percentilePercent"/> 取 (0,100]（如 95 表示 p95）。</summary>
    public static double NearestRank(IReadOnlyCollection<double> values, double percentilePercent)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
            throw new ArgumentException("样本不能为空（性能门禁不接受零样本）", nameof(values));
        if (double.IsNaN(percentilePercent) || percentilePercent <= 0d || percentilePercent > 100d)
            throw new ArgumentOutOfRangeException(nameof(percentilePercent), percentilePercent,
                "百分位须落在 (0,100] 区间");

        var sorted = new double[values.Count];
        var i = 0;
        foreach (var value in values)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new ArgumentException("样本含 NaN/Infinity", nameof(values));
            sorted[i++] = value;
        }
        Array.Sort(sorted);

        var rank = (int)Math.Ceiling(percentilePercent / 100d * sorted.Length);
        if (rank < 1) rank = 1;
        if (rank > sorted.Length) rank = sorted.Length;
        return sorted[rank - 1];
    }

    /// <summary>中位数口径（供报告 p50 展示）。</summary>
    public static double P50(IReadOnlyCollection<double> values) => NearestRank(values, 50d);

    /// <summary>p95（NFR-03 门禁判据）。</summary>
    public static double P95(IReadOnlyCollection<double> values) => NearestRank(values, 95d);
}