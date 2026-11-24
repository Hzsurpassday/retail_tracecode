using System.Globalization;
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Security;
using TraceFlow.Host.Security;

namespace TraceFlow.Host.Endpoints;

/// <summary>
/// 可观测性只读面（NFR-06、UI-01、JOB-03 收尾）：管道健康指标、作业状态列表与派生站内告警。
/// 全部 GET、权限 <c>trace:read</c>、匿名 401；env 恒为宿主 env（不接受跨 env 查询，与 ops 页一致）。
/// 只读：不写库、不发外呼、不落临时文件；原始平台响应/密钥一律不入响应。
/// 方言：SQL Server 2008 R2（无跳行取行子句；ROW_NUMBER 区间分页）；Dapper 参数化。
/// 时间：datetime2 以 UTC 存；"今日"按 Asia/Shanghai 当日 00:00 换算为 UTC 参数传入（Windows 时区 ID）。
/// </summary>
public static class OpsStatusEndpoints
{
    // 告警阈值常量（brief 指定，不接受配置漂移）。
    internal const int BacklogThreshold = 100;
    internal const int FailedJobs24hWarn = 1;
    internal const int FailedJobs24hHigh = 10;
    internal const int LeaseRecoveryWarn = 1;
    internal const int IncompleteReconWarn = 1;

    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 100;
    private const int LastErrorMaxChars = 200;
    private const int FailureRateDigits = 4;

    private static readonly string[] JobStates = { "PENDING", "RUNNING", "DONE", "FAILED", "CANCELLED" };

    /// <summary>部署目标 Windows（ADR-0001）：用 Windows 时区 ID 做上海日界换算。</summary>
    private static readonly TimeZoneInfo Shanghai = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");

    public static void MapOpsStatusEndpoints(this WebApplication app, string connectionString, string env)
    {
        // ---------------------------------------------------------------- 1. 管道健康指标
        app.MapGet("/api/v1/ops/metrics", async (HttpContext ctx) =>
        {
            var ct = ctx.RequestAborted;
            var now = DateTime.UtcNow;
            var since = now.AddHours(-24);
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ct);
            var orgId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT id FROM dbo.organization WHERE is_active=1", cancellationToken: ct));

            var stateRows = (await conn.QueryAsync<StateCountRow>(new CommandDefinition(
                "SELECT job_state AS JobState, COUNT(*) AS N FROM dbo.outbox_job WHERE env=@env GROUP BY job_state",
                new { env }, cancellationToken: ct))).ToList();
            var pending = CountOf(stateRows, "PENDING");
            var running = CountOf(stateRows, "RUNNING");
            var done = CountOf(stateRows, "DONE");
            var failed = CountOf(stateRows, "FAILED");
            var cancelled = CountOf(stateRows, "CANCELLED");

            // 最老待处理作业：PENDING 的最小 next_run_at；未来时间（尚未到点）按 0 处理。
            var oldestPending = await conn.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
                "SELECT MIN(next_run_at) FROM dbo.outbox_job WHERE env=@env AND job_state=N'PENDING'",
                new { env }, cancellationToken: ct));
            var oldestPendingAgeSeconds = 0;
            if (oldestPending is DateTime oldest)
            {
                var age = (now - AsUtc(oldest)).TotalSeconds;
                oldestPendingAgeSeconds = age <= 0 ? 0 : (int)Math.Floor(age);
            }

            var failure = await conn.QuerySingleAsync<FailureRateRow>(new CommandDefinition(@"
SELECT ISNULL(SUM(CASE WHEN job_state=N'DONE' THEN 1 ELSE 0 END),0) AS DoneCount,
       ISNULL(SUM(CASE WHEN job_state=N'FAILED' THEN 1 ELSE 0 END),0) AS FailedCount
FROM dbo.outbox_job
WHERE env=@env AND job_state IN (N'DONE',N'FAILED') AND finished_at>=@since",
                new { env, since }, cancellationToken: ct));
            var failureDenominator = failure.DoneCount + failure.FailedCount;
            double? failureRate24h = failureDenominator == 0
                ? null
                : Math.Round((double)failure.FailedCount / failureDenominator, FailureRateDigits);

