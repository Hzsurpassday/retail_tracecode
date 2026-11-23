using System.Runtime.Versioning; // brief 缺失此 using（DpapiProtector 的 SupportedOSPlatform 标注所需）；与 InboundTaskFactoryTests 同模式
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Infrastructure.Codes;
using TraceFlow.Infrastructure.Inbound;
using TraceFlow.Infrastructure.Jobs;
using TraceFlow.Infrastructure.Security;
using TraceFlow.Infrastructure.Settings;
using TraceFlow.IntegrationTests.Sources;
using Xunit;

namespace TraceFlow.IntegrationTests.Inbound;

[SupportedOSPlatform("windows")] // DpapiProtector 仅 Windows（CA1416）
// 每测试独立库：brief 的种子对 platform_enterprise_version(ref_ent_id,ent_id) 与
// supplier_mapping 的 active 唯一索引是全局唯一——类共享库会在第 2 个测试即撞唯一约束（与 SyncSchedulerTests 同模式）。
public class InboundMatchTests : IAsyncLifetime
{
    private Sql2008Fixture _fx = null!;
    public async Task InitializeAsync() { _fx = new Sql2008Fixture(); await _fx.InitializeAsync(); }
    public Task DisposeAsync() => _fx.DisposeAsync();

    private InboundMatchService NewService() => new(_fx.ConnectionString,
        new InboundTaskFactory(new SettingsService(_fx.ConnectionString, new DpapiProtector()),
            new JobQueue(_fx.ConnectionString, new AuditLog())), new AuditLog());

    private sealed record Seed(long OrgId, long SysId, long SnapId, long LineId, long BillId, long UpLineId,
        string Dwbh, string Spid, string Code1, string Code2);

    private static string Code(int i) => "812345678901234567" + i.ToString("D2");

