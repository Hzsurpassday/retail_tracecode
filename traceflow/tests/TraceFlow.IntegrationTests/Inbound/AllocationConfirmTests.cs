using System.Runtime.Versioning; // DpapiProtector 仅 Windows（CA1416，与 InboundTaskFactoryTests 同模式）
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

// 与 AllocationServiceTests 同因：种子插入 platform_enterprise_version(REF-SUP,ENT-SUP) 与 active supplier_mapping，
// 二者是全局唯一约束（UQ_ent_version / UX_smap_active_platform），类共享库从第 2 个测试起必撞约束。
[SupportedOSPlatform("windows")]
public class AllocationConfirmTests : IAsyncLifetime
{
    private Sql2008Fixture _fx = null!;
    public async Task InitializeAsync() { _fx = new Sql2008Fixture(); await _fx.InitializeAsync(); }
    public Task DisposeAsync() => _fx.DisposeAsync();

    private sealed record Seed(long OrgId, long SnapId, long LineId, long UpLineId, string Code1, string Code2);

    private static string Code(int i) => "812345678901234567" + i.ToString("D2");

    private async Task<Seed> SeedAsync(SqlConnection c, string tag, int codeBase, bool secondLine = false)
    {
        var (orgId, sysId) = await TestSeed.EnsureAsync(c);
        await c.ExecuteAsync("UPDATE dbo.organization SET ent_id=N'ENT-STORE', ref_ent_id=N'REF-STORE' WHERE id=@orgId", new { orgId });
        var dwbh = "DW" + tag; var spid = "SP" + tag; var drug = "DRUG-" + tag;
        await c.ExecuteAsync("INSERT INTO dbo.supplier(org_id,source_system_id,dwbh,name) VALUES(@orgId,@sysId,@dwbh,N'供应商')", new { orgId, sysId, dwbh });
        var supId = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.supplier WHERE org_id=@orgId AND dwbh=@dwbh", new { orgId, dwbh });
        var entVerId = await c.ExecuteScalarAsync<long>("INSERT INTO dbo.platform_enterprise_version(ref_ent_id,ent_id,name) OUTPUT INSERTED.id VALUES(N'REF-SUP',N'ENT-SUP',N'上游企业')");
        await c.ExecuteAsync("INSERT INTO dbo.supplier_mapping(org_id,supplier_id,enterprise_version_id,state) VALUES(@orgId,@supId,@ev,N'MANUAL_CONFIRMED')", new { orgId, supId, ev = entVerId });
        await c.ExecuteAsync("INSERT INTO dbo.product(org_id,source_system_id,spid,name,trace_classification) VALUES(@orgId,@sysId,@spid,N'药品',N'REQUIRED')", new { orgId, sysId, spid });
        var prodId = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.product WHERE org_id=@orgId AND spid=@spid", new { orgId, spid });
        var drugId = await c.ExecuteScalarAsync<long>("INSERT INTO dbo.platform_drug(platform_drug_id,source_kind) OUTPUT INSERTED.id VALUES(@drug,N'fixture')", new { drug });
        await c.ExecuteAsync("INSERT INTO dbo.product_mapping(org_id,product_id,platform_drug_id,state) VALUES(@orgId,@prodId,@drugId,N'MANUAL_CONFIRMED')", new { orgId, prodId, drugId });
        var snapId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.source_snapshot(org_id,source_system_id,source_document_key,document_type,version,business_datetime,completeness_status,content_hash,raw_header)
OUTPUT INSERTED.id VALUES(@orgId,@sysId,@key,N'PURCHASE_IN',1,@biz,N'COMPLETE',@hash,@header)",
            new { orgId, sysId, key = "PI-C-" + tag, biz = new DateTime(2026, 11, 20, 2, 0, 0, DateTimeKind.Utc),
                  hash = new string('0', 64), header = "{\"dwbh\":\"" + dwbh + "\"}" });
        var lineId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.source_line(snapshot_id,source_line_key,product_source_id,quantity_original,unit_original,batch_raw,line_hash,event_kind)
OUTPUT INSERTED.id VALUES(@snapId,N'1',@spid,2,N'盒',N'B2026',@hash,N'PURCHASE_IN')", new { snapId, spid, hash = new string('0', 64) });
        if (secondLine)
            await c.ExecuteAsync(@"
INSERT INTO dbo.source_line(snapshot_id,source_line_key,product_source_id,quantity_original,unit_original,batch_raw,line_hash,event_kind)
VALUES(@snapId,N'2',@spid,1,N'盒',N'B2026',@hash,N'PURCHASE_IN')", new { snapId, spid, hash = new string('0', 64) });
        var billId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upstream_bill(org_id,env,upstream_ref_ent_id,platform_bill_id,bill_no,bill_date,to_ent_id,completeness)
OUTPUT INSERTED.id VALUES(@orgId,N'MOCK',N'REF-SUP',@pb,@no,@date,N'ENT-STORE',N'COMPLETE')",
            new { orgId, pb = "PB-C-" + tag, no = "UP-C-" + tag, date = new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc) });
        var upLineId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upstream_bill_line(bill_id,platform_line_id,platform_drug_id,batch_no,quantity,unit)
