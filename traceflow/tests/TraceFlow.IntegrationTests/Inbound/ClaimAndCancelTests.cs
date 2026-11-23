using System.Runtime.Versioning; // brief 缺失此 using（DpapiProtector 的 SupportedOSPlatform 标注所需）；与 SettingsServiceTests 同模式（T6 偏差 1）
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
public class ClaimAndCancelTests : IClassFixture<Sql2008Fixture>
{
    private readonly Sql2008Fixture _fx;
    public ClaimAndCancelTests(Sql2008Fixture fx) => _fx = fx;

    private InboundTaskFactory NewFactory() => new(new SettingsService(_fx.ConnectionString, new DpapiProtector()),
        new JobQueue(_fx.ConnectionString, new AuditLog()));

    private sealed record Seed(long OrgId, long SnapshotId, long LineId, long UpLineId, long CodeId, long AllocId, string DocKey);

    private async Task<Seed> SeedInboundAsync(SqlConnection c, string docKey, string code)
    {
        var (orgId, sysId) = await TestSeed.EnsureAsync(c);
        var billId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upstream_bill(org_id,env,upstream_ref_ent_id,platform_bill_id,completeness)
OUTPUT INSERTED.id VALUES(@orgId,N'MOCK',N'REF-SUP',@bill,N'COMPLETE')", new { orgId, bill = "BILL-" + docKey });
        var upLineId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upstream_bill_line(bill_id,platform_line_id,platform_drug_id,batch_no,quantity,unit)
OUTPUT INSERTED.id VALUES(@billId,N'L1',N'DRUG-1',N'B2026',2,N'盒')", new { billId });
        var snapId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.source_snapshot(org_id,source_system_id,source_document_key,document_type,version,completeness_status,content_hash)
OUTPUT INSERTED.id VALUES(@orgId,@sysId,@key,N'PURCHASE_IN',1,N'COMPLETE',@hash)",
            new { orgId, sysId, key = docKey, hash = new string('0', 64) });
        var lineId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.source_line(snapshot_id,source_line_key,product_source_id,quantity_original,unit_original,batch_raw,line_hash,event_kind)
OUTPUT INSERTED.id VALUES(@snapId,N'1',N'SP0001',2,N'盒',N'B2026',@hash,N'PURCHASE_IN')",
            new { snapId, hash = new string('0', 64) });
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
        var stored = await TraceCodeStore.EnsureAsync(c, tx, code, default);
        var allocId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.inbound_allocation(org_id,env,source_line_id,upstream_line_id,quantity,unit_ratio_numerator,unit_ratio_denominator,state,created_by)