    // 直接读库断言持久化状态（非服务返回值），使“只判不落库”的实现无法通过。
    private static Task<MatchRow> LoadMatchAsync(SqlConnection c, long snapshotId)
        => c.QuerySingleAsync<MatchRow>(@"
SELECT match_state,reason,matched_at,supplier_dwbh,version
FROM dbo.inbound_match WHERE source_snapshot_id=@id", new { id = snapshotId });

    private sealed class MatchRow
    {
        public string match_state { get; set; } = "";
        public string? reason { get; set; }
        public DateTime? matched_at { get; set; }
        public string? supplier_dwbh { get; set; }
        public int version { get; set; }
    }

    private static Task<int> CountAllocationsAsync(SqlConnection c, long snapshotId, string state)
        => c.ExecuteScalarAsync<int>(@"
SELECT COUNT(*) FROM dbo.inbound_allocation
WHERE state=@state AND source_line_id IN (SELECT id FROM dbo.source_line WHERE snapshot_id=@snap)",
            new { state, snap = snapshotId });

    private static Task<int> CountClaimsAsync(SqlConnection c, long taskId, string state)
        => c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.code_claim WHERE upload_task_id=@id AND claim_state=@state", new { id = taskId, state });

    private async Task<Seed> SeedBaseAsync(SqlConnection c, string tag, int codeBase)
    {
        var (orgId, sysId) = await TestSeed.EnsureAsync(c);
        await c.ExecuteAsync("UPDATE dbo.organization SET ent_id=N'ENT-STORE', ref_ent_id=N'REF-STORE' WHERE id=@orgId", new { orgId });
        var dwbh = "DW" + tag; var spid = "SP" + tag; var drug = "DRUG-" + tag;
        await c.ExecuteAsync(@"
IF NOT EXISTS (SELECT 1 FROM dbo.supplier WHERE org_id=@orgId AND source_system_id=@sysId AND dwbh=@dwbh)
INSERT INTO dbo.supplier(org_id,source_system_id,dwbh,name) VALUES(@orgId,@sysId,@dwbh,N'供应商')", new { orgId, sysId, dwbh });
        var supId = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.supplier WHERE org_id=@orgId AND source_system_id=@sysId AND dwbh=@dwbh", new { orgId, sysId, dwbh });
        var entVerId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.platform_enterprise_version(ref_ent_id,ent_id,name) OUTPUT INSERTED.id VALUES(N'REF-SUP',N'ENT-SUP',N'上游企业')");
        await c.ExecuteAsync("INSERT INTO dbo.supplier_mapping(org_id,supplier_id,enterprise_version_id,state) VALUES(@orgId,@supId,@ev,N'MANUAL_CONFIRMED')", new { orgId, supId, ev = entVerId });
        await c.ExecuteAsync(@"
INSERT INTO dbo.product(org_id,source_system_id,spid,name,trace_classification) VALUES(@orgId,@sysId,@spid,N'药品',N'REQUIRED')", new { orgId, sysId, spid });
        var prodId = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.product WHERE org_id=@orgId AND source_system_id=@sysId AND spid=@spid", new { orgId, sysId, spid });
        var drugId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.platform_drug(platform_drug_id,source_kind) OUTPUT INSERTED.id VALUES(@drug,N'fixture')", new { drug });
        await c.ExecuteAsync("INSERT INTO dbo.product_mapping(org_id,product_id,platform_drug_id,state) VALUES(@orgId,@prodId,@drugId,N'MANUAL_CONFIRMED')", new { orgId, prodId, drugId });
        var snapId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.source_snapshot(org_id,source_system_id,source_document_key,document_type,version,business_datetime,completeness_status,content_hash,raw_header)
OUTPUT INSERTED.id VALUES(@orgId,@sysId,@key,N'PURCHASE_IN',1,@biz,N'COMPLETE',@hash,@header)",
            new { orgId, sysId, key = "PI-" + tag, biz = new DateTime(2026, 11, 20, 2, 0, 0, DateTimeKind.Utc),
                  hash = new string('0', 64), header = "{\"dwbh\":\"" + dwbh + "\"}" });
        var lineId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.source_line(snapshot_id,source_line_key,product_source_id,quantity_original,unit_original,batch_raw,line_hash,event_kind)
OUTPUT INSERTED.id VALUES(@snapId,N'1',@spid,2,N'盒',N'B2026',@hash,N'PURCHASE_IN')", new { snapId, spid, hash = new string('0', 64) });
        var (billId, upLineId) = await AddBillAsync(c, orgId, "PB-" + tag, "UP-" + tag, drug, "B2026", codeBase);
        return new Seed(orgId, sysId, snapId, lineId, billId, upLineId, dwbh, spid, Code(codeBase), Code(codeBase + 1));
    }

    private async Task<(long BillId, long UpLineId)> AddBillAsync(SqlConnection c, long orgId, string platformBillId, string billNo, string drug, string batch, int codeBase)
    {
        var billId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upstream_bill(org_id,env,upstream_ref_ent_id,platform_bill_id,bill_no,bill_date,to_ent_id,completeness,source_version)
OUTPUT INSERTED.id VALUES(@orgId,N'MOCK',N'REF-SUP',@pb,@no,@date,N'ENT-STORE',N'COMPLETE',N'v1')",
            new { orgId, pb = platformBillId, no = billNo, date = new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc) });
        var upLineId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upstream_bill_line(bill_id,platform_line_id,platform_drug_id,batch_no,quantity,unit)
OUTPUT INSERTED.id VALUES(@billId,N'L1',@drug,@batch,2,N'盒')", new { billId, drug, batch });
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
        foreach (var i in new[] { codeBase, codeBase + 1 })
        {
            var stored = await TraceCodeStore.EnsureAsync(c, tx, Code(i), default);
            await c.ExecuteAsync(@"
INSERT INTO dbo.upstream_bill_code(bill_id,line_id,code_normalized,code_raw,package_level)
VALUES(@billId,@lineId,@n,@raw,N'MIN')", new { billId, lineId = upLineId, n = stored.Normalized, raw = stored.Raw }, tx);
        }
        await tx.CommitAsync();
        return (billId, upLineId);
    }

    [Fact]
    public async Task Unique_full_solution_auto_creates_task()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedBaseAsync(c, "M1", 10);
        var sum = await NewService().RunAsync(s.OrgId, "MOCK", s.SnapId, default);
        Assert.Equal(1, sum.Complete);
        Assert.Equal(1, sum.TasksCreated);
        Assert.Equal("COMPLETE", await c.ExecuteScalarAsync<string>("SELECT match_state FROM dbo.inbound_match WHERE source_snapshot_id=@id", new { id = s.SnapId }));
        var m = await LoadMatchAsync(c, s.SnapId);
        Assert.Equal("COMPLETE", m.match_state);
        Assert.Null(m.reason);
        Assert.NotNull(m.matched_at);
        Assert.Equal(s.Dwbh, m.supplier_dwbh);
        var taskId = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.upload_task WHERE event_key=@k", new { k = "KSOA:PI-M1:PURCHASE_IN" });
        Assert.Equal("Ready", await c.ExecuteScalarAsync<string>("SELECT state FROM dbo.upload_task WHERE id=@id", new { id = taskId }));
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.inbound_allocation WHERE source_line_id=@id AND state=N'RESERVED'", new { id = s.LineId }));
        Assert.Equal(2, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.code_claim WHERE upload_task_id=@id AND claim_state=N'ACTIVE'", new { id = taskId }));
        Assert.Equal(2, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.code_event WHERE upload_task_id=@id", new { id = taskId }));
    }

    [Fact]
    public async Task All_exempt_lines_not_required_zero_tasks()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedBaseAsync(c, "M2", 20);
        await c.ExecuteAsync("UPDATE dbo.product SET trace_classification=N'EXEMPT' WHERE org_id=@orgId AND spid=@spid", new { orgId = s.OrgId, spid = s.Spid });
        var sum = await NewService().RunAsync(s.OrgId, "MOCK", s.SnapId, default);
        Assert.Equal(1, sum.NotRequired);
        Assert.Equal(0, sum.TasksCreated);
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.upload_task WHERE event_key=@k", new { k = "KSOA:PI-M2:PURCHASE_IN" }));
    }

    [Fact]
    public async Task Unknown_classification_unmatched()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedBaseAsync(c, "M3", 30);
        await c.ExecuteAsync("UPDATE dbo.product SET trace_classification=N'UNKNOWN' WHERE org_id=@orgId AND spid=@spid", new { orgId = s.OrgId, spid = s.Spid });
        var sum = await NewService().RunAsync(s.OrgId, "MOCK", s.SnapId, default);
        Assert.Equal(1, sum.Unmatched);
        Assert.Equal(0, sum.TasksCreated);
        var m = await LoadMatchAsync(c, s.SnapId);
        Assert.Equal("UNMATCHED", m.match_state);
        Assert.False(string.IsNullOrWhiteSpace(m.reason));
        Assert.NotNull(m.matched_at);
    }

