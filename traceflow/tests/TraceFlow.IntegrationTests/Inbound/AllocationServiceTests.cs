using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Infrastructure.Codes;
using TraceFlow.Infrastructure.Inbound;
using TraceFlow.Infrastructure.Security;
using TraceFlow.IntegrationTests.Sources;
using Xunit;

namespace TraceFlow.IntegrationTests.Inbound;

// 每测试独立库：种子每个测试都插入 platform_enterprise_version(REF-SUP,ENT-SUP) 与 active supplier_mapping，
// 二者有全局唯一约束（UQ_ent_version / UX_smap_active_platform），类共享库从第 2 个测试起必撞约束（与 InboundMatchTests 同模式）。
public class AllocationServiceTests : IAsyncLifetime
{
    private Sql2008Fixture _fx = null!;
    public async Task InitializeAsync() { _fx = new Sql2008Fixture(); await _fx.InitializeAsync(); }
    public Task DisposeAsync() => _fx.DisposeAsync();

    private sealed record Seed(long OrgId, long SnapId, long LineId, long UpLineId, string Code1, string Code2);

    private static string Code(int i) => "812345678901234567" + i.ToString("D2");

    private async Task<Seed> SeedAsync(SqlConnection c, string tag, int codeBase)
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
            new { orgId, sysId, key = "PI-A-" + tag, biz = new DateTime(2026, 11, 20, 2, 0, 0, DateTimeKind.Utc),
                  hash = new string('0', 64), header = "{\"dwbh\":\"" + dwbh + "\"}" });
        var lineId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.source_line(snapshot_id,source_line_key,product_source_id,quantity_original,unit_original,batch_raw,line_hash,event_kind)
OUTPUT INSERTED.id VALUES(@snapId,N'1',@spid,2,N'盒',N'B2026',@hash,N'PURCHASE_IN')", new { snapId, spid, hash = new string('0', 64) });
        var billId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upstream_bill(org_id,env,upstream_ref_ent_id,platform_bill_id,bill_no,bill_date,to_ent_id,completeness)