OUTPUT INSERTED.id VALUES(@orgId,N'MOCK',@lineId,@upLineId,2,1,1,N'RESERVED',N'test')",
            new { orgId, lineId, upLineId }, tx);
        await c.ExecuteAsync("INSERT INTO dbo.allocation_code(allocation_id,code_id) VALUES(@allocId,@codeId)",
            new { allocId, codeId = stored.Id }, tx);
        await CodeClaimWriter.InsertActiveAsync(c, tx, orgId, "MOCK", stored.Id, "PURCHASE_IN", allocId, null, default);
        await c.ExecuteAsync(@"
INSERT INTO dbo.inbound_match(org_id,env,source_snapshot_id,supplier_dwbh,match_state)
VALUES(@orgId,N'MOCK',@snapId,N'SUP001',N'COMPLETE')", new { orgId, snapId }, tx);
        await tx.CommitAsync();
        return new Seed(orgId, snapId, lineId, upLineId, stored.Id, allocId, docKey);
    }

    private static InboundTaskContext Ctx(Seed s, string code) => new(s.OrgId, "MOCK", s.SnapshotId, s.DocKey,
        new string('0', 64), new DateTime(2026, 11, 20, 6, 0, 0, DateTimeKind.Utc), "SUP001", "REF-SUP", "ENT-SUP",
        new[] { new AllocatedLine(s.AllocId, s.LineId, "1", "SP0001", "DRUG-1", "B2026", 2m, "盒",
            new[] { new StoredCodeRef(s.CodeId, code) }) },
        new[] { "product:SP0001:1", "supplier:SUP001:1" }, "test");

    [Fact]
    public async Task Second_active_claim_same_code_and_scope_is_rejected()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedInboundAsync(c, "PI-CLAIM-1", "81234567890123456701");
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            var ex = await Assert.ThrowsAsync<AllocationConflictException>(() =>
                CodeClaimWriter.InsertActiveAsync(c, tx, s.OrgId, "MOCK", s.CodeId, "PURCHASE_IN", null, null, default));
            Assert.Equal("CODE_CONTENDED", ex.ErrorCode);
            await tx.RollbackAsync();
        }
        // 释放后可再占用：过滤唯一索引只约束 ACTIVE（DB-05）
        await c.ExecuteAsync("UPDATE dbo.code_claim SET claim_state=N'RELEASED', released_at=SYSUTCDATETIME() WHERE code_id=@id AND claim_state=N'ACTIVE'", new { id = s.CodeId });
        await using var tx2 = (SqlTransaction)await c.BeginTransactionAsync();
        var id = await CodeClaimWriter.InsertActiveAsync(c, tx2, s.OrgId, "MOCK", s.CodeId, "PURCHASE_IN", null, null, default);
        await tx2.CommitAsync();
        Assert.True(id > 0);
    }

    [Fact]
    public async Task Cancel_pre_send_rolls_back_everything_atomically()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedInboundAsync(c, "PI-CANCEL-1", "81234567890123456702");
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            await NewFactory().CreateAsync(c, tx, Ctx(s, "81234567890123456702"), default);
            await tx.CommitAsync();
        }
        var verBefore = await c.ExecuteScalarAsync<int>("SELECT version FROM dbo.inbound_match WHERE source_snapshot_id=@id", new { id = s.SnapshotId });
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            var ok = await InboundCancelService.CancelPreSendAsync(c, tx, s.OrgId, "MOCK", s.SnapshotId, "tester", "上游单据作废", "LIVE", default);
            Assert.True(ok);
            await tx.CommitAsync();
        }
        var taskId = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.upload_task WHERE source_snapshot_id=@id", new { id = s.SnapshotId });
        var partId = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.upload_part WHERE task_id=@id", new { id = taskId });
        Assert.Equal("Cancelled", await c.ExecuteScalarAsync<string>("SELECT state FROM dbo.upload_task WHERE id=@id", new { id = taskId }));
        Assert.Equal("Cancelled", await c.ExecuteScalarAsync<string>("SELECT state FROM dbo.upload_part WHERE id=@id", new { id = partId }));
        Assert.Equal("CANCELLED", await c.ExecuteScalarAsync<string>("SELECT job_state FROM dbo.outbox_job WHERE job_kind=N'upload-dispatch' AND intent_key=@k", new { k = $"part:{partId}" }));
        Assert.Equal("CANCELLED", await c.ExecuteScalarAsync<string>("SELECT state FROM dbo.inbound_allocation WHERE id=@id", new { id = s.AllocId }));
        Assert.Equal("RELEASED", await c.ExecuteScalarAsync<string>("SELECT claim_state FROM dbo.code_claim WHERE allocation_id=@id", new { id = s.AllocId }));
        Assert.Equal("UNMATCHED", await c.ExecuteScalarAsync<string>("SELECT match_state FROM dbo.inbound_match WHERE source_snapshot_id=@id", new { id = s.SnapshotId }));
        Assert.Equal(verBefore + 1, await c.ExecuteScalarAsync<int>("SELECT version FROM dbo.inbound_match WHERE source_snapshot_id=@id", new { id = s.SnapshotId }));
        Assert.True(await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.audit_log WHERE action=N'inbound.cancel' AND object_id=@oid AND env=N'LIVE'", new { oid = taskId.ToString() }) >= 1);
    }

    [Fact]
    public async Task Cancel_refused_after_upload_attempt()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedInboundAsync(c, "PI-CANCEL-2", "81234567890123456703");
        long taskId;
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            var r = await NewFactory().CreateAsync(c, tx, Ctx(s, "81234567890123456703"), default);
            taskId = r.TaskId;
            await c.ExecuteAsync("INSERT INTO dbo.upload_attempt(task_id,attempt_no,attempt_kind,env,fencing_token) VALUES(@id,1,N'UPLOAD',N'MOCK',1)", new { id = taskId }, tx);
            await tx.CommitAsync();
        }
        await using var tx2 = (SqlTransaction)await c.BeginTransactionAsync();
        var ex = await Assert.ThrowsAsync<AllocationConflictException>(() =>
            InboundCancelService.CancelPreSendAsync(c, tx2, s.OrgId, "MOCK", s.SnapshotId, "tester", "取消", "LIVE", default));
        Assert.Equal("CANCEL_REFUSED", ex.ErrorCode);
        await tx2.RollbackAsync();
        Assert.Equal("Ready", await c.ExecuteScalarAsync<string>("SELECT state FROM dbo.upload_task WHERE id=@id", new { id = taskId }));
    }

    [Fact]
    public async Task Cancel_refused_when_dispatch_job_running()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedInboundAsync(c, "PI-CANCEL-3", "81234567890123456704");
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            var r = await NewFactory().CreateAsync(c, tx, Ctx(s, "81234567890123456704"), default);
            await c.ExecuteAsync("UPDATE dbo.outbox_job SET job_state=N'RUNNING' WHERE job_kind=N'upload-dispatch' AND intent_key=@k", new { k = $"part:{r.PartId}" }, tx);
            await tx.CommitAsync();
        }
        await using var tx2 = (SqlTransaction)await c.BeginTransactionAsync();
        var ex = await Assert.ThrowsAsync<AllocationConflictException>(() =>
            InboundCancelService.CancelPreSendAsync(c, tx2, s.OrgId, "MOCK", s.SnapshotId, "tester", "取消", "LIVE", default));
        Assert.Equal("CANCEL_REFUSED", ex.ErrorCode);
        await tx2.RollbackAsync();
    }

    [Fact]
    public async Task Cancel_refused_when_task_dispatching()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedInboundAsync(c, "PI-CANCEL-4", "81234567890123456705");
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            var r = await NewFactory().CreateAsync(c, tx, Ctx(s, "81234567890123456705"), default);
            await c.ExecuteAsync("UPDATE dbo.upload_task SET state=N'Dispatching' WHERE id=@id", new { id = r.TaskId }, tx);
            await tx.CommitAsync();
        }
        await using var tx2 = (SqlTransaction)await c.BeginTransactionAsync();
        var ex = await Assert.ThrowsAsync<AllocationConflictException>(() =>
            InboundCancelService.CancelPreSendAsync(c, tx2, s.OrgId, "MOCK", s.SnapshotId, "tester", "取消", "LIVE", default));
        Assert.Equal("CANCEL_REFUSED", ex.ErrorCode);
        await tx2.RollbackAsync();
    }

    [Fact]
    public async Task Cancel_without_task_returns_false()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedInboundAsync(c, "PI-CANCEL-5", "81234567890123456706");
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
        Assert.False(await InboundCancelService.CancelPreSendAsync(c, tx, s.OrgId, "MOCK", s.SnapshotId, "tester", "取消", "LIVE", default));
        await tx.RollbackAsync();
    }
}