    [Fact]
    public async Task Nonpositive_required_qty_writes_ingest_exception()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedBaseAsync(c, "M4", 40);
        await c.ExecuteAsync("UPDATE dbo.source_line SET quantity_original=0 WHERE id=@id", new { id = s.LineId });
        var sum = await NewService().RunAsync(s.OrgId, "MOCK", s.SnapId, default);
        Assert.Equal(1, sum.Unmatched);
        Assert.True(await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.ingest_exception WHERE source_document_key=N'PI-M4' AND exception_type=N'QTY_NONPOSITIVE' AND status=N'OPEN'") >= 1);
    }

    [Fact]
    public async Task Multiple_candidates_ambiguous_no_auto_pick()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedBaseAsync(c, "M5", 50);
        await AddBillAsync(c, s.OrgId, "PB-M5B", "UP-M5B", "DRUG-M5", "B2026", 52);
        var sum = await NewService().RunAsync(s.OrgId, "MOCK", s.SnapId, default);
        Assert.Equal(1, sum.Ambiguous);
        Assert.Equal(0, sum.TasksCreated);
        var m = await LoadMatchAsync(c, s.SnapId);
        Assert.Equal("AMBIGUOUS", m.match_state);
        Assert.Contains("多解", m.reason);
        Assert.NotNull(m.matched_at);
    }

    [Fact]
    public async Task External_no_hit_but_batch_conflict()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedBaseAsync(c, "M6", 60);
        await c.ExecuteAsync("UPDATE dbo.source_snapshot SET raw_header=@h WHERE id=@id", new { id = s.SnapId, h = "{\"dwbh\":\"" + s.Dwbh + "\",\"ywdjbh\":\"UP-M6\"}" });
        await c.ExecuteAsync("UPDATE dbo.upstream_bill_line SET batch_no=N'B9999' WHERE id=@id", new { id = s.UpLineId });
        var sum = await NewService().RunAsync(s.OrgId, "MOCK", s.SnapId, default);
        Assert.Equal(1, sum.Conflict);
        Assert.Equal(0, sum.TasksCreated);
        var m = await LoadMatchAsync(c, s.SnapId);
        Assert.Equal("CONFLICT", m.match_state);
        Assert.Contains("批号冲突", m.reason);
        Assert.NotNull(m.matched_at);
    }

    [Fact]
    public async Task One_line_unmatched_partial()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedBaseAsync(c, "M7", 70);
        await c.ExecuteAsync(@"
INSERT INTO dbo.source_line(snapshot_id,source_line_key,product_source_id,quantity_original,unit_original,batch_raw,line_hash,event_kind)
VALUES(@snapId,N'2',@spid,2,N'盒',N'B0000',@hash,N'PURCHASE_IN')", new { snapId = s.SnapId, spid = s.Spid, hash = new string('0', 64) });
        var sum = await NewService().RunAsync(s.OrgId, "MOCK", s.SnapId, default);
        Assert.Equal(1, sum.Partial);
        Assert.Equal(0, sum.TasksCreated);
        var m = await LoadMatchAsync(c, s.SnapId);
        Assert.Equal("PARTIAL", m.match_state);
        Assert.False(string.IsNullOrWhiteSpace(m.reason));
        Assert.NotNull(m.matched_at);
    }

    [Fact]
    public async Task Sent_task_frozen_on_rematch()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedBaseAsync(c, "M8", 80);
        await NewService().RunAsync(s.OrgId, "MOCK", s.SnapId, default);
        var taskId = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.upload_task WHERE event_key=@k", new { k = "KSOA:PI-M8:PURCHASE_IN" });
        await c.ExecuteAsync("INSERT INTO dbo.upload_attempt(task_id,attempt_no,attempt_kind,env,fencing_token) VALUES(@id,1,N'UPLOAD',N'MOCK',1)", new { id = taskId });
        await c.ExecuteAsync("UPDATE dbo.upload_task SET state=N'Succeeded' WHERE id=@id", new { id = taskId });
        await c.ExecuteAsync("UPDATE dbo.source_snapshot SET content_hash=@h WHERE id=@id", new { id = s.SnapId, h = new string('1', 64) });
        var sum = await NewService().RunAsync(s.OrgId, "MOCK", s.SnapId, default);
        Assert.Equal(1, sum.Complete);
        Assert.Equal(0, sum.TasksCreated);
        Assert.Equal("Succeeded", await c.ExecuteScalarAsync<string>("SELECT state FROM dbo.upload_task WHERE id=@id", new { id = taskId }));
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.inbound_allocation WHERE source_line_id=@id AND state=N'RESERVED'", new { id = s.LineId }));
        var m = await LoadMatchAsync(c, s.SnapId);
        Assert.Equal("COMPLETE", m.match_state);
        Assert.Contains("冻结", m.reason);
        Assert.NotNull(m.matched_at);
    }

    [Fact]
    public async Task New_snapshot_version_rebuilds_pre_send_task_same_bill_code()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedBaseAsync(c, "M9", 90);
        await NewService().RunAsync(s.OrgId, "MOCK", s.SnapId, default);
        var taskId = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.upload_task WHERE event_key=@k", new { k = "KSOA:PI-M9:PURCHASE_IN" });
        var billCode = await c.ExecuteScalarAsync<string>("SELECT bill_code FROM dbo.upload_part WHERE task_id=@id", new { id = taskId });
        var snap2 = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.source_snapshot(org_id,source_system_id,source_document_key,document_type,version,business_datetime,completeness_status,content_hash,raw_header)