OUTPUT INSERTED.id VALUES(@billId,N'L1',@drug,N'B2026',2,N'盒')", new { billId, drug });
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
        foreach (var i in new[] { codeBase, codeBase + 1 })
        {
            var stored = await TraceCodeStore.EnsureAsync(c, tx, Code(i), default);
            await c.ExecuteAsync("INSERT INTO dbo.upstream_bill_code(bill_id,line_id,code_normalized,code_raw,package_level) VALUES(@billId,@lineId,@n,@raw,N'MIN')",
                new { billId, lineId = upLineId, n = stored.Normalized, raw = stored.Raw }, tx);
        }
        await c.ExecuteAsync("INSERT INTO dbo.inbound_match(org_id,env,source_snapshot_id,supplier_dwbh,match_state) VALUES(@orgId,N'MOCK',@snapId,@dwbh,N'UNMATCHED')",
            new { orgId, snapId, dwbh }, tx);
        await tx.CommitAsync();
        return new Seed(orgId, snapId, lineId, upLineId, Code(codeBase), Code(codeBase + 1));
    }

    private InboundTaskFactory NewFactory()
        => new(new SettingsService(_fx.ConnectionString, new DpapiProtector()), new JobQueue(_fx.ConnectionString, new AuditLog()));

    private static AllocationRequest Req(long lineId, long upLineId, string version, params string[] codes)
        => new(version, new[] { new AllocationLineInput(lineId, upLineId, 2m, "1/1", null, codes) });

    private static AllocationRequest ReqQty(long lineId, long upLineId, string version, decimal qty, params string[] codes)
        => new(version, new[] { new AllocationLineInput(lineId, upLineId, qty, "1/1", null, codes) });

    // fieldErrors 元素是 Infrastructure 程序集的匿名类型，跨程序集 dynamic 不可用，用反射读 code
    private static bool HasError(IReadOnlyList<object> errors, string code)
        => errors.Any(e => (string?)e.GetType().GetProperty("code")?.GetValue(e) == code);

    [Fact]
    public async Task Confirm_commits_allocations_and_creates_ready_task()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedAsync(c, "C1", 10);
        var svc = new AllocationService(new AuditLog());
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            await svc.SaveAsync(c, tx, s.OrgId, "MOCK", s.SnapId, Req(s.LineId, s.UpLineId, "1", s.Code1, s.Code2), "tester", default);
            await tx.CommitAsync();
        }
        ConfirmResult r;
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            r = await svc.ConfirmAsync(c, tx, s.OrgId, "MOCK", s.SnapId, "2", "tester", NewFactory(), default);
            await tx.CommitAsync();
        }
        Assert.Equal(3, r.MatchVersion);
        Assert.StartsWith("TF", r.BillCode);
        Assert.False(r.Reused);
        Assert.Equal("Ready", await c.ExecuteScalarAsync<string>("SELECT state FROM dbo.upload_task WHERE id=@id", new { id = r.TaskId }));
        Assert.Equal("PURCHASE_IN_102", await c.ExecuteScalarAsync<string>("SELECT task_type FROM dbo.upload_task WHERE id=@id", new { id = r.TaskId }));
        Assert.Equal("COMMITTED", await c.ExecuteScalarAsync<string>("SELECT state FROM dbo.inbound_allocation WHERE source_line_id=@id", new { id = s.LineId }));
        Assert.Equal(2, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.code_claim WHERE upload_task_id=@id AND claim_state=N'ACTIVE'", new { id = r.TaskId }));
        Assert.Equal("COMPLETE", await c.ExecuteScalarAsync<string>("SELECT match_state FROM dbo.inbound_match WHERE source_snapshot_id=@id", new { id = s.SnapId }));
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=N'upload-dispatch' AND job_state=N'PENDING' AND intent_key=@k",
            new { k = $"part:{await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.upload_part WHERE task_id=@id AND part_index=0", new { id = r.TaskId })}" }));
    }

    [Fact]
    public async Task Confirm_stale_version_conflict()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedAsync(c, "C2", 20);
        var svc = new AllocationService(new AuditLog());
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            await svc.SaveAsync(c, tx, s.OrgId, "MOCK", s.SnapId, Req(s.LineId, s.UpLineId, "1", s.Code1, s.Code2), "tester", default);
            await tx.CommitAsync();
        }
        await using var tx2 = (SqlTransaction)await c.BeginTransactionAsync();
        var ex = await Assert.ThrowsAsync<AllocationConflictException>(() =>
            svc.ConfirmAsync(c, tx2, s.OrgId, "MOCK", s.SnapId, "1", "tester", NewFactory(), default));
        Assert.Equal("VERSION_CONFLICT", ex.ErrorCode);
        await tx2.RollbackAsync();
    }

    [Fact]
    public async Task Confirm_rechecks_eligibility_after_tamper()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedAsync(c, "C3", 30);
        var svc = new AllocationService(new AuditLog());
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            await svc.SaveAsync(c, tx, s.OrgId, "MOCK", s.SnapId, Req(s.LineId, s.UpLineId, "1", s.Code1, s.Code2), "tester", default);
            await tx.CommitAsync();
        }
        await c.ExecuteAsync("UPDATE dbo.upstream_bill_line SET batch_no=N'B9999' WHERE id=@id", new { id = s.UpLineId });
        await using var tx2 = (SqlTransaction)await c.BeginTransactionAsync();
        var ex = await Assert.ThrowsAsync<AllocationValidationException>(() =>
            svc.ConfirmAsync(c, tx2, s.OrgId, "MOCK", s.SnapId, "2", "tester", NewFactory(), default));
        Assert.Equal("ALLOCATION_INVALID", ex.ErrorCode);
        Assert.True(HasError(ex.FieldErrors!, "BATCH_CONFLICT"));
        await tx2.RollbackAsync();
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.upload_task WHERE source_snapshot_id=@id", new { id = s.SnapId }));
        // 复核失败不得落任何效果：回滚后仍停在保存态（分配 RESERVED、匹配版本未推进、占用未绑定任务）
        Assert.Equal("RESERVED", await c.ExecuteScalarAsync<string>("SELECT state FROM dbo.inbound_allocation WHERE source_line_id=@id", new { id = s.LineId }));
        Assert.Equal(2, await c.ExecuteScalarAsync<int>("SELECT version FROM dbo.inbound_match WHERE source_snapshot_id=@id", new { id = s.SnapId }));
        Assert.Equal(2, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.code_claim WHERE claim_state=N'ACTIVE' AND allocation_id IS NOT NULL"));
    }

    // 覆盖不全与数量合计不符同一闸：line2 完全未覆盖（LINE_UNCOVERED），line1 只分配 1 盒而来源 2 盒（QTY_COVERAGE）。
    [Fact]
    public async Task Confirm_partial_coverage_rejected()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedAsync(c, "C4", 40, secondLine: true);
        var svc = new AllocationService(new AuditLog());
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            var save = await svc.SaveAsync(c, tx, s.OrgId, "MOCK", s.SnapId, ReqQty(s.LineId, s.UpLineId, "1", 1m, s.Code1), "tester", default);
            Assert.False(save.AllRequiredCovered);
            await tx.CommitAsync();
        }
        await using var tx2 = (SqlTransaction)await c.BeginTransactionAsync();
        var ex = await Assert.ThrowsAsync<AllocationValidationException>(() =>
            svc.ConfirmAsync(c, tx2, s.OrgId, "MOCK", s.SnapId, "2", "tester", NewFactory(), default));
        Assert.Equal("ALLOCATION_INVALID", ex.ErrorCode);
        Assert.True(HasError(ex.FieldErrors!, "LINE_UNCOVERED"));
        Assert.True(HasError(ex.FieldErrors!, "QTY_COVERAGE"));
        await tx2.RollbackAsync();
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.upload_task WHERE source_snapshot_id=@id", new { id = s.SnapId }));
    }

    [Fact]
    public async Task Confirm_frozen_when_sent()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedAsync(c, "C5", 50);
        var svc = new AllocationService(new AuditLog());
        long taskId;
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            await svc.SaveAsync(c, tx, s.OrgId, "MOCK", s.SnapId, Req(s.LineId, s.UpLineId, "1", s.Code1, s.Code2), "tester", default);
            var r = await svc.ConfirmAsync(c, tx, s.OrgId, "MOCK", s.SnapId, "2", "tester", NewFactory(), default);
            taskId = r.TaskId;
            await tx.CommitAsync();
        }
        // 0006 的 fencing_token 无默认值，必须显式给值
        await c.ExecuteAsync(@"
INSERT INTO dbo.upload_attempt(task_id,attempt_no,attempt_kind,env,fencing_token,started_at)
VALUES(@taskId,1,N'UPLOAD',N'MOCK',0,SYSUTCDATETIME())", new { taskId });
        await using var tx2 = (SqlTransaction)await c.BeginTransactionAsync();
        var ex = await Assert.ThrowsAsync<AllocationConflictException>(() =>
            svc.ConfirmAsync(c, tx2, s.OrgId, "MOCK", s.SnapId, "3", "tester", NewFactory(), default));
        Assert.Equal("FROZEN", ex.ErrorCode);
        await tx2.RollbackAsync();
    }

    [Fact]
    public async Task Confirm_after_cancel_reactivates_same_bill_code()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedAsync(c, "C6", 60);
        var svc = new AllocationService(new AuditLog());
        ConfirmResult first;
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            await svc.SaveAsync(c, tx, s.OrgId, "MOCK", s.SnapId, Req(s.LineId, s.UpLineId, "1", s.Code1, s.Code2), "tester", default);
            first = await svc.ConfirmAsync(c, tx, s.OrgId, "MOCK", s.SnapId, "2", "tester", NewFactory(), default);
            await tx.CommitAsync();
        }
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            await svc.CancelAsync(c, tx, s.OrgId, "MOCK", s.SnapId, null, "tester", "整单取消", default);
            await tx.CommitAsync();
        }
        Assert.Equal("Cancelled", await c.ExecuteScalarAsync<string>("SELECT state FROM dbo.upload_task WHERE id=@id", new { id = first.TaskId }));
        var ver = await c.ExecuteScalarAsync<int>("SELECT version FROM dbo.inbound_match WHERE source_snapshot_id=@id", new { id = s.SnapId });
        ConfirmResult second;
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            await svc.SaveAsync(c, tx, s.OrgId, "MOCK", s.SnapId, Req(s.LineId, s.UpLineId, ver.ToString(), s.Code1, s.Code2), "tester", default);
            // 同一连接上已有挂起事务时命令必须显式带事务（brief 原稿缺 tx 会抛 InvalidOperationException）
            var ver2 = await c.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT version FROM dbo.inbound_match WHERE source_snapshot_id=@id", new { id = s.SnapId }, tx));
            second = await svc.ConfirmAsync(c, tx, s.OrgId, "MOCK", s.SnapId, ver2.ToString(), "tester", NewFactory(), default);
            await tx.CommitAsync();
        }
        Assert.Equal(first.TaskId, second.TaskId);
        Assert.Equal(first.BillCode, second.BillCode);
        Assert.Equal("Ready", await c.ExecuteScalarAsync<string>("SELECT state FROM dbo.upload_task WHERE id=@id", new { id = second.TaskId }));
    }
}