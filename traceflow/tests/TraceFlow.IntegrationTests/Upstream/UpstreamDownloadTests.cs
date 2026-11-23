using System.Globalization;
using System.Text.Json;
using Dapper;
using TraceFlow.Application.Abstractions;
using TraceFlow.Infrastructure.Jobs;
using TraceFlow.Infrastructure.Security;
using TraceFlow.Infrastructure.Top;
using TraceFlow.Infrastructure.Upstream;
using TraceFlow.IntegrationTests.Sources; // brief 缺失此 using（TestSeed 所在命名空间）；编译必需（T5 偏差 1）
using Xunit;

namespace TraceFlow.IntegrationTests.Upstream;

// brief 原为 IClassFixture 共享库；但 TestSeed O1 组织共享 + top_rate_budget 持久等待 + 残留单据
// 会跨测试污染（限流等待 30s 阻断后续调用、COMPLETE 单据被详情查询排除、码计数叠加）。
// 改用 SyncSchedulerTests 既有"每测试独立测试库"模式保证隔离；测试体与断言逐字保留（T5 偏差 3）。
public class UpstreamDownloadTests : IAsyncLifetime
{
    private Sql2008Fixture _fx = null!;
    public async Task InitializeAsync() { _fx = new Sql2008Fixture(); await _fx.InitializeAsync(); }
    public Task DisposeAsync() => _fx.DisposeAsync();

    private sealed class Script
    {
        public Dictionary<(string Method, string PageNo), TopRawResponse> Responses = new();
        public List<TopCall> Calls = new();
        // Fix Round 1：可选兜底 lambda，表达"无限不同页"等字典无法枚举的脚本；null 时行为与原先逐字一致
        public Func<TopCall, TopRawResponse>? Fallback;
        public TopRawResponse Handle(TopCall call)
        {
            Calls.Add(call);
            var pageNo = call.Parameters.TryGetValue("page_no", out var p) ? p : "1";
            return Responses.TryGetValue((call.Method, pageNo), out var r)
                ? r
                : Fallback?.Invoke(call) ?? Ok("""{"total_pages":1,"page_no":1,"bills":[]}""");
        }
    }

    private static TopRawResponse Ok(string resultJson)
        => new(true, 200, null, $$"""{"response_success":true,"result":{{resultJson}}}""", null);

    private static string ListResult(string billsJson, int totalPages = 1, int? totalCount = null)
        => $$"""{"total_pages":{{totalPages}},"total_count":{{totalCount ?? 0}},"page_no":1,"bills":{{billsJson}}}""";

    private const string BillA = """
        {"platform_bill_id":"UB-A","bill_no":"XS-SAME","upstream_ref_ent_id":"REF-SUP1","bill_date":"2026-11-18T02:00:00Z","from_ent_id":"ENT-SUP1","to_ent_id":"ENT-STORE","owner_ent_id":"ENT-SUP1","deliver_ent_id":null,"source_version":"v1"}
        """;
    private const string BillB = """
        {"platform_bill_id":"UB-B","bill_no":"XS-SAME","upstream_ref_ent_id":"REF-SUP2","bill_date":"2026-11-18T03:00:00Z","from_ent_id":"ENT-SUP2","to_ent_id":"ENT-STORE","owner_ent_id":"ENT-SUP2","deliver_ent_id":null,"source_version":"v1"}
        """;
    private const string DetailA = """
        {"total_pages":1,"page_no":1,"lines":[{"platform_line_id":"L1","platform_drug_id":"PD1","batch_no":"P20260101","quantity":2,"unit":"盒","package_level":"MIN","codes":[{"code":"81234567890123456701","package_level":"MIN","parent_code":null},{"code":"81234567890123456702","package_level":"MIN","parent_code":null}]}]}
        """;
    private const string DetailB = """
        {"total_pages":1,"page_no":1,"lines":[{"platform_line_id":"L1","platform_drug_id":"PD2","batch_no":"P20260202","quantity":1,"unit":"盒","package_level":"MIN","codes":[{"code":"81234567890123456703","package_level":"MIN","parent_code":null}]}]}
        """;