OUTPUT INSERTED.id VALUES(@orgId,N'MOCK',N'REF-SUP',@pb,@no,@date,N'ENT-STORE',N'COMPLETE')",
            new { orgId, pb = "PB-A-" + tag, no = "UP-A-" + tag, date = new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc) });
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

    private static AllocationRequest Req(long lineId, long upLineId, string version, decimal qty, string ratio, string? evidence, params string[] codes)
        => new(version, new[] { new AllocationLineInput(lineId, upLineId, qty, ratio, evidence, codes) });

    private static AllocationService NewService() => new(new AuditLog());

    // fieldErrors 元素是 Infrastructure 程序集的匿名类型，跨程序集 dynamic 不可用，用反射读 code
    private static bool HasError(IReadOnlyList<object> errors, string code)
        => errors.Any(e => (string?)e.GetType().GetProperty("code")?.GetValue(e) == code);

    [Fact]
    public async Task Save_valid_reserves_claims_and_completes_match()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedAsync(c, "A1", 10);
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
        var r = await NewService().SaveAsync(c, tx, s.OrgId, "MOCK", s.SnapId, Req(s.LineId, s.UpLineId, "1", 2m, "1/1", null, s.Code1, s.Code2), "tester", default);
        await tx.CommitAsync();
        Assert.Equal(1, r.AllocationsSaved);
        Assert.True(r.AllRequiredCovered);
        Assert.Equal(2, r.MatchVersion);
        Assert.Equal("RESERVED", await c.ExecuteScalarAsync<string>("SELECT state FROM dbo.inbound_allocation WHERE source_line_id=@id", new { id = s.LineId }));
        Assert.Equal(2, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.code_claim cc JOIN dbo.inbound_allocation a ON a.id=cc.allocation_id WHERE a.source_line_id=@id AND cc.claim_state=N'ACTIVE'", new { id = s.LineId }));
        Assert.Equal("COMPLETE", await c.ExecuteScalarAsync<string>("SELECT match_state FROM dbo.inbound_match WHERE source_snapshot_id=@id", new { id = s.SnapId }));
    }

    [Fact]
    public async Task Save_stale_version_conflict()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedAsync(c, "A2", 20);
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
        var ex = await Assert.ThrowsAsync<AllocationConflictException>(() =>
            NewService().SaveAsync(c, tx, s.OrgId, "MOCK", s.SnapId, Req(s.LineId, s.UpLineId, "99", 2m, "1/1", null, s.Code1, s.Code2), "tester", default));
        Assert.Equal("VERSION_CONFLICT", ex.ErrorCode);
        await tx.RollbackAsync();
    }

    [Fact]
    public async Task Save_big_code_blocked()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedAsync(c, "A3", 30);
        await c.ExecuteAsync("UPDATE dbo.upstream_bill_code SET package_level=N'BIG' WHERE code_normalized=@n", new { n = s.Code1 });
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
        var ex = await Assert.ThrowsAsync<AllocationValidationException>(() =>
            NewService().SaveAsync(c, tx, s.OrgId, "MOCK", s.SnapId, Req(s.LineId, s.UpLineId, "1", 2m, "1/1", null, s.Code1, s.Code2), "tester", default));
        Assert.Equal("ALLOCATION_INVALID", ex.ErrorCode);
        Assert.True(HasError(ex.FieldErrors!, "BIG_CODE_BLOCKED"));
        await tx.RollbackAsync();
    }

    [Fact]
    public async Task Save_conversion_inexact_rejected()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedAsync(c, "A4", 40);
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
        var ex = await Assert.ThrowsAsync<AllocationValidationException>(() =>
            NewService().SaveAsync(c, tx, s.OrgId, "MOCK", s.SnapId, Req(s.LineId, s.UpLineId, "1", 2m, "1/3", null, s.Code1, s.Code2), "tester", default));
        Assert.True(HasError(ex.FieldErrors!, "CONVERSION_INEXACT"));
        await tx.RollbackAsync();
    }

    [Fact]
    public async Task Save_conversion_count_mismatch_rejected()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedAsync(c, "A5", 50);
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
        var ex = await Assert.ThrowsAsync<AllocationValidationException>(() =>
            NewService().SaveAsync(c, tx, s.OrgId, "MOCK", s.SnapId, Req(s.LineId, s.UpLineId, "1", 2m, "3/2", null, s.Code1, s.Code2), "tester", default));
        Assert.True(HasError(ex.FieldErrors!, "CONVERSION_INEXACT"));
        await tx.RollbackAsync();
    }

    [Fact]
    public async Task Save_nonunit_ratio_requires_evidence()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedAsync(c, "A6", 60);
        await c.ExecuteAsync("UPDATE dbo.source_line SET quantity_original=1 WHERE id=@id", new { id = s.LineId });
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            var ex = await Assert.ThrowsAsync<AllocationValidationException>(() =>
                NewService().SaveAsync(c, tx, s.OrgId, "MOCK", s.SnapId, Req(s.LineId, s.UpLineId, "1", 1m, "2/1", null, s.Code1, s.Code2), "tester", default));
            Assert.True(HasError(ex.FieldErrors!, "CONVERSION_EVIDENCE_REQUIRED"));
            await tx.RollbackAsync();
        }
        await using var tx2 = (SqlTransaction)await c.BeginTransactionAsync();
        var r = await NewService().SaveAsync(c, tx2, s.OrgId, "MOCK", s.SnapId, Req(s.LineId, s.UpLineId, "1", 1m, "2/1", "1 箱=2 盒，随货同行单编号 X", s.Code1, s.Code2), "tester", default);
        await tx2.CommitAsync();
        Assert.True(r.AllRequiredCovered);
    }

    [Fact]
    public async Task Save_code_contended_409()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedAsync(c, "A7", 70);
        var codeId = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.trace_code WHERE code_normalized=@n", new { n = s.Code1 });
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            await CodeClaimWriter.InsertActiveAsync(c, tx, s.OrgId, "MOCK", codeId, "PURCHASE_IN", null, null, default);
            await tx.CommitAsync();
        }
        await using var tx2 = (SqlTransaction)await c.BeginTransactionAsync();
        var ex = await Assert.ThrowsAsync<AllocationConflictException>(() =>
            NewService().SaveAsync(c, tx2, s.OrgId, "MOCK", s.SnapId, Req(s.LineId, s.UpLineId, "1", 2m, "1/1", null, s.Code1, s.Code2), "tester", default));
        Assert.Equal("CODE_CONTENDED", ex.ErrorCode);
        await tx2.RollbackAsync();
    }

    [Fact]
    public async Task Save_drug_conflict_rejected()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedAsync(c, "A8", 80);
        await c.ExecuteAsync("UPDATE dbo.upstream_bill_line SET platform_drug_id=N'OTHER-DRUG' WHERE id=@id", new { id = s.UpLineId });
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
        var ex = await Assert.ThrowsAsync<AllocationValidationException>(() =>
            NewService().SaveAsync(c, tx, s.OrgId, "MOCK", s.SnapId, Req(s.LineId, s.UpLineId, "1", 2m, "1/1", null, s.Code1, s.Code2), "tester", default));
        Assert.True(HasError(ex.FieldErrors!, "DRUG_CONFLICT"));
        await tx.RollbackAsync();
    }

    [Fact]
    public async Task Cancel_reserved_releases_claims_and_unmatches()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedAsync(c, "A9", 90);
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            await NewService().SaveAsync(c, tx, s.OrgId, "MOCK", s.SnapId, Req(s.LineId, s.UpLineId, "1", 2m, "1/1", null, s.Code1, s.Code2), "tester", default);
            await tx.CommitAsync();
        }
        var allocId = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.inbound_allocation WHERE source_line_id=@id", new { id = s.LineId });
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            var n = await NewService().CancelAsync(c, tx, s.OrgId, "MOCK", s.SnapId, allocId, "tester", "取消补配", default);
            Assert.Equal(1, n);
            await tx.CommitAsync();
        }
        Assert.Equal("CANCELLED", await c.ExecuteScalarAsync<string>("SELECT state FROM dbo.inbound_allocation WHERE id=@id", new { id = allocId }));
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.code_claim WHERE allocation_id=@id AND claim_state=N'ACTIVE'", new { id = allocId }));
        Assert.Equal("UNMATCHED", await c.ExecuteScalarAsync<string>("SELECT match_state FROM dbo.inbound_match WHERE source_snapshot_id=@id", new { id = s.SnapId }));
    }

    [Fact]
    public async Task Save_replaces_previous_reserved()
    {
        await using var c = await _fx.OpenAsync();
        // codeBase=10（非 brief 的 100）：Code(i) 为 18 位前缀+2 位序号，100 会产出 21 位码而非 Supported8Prefix20。
        var s = await SeedAsync(c, "A10", 10);
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            await NewService().SaveAsync(c, tx, s.OrgId, "MOCK", s.SnapId, Req(s.LineId, s.UpLineId, "1", 2m, "1/1", null, s.Code1, s.Code2), "tester", default);
            await tx.CommitAsync();
        }
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            var r = await NewService().SaveAsync(c, tx, s.OrgId, "MOCK", s.SnapId, Req(s.LineId, s.UpLineId, "2", 2m, "1/1", null, s.Code1, s.Code2), "tester", default);
            Assert.Equal(3, r.MatchVersion);
            await tx.CommitAsync();
        }
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.inbound_allocation WHERE source_line_id=@id AND state=N'CANCELLED'", new { id = s.LineId }));
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.inbound_allocation WHERE source_line_id=@id AND state=N'RESERVED'", new { id = s.LineId }));
        Assert.Equal(2, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.code_claim cc JOIN dbo.inbound_allocation a ON a.id=cc.allocation_id WHERE a.source_line_id=@id AND cc.claim_state=N'ACTIVE'", new { id = s.LineId }));
    }

    // 快照一致性：发送前任务挂靠 v1，却用 v2（同文档新版本）保存 → 409 VERSION_CONFLICT，且不得释放 v1 的分配/占用。
    [Fact]
    public async Task Save_rejects_task_from_other_snapshot()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedAsync(c, "A11", 10);

        // v1：正常保存流程落分配 + 码占用；随后挂 Blocked 发送前任务并转 COMMITTED（T10 确认后的形态）。
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            await NewService().SaveAsync(c, tx, s.OrgId, "MOCK", s.SnapId, Req(s.LineId, s.UpLineId, "1", 1m, "1/1", null, s.Code1), "tester", default);
            await tx.CommitAsync();
        }
        var v1AllocId = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.inbound_allocation WHERE source_line_id=@id", new { id = s.LineId });
        await c.ExecuteAsync("UPDATE dbo.inbound_allocation SET state=N'COMMITTED' WHERE id=@id", new { id = v1AllocId });
        var docKey = await c.ExecuteScalarAsync<string>("SELECT source_document_key FROM dbo.source_snapshot WHERE id=@id", new { id = s.SnapId });
        var taskId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upload_task(org_id,env,event_key,task_type,source_snapshot_id,source_hash,payload_hash,state)
