using Dapper;
using Microsoft.Data.SqlClient;

namespace TraceFlow.Infrastructure.Top;

/// <summary>JOB-03：持久共享限流预算。计数落库重启不清零；限值列 NULL=本地不设帽（G-TOP 未取证不虚构限值）。</summary>
public sealed class RateBudgetService
{
    private sealed record BudgetRow(
        long Id, DateTime? NextAllowedAt,
        int DayCount, DateTime? DayWindowStart,
        int MinuteCount, DateTime? MinuteWindowStart,
        int SecondCount, DateTime? SecondWindowStart,
        int? DayLimit, int? MinuteLimit, int? SecondLimit);

    public async Task<TimeSpan?> TryAcquireAsync(SqlConnection conn, SqlTransaction tx, string env,
        IReadOnlyList<string> budgetKeys, DateTime nowUtc, CancellationToken ct)
    {
        // 单把 per-env 应用锁串行化写路径，消除缺行 RangeS-U 与 INSERT 的锁序环（1205）
        await AppLockAsync(conn, tx, "topbudget:" + env, ct);
        var rows = new List<BudgetRow>();
        foreach (var key in budgetKeys.OrderBy(k => k, StringComparer.Ordinal)) // 固定加锁顺序防死锁
        {
            var row = await SelectAsync(conn, tx, env, key, ct);
            if (row is null)
            {
                try
                {
                    await conn.ExecuteAsync(new CommandDefinition(
                        "INSERT INTO dbo.top_rate_budget(env,budget_key) VALUES(@env,@key)",
                        new { env, key }, tx, cancellationToken: ct));
                }
                catch (SqlException e) when (e.Number is 2601 or 2627) { }
                row = await SelectAsync(conn, tx, env, key, ct);
            }
            rows.Add(row!);
        }

        var wait = TimeSpan.Zero;
        var updates = new List<(BudgetRow Row, int Day, DateTime DayStart, int Minute, DateTime MinuteStart, int Second, DateTime SecondStart)>();
        foreach (var r in rows)
        {
            if (r.NextAllowedAt is DateTime na && na > nowUtc)
                wait = Max(wait, na - nowUtc);
            var (day, dayStart) = Roll(r.DayCount, r.DayWindowStart, nowUtc, TimeSpan.FromDays(1));
            var (minute, minuteStart) = Roll(r.MinuteCount, r.MinuteWindowStart, nowUtc, TimeSpan.FromMinutes(1));
            var (second, secondStart) = Roll(r.SecondCount, r.SecondWindowStart, nowUtc, TimeSpan.FromSeconds(1));
            if (r.DayLimit is int dl && day >= dl) wait = Max(wait, dayStart.AddDays(1) - nowUtc);
            if (r.MinuteLimit is int ml && minute >= ml) wait = Max(wait, minuteStart.AddMinutes(1) - nowUtc);
            if (r.SecondLimit is int sl && second >= sl) wait = Max(wait, secondStart.AddSeconds(1) - nowUtc);
            updates.Add((r, day, dayStart, minute, minuteStart, second, secondStart));
        }

        if (wait > TimeSpan.Zero) return wait; // 超限不消费任何计数（全有或全无）

        foreach (var (r, day, dayStart, minute, minuteStart, second, secondStart) in updates)
            await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.top_rate_budget
SET day_count=@day, day_window_start=@dayStart,
    minute_count=@minute, minute_window_start=@minuteStart,
    second_count=@second, second_window_start=@secondStart,
    updated_at=@now
WHERE id=@id",
                new { day = day + 1, dayStart, minute = minute + 1, minuteStart, second = second + 1, secondStart, now = nowUtc, id = r.Id },
                tx, cancellationToken: ct));
        return null;
    }

    public async Task RegisterWaitAsync(SqlConnection conn, SqlTransaction tx, string env,
        IReadOnlyList<string> budgetKeys, DateTime waitUntilUtc, CancellationToken ct)
    {
        // 单把 per-env 应用锁串行化写路径，消除缺行 RangeS-U 与 INSERT 的锁序环（1205）
        await AppLockAsync(conn, tx, "topbudget:" + env, ct);
        foreach (var key in budgetKeys)
            await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.top_rate_budget
SET next_allowed_at = CASE WHEN next_allowed_at IS NULL OR next_allowed_at < @w THEN @w ELSE next_allowed_at END,
    updated_at = SYSUTCDATETIME()
WHERE env=@env AND budget_key=@key",
                new { env, key, w = waitUntilUtc }, tx, cancellationToken: ct));
    }

    public static TimeSpan Backoff(int attemptCount, int jitterSeconds)
        => TimeSpan.FromSeconds(Math.Min(600, 30 * Math.Pow(2, Math.Min(attemptCount, 5))) + jitterSeconds);

    public static TimeSpan Backoff(int attemptCount) => Backoff(attemptCount, Random.Shared.Next(0, 6));

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private static async Task AppLockAsync(SqlConnection conn, SqlTransaction tx, string resource, CancellationToken ct)
        => await conn.ExecuteAsync(new CommandDefinition(@"
DECLARE @rc int;
EXEC @rc = sp_getapplock @Resource=@res, @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=30000;
IF @rc < 0 RAISERROR('applock %s failed (%d)', 16, 1, @res, @rc);",
            new { res = resource }, tx, cancellationToken: ct));

    private static (int Count, DateTime Start) Roll(int count, DateTime? start, DateTime now, TimeSpan period)
        => start is DateTime s && now < s + period ? (count, s) : (0, now);

    private static async Task<BudgetRow?> SelectAsync(SqlConnection conn, SqlTransaction tx, string env, string key, CancellationToken ct)
        => await conn.QuerySingleOrDefaultAsync<BudgetRow>(new CommandDefinition(@"
SELECT id AS Id, next_allowed_at AS NextAllowedAt,
       day_count AS DayCount, day_window_start AS DayWindowStart,
       minute_count AS MinuteCount, minute_window_start AS MinuteWindowStart,
       second_count AS SecondCount, second_window_start AS SecondWindowStart,
       day_limit AS DayLimit, minute_limit AS MinuteLimit, second_limit AS SecondLimit
FROM dbo.top_rate_budget WITH (UPDLOCK, HOLDLOCK)
WHERE env=@env AND budget_key=@key",
            new { env, key }, tx, cancellationToken: ct));
}
