using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using TraceFlow.Application.Security;
using TraceFlow.IntegrationTests.Sources;

namespace TraceFlow.IntegrationTests.Ops;

/// <summary>
/// M4 Task 5 可观测性只读面：管道健康指标（作业状态/积压年龄/24h 失败率/尝试数/今日上传/限流预算/暂停/
/// 租约回收/对账）、作业状态列表筛选与 ROW_NUMBER 分页、派生站内告警阈值、dashboard 增量字段与匿名 401。
/// 种子直写 SQL；只读端点不改库。env 恒为宿主 env（LIVE/MOCK 各自独立）。
/// </summary>
[SupportedOSPlatform("windows")]
public class OpsStatusTests : IAsyncLifetime
{
    private static readonly DateTime T0 = new(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc);

    private const string AdminPassword = "Ops-Admin-123";

    private Sql2008Fixture _fx = null!;
    private WebApplicationFactory<Program> _factory = null!;
    private WebApplicationFactory<Program> _liveFactory = null!;
    private long _orgId;
    private long _sysId;

    public async Task InitializeAsync()
    {
        _fx = new Sql2008Fixture();
        await _fx.InitializeAsync();
        _factory = CreateFactory("Mock");
        _liveFactory = CreateFactory("Live");
        await using var c = await _fx.OpenAsync();
        (_orgId, _sysId) = await TestSeed.EnsureAsync(c);
        await SeedUserAsync(c, "ops-admin", "作业状态管理员", AdminPassword,
            new[] { PermissionCatalog.TraceRead, PermissionCatalog.AuditRead });
    }

    public async Task DisposeAsync()
    {
        _liveFactory.Dispose();
        _factory.Dispose();
        await _fx.DisposeAsync();
    }

