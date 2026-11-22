namespace TraceFlow.Domain.Sources;

/// <summary>SYNC-02/04 默认工程参数（可调有界，设置服务校验）。非监管时限。</summary>
public sealed record SyncPolicy(int PollSeconds, int RescanHours, int DailyRescanDays, int StabilizeSeconds, int RoundIntervalSeconds)
{
    public static readonly SyncPolicy Default = new(30, 72, 30, 10, 5);
}

public static class SyncWindowPolicy
{
    /// <summary>
    /// SYNC-01/04：起点=检查点终点−回扫窗（重叠回扫采到更小单号/晚到行，AT-004）；
    /// 起点恒 ≤ 检查点终点 → 任意停机缺口全覆盖，绝不截断成 now−72h（AT-007）。
    /// 首次无检查点：now−每日回扫天数。
    /// </summary>
    public static (DateTime Start, DateTime End) Incremental(DateTime? checkpointEndUtc, DateTime nowUtc, SyncPolicy p)
        => (checkpointEndUtc.HasValue ? checkpointEndUtc.Value.AddHours(-p.RescanHours) : nowUtc.AddDays(-p.DailyRescanDays), nowUtc);

    public static (DateTime Start, DateTime End) Daily(DateTime nowUtc, SyncPolicy p)
        => (nowUtc.AddDays(-p.DailyRescanDays), nowUtc);
}