    private async Task<(UpstreamDownloadService svc, Script script, long orgId)> BuildAsync(bool withIdentity = true)
    {
        await using var c = await _fx.OpenAsync();
        var (orgId, _) = await TestSeed.EnsureAsync(c);
        if (withIdentity)
            await c.ExecuteAsync("UPDATE dbo.organization SET ref_ent_id=N'REF-STORE', ent_id=N'ENT-STORE' WHERE id=@org", new { org = orgId });
        var script = new Script();
        var top = new TopClientService(_fx.ConnectionString, new FakeTopGateway(script.Handle),
            new RateBudgetService(), new ChannelPauseService(), new AuditLog());
        var svc = new UpstreamDownloadService(_fx.ConnectionString, top, new JobQueue(_fx.ConnectionString, new AuditLog()), new AuditLog());
        return (svc, script, orgId);
    }

    private static (DateTime Start, DateTime End) Window => (DateTime.UtcNow.AddDays(-7), DateTime.UtcNow);

    [Fact]
    public async Task Full_download_completes_bills_and_enqueues_match_same_bill_no_different_subjects_not_merged()
    {
        await using var c0 = await _fx.OpenAsync();
        var (orgId, _) = await TestSeed.EnsureAsync(c0);
        await c0.ExecuteAsync("UPDATE dbo.organization SET ref_ent_id=N'REF-STORE', ent_id=N'ENT-STORE' WHERE id=@org", new { org = orgId });
        var detailByBill = new Dictionary<string, string> { ["UB-A"] = DetailA, ["UB-B"] = DetailB };
        var top = new TopClientService(_fx.ConnectionString, new FakeTopGateway(call =>
            call.Method == "listupout.detail"
                ? Ok(detailByBill[call.Parameters["bill_id"]])
                : Ok(ListResult($"[{BillA},{BillB}]", 1, 2))),
            new RateBudgetService(), new ChannelPauseService(), new AuditLog());
        var svc = new UpstreamDownloadService(_fx.ConnectionString, top, new JobQueue(_fx.ConnectionString, new AuditLog()), new AuditLog());

        var (start, end) = Window;
        var sum = await svc.RunAsync(orgId, "MOCK", start, end, null, default);
        Assert.Equal(2, sum.BillsSeen);
        Assert.Equal(2, sum.BillsComplete);
        Assert.Equal(0, sum.BillsIncomplete);

        await using var c = await _fx.OpenAsync();
        // AT-023：同 bill_no 不同上游主体→两条独立单据
        var bills = (await c.QueryAsync<BillView>(
            "SELECT id AS Id, platform_bill_id AS PlatformBillId, completeness AS Completeness FROM dbo.upstream_bill WHERE org_id=@org AND env=N'MOCK' AND bill_no=N'XS-SAME'",
            new { org = orgId })).ToList();
        Assert.Equal(2, bills.Count);
        Assert.All(bills, b => Assert.Equal("COMPLETE", b.Completeness));
        Assert.Equal(2, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.upstream_bill_code WHERE bill_id=@b", new { b = bills.Single(x => x.PlatformBillId == "UB-A").Id }));
        Assert.Equal(3, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.trace_code WHERE code_normalized IN (N'81234567890123456701',N'81234567890123456702',N'81234567890123456703')"));
        // 列表/详情进度分离且 COMPLETE
        Assert.Equal("COMPLETE", await c.ExecuteScalarAsync<string>("SELECT status FROM dbo.download_progress WHERE org_id=@org AND job_kind=N'upstream-list' AND bill_id=0", new { org = orgId }));
        // 每单入队一个 inbound-match 事件作业（意图含版本）
        Assert.Equal(2, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.outbox_job WHERE org_id=@org AND job_kind=N'inbound-match' AND intent_key LIKE 'up:%:v1'", new { org = orgId }));
    }

    private sealed record BillView(long Id, string PlatformBillId, string Completeness);

    [Fact]
    public async Task Detail_page2_failure_marks_incomplete_never_complete()
    {
        await using var c0 = await _fx.OpenAsync();
        var (orgId, _) = await TestSeed.EnsureAsync(c0);
        await c0.ExecuteAsync("UPDATE dbo.organization SET ref_ent_id=N'REF-STORE', ent_id=N'ENT-STORE' WHERE id=@org", new { org = orgId });
        var detailCalls = 0;
        var top = new TopClientService(_fx.ConnectionString, new FakeTopGateway(call =>
        {
            if (call.Method == "listupout")
                return Ok(ListResult($"[{BillA}]", 1, 1));
            detailCalls++;
            if (call.Parameters["page_no"] == "1")
                return Ok("""{"total_pages":2,"page_no":1,"lines":[{"platform_line_id":"L1","platform_drug_id":"PD1","batch_no":"P1","quantity":1,"unit":"盒","package_level":"MIN","codes":[{"code":"81234567890123456711","package_level":"MIN","parent_code":null}]}]}""");
            return new TopRawResponse(true, 200, """{"code":2,"sub_code":"isp.top-remote-connection-timeout"}""", null, null);
        }), new RateBudgetService(), new ChannelPauseService(), new AuditLog());
        var svc = new UpstreamDownloadService(_fx.ConnectionString, top, new JobQueue(_fx.ConnectionString, new AuditLog()), new AuditLog());
        var (start, end) = Window;
        var sum = await svc.RunAsync(orgId, "MOCK", start, end, null, default);
        Assert.Equal(1, sum.BillsIncomplete); // AT-022：第 2 页失败→INCOMPLETE，不供完整入库上传
        Assert.Equal(0, sum.BillsComplete);
        await using var c = await _fx.OpenAsync();
        Assert.Equal("INCOMPLETE", await c.ExecuteScalarAsync<string>("SELECT completeness FROM dbo.upstream_bill WHERE org_id=@org AND platform_bill_id=N'UB-A'", new { org = orgId }));
        Assert.Equal("FAILED", await c.ExecuteScalarAsync<string>("SELECT status FROM dbo.download_progress WHERE org_id=@org AND job_kind=N'upstream-detail' AND bill_id<>0", new { org = orgId }));
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.outbox_job WHERE org_id=@org AND job_kind=N'inbound-match'", new { org = orgId }));
        // 第 1 页码已落库（页级同事务），但单据不完整
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.upstream_bill_code bc JOIN dbo.upstream_bill b ON b.id=bc.bill_id WHERE b.org_id=@org", new { org = orgId }));
    }

    [Fact]
    public async Task Duplicate_list_page_never_fakes_complete()
    {
        var (svc, script, orgId) = await BuildAsync();
        script.Responses[("listupout", "1")] = Ok(ListResult($"[{BillA}]", 2, 2));
        script.Responses[("listupout", "2")] = Ok(ListResult($"[{BillA}]", 2, 2)); // 重复页
        var (start, end) = Window;
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RunAsync(orgId, "MOCK", start, end, null, default));
        await using var c = await _fx.OpenAsync();
        Assert.Equal("FAILED", await c.ExecuteScalarAsync<string>("SELECT status FROM dbo.download_progress WHERE org_id=@org AND job_kind=N'upstream-list' AND bill_id=0", new { org = orgId }));
    }

    [Fact]
    public async Task Total_count_mismatch_never_fakes_complete()
    {
        var (svc, script, orgId) = await BuildAsync();
        script.Responses[("listupout", "1")] = Ok(ListResult($"[{BillA}]", 1, 5)); // 期望 5 实收 1
        var (start, end) = Window;
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RunAsync(orgId, "MOCK", start, end, null, default));
        await using var c = await _fx.OpenAsync();
        Assert.Equal("FAILED", await c.ExecuteScalarAsync<string>("SELECT status FROM dbo.download_progress WHERE org_id=@org AND job_kind=N'upstream-list' AND bill_id=0", new { org = orgId }));
    }

    [Fact]
    public async Task Live_env_rejected_as_not_certified()
    {
        var (svc, _, orgId) = await BuildAsync();
        var (start, end) = Window;
        await Assert.ThrowsAsync<TopNotCertifiedException>(() => svc.RunAsync(orgId, "LIVE", start, end, null, default));
    }

    // Fix Round 1：页帽（MaxPages=1000）耗尽绝不伪造 COMPLETE——列表无 total_pages/total_count 且页页不同
    [Fact]
    public async Task List_page_cap_exhaustion_never_fakes_complete()
    {
        var (svc, script, orgId) = await BuildAsync();
        var n = 0;
        script.Fallback = call =>
        {
            if (call.Method == "listupout.detail")
                return Ok("""{"total_pages":1,"page_no":1,"lines":[]}""");
            n++;
            // 每次调用返回全新不同单据页（1 单/页），且不带 total_pages/total_count → 只能靠页帽终止
            var bill = $$"""{"platform_bill_id":"UB-CAP-{{n}}","bill_no":"XS-CAP","upstream_ref_ent_id":"REF-SUP1","bill_date":"2026-11-18T02:00:00Z","from_ent_id":"ENT-SUP1","to_ent_id":"ENT-STORE","owner_ent_id":"ENT-SUP1","deliver_ent_id":null,"source_version":"v1"}""";
            return Ok($$"""{"page_no":{{n}},"bills":[{{bill}}]}""");
        };
        var (start, end) = Window;
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RunAsync(orgId, "MOCK", start, end, null, default));
        Assert.Equal(1000, script.Calls.Count); // 恰好拉满页帽后失败，未进入详情阶段
        await using var c = await _fx.OpenAsync();
        Assert.Equal("FAILED", await c.ExecuteScalarAsync<string>("SELECT status FROM dbo.download_progress WHERE org_id=@org AND job_kind=N'upstream-list' AND bill_id=0", new { org = orgId }));
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.upstream_bill WHERE org_id=@org AND completeness=N'COMPLETE'", new { org = orgId }));
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.outbox_job WHERE org_id=@org AND job_kind=N'inbound-match'", new { org = orgId }));
    }

