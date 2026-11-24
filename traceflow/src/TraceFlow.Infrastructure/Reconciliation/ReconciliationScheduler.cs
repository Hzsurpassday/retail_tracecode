using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;

namespace TraceFlow.Infrastructure.Reconciliation;

/// <summary>
/// 日对账调度（REC-01）：Worker 恢复块每轮调用一次（幂等，重复调用无副作用）。
/// 上海时区 02:00 之后为「昨日」整日窗（本地 00:00 ~ 次日 00:00，转 UTC）各入队 SALE/RETURN 一次；
/// 随后按 intent 键回扫最近有界天数，把停机/崩溃漏掉的日窗补入队（同日只补一次，幂等）。
/// 补跑边界：从该 org/env/job-kind 最早作业活动（created_at 转上海本地日）与「昨日往前 6 天」中
/// 较晚者起，逐日补到昨日，最多 7 个日窗；无任何作业记录（全新安装）不补跑。
/// 意图键与人工触发同口径（<c>recon:run:{yyyyMMddHHmm}:{type}</c>），由 outbox UQ(org,env,kind,intent) 天然去重。
/// 开关 <c>recon.autoDaily</c>：缺行视为 true，显式 false 才停用。
/// </summary>
public sealed class ReconciliationScheduler
{
    public const string AutoDailySettingKey = "recon.autoDaily";

    /// <summary>补跑上限：含昨日在内最多 7 个日窗（再早的缺口需人工触发）。</summary>
    public const int MaxBackfillDays = 7;

    private static readonly TimeZoneInfo Shanghai = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");

    private readonly string _cs;
    private readonly IJobQueue _queue;
    private readonly IAuditLog _audit;
    private readonly Func<DateTimeOffset> _clock;

    public ReconciliationScheduler(string connectionString, IJobQueue queue, IAuditLog audit,
        Func<DateTimeOffset>? clock = null)
    {
        _cs = connectionString;
        _queue = queue;
        _audit = audit;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// 返回本次真正新建的作业数（昨日窗 0–2 个 + 补齐的缺失日窗各 0–2 个，均为 SALE/RETURN）；
    /// 已存在同意图作业时不重复计数、不重复写审计。
    /// </summary>
    public async Task<int> TryEnqueueDailyAsync(string env, CancellationToken ct)
    {
        var scope = (env ?? "").Trim().ToUpperInvariant();
        if (scope is not ("MOCK" or "LIVE")) return 0;

        var nowLocal = TimeZoneInfo.ConvertTime(_clock(), Shanghai);
        if (nowLocal.Hour < 2) return 0;
        var yesterdayLocal = nowLocal.Date.AddDays(-1);

        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        if (!await IsAutoDailyEnabledAsync(conn, ct)) return 0;
        var orgId = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT id FROM dbo.organization WHERE is_active=1", cancellationToken: ct));
        if (orgId is null) return 0;

        var created = 0;
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        // 补跑边界：最早作业活动（UTC created_at）→ 上海本地日；无记录（全新安装）不补跑。
        var earliestUtc = await conn.ExecuteScalarAsync<DateTime?>(new CommandDefinition(@"
SELECT MIN(created_at) FROM dbo.outbox_job
WHERE org_id=@orgId AND env=@env AND job_kind=@jobKind",
            new { orgId = orgId.Value, env = scope, jobKind = ReconciliationService.JobKind }, tx,
            cancellationToken: ct));
        DateTime? earliestDay = earliestUtc is DateTime utc
            ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Shanghai).Date
            : null;

        // 1) 昨日窗（行为与既有实现一致）。
        created += await EnqueueDayAsync(conn, tx, orgId.Value, scope, yesterdayLocal, backfill: false, ct);

        // 2) 有界补跑：最早活动日与「昨日往前 6 天」取较晚者，逐日补到昨日（存在同意图则跳过）。
        if (earliestDay is DateTime earliest)
        {
            var backfillFrom = yesterdayLocal.AddDays(-(MaxBackfillDays - 1));
            var startDay = earliest > backfillFrom ? earliest : backfillFrom;
            for (var day = startDay; day < yesterdayLocal; day = day.AddDays(1))
                created += await EnqueueDayAsync(conn, tx, orgId.Value, scope, day, backfill: true, ct);
        }

        await tx.CommitAsync(ct);
        return created;
    }

    /// <summary>单个本地日窗（00:00 ~ 次日 00:00 上海）的类型化入队 + 审计；返回新建作业数（0–2）。</summary>
    private async Task<int> EnqueueDayAsync(SqlConnection conn, SqlTransaction tx, long orgId, string scope,
        DateTime windowStartLocal, bool backfill, CancellationToken ct)
    {
        var startUtc = TimeZoneInfo.ConvertTimeToUtc(windowStartLocal, Shanghai);
        var endUtc = TimeZoneInfo.ConvertTimeToUtc(windowStartLocal.AddDays(1), Shanghai);

        var created = 0;
        foreach (var businessType in new[] { ReconciliationService.BusinessSale, ReconciliationService.BusinessReturn })
        {
            var intent = ReconciliationService.IntentPrefix
                + startUtc.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture) + ":" + businessType;
            var existing = await conn.ExecuteScalarAsync<int>(new CommandDefinition(@"
SELECT COUNT(*) FROM dbo.outbox_job
WHERE org_id=@orgId AND env=@env AND job_kind=@jobKind AND intent_key=@intent",
                new { orgId, env = scope, jobKind = ReconciliationService.JobKind, intent }, tx,
                cancellationToken: ct));
            if (existing > 0) continue;

            var payload = JsonSerializer.Serialize(new
            {
                windowStart = startUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                windowEnd = endUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                businessType,
                env = scope,
            });
            var jobId = await _queue.EnqueueAsync(conn, tx, orgId, scope, ReconciliationService.JobKind,
                intent, payload, null, ct);
            // 自动作业审计 env 用作业 env（控制面人工触发才恒 LIVE）。
            var message = backfill
                ? $"日对账补跑入队 类型 {businessType} 窗口 {windowStartLocal:yyyy-MM-dd}（Asia/Shanghai）"
                : $"日对账入队 类型 {businessType} 窗口 {windowStartLocal:yyyy-MM-dd}（Asia/Shanghai）";
            await _audit.AppendAsync(conn, tx, new AuditEntry("SYSTEM", "Worker", "recon.auto_triggered",
                "outbox_job", jobId.ToString(CultureInfo.InvariantCulture), message, null, payload, null, null, scope));
            created++;
        }
        return created;
    }

    private static async Task<bool> IsAutoDailyEnabledAsync(SqlConnection conn, CancellationToken ct)
    {
        var raw = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT value_json FROM dbo.app_setting WHERE setting_key=@k",
            new { k = AutoDailySettingKey }, cancellationToken: ct));
        // 缺行 → 默认开启；只有显式 false 才停用（非布尔文本按开启处理，调度本身不写业务数据）。
        if (raw is null) return true;
        return !bool.TryParse(raw.Trim().Trim('"'), out var enabled) || enabled;
    }
}