OUTPUT INSERTED.id VALUES(@orgId,@sysId,N'PI-M9',N'PURCHASE_IN',2,@biz,N'COMPLETE',@hash,@header)",
            new { orgId = s.OrgId, sysId = s.SysId, biz = new DateTime(2026, 11, 20, 2, 0, 0, DateTimeKind.Utc),
                  hash = new string('1', 64), header = "{\"dwbh\":\"" + s.Dwbh + "\"}" });
        await c.ExecuteAsync(@"
INSERT INTO dbo.source_line(snapshot_id,source_line_key,product_source_id,quantity_original,unit_original,batch_raw,line_hash,event_kind)
VALUES(@snapId,N'1',@spid,2,N'盒',N'B2026',@hash,N'PURCHASE_IN')", new { snapId = snap2, spid = s.Spid, hash = new string('1', 64) });
        var sum = await NewService().RunAsync(s.OrgId, "MOCK", snap2, default);
        Assert.Equal(1, sum.Complete);
        Assert.Equal(1, sum.TasksCreated);
        Assert.Equal(taskId, await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.upload_task WHERE event_key=@k", new { k = "KSOA:PI-M9:PURCHASE_IN" }));
        Assert.Equal(billCode, await c.ExecuteScalarAsync<string>("SELECT bill_code FROM dbo.upload_part WHERE task_id=@id", new { id = taskId }));
        Assert.Equal(snap2, await c.ExecuteScalarAsync<long>("SELECT source_snapshot_id FROM dbo.upload_task WHERE id=@id", new { id = taskId }));
        Assert.Equal("UNMATCHED", await c.ExecuteScalarAsync<string>("SELECT match_state FROM dbo.inbound_match WHERE source_snapshot_id=@id", new { id = s.SnapId }));
        Assert.Equal("COMPLETE", await c.ExecuteScalarAsync<string>("SELECT match_state FROM dbo.inbound_match WHERE source_snapshot_id=@id", new { id = snap2 }));
        Assert.Equal(2, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.code_claim WHERE upload_task_id=@id AND claim_state=N'ACTIVE'", new { id = taskId }));
    }

    [Fact]
    public async Task Stale_superseded_version_never_rescanned()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedBaseAsync(c, "M10", 95);
        await NewService().RunAsync(s.OrgId, "MOCK", s.SnapId, default);
        var taskId = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.upload_task WHERE event_key=@k", new { k = "KSOA:PI-M10:PURCHASE_IN" });
        var snap2 = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.source_snapshot(org_id,source_system_id,source_document_key,document_type,version,business_datetime,completeness_status,content_hash,raw_header)
OUTPUT INSERTED.id VALUES(@orgId,@sysId,N'PI-M10',N'PURCHASE_IN',2,@biz,N'COMPLETE',@hash,@header)",
            new { orgId = s.OrgId, sysId = s.SysId, biz = new DateTime(2026, 11, 20, 2, 0, 0, DateTimeKind.Utc),
                  hash = new string('1', 64), header = "{\"dwbh\":\"" + s.Dwbh + "\"}" });
        await c.ExecuteAsync(@"
INSERT INTO dbo.source_line(snapshot_id,source_line_key,product_source_id,quantity_original,unit_original,batch_raw,line_hash,event_kind)
VALUES(@snapId,N'1',@spid,2,N'盒',N'B2026',@hash,N'PURCHASE_IN')", new { snapId = snap2, spid = s.Spid, hash = new string('1', 64) });

        // null 扫描选中新版本 v2：取消按旧任务自己的快照（v1）执行，v1 的匹配置被改为 UNMATCHED。
        var first = await NewService().RunAsync(s.OrgId, "MOCK", null, default);
        Assert.Equal(1, first.SnapshotsSeen);
        Assert.Equal(1, first.TasksCreated);
        var stale = await LoadMatchAsync(c, s.SnapId);
        Assert.Equal("UNMATCHED", stale.match_state);
        var cancelSql = "SELECT COUNT(*) FROM dbo.audit_log WHERE action=N'inbound.cancel' AND object_type=N'upload_task' AND object_id=@id";
        var cancelsBefore = await c.ExecuteScalarAsync<int>(cancelSql, new { id = taskId.ToString() });
        Assert.Equal(1, cancelsBefore);
        var taskVersion = await c.ExecuteScalarAsync<int>("SELECT version FROM dbo.upload_task WHERE id=@id", new { id = taskId });
        var tasksBefore = await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.upload_task WHERE org_id=@orgId", new { orgId = s.OrgId });

        var second = await NewService().RunAsync(s.OrgId, "MOCK", null, default);

        Assert.Equal(0, second.SnapshotsSeen);
        Assert.Equal(0, second.TasksCreated);
        Assert.Equal(tasksBefore, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.upload_task WHERE org_id=@orgId", new { orgId = s.OrgId }));
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.upload_task WHERE event_key=@k", new { k = "KSOA:PI-M10:PURCHASE_IN" }));
        Assert.Equal(cancelsBefore, await c.ExecuteScalarAsync<int>(cancelSql, new { id = taskId.ToString() }));
        Assert.Equal(taskVersion, await c.ExecuteScalarAsync<int>("SELECT version FROM dbo.upload_task WHERE id=@id", new { id = taskId }));
        var staleAfter = await LoadMatchAsync(c, s.SnapId);
        Assert.Equal("UNMATCHED", staleAfter.match_state);
        Assert.Equal(stale.version, staleAfter.version);
        Assert.Equal(stale.matched_at, staleAfter.matched_at);
        Assert.Equal(snap2, await c.ExecuteScalarAsync<long>("SELECT source_snapshot_id FROM dbo.upload_task WHERE id=@id", new { id = taskId }));
        Assert.Equal("COMPLETE", (await LoadMatchAsync(c, snap2)).match_state);
    }

    [Fact]
    public async Task Scheduler_enqueues_inbound_match_even_when_paused()
    {
        await using var c = await _fx.OpenAsync();
        var (orgId, _) = await TestSeed.EnsureAsync(c);
        var settings = new SettingsService(_fx.ConnectionString, new DpapiProtector());
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            await settings.UpdateAsync(c, tx, "sync.paused", "true", null, "0", "测试暂停", "test", default);
            await tx.CommitAsync();
        }
        var scheduler = new TraceFlow.Infrastructure.Sources.SyncScheduler(_fx.ConnectionString,
            new JobQueue(_fx.ConnectionString, new AuditLog()), settings, "MOCK");
        await scheduler.TickAsync(new DateTime(2026, 11, 23, 3, 0, 0, DateTimeKind.Utc), default);
        Assert.True(await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.outbox_job WHERE org_id=@org AND job_kind=N'inbound-match' AND intent_key LIKE 'inbound:periodic:%'", new { org = orgId }) >= 1);
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.outbox_job WHERE org_id=@org AND job_kind=N'source-sync'", new { org = orgId }));
    }

    /// <summary>
    /// 终审修复 F1：非 COMPLETE 快照的空转重扫必须零写入——周期扫描（≤5 分钟一拍）每拍都会重扫未完成快照，
    /// 若无条件推进 inbound_match.version 并追加匹配审计，打开编辑器的用户会拿到无意义的 409 VERSION_CONFLICT，
    /// 审计也会按拍增长。断言全部落在持久状态上：版本、matched_at、匹配审计行数在空转重扫后不变；
    /// 真正变化（UNKNOWN→REQUIRED，UNMATCHED→COMPLETE）时仍须照常推进版本并留审计。
    /// </summary>
    [Fact]
    public async Task Noop_rescan_keeps_version_and_audit_changed_rescan_bumps()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedBaseAsync(c, "M12", 96);
        await c.ExecuteAsync("UPDATE dbo.product SET trace_classification=N'UNKNOWN' WHERE org_id=@orgId AND spid=@spid",
            new { orgId = s.OrgId, spid = s.Spid });
        const string auditSql = @"SELECT COUNT(*) FROM dbo.audit_log
WHERE action=N'inbound.match' AND object_type=N'source_snapshot' AND object_id=@id";
        var svc = NewService();

        var first = await svc.RunAsync(s.OrgId, "MOCK", s.SnapId, default);
        Assert.Equal(1, first.Unmatched);
        var before = await LoadMatchAsync(c, s.SnapId);
        Assert.Equal("UNMATCHED", before.match_state);
        Assert.NotNull(before.matched_at);
        Assert.Equal(1, await c.ExecuteScalarAsync<int>(auditSql, new { id = s.SnapId.ToString() }));

        // 同样的来源、同样的判定 → 空转重扫：返回摘要不变，但不得推进版本/时间，也不得新增审计。
        var again = await svc.RunAsync(s.OrgId, "MOCK", s.SnapId, default);
        Assert.Equal(1, again.SnapshotsSeen);
        Assert.Equal(1, again.Unmatched);
        var after = await LoadMatchAsync(c, s.SnapId);
        Assert.Equal(before.match_state, after.match_state);
        Assert.Equal(before.reason, after.reason);
        Assert.Equal(before.version, after.version);
        Assert.Equal(before.matched_at, after.matched_at);
        Assert.Equal(1, await c.ExecuteScalarAsync<int>(auditSql, new { id = s.SnapId.ToString() }));

        // 分类改回 REQUIRED → 判定真的变了（UNMATCHED→COMPLETE）：照常推进版本、刷新时间、留一条审计。
        await c.ExecuteAsync("UPDATE dbo.product SET trace_classification=N'REQUIRED' WHERE org_id=@orgId AND spid=@spid",
            new { orgId = s.OrgId, spid = s.Spid });
        var changed = await svc.RunAsync(s.OrgId, "MOCK", s.SnapId, default);
        Assert.Equal(1, changed.Complete);
        Assert.Equal(1, changed.TasksCreated);
        var final = await LoadMatchAsync(c, s.SnapId);
        Assert.Equal("COMPLETE", final.match_state);
        Assert.True(final.version > before.version);
        Assert.NotEqual(before.matched_at, final.matched_at);
        Assert.Equal(2, await c.ExecuteScalarAsync<int>(auditSql, new { id = s.SnapId.ToString() }));
    }

    /// <summary>
    /// R11c 证据固化（Task 11 修复 2）：trace-policy 取消发送前任务后入队的 map: 重匹配不是空转——
    /// CancelPreSend 已把匹配置 UNMATCHED、分配置 CANCELLED、占用释放，任务行仍保留（Cancelled）；
    /// 重匹配必须绕开 R8a 的既有任务短路（该短路只对非 Cancelled 任务成立）并真正重算。
    /// 断言全部落在「只有真正重算才会变」的持久状态上：任务复活为 Ready、RESERVED 分配 2→1、ACTIVE 占用重建、匹配 reason 清空。
    /// </summary>
    [Fact]
    public async Task Map_rematch_bypasses_content_hash_noop_for_cancelled_task()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedBaseAsync(c, "M11", 10);
        // 第二行：独立商品/映射/上游明细，重匹配前改为 EXEMPT（对应修复报告里探针的 RESERVED 2→1）
        var spid2 = "SP-M11-B"; var drug2 = "DRUG-M11-B";
        await c.ExecuteAsync(@"
INSERT INTO dbo.product(org_id,source_system_id,spid,name,trace_classification) VALUES(@orgId,@sysId,@spid,N'药品',N'REQUIRED')",
            new { orgId = s.OrgId, sysId = s.SysId, spid = spid2 });
        var prod2 = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.product WHERE org_id=@orgId AND source_system_id=@sysId AND spid=@spid",
            new { orgId = s.OrgId, sysId = s.SysId, spid = spid2 });
        var drugId2 = await c.ExecuteScalarAsync<long>(
            "INSERT INTO dbo.platform_drug(platform_drug_id,source_kind) OUTPUT INSERTED.id VALUES(@drug,N'fixture')", new { drug = drug2 });
        await c.ExecuteAsync("INSERT INTO dbo.product_mapping(org_id,product_id,platform_drug_id,state) VALUES(@orgId,@prodId,@drugId,N'MANUAL_CONFIRMED')",
            new { orgId = s.OrgId, prodId = prod2, drugId = drugId2 });
        await c.ExecuteAsync(@"
INSERT INTO dbo.source_line(snapshot_id,source_line_key,product_source_id,quantity_original,unit_original,batch_raw,line_hash,event_kind)
VALUES(@snapId,N'2',@spid,2,N'盒',N'B2027',@hash,N'PURCHASE_IN')", new { snapId = s.SnapId, spid = spid2, hash = new string('0', 64) });
        await AddBillAsync(c, s.OrgId, "PB-M11B", "UP-M11B", drug2, "B2027", 12);

        var svc = NewService();
        var first = await svc.RunAsync(s.OrgId, "MOCK", s.SnapId, default);
        Assert.Equal(1, first.Complete);
        Assert.Equal(1, first.TasksCreated);
        var taskId = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.upload_task WHERE event_key=@k", new { k = "KSOA:PI-M11:PURCHASE_IN" });
        var billCode = await c.ExecuteScalarAsync<string>("SELECT bill_code FROM dbo.upload_part WHERE task_id=@id", new { id = taskId });
        Assert.Equal("Ready", await c.ExecuteScalarAsync<string>("SELECT state FROM dbo.upload_task WHERE id=@id", new { id = taskId }));
        Assert.Equal(2, await CountAllocationsAsync(c, s.SnapId, "RESERVED"));
        Assert.Equal(4, await CountClaimsAsync(c, taskId, "ACTIVE"));

        // R11c 形态：分类变更前先取消发送前任务（auditEnv=LIVE），任务/分配/占用/匹配同事务回退
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            Assert.True(await InboundCancelService.CancelPreSendAsync(c, tx, s.OrgId, "MOCK", s.SnapId,
                "m11actor", "商品分类变更失效发送前任务", "LIVE", default));
            await tx.CommitAsync();
        }
        Assert.Equal("Cancelled", await c.ExecuteScalarAsync<string>("SELECT state FROM dbo.upload_task WHERE id=@id", new { id = taskId }));
        var cancelledTaskVersion = await c.ExecuteScalarAsync<int>("SELECT version FROM dbo.upload_task WHERE id=@id", new { id = taskId });
        Assert.True(cancelledTaskVersion > 1); // 取消推进了任务版本
        Assert.Equal(0, await CountAllocationsAsync(c, s.SnapId, "RESERVED"));
        Assert.Equal(2, await CountAllocationsAsync(c, s.SnapId, "CANCELLED"));
        Assert.Equal(0, await CountClaimsAsync(c, taskId, "ACTIVE"));
        var cancelledMatch = await LoadMatchAsync(c, s.SnapId);
        Assert.Equal("UNMATCHED", cancelledMatch.match_state);

        await c.ExecuteAsync("UPDATE dbo.product SET trace_classification=N'EXEMPT' WHERE org_id=@orgId AND spid=@spid",
            new { orgId = s.OrgId, spid = spid2 });

        // map:{Guid:N} 触发形态（与 MasterEndpoints trace-policy 入队同形），经作业处理器执行
        var intent = $"map:{Guid.NewGuid():N}";
        var payload = JsonSerializer.Serialize(new { orgId = s.OrgId, env = "MOCK", snapshotId = (long?)null, trigger = "map" });
        long jobId;
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            jobId = await new JobQueue(_fx.ConnectionString, new AuditLog())
                .EnqueueAsync(c, tx, s.OrgId, "MOCK", "inbound-match", intent, payload, DateTime.UtcNow, default);
            await tx.CommitAsync();
        }
        Assert.Equal(1, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.outbox_job WHERE id=@id AND job_kind=N'inbound-match' AND intent_key LIKE N'map:%'", new { id = jobId }));
        await new InboundMatchJobHandler(svc).HandleAsync(
            new ClaimedJob(jobId, "inbound-match", intent, payload, 1, DateTime.UtcNow.AddMinutes(5), 0), default);

        // 任务行政性复活（R7a）：同一行、bill_code 不变、版本继续推进
        Assert.Equal("Ready", await c.ExecuteScalarAsync<string>("SELECT state FROM dbo.upload_task WHERE id=@id", new { id = taskId }));
        Assert.True(await c.ExecuteScalarAsync<int>("SELECT version FROM dbo.upload_task WHERE id=@id", new { id = taskId }) > cancelledTaskVersion);
        Assert.Equal(billCode, await c.ExecuteScalarAsync<string>("SELECT bill_code FROM dbo.upload_part WHERE task_id=@id", new { id = taskId }));
        // 分配重算：仅仍为 REQUIRED 的第一行新建 RESERVED（2→1）；两条旧分配保持 CANCELLED，第二行不复活
        Assert.Equal(1, await CountAllocationsAsync(c, s.SnapId, "RESERVED"));
        Assert.Equal(2, await CountAllocationsAsync(c, s.SnapId, "CANCELLED"));
        Assert.Equal(3, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.inbound_allocation WHERE source_line_id IN (SELECT id FROM dbo.source_line WHERE snapshot_id=@snap)",
            new { snap = s.SnapId }));
        Assert.Equal(2, await CountClaimsAsync(c, taskId, "ACTIVE"));
        // 匹配重算并落库：COMPLETE / 无 reason —— 若 R8a 短路误用于 Cancelled 任务，这里会停在「冻结」COMPLETE 且任务不复活
        var after = await LoadMatchAsync(c, s.SnapId);
        Assert.Equal("COMPLETE", after.match_state);
        Assert.Null(after.reason);
        Assert.NotNull(after.matched_at);
        Assert.Equal(s.Dwbh, after.supplier_dwbh);
        Assert.True(after.version > cancelledMatch.version);
    }
}