OUTPUT INSERTED.id VALUES(@orgId,N'MOCK',@key,N'PURCHASE_IN_102',@snapId,@hash,@hash,N'Blocked')",
            new { s.OrgId, key = "KSOA:" + docKey + ":PURCHASE_IN", snapId = s.SnapId, hash = new string('0', 64) });
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.code_claim WHERE allocation_id=@id AND claim_state=N'ACTIVE'", new { id = v1AllocId }));

        // v2：同一 source_document_key 的新版本快照（自带来源行与匹配记录）。
        var sysId = await c.ExecuteScalarAsync<long>("SELECT source_system_id FROM dbo.source_snapshot WHERE id=@id", new { id = s.SnapId });
        var header = await c.ExecuteScalarAsync<string>("SELECT raw_header FROM dbo.source_snapshot WHERE id=@id", new { id = s.SnapId });
        var spid = await c.ExecuteScalarAsync<string>("SELECT product_source_id FROM dbo.source_line WHERE id=@id", new { id = s.LineId });
        var v2SnapId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.source_snapshot(org_id,source_system_id,source_document_key,document_type,version,business_datetime,completeness_status,content_hash,raw_header)
OUTPUT INSERTED.id VALUES(@orgId,@sysId,@docKey,N'PURCHASE_IN',2,@biz,N'COMPLETE',@hash,@header)",
            new { s.OrgId, sysId, docKey, biz = new DateTime(2026, 11, 20, 3, 0, 0, DateTimeKind.Utc), hash = new string('1', 64), header });
        var v2LineId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.source_line(snapshot_id,source_line_key,product_source_id,quantity_original,unit_original,batch_raw,line_hash,event_kind)
