using System.Runtime.Versioning; // brief 缺失此 using（DpapiProtector 的 SupportedOSPlatform 标注所需）；与 InboundTaskFactoryTests 同模式
using Dapper;
using Microsoft.Data.SqlClient;
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
}