            var attemptRows = (await conn.QueryAsync<StateCountRow>(new CommandDefinition(@"
SELECT attempt_kind AS JobState, COUNT(*) AS N
FROM dbo.upload_attempt
WHERE env=@env AND started_at>=@since
GROUP BY attempt_kind", new { env, since }, cancellationToken: ct))).ToList();
            var uploadAttempts = CountOf(attemptRows, "UPLOAD");
            var queryAttempts = CountOf(attemptRows, "QUERY");

            var today = await LoadUploadsTodayAsync(conn, env, now, ct);

            var perJobKind = (await conn.QueryAsync<PerJobKindRow>(new CommandDefinition(@"
SELECT job_kind AS JobKind,
       ISNULL(SUM(CASE WHEN job_state=N'PENDING' THEN 1 ELSE 0 END),0) AS Backlog,
       MAX(CASE WHEN job_state=N'DONE' THEN finished_at ELSE NULL END) AS LastDoneAt
FROM dbo.outbox_job
WHERE env=@env
GROUP BY job_kind
ORDER BY job_kind", new { env }, cancellationToken: ct))).ToList();

            // 限流预算原样回显：limit 为 NULL 表示本地未设帽（前端显示"未取证"），不计算/不虚构限值。
            var rateBudget = (await conn.QueryAsync<RateBudgetRow>(new CommandDefinition(@"
SELECT budget_key AS BudgetKey, day_count AS DayCount, day_limit AS DayLimit,
       minute_count AS MinuteCount, minute_limit AS MinuteLimit,
       second_count AS SecondCount, second_limit AS SecondLimit,
       next_allowed_at AS NextAllowedAt
FROM dbo.top_rate_budget
WHERE env=@env
ORDER BY budget_key", new { env }, cancellationToken: ct))).ToList();

            var pauses = (await conn.QueryAsync<PauseRow>(new CommandDefinition(@"
SELECT scope_key AS ScopeKey, reason AS Reason, paused_at AS PausedAt, paused_by AS PausedBy
FROM dbo.top_channel_pause
WHERE env=@env AND is_active=1
ORDER BY scope_key", new { env }, cancellationToken: ct))).ToList();

            // audit_log 有 env 列：租约回收审计按作业 env 落库（JobQueue.cs 传 j.Env），故按宿主 env 过滤。
            var leaseRecoveries = await conn.ExecuteScalarAsync<int>(new CommandDefinition(@"
SELECT COUNT(*) FROM dbo.audit_log
WHERE action=N'job.lease.recovered' AND env=@env AND occurred_at>=@since",
                new { env, since }, cancellationToken: ct));

            var recon = await LoadReconCountsAsync(conn, orgId, env, since, ct);

            return Results.Ok(new
            {
                data = new
                {
                    env,
                    jobs = new
                    {
                        pending, running, done, failed, cancelled,
                        backlog = pending,
                        oldestPendingAgeSeconds,
                        failureRate24h,
                    },
                    attempts24h = new { upload = uploadAttempts, query = queryAttempts },
                    uploadsToday = new
                    {
                        total = today.Total,
                        succeeded = today.Succeeded,
                        failed = today.Failed,
                        successRate = Ratio(today.Succeeded, today.Succeeded + today.Failed),
                    },
                    perJobKind = perJobKind.Select(r => new
                    {
                        r.JobKind, r.Backlog, lastDoneAtUtc = UtcOrNull(r.LastDoneAt),
                    }),
                    rateBudget = rateBudget.Select(r => new
                    {
                        r.BudgetKey, r.DayCount, r.DayLimit, r.MinuteCount, r.MinuteLimit,
                        r.SecondCount, r.SecondLimit, nextAllowedAtUtc = UtcOrNull(r.NextAllowedAt),
                    }),
                    pauses = pauses.Select(p => new
                    {
                        p.ScopeKey, p.Reason, pausedAtUtc = Utc(p.PausedAt), p.PausedBy,
                    }),
                    leaseRecoveries24h = leaseRecoveries,
                    recon = new { incomplete24h = recon.Incomplete24h, openDifferences = recon.OpenDifferences },
                },
                requestId = ctx.TraceIdentifier,
            });
        }).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.TraceRead));

        // ---------------------------------------------------------------- 2. 作业状态列表
        app.MapGet("/api/v1/ops/jobs", async (HttpContext ctx, string? state, string? kind, int? page, int? pageSize) =>
        {
            if (!TryPaging(page, pageSize, out var p, out var size, out var pagingError))
                return Err(ctx, 400, "VALIDATION", pagingError!);
            string? stateFilter = null;
            if (!string.IsNullOrWhiteSpace(state))
            {
                stateFilter = state.Trim().ToUpperInvariant();
                if (Array.IndexOf(JobStates, stateFilter) < 0)
                    return Err(ctx, 400, "VALIDATION", "state 只能为 PENDING/RUNNING/DONE/FAILED/CANCELLED");
            }
            var kindFilter = string.IsNullOrWhiteSpace(kind) ? null : kind.Trim();
            var ct = ctx.RequestAborted;

            var where = "WHERE env=@env";
            var args = new DynamicParameters();
            args.Add("env", env);
            if (stateFilter is not null) { where += " AND job_state=@state"; args.Add("state", stateFilter); }
            if (kindFilter is not null) { where += " AND job_kind=@kind"; args.Add("kind", kindFilter); }

            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ct);
            var total = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                $"SELECT COUNT(*) FROM dbo.outbox_job {where}", args, cancellationToken: ct));

            // 64 位算区间：page 极大时 int 乘法会溢出为负、区间错位。
            args.Add("lo", ((long)p - 1) * size + 1);
            args.Add("hi", (long)p * size);
            // 2008 R2：ROW_NUMBER 区间取行（排序 created_at DESC, id DESC）。
            var rows = (await conn.QueryAsync<JobListRow>(new CommandDefinition($@"
SELECT x.id AS JobId, x.job_kind AS JobKind, x.job_state AS JobState, x.attempt_count AS AttemptCount,
       x.max_attempts AS MaxAttempts, x.next_run_at AS NextRunAt,
       LEFT(x.last_error, {LastErrorMaxChars}) AS LastError,
       x.created_at AS CreatedAt, x.finished_at AS FinishedAt
FROM (
    SELECT id, job_kind, job_state, attempt_count, max_attempts, next_run_at, last_error, created_at, finished_at,
           ROW_NUMBER() OVER (ORDER BY created_at DESC, id DESC) AS rn
    FROM dbo.outbox_job {where}
) x
WHERE x.rn BETWEEN @lo AND @hi
ORDER BY x.rn", args, cancellationToken: ct))).ToList();

            var items = rows.Select(r => new
            {
                jobId = r.JobId.ToString(CultureInfo.InvariantCulture),
                r.JobKind,
                r.JobState,
                r.AttemptCount,
                r.MaxAttempts,
                nextRunAtUtc = Utc(r.NextRunAt),
                r.LastError,
                createdAtUtc = Utc(r.CreatedAt),
                finishedAtUtc = UtcOrNull(r.FinishedAt),
            }).ToList();
            return Results.Ok(new
            {
                data = new { items, page = p, pageSize = size, total, env },
                requestId = ctx.TraceIdentifier,
            });
        }).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.TraceRead));

        // ---------------------------------------------------------------- 3. 派生站内告警（只读）
        app.MapGet("/api/v1/ops/alerts", async (HttpContext ctx) =>
        {
            var ct = ctx.RequestAborted;
            var now = DateTime.UtcNow;
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ct);
            var orgId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT id FROM dbo.organization WHERE is_active=1", cancellationToken: ct));
            var alerts = await LoadAlertsAsync(conn, orgId, env, now, ct);
            return Results.Ok(new
            {
                data = new
                {
                    env,
                    alerts = alerts.Select(a => new
                    {
                        a.Code, a.Severity, a.Title, a.Detail, a.Count, a.DeepLink,
                    }).ToList(),
                },
                requestId = ctx.TraceIdentifier,
            });
        }).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.TraceRead));
    }

    // ---------------------------------------------------------------- 共享加载器（dashboard 复用，保证 alertsCount 与告警条数同源）

    /// <summary>站内告警派生：无告警返回空列表（不造"一切正常"伪告警）。只读，不写库。</summary>
    internal static async Task<List<AlertItem>> LoadAlertsAsync(SqlConnection conn, long orgId, string env,
        DateTime nowUtc, CancellationToken ct)
    {
        var since = nowUtc.AddHours(-24);
        var pauses = (await conn.QueryAsync<PauseRow>(new CommandDefinition(@"
SELECT scope_key AS ScopeKey, reason AS Reason, paused_at AS PausedAt, paused_by AS PausedBy
FROM dbo.top_channel_pause
WHERE env=@env AND is_active=1
ORDER BY scope_key", new { env }, cancellationToken: ct))).ToList();
        var backlog = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM dbo.outbox_job WHERE env=@env AND job_state=N'PENDING'",
            new { env }, cancellationToken: ct));
        var failed24h = await conn.ExecuteScalarAsync<int>(new CommandDefinition(@"
SELECT COUNT(*) FROM dbo.outbox_job
WHERE env=@env AND job_state=N'FAILED' AND finished_at>=@since",
            new { env, since }, cancellationToken: ct));
        var leaseRecoveries = await conn.ExecuteScalarAsync<int>(new CommandDefinition(@"
SELECT COUNT(*) FROM dbo.audit_log
WHERE action=N'job.lease.recovered' AND env=@env AND occurred_at>=@since",
            new { env, since }, cancellationToken: ct));
        var recon = await LoadReconCountsAsync(conn, orgId, env, since, ct);
        var budgetWaiting = await conn.ExecuteScalarAsync<int>(new CommandDefinition(@"
SELECT COUNT(*) FROM dbo.top_rate_budget
WHERE env=@env AND next_allowed_at IS NOT NULL AND next_allowed_at>@now",
            new { env, now = nowUtc }, cancellationToken: ct));

        var alerts = new List<AlertItem>();
        foreach (var pause in pauses)
            alerts.Add(new AlertItem("CHANNEL_PAUSED", "HIGH", "通道已暂停",
                $"{pause.ScopeKey}（{env}）自 {FmtShanghai(pause.PausedAt)} 暂停，操作人 {pause.PausedBy}：{pause.Reason}",
                1, "/ops"));
        if (leaseRecoveries >= LeaseRecoveryWarn)
            alerts.Add(new AlertItem("LEASE_RECOVERED_24H", "HIGH", "作业租约被回收（24 小时）",
                $"过去 24 小时回收 {leaseRecoveries} 个过期租约，作业已重回待处理；请检查 Worker 存活与单次作业时长。",
                leaseRecoveries, "/ops"));
        if (backlog > BacklogThreshold)
            alerts.Add(new AlertItem("JOB_BACKLOG_HIGH", "HIGH", "作业积压超阈值",
                $"待处理作业 {backlog} 个，超过阈值 {BacklogThreshold}；队列可能停滞。", backlog, "/ops"));
        if (failed24h >= FailedJobs24hWarn)
            alerts.Add(new AlertItem("JOBS_FAILED_24H", failed24h >= FailedJobs24hHigh ? "HIGH" : "MEDIUM",
                "作业失败（24 小时）",
                $"过去 24 小时失败作业 {failed24h} 个（MEDIUM≥{FailedJobs24hWarn}、HIGH≥{FailedJobs24hHigh}）。",
                failed24h, "/ops?state=FAILED"));
        if (recon.Incomplete24h >= IncompleteReconWarn)
            alerts.Add(new AlertItem("RECON_INCOMPLETE_24H", "MEDIUM", "对账未完成（24 小时）",
                $"过去 24 小时对账运行未完成 {recon.Incomplete24h} 次；请核对窗口与平台证据。",
                recon.Incomplete24h, "/exceptions?category=RECON"));
        if (recon.OpenDifferences > 0)
            alerts.Add(new AlertItem("RECON_DIFFERENCES_OPEN", "MEDIUM", "对账差异待处理",
                $"对账差异待处理 {recon.OpenDifferences} 条。", recon.OpenDifferences, "/exceptions?category=RECON"));
        if (budgetWaiting > 0)
            alerts.Add(new AlertItem("RATE_BUDGET_WAITING", "MEDIUM", "限流等待中",
                $"{budgetWaiting} 个限流预算处于等待窗口（next_allowed_at 未到），发送将延后。",
                budgetWaiting, "/ops"));
        return alerts;
    }

    /// <summary>今日（Asia/Shanghai 00:00 起）新建上传任务口径；与 dashboard todayUploads 同源。</summary>
    internal static async Task<TodayUploads> LoadUploadsTodayAsync(SqlConnection conn, string env, DateTime nowUtc,
        CancellationToken ct)
    {
        var dayStart = ShanghaiDayStartUtc(nowUtc);
        return await conn.QuerySingleAsync<TodayUploads>(new CommandDefinition(@"
SELECT COUNT(*) AS Total,
       ISNULL(SUM(CASE WHEN state=N'Succeeded' THEN 1 ELSE 0 END),0) AS Succeeded,
       ISNULL(SUM(CASE WHEN state=N'Failed' THEN 1 ELSE 0 END),0) AS Failed
FROM dbo.upload_task
WHERE env=@env AND created_at>=@dayStart",
            new { env, dayStart }, cancellationToken: ct));
    }

    /// <summary>对账指标：24 小时内未完成运行数 + 全量 OPEN 差异数（同异常队列 counts 口径）。</summary>
    internal static async Task<ReconCounts> LoadReconCountsAsync(SqlConnection conn, long orgId, string env,
        DateTime since, CancellationToken ct)
        => await conn.QuerySingleAsync<ReconCounts>(new CommandDefinition(@"
SELECT
    (SELECT COUNT(*) FROM dbo.reconciliation_run r
      WHERE r.org_id=@orgId AND r.env=@env AND r.status=N'INCOMPLETE' AND r.started_at>=@since) AS Incomplete24h,
    (SELECT COUNT(*) FROM dbo.reconciliation_difference d
      JOIN dbo.reconciliation_run r ON r.id=d.run_id
      WHERE r.org_id=@orgId AND r.env=@env AND d.status=N'OPEN') AS OpenDifferences",
            new { orgId, env, since }, cancellationToken: ct));

    /// <summary>比例：分母 0 → null（前端显示"—"）；保留 4 位小数。</summary>
    internal static double? Ratio(int numerator, int denominator)
        => denominator == 0 ? null : Math.Round((double)numerator / denominator, FailureRateDigits);

    /// <summary>上海当日 00:00 对应的 UTC 时刻（部署目标 Windows 时区 ID）。</summary>
    internal static DateTime ShanghaiDayStartUtc(DateTime nowUtc)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(AsUtc(nowUtc), Shanghai);
        var midnight = DateTime.SpecifyKind(local.Date, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(midnight, Shanghai);
    }

    // ---------------------------------------------------------------- 辅助

    private static DateTime AsUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    /// <summary>
    /// 投影用的 UTC 标注：DB <c>datetime2</c> 读出 <see cref="DateTimeKind.Unspecified"/>，
    /// 直接序列化不带 <c>Z</c>（契约含糊）。仅对响应中的 UTC 时间字段调用，不做全局转换器。
    /// </summary>
    private static DateTime Utc(DateTime value) => AsUtc(value);

    private static DateTime? UtcOrNull(DateTime? value) => value is DateTime v ? AsUtc(v) : null;

    private static int CountOf(List<StateCountRow> rows, string key)
    {
        var n = 0;
        foreach (var row in rows)
            if (string.Equals(row.JobState, key, StringComparison.Ordinal))
                n += row.N;
        return n;
    }

    private static bool TryPaging(int? page, int? pageSize, out int p, out int size, out string? error)
    {
        p = 1;
        size = DefaultPageSize;
        error = null;
        if (page is int rawPage)
        {
            if (rawPage < 1) { error = "page 必须 ≥ 1"; return false; }
            p = rawPage;
        }
        if (pageSize is int rawSize)
        {
            if (rawSize < 1) { error = "pageSize 必须 ≥ 1"; return false; }
            size = Math.Min(rawSize, MaxPageSize);
        }
        return true;
    }

    private static string FmtShanghai(DateTime value)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(AsUtc(value), Shanghai);
        return local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "（Asia/Shanghai）";
    }

    private static IResult Err(HttpContext ctx, int status, string code, string message)
        => Results.Json(new
        {
            code, message, fieldErrors = Array.Empty<object>(), retryable = false, retryAfter = (int?)null,
            requestId = ctx.TraceIdentifier,
        }, statusCode: status);

    internal sealed record AlertItem(string Code, string Severity, string Title, string Detail, int Count,
        string DeepLink);

    /// <summary>今日上传口径（Dapper 映射用可写属性，避免构造器匹配歧义）。</summary>
    internal sealed class TodayUploads
    {
        public int Total { get; set; }
        public int Succeeded { get; set; }
        public int Failed { get; set; }
    }

    /// <summary>对账口径（同上）。</summary>
    internal sealed class ReconCounts
    {
        public int Incomplete24h { get; set; }
        public int OpenDifferences { get; set; }
    }

    private sealed class StateCountRow
    {
        public string JobState { get; set; } = "";
        public int N { get; set; }
    }

    private sealed class FailureRateRow
    {
        public int DoneCount { get; set; }
        public int FailedCount { get; set; }
    }

    private sealed class PerJobKindRow
    {
        public string JobKind { get; set; } = "";
        public int Backlog { get; set; }
        public DateTime? LastDoneAt { get; set; }
    }

    private sealed class RateBudgetRow
    {
        public string BudgetKey { get; set; } = "";
        public int DayCount { get; set; }
        public int? DayLimit { get; set; }
        public int MinuteCount { get; set; }
        public int? MinuteLimit { get; set; }
        public int SecondCount { get; set; }
        public int? SecondLimit { get; set; }
        public DateTime? NextAllowedAt { get; set; }
    }

    private sealed class PauseRow
    {
        public string ScopeKey { get; set; } = "";
        public string Reason { get; set; } = "";
        public DateTime PausedAt { get; set; }
        public string PausedBy { get; set; } = "";
    }

    private sealed class JobListRow
    {
        public long JobId { get; set; }
        public string JobKind { get; set; } = "";
        public string JobState { get; set; } = "";
        public int AttemptCount { get; set; }
        public int MaxAttempts { get; set; }
        public DateTime NextRunAt { get; set; }
        public string? LastError { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? FinishedAt { get; set; }
    }
}