    // Fix Round 1：详情 total_pages=1001 超过页帽→INCOMPLETE + FAILED + 审计，零入队，已拉页码保留
    [Fact]
    public async Task Detail_page_cap_exhaustion_marks_incomplete()
    {
        await using var c0 = await _fx.OpenAsync();
        var (orgId, _) = await TestSeed.EnsureAsync(c0);
        await c0.ExecuteAsync("UPDATE dbo.organization SET ref_ent_id=N'REF-STORE', ent_id=N'ENT-STORE' WHERE id=@org", new { org = orgId });
        var top = new TopClientService(_fx.ConnectionString, new FakeTopGateway(call =>
        {
            if (call.Method == "listupout")
                return Ok(ListResult($"[{BillA}]", 1, 1));
            var pageNo = int.Parse(call.Parameters["page_no"], CultureInfo.InvariantCulture);
            var code = "8123456789012345" + pageNo.ToString("D4", CultureInfo.InvariantCulture); // 每页 1 个有效 20 位码（互不相同）
            return Ok($$"""{"total_pages":1001,"page_no":{{pageNo}},"lines":[{"platform_line_id":"L1","platform_drug_id":"PD1","batch_no":"P1","quantity":1,"unit":"盒","package_level":"MIN","codes":[{"code":"{{code}}","package_level":"MIN","parent_code":null}]}]}""");
        }), new RateBudgetService(), new ChannelPauseService(), new AuditLog());
        var svc = new UpstreamDownloadService(_fx.ConnectionString, top, new JobQueue(_fx.ConnectionString, new AuditLog()), new AuditLog());
        var (start, end) = Window;
        var sum = await svc.RunAsync(orgId, "MOCK", start, end, null, default);
        Assert.Equal(1, sum.BillsIncomplete); // 页帽耗尽=详情页未全部成功→INCOMPLETE，绝不 COMPLETE
        Assert.Equal(0, sum.BillsComplete);
        await using var c = await _fx.OpenAsync();
        Assert.Equal("INCOMPLETE", await c.ExecuteScalarAsync<string>("SELECT completeness FROM dbo.upstream_bill WHERE org_id=@org AND platform_bill_id=N'UB-A'", new { org = orgId }));
        Assert.Equal("FAILED", await c.ExecuteScalarAsync<string>("SELECT status FROM dbo.download_progress WHERE org_id=@org AND job_kind=N'upstream-detail' AND bill_id<>0", new { org = orgId }));
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.outbox_job WHERE org_id=@org AND job_kind=N'inbound-match'", new { org = orgId }));
        // 已拉的 1000 页码逐页同事务落库保留，但单据不完整
        Assert.Equal(1000, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.upstream_bill_code bc JOIN dbo.upstream_bill b ON b.id=bc.bill_id WHERE b.org_id=@org", new { org = orgId }));
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.audit_log WHERE action='upstream.detail.incomplete' AND env=N'MOCK'"));
    }

    [Fact]
    public async Task Rate_limit_midrun_keeps_page1_progress_for_next_run()
    {
        var (svc, script, orgId) = await BuildAsync();
        script.Responses[("listupout", "1")] = Ok(ListResult($"[{BillA}]", 2, 2));
        script.Responses[("listupout", "2")] = new TopRawResponse(true, 200, """{"code":7,"sub_code":"FAIL_BIZ_RATE_LIMIT"}""", null, 30);
        var (start, end) = Window;
        await Assert.ThrowsAsync<TopRateLimitedException>(() => svc.RunAsync(orgId, "MOCK", start, end, null, default));
        await using var c = await _fx.OpenAsync();
        // 第 1 页数据与进度同事务已提交；限流是瞬态→进度保持 RUNNING 供下轮重拉
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT pages_done FROM dbo.download_progress WHERE org_id=@org AND job_kind=N'upstream-list' AND bill_id=0", new { org = orgId }));
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.upstream_bill WHERE org_id=@org", new { org = orgId }));
    }

    [Fact]
    public async Task Missing_store_identity_skips_with_audit()
    {
        var (svc, script, orgId) = await BuildAsync(withIdentity: false);
        await using var c0 = await _fx.OpenAsync();
        await c0.ExecuteAsync("UPDATE dbo.organization SET ref_ent_id=NULL WHERE id=@org", new { org = orgId });
        var (start, end) = Window;
        var sum = await svc.RunAsync(orgId, "MOCK", start, end, null, default);
        Assert.Equal(0, sum.BillsSeen);
        Assert.Empty(script.Calls);
        await using var c = await _fx.OpenAsync();
        Assert.Equal(1, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.audit_log WHERE action='upstream.download.skip' AND env=N'MOCK' AND object_id=@oid",
            new { oid = orgId.ToString() }));
    }

    [Fact]
    public async Task Handler_reads_payload_and_runs()
    {
        var (svc, script, orgId) = await BuildAsync();
        script.Responses[("listupout", "1")] = Ok(ListResult("[]", 1, 0));
        var handler = new UpstreamDownloadJobHandler(svc);
        Assert.Equal("upstream-download", handler.Kind);
        var payload = JsonSerializer.Serialize(new { orgId, env = "MOCK", trigger = "manual" });
        await handler.HandleAsync(new ClaimedJob(1, "upstream-download", "manual:test", payload, 1, DateTime.UtcNow.AddMinutes(5), 0), default);
        Assert.Single(script.Calls);
    }
}