    private WebApplicationFactory<Program> CreateFactory(string env)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:TraceFlow", _fx.ConnectionString);
            b.UseSetting("TraceFlow:Env", env);
            b.ConfigureServices(s => s.RemoveAll<IHostedService>());
        });

    // ---------------------------------------------------------------- 1. 作业状态计数/积压年龄/尝试/今日上传

    [Fact]
    public async Task Metrics_counts_jobs_by_state_and_backlog_age()
    {
        await using var c = await _fx.OpenAsync();
        var now = DateTime.UtcNow;
        await SeedJobAsync(c, "MOCK", "source-sync", "PENDING", now.AddMinutes(-30), now.AddHours(-2));
        await SeedJobAsync(c, "MOCK", "source-sync", "PENDING", now.AddMinutes(-20), now.AddHours(-1));
        await SeedJobAsync(c, "MOCK", "recon:run", "RUNNING", now.AddMinutes(-10), now);
        await SeedJobAsync(c, "MOCK", "source-sync", "DONE", now.AddMinutes(-5), now, now.AddMinutes(-1));
        await SeedJobAsync(c, "MOCK", "upstream-download", "FAILED", now.AddMinutes(-4), now, now.AddMinutes(-1),
            "平台暂时不可用");
        await SeedJobAsync(c, "MOCK", "upstream-download", "CANCELLED", now.AddMinutes(-3), now, now.AddMinutes(-1));

        // 今日上传任务（上海日界）：Succeeded/Failed/Processing 各 1；3 天前的 Succeeded 不计入。
        var todayTask = await SeedTaskAsync(c, "MOCK", "KSOA:OPS-M1:RETAIL_321", "Succeeded", now);
        await SeedTaskAsync(c, "MOCK", "KSOA:OPS-M2:RETAIL_321", "Failed", now.AddMinutes(-5), "PLATFORM_REJECTED");
        await SeedTaskAsync(c, "MOCK", "KSOA:OPS-M3:RETAIL_321", "Processing", now.AddMinutes(-6));
        await SeedTaskAsync(c, "MOCK", "KSOA:OPS-M4:RETAIL_321", "Succeeded", now.AddDays(-3));
        // 24h 内尝试：UPLOAD×2 + QUERY×1；30 小时前的 UPLOAD 不计入。
        await SeedAttemptAsync(c, todayTask, 1, "UPLOAD", now.AddHours(-3));
        await SeedAttemptAsync(c, todayTask, 2, "UPLOAD", now.AddHours(-2));
        await SeedAttemptAsync(c, todayTask, 1, "QUERY", now.AddHours(-1));
        await SeedAttemptAsync(c, todayTask, 3, "UPLOAD", now.AddHours(-30));

        var client = await LoginAsync(_factory);
        var data = (await GetJsonAsync(client, "/api/v1/ops/metrics")).GetProperty("data");

        Assert.Equal("MOCK", data.GetProperty("env").GetString());
        var jobs = data.GetProperty("jobs");
        Assert.Equal(2, jobs.GetProperty("pending").GetInt32());
        Assert.Equal(1, jobs.GetProperty("running").GetInt32());
        Assert.Equal(1, jobs.GetProperty("done").GetInt32());
        Assert.Equal(1, jobs.GetProperty("failed").GetInt32());
        Assert.Equal(1, jobs.GetProperty("cancelled").GetInt32());
        Assert.Equal(2, jobs.GetProperty("backlog").GetInt32());
        var age = jobs.GetProperty("oldestPendingAgeSeconds").GetInt32();
        Assert.InRange(age, 7000, 7400);

        var attempts = data.GetProperty("attempts24h");
        Assert.Equal(2, attempts.GetProperty("upload").GetInt32());
        Assert.Equal(1, attempts.GetProperty("query").GetInt32());

        var uploadsToday = data.GetProperty("uploadsToday");
        Assert.Equal(3, uploadsToday.GetProperty("total").GetInt32());
        Assert.Equal(1, uploadsToday.GetProperty("succeeded").GetInt32());
        Assert.Equal(1, uploadsToday.GetProperty("failed").GetInt32());
        Assert.Equal(0.5, uploadsToday.GetProperty("successRate").GetDouble(), 4);

        var perKind = data.GetProperty("perJobKind").EnumerateArray().ToList();
        var sync = perKind.Single(k => k.GetProperty("jobKind").GetString() == "source-sync");
        Assert.Equal(2, sync.GetProperty("backlog").GetInt32());
        Assert.Equal(JsonValueKind.String, sync.GetProperty("lastDoneAtUtc").ValueKind);
        var upstream = perKind.Single(k => k.GetProperty("jobKind").GetString() == "upstream-download");
        Assert.Equal(0, upstream.GetProperty("backlog").GetInt32());
        Assert.Equal(JsonValueKind.Null, upstream.GetProperty("lastDoneAtUtc").ValueKind);

        // 所有待处理作业的 next_run_at 都在未来 → 积压年龄按 0 处理（不返回负数）。
        await c.ExecuteAsync("UPDATE dbo.outbox_job SET next_run_at=DATEADD(hour,1,SYSUTCDATETIME()) WHERE job_state=N'PENDING'");
        var future = (await GetJsonAsync(client, "/api/v1/ops/metrics")).GetProperty("data");
        Assert.Equal(0, future.GetProperty("jobs").GetProperty("oldestPendingAgeSeconds").GetInt32());
    }

    // ---------------------------------------------------------------- 2. 24h 失败率（零分母）

    [Fact]
    public async Task Metrics_failure_rate_24h_handles_zero_denominator()
    {
        await using var c = await _fx.OpenAsync();
        var now = DateTime.UtcNow;
        var client = await LoginAsync(_factory);

        var empty = (await GetJsonAsync(client, "/api/v1/ops/metrics")).GetProperty("data").GetProperty("jobs");
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("failureRate24h").ValueKind);

        await SeedJobAsync(c, "MOCK", "source-sync", "DONE", now.AddHours(-5), now, now.AddHours(-1));
        await SeedJobAsync(c, "MOCK", "source-sync", "FAILED", now.AddHours(-5), now, now.AddHours(-1), "e1");
        await SeedJobAsync(c, "MOCK", "source-sync", "FAILED", now.AddHours(-6), now, now.AddHours(-2), "e2");
        await SeedJobAsync(c, "MOCK", "source-sync", "FAILED", now.AddHours(-7), now, now.AddHours(-3), "e3");
        // 24 小时窗口外：DONE 与 FAILED 各 1，均不计入。
        await SeedJobAsync(c, "MOCK", "source-sync", "DONE", now.AddHours(-30), now, now.AddHours(-29));
        await SeedJobAsync(c, "MOCK", "source-sync", "FAILED", now.AddHours(-31), now, now.AddHours(-30), "old");

        var jobs = (await GetJsonAsync(client, "/api/v1/ops/metrics")).GetProperty("data").GetProperty("jobs");
        // 3 FAILED / (1 DONE + 3 FAILED) = 0.75。
        Assert.Equal(0.75, jobs.GetProperty("failureRate24h").GetDouble(), 4);
    }

    // ---------------------------------------------------------------- 3. 环境定界

    [Fact]
    public async Task Metrics_are_env_scoped()
    {
        await using var c = await _fx.OpenAsync();
        var now = DateTime.UtcNow;
        await SeedJobAsync(c, "MOCK", "source-sync", "PENDING", now.AddMinutes(-1), now.AddMinutes(-1));
        await SeedJobAsync(c, "LIVE", "source-sync", "PENDING", now.AddMinutes(-1), now.AddMinutes(-1));
        await SeedJobAsync(c, "LIVE", "source-sync", "PENDING", now.AddMinutes(-1), now.AddMinutes(-1));
        await SeedPauseAsync(c, "MOCK", "scope-mock", "MOCK 暂停", "tester", now.AddMinutes(-5));
        await SeedPauseAsync(c, "LIVE", "scope-live", "LIVE 暂停", "tester", now.AddMinutes(-4));
        await SeedRateBudgetAsync(c, "MOCK", "budget-mock", null, null, null, null);
        await SeedRateBudgetAsync(c, "LIVE", "budget-live", 1000, 60, 10, null);
        await SeedTaskAsync(c, "MOCK", "KSOA:ENV-M:RETAIL_321", "Succeeded", now);
        await SeedTaskAsync(c, "LIVE", "KSOA:ENV-L:RETAIL_321", "Succeeded", now);
        await SeedTaskAsync(c, "LIVE", "KSOA:ENV-L2:RETAIL_321", "Succeeded", now);

        var mockClient = await LoginAsync(_factory);
        var liveClient = await LoginAsync(_liveFactory);
        var mock = (await GetJsonAsync(mockClient, "/api/v1/ops/metrics")).GetProperty("data");
        var live = (await GetJsonAsync(liveClient, "/api/v1/ops/metrics")).GetProperty("data");

        Assert.Equal("MOCK", mock.GetProperty("env").GetString());
        Assert.Equal("LIVE", live.GetProperty("env").GetString());
        Assert.Equal(1, mock.GetProperty("jobs").GetProperty("pending").GetInt32());
        Assert.Equal(2, live.GetProperty("jobs").GetProperty("pending").GetInt32());
        Assert.Equal(1, mock.GetProperty("uploadsToday").GetProperty("total").GetInt32());
        Assert.Equal(2, live.GetProperty("uploadsToday").GetProperty("total").GetInt32());

        var mockBudgets = mock.GetProperty("rateBudget").EnumerateArray().ToList();
        Assert.Equal("budget-mock", Assert.Single(mockBudgets).GetProperty("budgetKey").GetString());
        var liveBudgets = live.GetProperty("rateBudget").EnumerateArray().ToList();
        Assert.Equal("budget-live", Assert.Single(liveBudgets).GetProperty("budgetKey").GetString());

        Assert.Equal("scope-mock",
            Assert.Single(mock.GetProperty("pauses").EnumerateArray().ToList()).GetProperty("scopeKey").GetString());
        Assert.Equal("scope-live",
            Assert.Single(live.GetProperty("pauses").EnumerateArray().ToList()).GetProperty("scopeKey").GetString());

        // 作业列表同样按宿主 env 定界。
        var liveJobs = (await GetJsonAsync(liveClient, "/api/v1/ops/jobs?state=PENDING")).GetProperty("data");
        Assert.Equal(2, liveJobs.GetProperty("total").GetInt32());
        Assert.All(liveJobs.GetProperty("items").EnumerateArray(),
            i => Assert.Equal("PENDING", i.GetProperty("jobState").GetString()));
    }

    // ---------------------------------------------------------------- 4. 限流预算 NULL 限值

    [Fact]
    public async Task Metrics_rate_budget_reports_null_limits_as_unset()
    {
        await using var c = await _fx.OpenAsync();
        await SeedRateBudgetAsync(c, "MOCK", "aaa", null, null, null, null, 7, 3, 1);
        await SeedRateBudgetAsync(c, "MOCK", "bbb", 1000, 60, 10, DateTime.UtcNow.AddMinutes(-5), 12, 4, 2);

        var client = await LoginAsync(_factory);
        var data = (await GetJsonAsync(client, "/api/v1/ops/metrics")).GetProperty("data");
        var budgets = data.GetProperty("rateBudget").EnumerateArray().ToList();
        Assert.Equal(2, budgets.Count);

        var unset = budgets.Single(b => b.GetProperty("budgetKey").GetString() == "aaa");
        Assert.Equal(JsonValueKind.Null, unset.GetProperty("dayLimit").ValueKind);
        Assert.Equal(JsonValueKind.Null, unset.GetProperty("minuteLimit").ValueKind);
        Assert.Equal(JsonValueKind.Null, unset.GetProperty("secondLimit").ValueKind);
        Assert.Equal(7, unset.GetProperty("dayCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, unset.GetProperty("nextAllowedAtUtc").ValueKind);

        var set = budgets.Single(b => b.GetProperty("budgetKey").GetString() == "bbb");
        Assert.Equal(1000, set.GetProperty("dayLimit").GetInt32());
        Assert.Equal(60, set.GetProperty("minuteLimit").GetInt32());
        Assert.Equal(10, set.GetProperty("secondLimit").GetInt32());
        Assert.Equal(JsonValueKind.String, set.GetProperty("nextAllowedAtUtc").ValueKind);

        // 无暂停/积压/失败/对账/等待窗口 → 告警为空数组（不造"一切正常"伪告警）。
        var alerts = (await GetJsonAsync(client, "/api/v1/ops/alerts")).GetProperty("data").GetProperty("alerts");
        Assert.Equal(0, alerts.GetArrayLength());
    }

    // ---------------------------------------------------------------- 5. 作业列表筛选与分页

    [Fact]
    public async Task Jobs_list_filters_by_state_and_kind_and_paginates()
    {
        await using var c = await _fx.OpenAsync();
        var now = DateTime.UtcNow;
        var oldest = await SeedJobAsync(c, "MOCK", "source-sync", "FAILED", T0.AddHours(1), T0, T0.AddHours(2), "e-old");
        var middle = await SeedJobAsync(c, "MOCK", "source-sync", "FAILED", T0.AddHours(2), T0, T0.AddHours(3),
            new string('X', 260));
        var newest = await SeedJobAsync(c, "MOCK", "recon:run", "PENDING", T0.AddHours(3), now);
        var sameTimeHighId = await SeedJobAsync(c, "MOCK", "recon:run", "PENDING", T0.AddHours(3), now);
        var doneId = await SeedJobAsync(c, "MOCK", "source-sync", "DONE", T0.AddHours(4), T0, T0.AddHours(5));
        await SeedJobAsync(c, "LIVE", "source-sync", "PENDING", T0.AddHours(5), now);

        var client = await LoginAsync(_factory);

        var failed = (await GetJsonAsync(client, "/api/v1/ops/jobs?state=FAILED")).GetProperty("data");
        Assert.Equal(2, failed.GetProperty("total").GetInt32());
        Assert.Equal(new[] { middle, oldest },
            failed.GetProperty("items").EnumerateArray()
                .Select(i => long.Parse(i.GetProperty("jobId").GetString()!)).ToArray());
        // lastError 截断到 200 字符；maxAttempts/createdAtUtc/finishedAtUtc 出网。
        var middleItem = failed.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("jobId").GetString() == middle.ToString());
        Assert.Equal(200, middleItem.GetProperty("lastError").GetString()!.Length);
        Assert.Equal(10, middleItem.GetProperty("maxAttempts").GetInt32());
        Assert.Equal(JsonValueKind.String, middleItem.GetProperty("createdAtUtc").ValueKind);
        Assert.Equal(JsonValueKind.String, middleItem.GetProperty("finishedAtUtc").ValueKind);

        var kindFiltered = (await GetJsonAsync(client, "/api/v1/ops/jobs?kind=recon:run")).GetProperty("data");
        Assert.Equal(2, kindFiltered.GetProperty("total").GetInt32());

        // ROW_NUMBER 分页：pageSize=2；page1 = 最新两条（T0+4，再按 id DESC 取 T0+3 的较大 id），page2 = 其余。
        var page1 = (await GetJsonAsync(client, "/api/v1/ops/jobs?page=1&pageSize=2")).GetProperty("data");
        Assert.Equal(5, page1.GetProperty("total").GetInt32()); // 仅 MOCK 5 条
        Assert.Equal(new[] { doneId, sameTimeHighId },
            page1.GetProperty("items").EnumerateArray()
                .Select(i => long.Parse(i.GetProperty("jobId").GetString()!)).ToArray());
        var page2 = (await GetJsonAsync(client, "/api/v1/ops/jobs?page=2&pageSize=2")).GetProperty("data");
        Assert.Equal(new[] { newest, middle },
            page2.GetProperty("items").EnumerateArray()
                .Select(i => long.Parse(i.GetProperty("jobId").GetString()!)).ToArray());

        // 非法 state / page → 400；pageSize 上限 100。
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/ops/jobs?state=BOGUS")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/ops/jobs?page=0")).StatusCode);
        var clamped = (await GetJsonAsync(client, "/api/v1/ops/jobs?pageSize=500")).GetProperty("data");
        Assert.Equal(100, clamped.GetProperty("pageSize").GetInt32());
    }

    // ---------------------------------------------------------------- 6. 暂停与租约回收告警

    [Fact]
    public async Task Alerts_emit_channel_paused_and_lease_recovery()
    {
        await using var c = await _fx.OpenAsync();
        var now = DateTime.UtcNow;
        await SeedPauseAsync(c, "MOCK", "upstream", "平台维护窗口", "tester", now.AddMinutes(-20));
        await SeedPauseAsync(c, "LIVE", "upstream", "LIVE 暂停不应出现在 MOCK", "tester", now.AddMinutes(-10));
        await SeedLeaseAuditAsync(c, "MOCK", now.AddMinutes(-30), "1");
        await SeedLeaseAuditAsync(c, "LIVE", now.AddMinutes(-20), "2");
        await SeedLeaseAuditAsync(c, "MOCK", now.AddHours(-25), "3"); // 窗口外

        var client = await LoginAsync(_factory);
        var alerts = (await GetJsonAsync(client, "/api/v1/ops/alerts")).GetProperty("data").GetProperty("alerts")
            .EnumerateArray().ToList();

        Assert.Equal(new[] { "CHANNEL_PAUSED", "LEASE_RECOVERED_24H" },
            alerts.Select(a => a.GetProperty("code").GetString()).ToArray());
        var paused = alerts[0];
        Assert.Equal("HIGH", paused.GetProperty("severity").GetString());
        Assert.Equal(1, paused.GetProperty("count").GetInt32());
        Assert.Equal("/ops", paused.GetProperty("deepLink").GetString());
        Assert.Contains("upstream", paused.GetProperty("detail").GetString()!, StringComparison.Ordinal);
        Assert.DoesNotContain("LIVE 暂停", paused.GetProperty("detail").GetString()!, StringComparison.Ordinal);

        var lease = alerts[1];
        Assert.Equal("HIGH", lease.GetProperty("severity").GetString());
        Assert.Equal(1, lease.GetProperty("count").GetInt32());
        Assert.Equal("/ops", lease.GetProperty("deepLink").GetString());

        // 指标口径与告警同源：24h 租约恢复 = 1（跨 env/过期不计）。
        var metrics = (await GetJsonAsync(client, "/api/v1/ops/metrics")).GetProperty("data");
        Assert.Equal(1, metrics.GetProperty("leaseRecoveries24h").GetInt32());
    }

    // ---------------------------------------------------------------- 7. 积压与失败阈值告警

    [Fact]
    public async Task Alerts_emit_backlog_and_failed_jobs_thresholds()
    {
        await using var c = await _fx.OpenAsync();
        var now = DateTime.UtcNow;
        for (var i = 0; i < 100; i++)
            await SeedJobAsync(c, "MOCK", "source-sync", "PENDING", now.AddMinutes(-i - 1), now.AddMinutes(-i - 1));
        // 窗口外失败不计入。
        await SeedJobAsync(c, "MOCK", "source-sync", "FAILED", now.AddHours(-31), now, now.AddHours(-30), "old");

        var client = await LoginAsync(_factory);
        var atThreshold = (await GetJsonAsync(client, "/api/v1/ops/alerts")).GetProperty("data")
            .GetProperty("alerts");
        Assert.Equal(0, atThreshold.GetArrayLength()); // PENDING=100 不超阈值；无 24h 失败。

        await SeedJobAsync(c, "MOCK", "source-sync", "PENDING", now.AddMinutes(-1), now.AddMinutes(-1));
        var overBacklog = (await GetJsonAsync(client, "/api/v1/ops/alerts")).GetProperty("data")
            .GetProperty("alerts").EnumerateArray().ToList();
        var backlogAlert = Assert.Single(overBacklog);
        Assert.Equal("JOB_BACKLOG_HIGH", backlogAlert.GetProperty("code").GetString());
        Assert.Equal("HIGH", backlogAlert.GetProperty("severity").GetString());
        Assert.Equal(101, backlogAlert.GetProperty("count").GetInt32());

        for (var i = 0; i < 5; i++)
            await SeedJobAsync(c, "MOCK", "source-sync", "FAILED", now.AddMinutes(-i - 1), now, now.AddMinutes(-i - 1), "e");
        var medium = (await GetJsonAsync(client, "/api/v1/ops/alerts")).GetProperty("data").GetProperty("alerts")
            .EnumerateArray().Single(a => a.GetProperty("code").GetString() == "JOBS_FAILED_24H");
        Assert.Equal("MEDIUM", medium.GetProperty("severity").GetString());
        Assert.Equal(5, medium.GetProperty("count").GetInt32());
        Assert.Equal("/ops?state=FAILED", medium.GetProperty("deepLink").GetString());

        for (var i = 5; i < 10; i++)
            await SeedJobAsync(c, "MOCK", "source-sync", "FAILED", now.AddMinutes(-i - 1), now, now.AddMinutes(-i - 1), "e");
        var high = (await GetJsonAsync(client, "/api/v1/ops/alerts")).GetProperty("data").GetProperty("alerts")
            .EnumerateArray().Single(a => a.GetProperty("code").GetString() == "JOBS_FAILED_24H");
        Assert.Equal("HIGH", high.GetProperty("severity").GetString());
        Assert.Equal(10, high.GetProperty("count").GetInt32());
    }

    // ---------------------------------------------------------------- 8. dashboard 增量字段

    [Fact]
    public async Task Dashboard_includes_queue_counts_and_keeps_existing_fields()
    {
        await using var c = await _fx.OpenAsync();
        var now = DateTime.UtcNow;
        await SeedIngestAsync(c, "DOC-OPS-1", "EMPTY_DETAIL", "接入异常", T0.AddHours(1));
        var runId = await SeedRunAsync(c, "MOCK", "COMPLETE", now.AddMinutes(-10));
        await SeedDifferenceAsync(c, "MOCK", "DOC-OPS-R", now, "COMPLETE", "OPEN", runId);
        await SeedDifferenceAsync(c, "MOCK", "DOC-OPS-R2", now, "COMPLETE", "OPEN", runId);
        await SeedTaskAsync(c, "MOCK", "KSOA:OPS-D1:RETAIL_321", "ManualAction", now, "需人工核对");
        await SeedTaskAsync(c, "MOCK", "KSOA:OPS-D2:RETAIL_321", "Succeeded", now);
        await SeedProductConflictAsync(c, "SPID-OPS", "工作台药品", "PD-OPS");
        await SeedJobAsync(c, "MOCK", "source-sync", "FAILED", now.AddMinutes(-5), now, now.AddMinutes(-1), "e");
        await SeedJobAsync(c, "MOCK", "source-sync", "PENDING", now.AddMinutes(-5), now.AddMinutes(-5));
        await SeedPauseAsync(c, "MOCK", "upstream", "维护", "tester", now.AddMinutes(-5));
        await SeedLeaseAuditAsync(c, "MOCK", now.AddMinutes(-3), "10");

        var client = await LoginAsync(_factory);
        var data = (await GetJsonAsync(client, "/api/v1/dashboard")).GetProperty("data");

        // 既有字段名/类型保持不变。
        Assert.Equal("MOCK", data.GetProperty("env").GetString());
        Assert.Equal(1, data.GetProperty("openIngestExceptions").GetInt32());
        Assert.Equal(1, data.GetProperty("pendingJobs").GetInt32());
        Assert.Equal(1, data.GetProperty("uploadTasksByState").GetProperty("ManualAction").GetInt32());
        Assert.Equal(JsonValueKind.String, data.GetProperty("lastActivityUtc").ValueKind);

        var queue = data.GetProperty("queue");
        Assert.Equal(1, queue.GetProperty("ingestOpen").GetInt32());
        Assert.Equal(2, queue.GetProperty("reconOpen").GetInt32());
        Assert.Equal(1, queue.GetProperty("taskOpen").GetInt32());
        Assert.Equal(1, queue.GetProperty("mappingOpen").GetInt32());
        Assert.Equal(2, data.GetProperty("openDifferences").GetInt32());
        Assert.Equal(1, data.GetProperty("failedJobs24h").GetInt32());
        Assert.Equal(1, data.GetProperty("activePauses").GetInt32());
        var todayUploads = data.GetProperty("todayUploads");
        Assert.Equal(2, todayUploads.GetProperty("total").GetInt32());
        Assert.Equal(1, todayUploads.GetProperty("succeeded").GetInt32());
        Assert.Equal(1, todayUploads.GetProperty("successRate").GetDouble(), 4);

        // alertsCount 与 /ops/alerts 条数一致（暂停/租约/失败/开放差异各 1 条）。
        var alerts = (await GetJsonAsync(client, "/api/v1/ops/alerts")).GetProperty("data")
            .GetProperty("alerts");
        Assert.Equal(4, alerts.GetArrayLength());
        Assert.Equal(alerts.GetArrayLength(), data.GetProperty("alertsCount").GetInt32());
        Assert.Equal(new[] { "CHANNEL_PAUSED", "LEASE_RECOVERED_24H", "JOBS_FAILED_24H", "RECON_DIFFERENCES_OPEN" },
            alerts.EnumerateArray().Select(a => a.GetProperty("code").GetString()).ToArray());
    }

    // ---------------------------------------------------------------- 9. 匿名 401

    [Fact]
    public async Task Ops_endpoints_reject_anonymous_with_401()
    {
        var anon = _factory.CreateClient();
        foreach (var path in new[]
                 {
                     "/api/v1/ops/metrics", "/api/v1/ops/jobs", "/api/v1/ops/alerts",
                     "/api/v1/jobs/1", "/api/v1/dashboard",
                 })
            Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync(path)).StatusCode);
    }

    // ---------------------------------------------------------------- 10. 对账与限流等待告警

    [Fact]
    public async Task Alerts_emit_recon_and_rate_budget_waiting()
    {
        await using var c = await _fx.OpenAsync();
        var now = DateTime.UtcNow;
        var incompleteRunId = await SeedRunAsync(c, "MOCK", "INCOMPLETE", now.AddHours(-3));
        await SeedRunAsync(c, "MOCK", "INCOMPLETE", now.AddHours(-30)); // 窗口外
        await SeedRunAsync(c, "LIVE", "INCOMPLETE", now.AddHours(-2));
        var completeRunId = await SeedRunAsync(c, "MOCK", "COMPLETE", now.AddHours(-4));
        await c.ExecuteAsync(@"
INSERT INTO dbo.reconciliation_difference(run_id,diff_type,severity,object_ref,status,found_at)
VALUES(@runId,N'SOURCE_MISSING',N'HIGH',N'DOC-A',N'OPEN',@now),
      (@runId,N'SOURCE_MISSING',N'HIGH',N'DOC-B',N'OPEN',@now),
      (@runId,N'SOURCE_MISSING',N'HIGH',N'DOC-C',N'RESOLVED',@now)",
            new { runId = completeRunId, now });
        await c.ExecuteAsync(@"
INSERT INTO dbo.reconciliation_difference(run_id,diff_type,severity,object_ref,status,found_at)
VALUES(@runId,N'SOURCE_MISSING',N'HIGH',N'DOC-L',N'OPEN',@now)",
            new { runId = incompleteRunId, now });
        await SeedRateBudgetAsync(c, "MOCK", "waiting", null, null, null, now.AddSeconds(30));

        var client = await LoginAsync(_factory);
        var alerts = (await GetJsonAsync(client, "/api/v1/ops/alerts")).GetProperty("data")
            .GetProperty("alerts").EnumerateArray().ToList();

        Assert.Equal(new[] { "RECON_INCOMPLETE_24H", "RECON_DIFFERENCES_OPEN", "RATE_BUDGET_WAITING" },
            alerts.Select(a => a.GetProperty("code").GetString()).ToArray());
        Assert.All(alerts, a => Assert.Equal("MEDIUM", a.GetProperty("severity").GetString()));
        // RECON_INCOMPLETE_24H：仅 24h 内 MOCK 的未完成运行（1）；RECON_DIFFERENCES_OPEN：全量 MOCK OPEN 差异（3）。
        Assert.Equal(1, alerts[0].GetProperty("count").GetInt32());
        Assert.Equal(3, alerts[1].GetProperty("count").GetInt32());
        Assert.Equal(1, alerts[2].GetProperty("count").GetInt32());
        Assert.Equal("/exceptions?category=RECON", alerts[0].GetProperty("deepLink").GetString());
        Assert.Equal("/exceptions?category=RECON", alerts[1].GetProperty("deepLink").GetString());

        var metrics = (await GetJsonAsync(client, "/api/v1/ops/metrics")).GetProperty("data").GetProperty("recon");
        Assert.Equal(1, metrics.GetProperty("incomplete24h").GetInt32());
        Assert.Equal(3, metrics.GetProperty("openDifferences").GetInt32());
    }

    // ---------------------------------------------------------------- 11. UTC 时间带 Z

    /// <summary>
    /// `datetime2` 读出为 Unspecified，投影必须显式标 UTC，否则 JSON 不带 `Z`（契约含糊）。
    /// 覆盖：/ops/jobs 列表项、/jobs/{id} 详情、/ops/metrics 的 budgets/pauses/按类型时间字段。
    /// </summary>
    [Fact]
    public async Task Ops_timestamps_serialize_with_utc_z()
    {
        await using var c = await _fx.OpenAsync();
        var createdAt = T0.AddHours(6);
        var finishedAt = T0.AddHours(7);
        var jobId = await SeedJobAsync(c, "MOCK", "source-sync", "DONE", createdAt, T0.AddHours(8), finishedAt);
        await SeedPauseAsync(c, "MOCK", "scope-utc", "UTC 暂停", "tester", T0.AddHours(9));
        await SeedRateBudgetAsync(c, "MOCK", "utc-budget", null, null, null, T0.AddHours(10));

        var client = await LoginAsync(_factory);

        // 列表项：一律 Z 结尾；本用例作业的具体值与种子的 UTC 时刻一致（解析不偏移）。
        var items = (await GetJsonAsync(client, "/api/v1/ops/jobs?state=DONE&pageSize=100")).GetProperty("data")
            .GetProperty("items").EnumerateArray().ToList();
        Assert.All(items, i =>
        {
            Assert.EndsWith("Z", i.GetProperty("nextRunAtUtc").GetString()!);
            Assert.EndsWith("Z", i.GetProperty("createdAtUtc").GetString()!);
        });
        var item = items.Single(i => i.GetProperty("jobId").GetString() == jobId.ToString());
        Assert.Equal(createdAt, ParseUtc(item.GetProperty("createdAtUtc")));
        Assert.Equal(finishedAt, ParseUtc(item.GetProperty("finishedAtUtc")));

        // 详情。
        var detail = (await GetJsonAsync(client, "/api/v1/jobs/" + jobId)).GetProperty("data");
        Assert.EndsWith("Z", detail.GetProperty("nextRunAtUtc").GetString()!);
        Assert.EndsWith("Z", detail.GetProperty("createdAtUtc").GetString()!);
        Assert.EndsWith("Z", detail.GetProperty("finishedAtUtc").GetString()!);
        Assert.Equal(createdAt, ParseUtc(detail.GetProperty("createdAtUtc")));
        Assert.Equal(finishedAt, ParseUtc(detail.GetProperty("finishedAtUtc")));

        // 指标：限流预算 nextAllowedAtUtc、暂停 pausedAtUtc、按类型 lastDoneAtUtc。
        var metrics = (await GetJsonAsync(client, "/api/v1/ops/metrics")).GetProperty("data");
        var budget = metrics.GetProperty("rateBudget").EnumerateArray()
            .Single(b => b.GetProperty("budgetKey").GetString() == "utc-budget");
        Assert.EndsWith("Z", budget.GetProperty("nextAllowedAtUtc").GetString()!);
        Assert.Equal(T0.AddHours(10), ParseUtc(budget.GetProperty("nextAllowedAtUtc")));
        var pause = metrics.GetProperty("pauses").EnumerateArray()
            .Single(p => p.GetProperty("scopeKey").GetString() == "scope-utc");
        Assert.EndsWith("Z", pause.GetProperty("pausedAtUtc").GetString()!);
        Assert.Equal(T0.AddHours(9), ParseUtc(pause.GetProperty("pausedAtUtc")));
        var kind = metrics.GetProperty("perJobKind").EnumerateArray()
            .Single(k => k.GetProperty("jobKind").GetString() == "source-sync");
        Assert.EndsWith("Z", kind.GetProperty("lastDoneAtUtc").GetString()!);
    }

    /// <summary>带 `Z` 的时间串解析回来必须是 UTC（不带偏移）。</summary>
    private static DateTime ParseUtc(JsonElement element)
        => DateTimeOffset.Parse(element.GetString()!, CultureInfo.InvariantCulture).UtcDateTime;

    // ---------------------------------------------------------------- 种子

    private static string Sha(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static async Task SeedUserAsync(SqlConnection c, string username, string display, string password,
        IEnumerable<string> permissions)
    {
        var hasher = new PasswordHasher<string>();
        var id = await c.ExecuteScalarAsync<long>(@"
IF NOT EXISTS (SELECT 1 FROM dbo.app_user WHERE username=@username)
    INSERT INTO dbo.app_user(username,display_name,password_hash) VALUES(@username,@display,@h);
SELECT id FROM dbo.app_user WHERE username=@username",
            new { username, display, h = hasher.HashPassword(username, password) });
        foreach (var p in permissions)
            await c.ExecuteAsync(@"
IF NOT EXISTS (SELECT 1 FROM dbo.user_permission WHERE user_id=@id AND permission=@p)
    INSERT INTO dbo.user_permission(user_id,permission,granted_by) VALUES(@id,@p,N'test')",
                new { id, p });
    }

    private async Task<long> SeedJobAsync(SqlConnection c, string env, string jobKind, string state,
        DateTime createdAt, DateTime nextRunAt, DateTime? finishedAt = null, string? lastError = null)
        => await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.outbox_job(org_id,env,job_kind,intent_key,job_state,next_run_at,created_at,finished_at,last_error,max_attempts)
OUTPUT inserted.id
VALUES(@orgId,@env,@jobKind,@intent,@state,@nextRunAt,@createdAt,@finishedAt,@lastError,10)",
            new
            {
                orgId = _orgId, env, jobKind, intent = "ops-" + Guid.NewGuid().ToString("N"), state,
                nextRunAt, createdAt, finishedAt, lastError,
            });

    private async Task<long> SeedTaskAsync(SqlConnection c, string env, string eventKey, string state,
        DateTime createdAt, string? stateReason = null)
        => await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upload_task(org_id,env,event_key,task_type,source_hash,payload_hash,state,state_reason,created_at)
