using System.Net;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using TraceFlow.Application.Security;
using TraceFlow.Infrastructure.Jobs;
using TraceFlow.Infrastructure.Reconciliation;
using TraceFlow.Infrastructure.Security;
using TraceFlow.IntegrationTests.Sources;

namespace TraceFlow.IntegrationTests.Reconciliation;

/// <summary>
/// 日对账（REC-01/02/03、AT-062/063/064）：集合比对（等量不等集必须报差异）、一单多分片非差异、
/// 非终态分片 → INCOMPLETE、11 类差异的类型/严重度/责任角色、逐行缺码、上游不完整、环境隔离、
/// 差异解决留证（422/409/LIVE 审计）、每日调度幂等，以及触发 API 的 202 入队 + LIVE 审计。
/// 种子直写 SQL；平台侧证据只来自 upload_attempt/code_result（作业从不调用 TOP）。
/// </summary>
[SupportedOSPlatform("windows")]
public class ReconciliationTests : IAsyncLifetime
{
    private static readonly DateTime DayStart = new(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime DayEnd = new(2026, 11, 21, 0, 0, 0, DateTimeKind.Utc);

    private const string AdminPassword = "Recon-Admin-123";

    private Sql2008Fixture _fx = null!;
    private WebApplicationFactory<Program> _factory = null!;
    private long _orgId;
    private long _sysId;

    public async Task InitializeAsync()
    {
        _fx = new Sql2008Fixture();
        await _fx.InitializeAsync();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:TraceFlow", _fx.ConnectionString);
            b.UseSetting("TraceFlow:Env", "Mock");
            b.ConfigureServices(s => s.RemoveAll<IHostedService>());
        });
        await using var c = await _fx.OpenAsync();
        (_orgId, _sysId) = await TestSeed.EnsureAsync(c);
        await SeedUserAsync(c, "recon-admin", "对账管理员", AdminPassword, PermissionCatalog.All);
    }

    public async Task DisposeAsync()
    {
        _factory.Dispose();
        await _fx.DisposeAsync();
    }

    private ReconciliationService NewService()
        => new(_fx.ConnectionString, new AuditLog());

    // ---------------------------------------------------------------- 1. SOURCE_MISSING

    [Fact]
    public async Task Recon_sale_documents_without_verification_are_source_missing_and_run_complete_with_differences()
    {
        await using var c = await _fx.OpenAsync();
        var verified = await SeedDocumentAsync(c, "SALE-OK-1");
        var unverified = await SeedDocumentAsync(c, "SALE-NO-VERIFY-1");
        await SeedLineAsync(c, verified, "L1", "SALE", "690000000000000101");
        await SeedLineAsync(c, unverified, "L1", "SALE", "690000000000000102");
        var taskId = await SeedTaskAsync(c, "KSOA:SALE-OK-1:RETAIL_321", verified);
        await SeedVerificationAsync(c, "SALE-OK-1", verified, "PASSED", taskId);
        await SeedPartAsync(c, taskId, 0, "Succeeded");

        var result = await NewService().RunAsync("MOCK", DayStart, DayEnd, "SALE", CancellationToken.None);

        Assert.Equal("COMPLETE", result.Status);
        Assert.Equal(1, result.DifferenceCount);
        var diffs = (await c.QueryAsync<(string DiffType, string ObjectRef, string Severity, string Owner)>(
            "SELECT diff_type, object_ref, severity, owner_role FROM dbo.reconciliation_difference WHERE run_id=@id",
            new { id = result.RunId })).ToList();
        var only = Assert.Single(diffs);
        Assert.Equal("SOURCE_MISSING", only.DiffType);
        Assert.Equal("SALE-NO-VERIFY-1", only.ObjectRef);
        Assert.Equal("HIGH", only.Severity);
        Assert.Equal("operator", only.Owner);
        // run 主表与差异同事务落库：完成时间非空、汇总为 JSON。
        Assert.Equal("COMPLETE", await c.ExecuteScalarAsync<string>(
            "SELECT status FROM dbo.reconciliation_run WHERE id=@id", new { id = result.RunId }));
        Assert.NotNull(await c.ExecuteScalarAsync<string>(
            "SELECT local_summary FROM dbo.reconciliation_run WHERE id=@id", new { id = result.RunId }));
    }

    // ---------------------------------------------------------------- 1b. RETURN 不做无核验 SOURCE_MISSING

    [Fact]
    public async Task Recon_return_run_does_not_report_source_missing_for_task_anchored_documents()
    {
        await using var c = await _fx.OpenAsync();
        var doc = await SeedDocumentAsync(c, "RETURN-TASK-1");
        await SeedLineAsync(c, doc, "R1", "CONSUMER_RETURN", "690000000000000103");
        // 任务锚定快照、无当前核验：SALE 口径会报 SOURCE_MISSING；RETURN 口径（来源未全量接入 G-ERP）不报。
        await SeedTaskAsync(c, "KSOA:RETURN-TASK-1:CONSUMER_RETURN_116", doc,
            taskType: "CONSUMER_RETURN_116");

        var result = await NewService().RunAsync("MOCK", DayStart, DayEnd, "RETURN", CancellationToken.None);

        Assert.Equal("COMPLETE", result.Status);
        Assert.Equal(0, result.DifferenceCount);
        var summaryJson = await c.ExecuteScalarAsync<string>(
            "SELECT local_summary FROM dbo.reconciliation_run WHERE id=@id", new { id = result.RunId });
        Assert.NotNull(summaryJson);
        using var summary = JsonDocument.Parse(summaryJson!);
        Assert.Equal(1, summary.RootElement.GetProperty("documents").GetInt32());
        Assert.Equal(1, summary.RootElement.GetProperty("tasks").GetInt32());
    }

    // ---------------------------------------------------------------- 2. AT-062 等量不等集

    [Fact]
    public async Task Recon_equal_counts_with_different_sets_reports_difference()
    {
        await using var c = await _fx.OpenAsync();
        var okDoc = await SeedDocumentAsync(c, "AT062-OK");
        await SeedLineAsync(c, okDoc, "L1", "SALE", "690000000000000201");
        var okTask = await SeedTaskAsync(c, "KSOA:AT062-OK:RETAIL_321", okDoc);
        await SeedVerificationAsync(c, "AT062-OK", okDoc, "PASSED", okTask);
        await SeedPartAsync(c, okTask, 0, "Succeeded");

        var lostDoc = await SeedDocumentAsync(c, "AT062-LOST");
        await SeedLineAsync(c, lostDoc, "L1", "SALE", "690000000000000202");
        var lostTask = await SeedTaskAsync(c, "KSOA:AT062-LOST:RETAIL_321", lostDoc, state: "Failed");
        await SeedVerificationAsync(c, "AT062-LOST", lostDoc, "PASSED", lostTask);
        var lostPart = await SeedPartAsync(c, lostTask, 0, "Failed");
        await SeedQueryAttemptAsync(c, lostTask, lostPart, 1, 200, "query-req-062");

        var result = await NewService().RunAsync("MOCK", DayStart, DayEnd, "SALE", CancellationToken.None);

        // 本地 2 单 / 平台 1 单成功：数量口径（2 任务）相同也不能宣称"无差异"。
        Assert.Equal("COMPLETE", result.Status);
        Assert.Equal(1, result.DifferenceCount);
        var diff = await c.QuerySingleAsync<(string DiffType, string Severity, string Owner, string Evidence)>(
            "SELECT diff_type, severity, owner_role, evidence_json FROM dbo.reconciliation_difference WHERE run_id=@id",
            new { id = result.RunId });
        Assert.Equal("PLATFORM_MISSING", diff.DiffType);
        Assert.Equal("HIGH", diff.Severity);
        Assert.Equal("ops", diff.Owner);
        Assert.Contains("query-req-062", diff.Evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("raw_response", diff.Evidence, StringComparison.OrdinalIgnoreCase);
        var localSummary = await c.ExecuteScalarAsync<string>(
            "SELECT local_summary FROM dbo.reconciliation_run WHERE id=@id", new { id = result.RunId });
        Assert.Contains("\"tasks\":2", localSummary, StringComparison.Ordinal);
        var platformSummary = await c.ExecuteScalarAsync<string>(
            "SELECT platform_summary FROM dbo.reconciliation_run WHERE id=@id", new { id = result.RunId });
        Assert.Contains("\"failedParts\":1", platformSummary, StringComparison.Ordinal);
        Assert.Contains("\"unavailable\":false", platformSummary, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- 3. AT-063 一单三分片

    [Fact]
    public async Task Recon_one_business_split_into_three_parts_is_not_a_mismatch()
    {
        await using var c = await _fx.OpenAsync();
        var doc = await SeedDocumentAsync(c, "AT063-ONE");
        await SeedLineAsync(c, doc, "L1", "SALE", "690000000000000301");
        var task = await SeedTaskAsync(c, "KSOA:AT063-ONE:RETAIL_321", doc);
        await SeedVerificationAsync(c, "AT063-ONE", doc, "PASSED", task);
        for (var i = 0; i < 3; i++) await SeedPartAsync(c, task, i, "Succeeded");

        var result = await NewService().RunAsync("MOCK", DayStart, DayEnd, "SALE", CancellationToken.None);

        Assert.Equal("COMPLETE", result.Status);
        Assert.Equal(0, result.DifferenceCount);
        Assert.Equal(0, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.reconciliation_difference WHERE run_id=@id", new { id = result.RunId }));
        Assert.Contains("\"parts\":3", await c.ExecuteScalarAsync<string>(
            "SELECT local_summary FROM dbo.reconciliation_run WHERE id=@id", new { id = result.RunId }), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- 4. AT-064 非终态 → INCOMPLETE

    [Fact]
    public async Task Recon_non_terminal_part_makes_run_incomplete()
    {
        await using var c = await _fx.OpenAsync();
        var doc = await SeedDocumentAsync(c, "AT064-PENDING");
        await SeedLineAsync(c, doc, "L1", "SALE", "690000000000000401");
        var task = await SeedTaskAsync(c, "KSOA:AT064-PENDING:RETAIL_321", doc, state: "Dispatching");
        await SeedVerificationAsync(c, "AT064-PENDING", doc, "PASSED", task);
        await SeedPartAsync(c, task, 0, "Dispatching");

        var result = await NewService().RunAsync("MOCK", DayStart, DayEnd, "SALE", CancellationToken.None);

        Assert.Equal("INCOMPLETE", result.Status);
        var platformSummary = await c.ExecuteScalarAsync<string>(
            "SELECT platform_summary FROM dbo.reconciliation_run WHERE id=@id", new { id = result.RunId });
        Assert.Contains("\"unavailable\":true", platformSummary, StringComparison.Ordinal);
        Assert.Contains("\"unavailableReason\":\"NON_TERMINAL_PARTS\"", platformSummary, StringComparison.Ordinal);
        Assert.Equal("INCOMPLETE", await c.ExecuteScalarAsync<string>(
            "SELECT status FROM dbo.reconciliation_run WHERE id=@id", new { id = result.RunId }));
    }

    // ---------------------------------------------------------------- 5. 部分成功 / 受理未知分型

    [Fact]
    public async Task Recon_partial_success_and_acceptance_unknown_are_typed()
    {
        await using var c = await _fx.OpenAsync();
        var doc = await SeedDocumentAsync(c, "TYPED-DOC");
        await SeedLineAsync(c, doc, "L1", "SALE", "690000000000000501");
        // 与 TaskStateAggregator 一致：分片 [PartialSuccess, AcceptanceUnknown] 聚合为 AcceptanceUnknown。
        var task = await SeedTaskAsync(c, "KSOA:TYPED-DOC:RETAIL_321", doc, state: "AcceptanceUnknown");
        await SeedVerificationAsync(c, "TYPED-DOC", doc, "PASSED", task);
        await SeedPartAsync(c, task, 0, "PartialSuccess");
        await SeedPartAsync(c, task, 1, "AcceptanceUnknown");

        var result = await NewService().RunAsync("MOCK", DayStart, DayEnd, "SALE", CancellationToken.None);

        var diffs = (await c.QueryAsync<(string DiffType, string Severity, string Owner, string Evidence)>(
            "SELECT diff_type, severity, owner_role, evidence_json FROM dbo.reconciliation_difference WHERE run_id=@id",
            new { id = result.RunId })).ToList();
        // 规则逐对象登记（任务或分片各一条，evidence.level 注明层级）：部分成功 1 条、受理未知 2 条。
        var partial = Assert.Single(diffs.Where(d => d.DiffType == "PARTIAL_SUCCESS"));
        Assert.Equal("MEDIUM", partial.Severity);
        Assert.Equal("ops", partial.Owner);
        Assert.Contains("\"level\":\"part\"", partial.Evidence, StringComparison.Ordinal);
        var unknown = diffs.Where(d => d.DiffType == "ACCEPTANCE_UNKNOWN").ToList();
        Assert.Equal(2, unknown.Count);
        Assert.All(unknown, d => Assert.Equal("HIGH", d.Severity));
        Assert.All(unknown, d => Assert.Equal("ops", d.Owner));
        Assert.Contains(unknown, d => d.Evidence.Contains("\"level\":\"task\"", StringComparison.Ordinal));
        Assert.Contains(unknown, d => d.Evidence.Contains("\"level\":\"part\"", StringComparison.Ordinal));
        Assert.Equal(3, result.DifferenceCount);
        Assert.Equal("INCOMPLETE", result.Status); // AcceptanceUnknown 属非终态分片
    }

    // ---------------------------------------------------------------- 6. 逐行缺码

    [Fact]
    public async Task Recon_missing_code_line_is_reported_per_line()
    {
        await using var c = await _fx.OpenAsync();
        var doc = await SeedDocumentAsync(c, "MISSING-CODE-DOC");
        await SeedLineAsync(c, doc, "L1", "SALE", null);
        await SeedLineAsync(c, doc, "L2", "SALE", "   ");
        await SeedLineAsync(c, doc, "L3", "SALE", "690000000000000601");
        var task = await SeedTaskAsync(c, "KSOA:MISSING-CODE-DOC:RETAIL_321", doc);
        await SeedVerificationAsync(c, "MISSING-CODE-DOC", doc, "PASSED", task);
        await SeedPartAsync(c, task, 0, "Succeeded");

        var result = await NewService().RunAsync("MOCK", DayStart, DayEnd, "SALE", CancellationToken.None);

        Assert.Equal(2, result.DifferenceCount);
        var refs = (await c.QueryAsync<(string DiffType, string ObjectRef, string Severity)>(
            "SELECT diff_type, object_ref, severity FROM dbo.reconciliation_difference WHERE run_id=@id ORDER BY object_ref",
            new { id = result.RunId })).ToList();
        Assert.All(refs, r => Assert.Equal("MISSING_CODE", r.DiffType));
        Assert.All(refs, r => Assert.Equal("HIGH", r.Severity));
        Assert.Equal(new[] { "L1", "L2" }, refs.Select(r => r.ObjectRef).ToArray());
    }

    // ---------------------------------------------------------------- 7. 上游不完整

    [Fact]
    public async Task Recon_upstream_incomplete_bill_is_reported()
    {
        await using var c = await _fx.OpenAsync();
        var doc = await SeedDocumentAsync(c, "UPSTREAM-DOC");
        await SeedLineAsync(c, doc, "L1", "SALE", "690000000000000701");
        var task = await SeedTaskAsync(c, "KSOA:UPSTREAM-DOC:RETAIL_321", doc);
        await SeedVerificationAsync(c, "UPSTREAM-DOC", doc, "PASSED", task);
        await SeedPartAsync(c, task, 0, "Succeeded");
        await c.ExecuteAsync(@"
INSERT INTO dbo.upstream_bill(org_id,env,upstream_ref_ent_id,platform_bill_id,bill_no,bill_date,completeness)
VALUES(@orgId,N'MOCK',N'E1',N'PB-777',N'BILL-777',@billDate,N'INCOMPLETE')",
            new { orgId = _orgId, billDate = DayStart.AddHours(9) });

        var result = await NewService().RunAsync("MOCK", DayStart, DayEnd, "SALE", CancellationToken.None);

        Assert.Equal(1, result.DifferenceCount);
        var diff = await c.QuerySingleAsync<(string DiffType, string ObjectRef, string Severity)>(
            "SELECT diff_type, object_ref, severity FROM dbo.reconciliation_difference WHERE run_id=@id",
            new { id = result.RunId });
        Assert.Equal("UPSTREAM_INCOMPLETE", diff.DiffType);
        Assert.Equal("BILL-777", diff.ObjectRef);
        Assert.Equal("HIGH", diff.Severity);
    }

    // ---------------------------------------------------------------- 8. 环境隔离

    [Fact]
    public async Task Recon_is_env_scoped()
    {
        await using var c = await _fx.OpenAsync();
        var doc = await SeedDocumentAsync(c, "ENV-DOC");
        await SeedLineAsync(c, doc, "L1", "SALE", "690000000000000801");
        // LIVE 侧存在"平台查无"证据：MOCK 对账绝不能读到它。
        var liveTask = await SeedTaskAsync(c, "KSOA:ENV-DOC:RETAIL_321", doc, env: "LIVE", state: "Failed");
        await SeedVerificationAsync(c, "ENV-DOC-LIVE", doc, "PASSED", liveTask, env: "LIVE");
        var livePart = await SeedPartAsync(c, liveTask, 0, "Failed", env: "LIVE");
        await SeedQueryAttemptAsync(c, liveTask, livePart, 1, 200, "live-req-only", env: "LIVE");

        var mockRun = await NewService().RunAsync("MOCK", DayStart, DayEnd, "SALE", CancellationToken.None);
        var localSummaryJson = await c.ExecuteScalarAsync<string>(
            "SELECT local_summary FROM dbo.reconciliation_run WHERE id=@id", new { id = mockRun.RunId });
        Assert.NotNull(localSummaryJson);
        using (var summary = JsonDocument.Parse(localSummaryJson!))
        {
            var root = summary.RootElement;
            Assert.Equal(1, root.GetProperty("documents").GetInt32());
            Assert.Equal(1, root.GetProperty("saleLines").GetInt32());
            Assert.Equal(0, root.GetProperty("tasks").GetInt32());
            Assert.Equal(0, root.GetProperty("parts").GetInt32());
            Assert.Equal(0, root.GetProperty("codes").GetInt32());
        }
        Assert.Equal(0, await c.ExecuteScalarAsync<int>(@"
SELECT COUNT(*) FROM dbo.reconciliation_difference WHERE run_id=@id AND diff_type IN (N'PLATFORM_MISSING',N'ACCEPTANCE_UNKNOWN')",
            new { id = mockRun.RunId }));

        // GET 详情跨 env 一律 404（LIVE run 对 MOCK 宿主不可见）。
        var liveRunId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.reconciliation_run(org_id,env,window_start,window_end,business_type,status,started_at,finished_at)
VALUES(@orgId,N'LIVE',@start,@end,N'SALE',N'COMPLETE',SYSUTCDATETIME(),SYSUTCDATETIME());
SELECT CAST(SCOPE_IDENTITY() AS bigint);", new { orgId = _orgId, start = DayStart, end = DayEnd });

        var client = await LoginAsync();
        var crossEnv = await client.GetAsync($"/api/v1/reconciliation-runs/{liveRunId}");
        Assert.Equal(HttpStatusCode.NotFound, crossEnv.StatusCode);
        var own = await client.GetAsync($"/api/v1/reconciliation-runs/{mockRun.RunId}");
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        var list = await client.GetFromJsonAsync<JsonElement>("/api/v1/reconciliation-runs");
        Assert.Equal(1, list.GetProperty("data").GetProperty("total").GetInt32());
    }

    // ---------------------------------------------------------------- 9. 差异解决

    [Fact]
    public async Task Recon_difference_resolve_requires_evidence_and_is_audited_live()
    {
        await using var c = await _fx.OpenAsync();
        await SeedDocumentAsync(c, "RESOLVE-DOC");
        var run = await NewService().RunAsync("MOCK", DayStart, DayEnd, "SALE", CancellationToken.None);
        var differenceId = await c.ExecuteScalarAsync<long>(
            "SELECT id FROM dbo.reconciliation_difference WHERE run_id=@id", new { id = run.RunId });

        var client = await LoginAsync();
        var path = $"/api/v1/reconciliation-differences/{differenceId}/resolve";

        var empty = await PostJsonAsync(client, path, new { resolutionEvidence = "   " }, "resolve-empty-1");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, empty.StatusCode);
        Assert.Equal("VALIDATION", (await ReadJsonAsync(empty)).GetProperty("code").GetString());

        var ok = await PostJsonAsync(client, path, new { resolutionEvidence = "已核对平台回查证据，确认平台未受理" }, "resolve-ok-1");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("RESOLVED", (await ReadJsonAsync(ok)).GetProperty("data").GetProperty("status").GetString());
        var row = await c.QuerySingleAsync<(string Status, string ResolvedBy, string Evidence)>(
            "SELECT status, resolved_by, resolution_evidence FROM dbo.reconciliation_difference WHERE id=@id",
            new { id = differenceId });
        Assert.Equal("RESOLVED", row.Status);
        Assert.Equal("recon-admin", row.ResolvedBy);
        Assert.Contains("平台未受理", row.Evidence, StringComparison.Ordinal);
        // 人工动作审计 env 恒 LIVE。
        Assert.Equal(1, await c.ExecuteScalarAsync<int>(@"
SELECT COUNT(*) FROM dbo.audit_log
WHERE action=N'recon.difference.resolved' AND env='LIVE' AND actor=N'recon-admin'
  AND object_type=N'reconciliation_difference' AND object_id=@oid",
            new { oid = differenceId.ToString() }));

        var again = await PostJsonAsync(client, path, new { resolutionEvidence = "重复处理" }, "resolve-again-1");
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("DIFFERENCE_ALREADY_RESOLVED", (await ReadJsonAsync(again)).GetProperty("code").GetString());

        var missing = await PostJsonAsync(client, "/api/v1/reconciliation-differences/999999/resolve",
            new { resolutionEvidence = "不存在" }, "resolve-missing-1");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    // ---------------------------------------------------------------- 10. 每日调度

    [Fact]
    public async Task Recon_daily_scheduler_enqueues_once_per_day()
    {
        var audit = new AuditLog();
        var queue = new JobQueue(_fx.ConnectionString, audit);
        // 2026-11-22 03:00 Asia/Shanghai = 2026-11-21T19:00Z → 昨日窗 = 11-21 00:00~24:00（上海）。
        var evening = new DateTimeOffset(2026, 11, 21, 19, 0, 0, TimeSpan.Zero);
        var scheduler = new ReconciliationScheduler(_fx.ConnectionString, queue, audit, () => evening);

        Assert.Equal(2, await scheduler.TryEnqueueDailyAsync("MOCK", CancellationToken.None));
        Assert.Equal(0, await scheduler.TryEnqueueDailyAsync("MOCK", CancellationToken.None)); // 同意图幂等

        await using var c = await _fx.OpenAsync();
        var jobs = (await c.QueryAsync<(string Intent, string Payload)>(
            "SELECT intent_key, payload_json FROM dbo.outbox_job WHERE job_kind=N'reconciliation' ORDER BY intent_key")).ToList();
        Assert.Equal(2, jobs.Count);
        Assert.Equal("recon:run:202611201600:RETURN", jobs[0].Intent);
        Assert.Equal("recon:run:202611201600:SALE", jobs[1].Intent);
        Assert.Contains("\"windowStart\":\"2026-11-20T16:00:00Z\"", jobs[1].Payload, StringComparison.Ordinal);
        Assert.Contains("\"windowEnd\":\"2026-11-21T16:00:00Z\"", jobs[1].Payload, StringComparison.Ordinal);
        Assert.Equal(2, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.audit_log WHERE action=N'recon.auto_triggered' AND env=N'MOCK'"));

        // 上海 01:00（未到 02:00）不入队。
        var early = new DateTimeOffset(2026, 11, 21, 17, 0, 0, TimeSpan.Zero);
        var earlyScheduler = new ReconciliationScheduler(_fx.ConnectionString, queue, audit, () => early);
        Assert.Equal(0, await earlyScheduler.TryEnqueueDailyAsync("MOCK", CancellationToken.None));
        Assert.Equal(2, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=N'reconciliation'"));
    }

    // ---------------------------------------------------------------- 11. 触发 API

    [Fact]
    public async Task Recon_trigger_api_enqueues_job_and_audits_live()
    {
        var client = await LoginAsync();
        var response = await PostJsonAsync(client, "/api/v1/reconciliation-runs",
            new { windowStart = "2026-11-20T00:00:00Z", windowEnd = "2026-11-21T00:00:00Z", businessType = "SALE" },
            "recon-trigger-1");
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var jobId = (await ReadJsonAsync(response)).GetProperty("data").GetProperty("jobId").GetString();
        Assert.NotNull(jobId);

        await using var c = await _fx.OpenAsync();
        var job = await c.QuerySingleAsync<(string Kind, string Intent, string State)>(
            "SELECT job_kind, intent_key, job_state FROM dbo.outbox_job WHERE id=@id", new { id = long.Parse(jobId!) });
        Assert.Equal("reconciliation", job.Kind);
        Assert.Equal("recon:run:202611200000:SALE", job.Intent);
        Assert.Equal("PENDING", job.State);
        Assert.Equal(1, await c.ExecuteScalarAsync<int>(@"
SELECT COUNT(*) FROM dbo.audit_log
WHERE action=N'recon.triggered' AND env='LIVE' AND object_type=N'outbox_job' AND object_id=@oid",
            new { oid = jobId }));

        // 窗口非法（end 不晚于 start）→ 422，且不产生第二条作业/审计。
        var invalid = await PostJsonAsync(client, "/api/v1/reconciliation-runs",
            new { windowStart = "2026-11-21T00:00:00Z", windowEnd = "2026-11-20T00:00:00Z", businessType = "SALE" },
            "recon-trigger-2");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        Assert.Equal("VALIDATION", (await ReadJsonAsync(invalid)).GetProperty("code").GetString());
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=N'reconciliation'"));
    }

    // ---------------------------------------------------------------- 种子

    private static string Sha(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private async Task SeedUserAsync(SqlConnection c, string username, string display, string password,
        IEnumerable<string> permissions)
    {
        var hasher = new Microsoft.AspNetCore.Identity.PasswordHasher<string>();
        var id = await c.ExecuteScalarAsync<long>(@"
IF NOT EXISTS (SELECT 1 FROM dbo.app_user WHERE username=@username)
    INSERT INTO dbo.app_user(username,display_name,password_hash) VALUES(@username,@display,@h);
SELECT id FROM dbo.app_user WHERE username=@username",
            new { username, display, h = hasher.HashPassword(username, password) });
        foreach (var p in permissions)
            await c.ExecuteAsync(@"
IF NOT EXISTS (SELECT 1 FROM dbo.user_permission WHERE user_id=@id AND permission=@p)
    INSERT INTO dbo.user_permission(user_id,permission,granted_by) VALUES(@id,@p,N'test')", new { id, p });
    }

    private async Task<long> SeedDocumentAsync(SqlConnection c, string documentKey,
        string completeness = "COMPLETE", int version = 1)
        => await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.source_snapshot(org_id,source_system_id,source_document_key,document_type,version,
    business_datetime,completeness_status,content_hash)
OUTPUT inserted.id
VALUES(@orgId,@sysId,@documentKey,N'SALE',@version,@businessDatetime,@completeness,@hash)",
            new
            {
                orgId = _orgId, sysId = _sysId, documentKey, version,
                businessDatetime = DayStart.AddHours(10), completeness, hash = Sha(documentKey + ":" + version),
            });

    private static async Task SeedLineAsync(SqlConnection c, long snapshotId, string lineKey, string eventKind,
        string? code)
        => await c.ExecuteAsync(@"
INSERT INTO dbo.source_line(snapshot_id,source_line_key,product_source_id,quantity_original,unit_original,
    trace_code_raw,trace_code_normalized,code_format,line_hash,event_kind)
VALUES(@snapshotId,@lineKey,N'P-1',1,N'盒',@code,@code,@format,@hash,@eventKind)",
            new
            {
                snapshotId, lineKey, code,
                format = code is null || code.Trim().Length == 0 ? "Empty" : "S8P20",
                hash = Sha(snapshotId + ":" + lineKey), eventKind,
            });

    private async Task<long> SeedTaskAsync(SqlConnection c, string eventKey, long snapshotId,
        string env = "MOCK", string taskType = "RETAIL_321", string state = "Succeeded",
        string matchState = "MATCHED", string verificationState = "PASSED", string dependencyState = "NONE",
        string? stateReason = null)
        => await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upload_task(org_id,env,event_key,task_type,source_snapshot_id,source_hash,payload_hash,
    state,state_reason,match_state,verification_state,dependency_state)
OUTPUT inserted.id
VALUES(@orgId,@env,@eventKey,@taskType,@snapshotId,@hash,@hash,@state,@stateReason,@matchState,@verificationState,@dependencyState)",
            new
            {
                orgId = _orgId, env, eventKey, taskType, snapshotId, hash = Sha(eventKey), state, stateReason,
                matchState, verificationState, dependencyState,
            });

    private async Task SeedVerificationAsync(SqlConnection c, string eventKey, long snapshotId, string state,
        long? uploadTaskId, string env = "MOCK")
        => await c.ExecuteAsync(@"
INSERT INTO dbo.verification(org_id,env,event_key,upload_task_id,source_snapshot_id,source_hash,code_set_hash,
    rule_version,state,is_current)
VALUES(@orgId,@env,@eventKey,@uploadTaskId,@snapshotId,@hash,@hash,N'M3-1',@state,1)",
            new { orgId = _orgId, env, eventKey, uploadTaskId, snapshotId, hash = Sha(eventKey), state });

    private async Task<long> SeedPartAsync(SqlConnection c, long taskId, int partIndex, string state, string env = "MOCK")
        => await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upload_part(task_id,org_id,env,part_index,bill_code,payload_json,payload_hash,code_set_hash,state)
OUTPUT inserted.id
VALUES(@taskId,@orgId,@env,@partIndex,@bill,N'{}',@hash,@hash,@state)",
            new
            {
                taskId, orgId = _orgId, env, partIndex,
                bill = "TFR" + Guid.NewGuid().ToString("N")[..10], hash = Sha("part:" + taskId + ":" + partIndex), state,
            });

    private async Task SeedQueryAttemptAsync(SqlConnection c, long taskId, long partId, int attemptNo,
        int httpStatus, string requestId, string env = "MOCK")
        => await c.ExecuteAsync(@"
INSERT INTO dbo.upload_attempt(task_id,part_id,attempt_no,attempt_kind,env,fencing_token,started_at,finished_at,
    http_status,request_id,raw_response)
VALUES(@taskId,@partId,@attemptNo,N'QUERY',@env,1,DATEADD(minute,-5,SYSUTCDATETIME()),SYSUTCDATETIME(),
    @httpStatus,@requestId,N'{""msgCode"":""SUCCESS""}')",
            new { taskId, partId, attemptNo, env, httpStatus, requestId });

    // ---------------------------------------------------------------- HTTP 辅助

    private sealed record SessionResp(SessionData Data);
    private sealed record SessionData(bool Authenticated, string CsrfToken);

    private async Task<HttpClient> LoginAsync()
    {
        var client = _factory.CreateClient();
        var session = await client.GetFromJsonAsync<SessionResp>("/api/v1/auth/session");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session!.Data.CsrfToken);
        (await client.PostAsJsonAsync("/api/v1/auth/login",
            new { username = "recon-admin", password = AdminPassword })).EnsureSuccessStatusCode();
        var session2 = await client.GetFromJsonAsync<SessionResp>("/api/v1/auth/session");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session2!.Data.CsrfToken);
        return client;
    }

    private static async Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string path, object body,
        string idempotencyKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        return doc.RootElement.Clone();
    }
}