OUTPUT INSERTED.id VALUES(@v2SnapId,N'1',@spid,1,N'盒',N'B2026',@hash,N'PURCHASE_IN')",
            new { v2SnapId, spid, hash = new string('1', 64) });
        await c.ExecuteAsync("INSERT INTO dbo.inbound_match(org_id,env,source_snapshot_id,supplier_dwbh,match_state) VALUES(@orgId,N'MOCK',@v2SnapId,N'DWA11',N'UNMATCHED')",
            new { s.OrgId, v2SnapId });

        // 保存 v2：VERSION_CONFLICT 拒绝，不释放 v1（任务所属快照）的分配/占用，也不落 v2 新分配。
        await using var tx2 = (SqlTransaction)await c.BeginTransactionAsync();
        var ex = await Assert.ThrowsAsync<AllocationConflictException>(() =>
            NewService().SaveAsync(c, tx2, s.OrgId, "MOCK", v2SnapId, Req(v2LineId, s.UpLineId, "1", 1m, "1/1", null, s.Code2), "tester", default));
        Assert.Equal("VERSION_CONFLICT", ex.ErrorCode);
        await tx2.RollbackAsync();

        Assert.Equal(0, await c.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM dbo.inbound_allocation a JOIN dbo.source_line l ON l.id=a.source_line_id WHERE l.snapshot_id=@id", new { id = v2SnapId }));
        Assert.Equal("UNMATCHED", await c.ExecuteScalarAsync<string>("SELECT match_state FROM dbo.inbound_match WHERE source_snapshot_id=@id", new { id = v2SnapId }));
        Assert.Equal("COMMITTED", await c.ExecuteScalarAsync<string>("SELECT state FROM dbo.inbound_allocation WHERE id=@id", new { id = v1AllocId }));
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.code_claim WHERE allocation_id=@id AND claim_state=N'ACTIVE'", new { id = v1AllocId }));
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.allocation_code WHERE allocation_id=@id", new { id = v1AllocId }));
        Assert.Equal("Blocked", await c.ExecuteScalarAsync<string>("SELECT state FROM dbo.upload_task WHERE id=@id", new { id = taskId }));
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.upload_task WHERE event_key=@key", new { key = "KSOA:" + docKey + ":PURCHASE_IN" }));
    }
}