OUTPUT inserted.id
VALUES(@orgId,@env,@eventKey,N'RETAIL_321',@hash,@hash,@state,@stateReason,@createdAt)",
            new { orgId = _orgId, env, eventKey, hash = Sha(eventKey), state, stateReason, createdAt });

    private static async Task SeedAttemptAsync(SqlConnection c, long taskId, int attemptNo, string kind,
        DateTime startedAt)
        => await c.ExecuteAsync(@"
INSERT INTO dbo.upload_attempt(task_id,attempt_no,attempt_kind,env,fencing_token,started_at,finished_at,http_status)
VALUES(@taskId,@attemptNo,@kind,N'MOCK',1,@startedAt,@startedAt,200)",
            new { taskId, attemptNo, kind, startedAt });

    private static async Task SeedPauseAsync(SqlConnection c, string env, string scope, string reason, string by,
        DateTime pausedAt)
        => await c.ExecuteAsync(@"
INSERT INTO dbo.top_channel_pause(env,scope_key,reason,is_active,paused_at,paused_by)
VALUES(@env,@scope,@reason,1,@pausedAt,@by)", new { env, scope, reason, pausedAt, by });

    private static async Task SeedRateBudgetAsync(SqlConnection c, string env, string key, int? dayLimit,
        int? minuteLimit, int? secondLimit, DateTime? nextAllowedAt, int dayCount = 0, int minuteCount = 0,
        int secondCount = 0)
        => await c.ExecuteAsync(@"
INSERT INTO dbo.top_rate_budget(env,budget_key,day_count,day_limit,minute_count,minute_limit,
    second_count,second_limit,next_allowed_at)
VALUES(@env,@key,@dayCount,@dayLimit,@minuteCount,@minuteLimit,@secondCount,@secondLimit,@nextAllowedAt)",
            new { env, key, dayCount, dayLimit, minuteCount, minuteLimit, secondCount, secondLimit, nextAllowedAt });

    private static async Task SeedLeaseAuditAsync(SqlConnection c, string env, DateTime occurredAt, string objectId)
        => await c.ExecuteAsync(@"
INSERT INTO dbo.audit_log(occurred_at,actor,actor_kind,action,object_type,object_id,env)
VALUES(@occurredAt,N'SYSTEM',N'Worker',N'job.lease.recovered',N'outbox_job',@objectId,@env)",
            new { occurredAt, objectId, env });

    private async Task<long> SeedIngestAsync(SqlConnection c, string documentKey, string exceptionType,
        string? detail, DateTime createdAt)
        => await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.ingest_exception(org_id,source_system_id,source_document_key,source_line_key,exception_type,
    detail,status,created_at)
OUTPUT inserted.id
VALUES(@orgId,@sysId,@documentKey,N'L1',@exceptionType,@detail,N'OPEN',@createdAt)",
            new { orgId = _orgId, sysId = _sysId, documentKey, exceptionType, detail, createdAt });

    private async Task<long> SeedRunAsync(SqlConnection c, string env, string status, DateTime startedAt)
        => await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.reconciliation_run(org_id,env,window_start,window_end,business_type,status,started_at)
OUTPUT inserted.id
VALUES(@orgId,@env,@start,@end,N'SALE',@status,@startedAt)",
            new { orgId = _orgId, env, start = T0, end = T0.AddDays(1), status, startedAt });

    /// <summary>独立运行的 COMPLETE/INCOMPLETE 运行 + 一条指定状态差异（便于对账 open 计数）。</summary>
    private async Task<long> SeedDifferenceAsync(SqlConnection c, string env, string objectRef, DateTime foundAt,
        string runStatus, string diffStatus = "OPEN", long? runId = null)
    {
        var id = runId ?? await SeedRunAsync(c, env, runStatus, foundAt);
        return await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.reconciliation_difference(run_id,diff_type,severity,object_ref,status,found_at)
OUTPUT inserted.id
VALUES(@id,N'SOURCE_MISSING',N'HIGH',@objectRef,@diffStatus,@foundAt)",
            new { id, objectRef, diffStatus, foundAt });
    }

    private async Task<long> SeedProductConflictAsync(SqlConnection c, string spid, string name, string drugKey)
    {
        var productId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.product(org_id,source_system_id,spid,name,updated_at)
OUTPUT inserted.id
VALUES(@orgId,@sysId,@spid,@name,SYSUTCDATETIME())",
            new { orgId = _orgId, sysId = _sysId, spid, name });
        var drugId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.platform_drug(platform_drug_id,source_kind)
OUTPUT inserted.id
VALUES(@drugKey,N'API')", new { drugKey });
        return await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.product_mapping(org_id,product_id,platform_drug_id,state,evidence_json,version)
OUTPUT inserted.id
VALUES(@orgId,@productId,@drugId,N'CONFLICT',N'{""reason"":""NAME_MISMATCH""}',1)",
            new { orgId = _orgId, productId, drugId });
    }

    // ---------------------------------------------------------------- HTTP 辅助

    private sealed record SessionResp(SessionData Data);
    private sealed record SessionData(bool Authenticated, string CsrfToken);

    private static async Task<HttpClient> LoginAsync(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        var session = await client.GetFromJsonAsync<SessionResp>("/api/v1/auth/session");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session!.Data.CsrfToken);
        (await client.PostAsJsonAsync("/api/v1/auth/login",
            new { username = "ops-admin", password = AdminPassword })).EnsureSuccessStatusCode();
        var session2 = await client.GetFromJsonAsync<SessionResp>("/api/v1/auth/session");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session2!.Data.CsrfToken);
        return client;
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string path)
    {
        using var stream = await client.GetStreamAsync(path);
        using var doc = await JsonDocument.ParseAsync(stream);
        return doc.RootElement.Clone();
    }
}