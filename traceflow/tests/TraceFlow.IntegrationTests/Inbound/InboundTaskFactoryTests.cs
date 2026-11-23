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
public class InboundTaskFactoryTests : IClassFixture<Sql2008Fixture>
{
    private readonly Sql2008Fixture _fx;
    public InboundTaskFactoryTests(Sql2008Fixture fx) => _fx = fx;

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
    public async Task Create_atomic_rollback_leaves_nothing()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedInboundAsync(c, "PI-FACT-1", "81234567890123456711");
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            var r = await NewFactory().CreateAsync(c, tx, Ctx(s, "81234567890123456711"), default);
            Assert.True(r.TaskId > 0);
            await tx.RollbackAsync();
        }
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.upload_task WHERE event_key=@k", new { k = $"KSOA:{s.DocKey}:PURCHASE_IN" }));
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.code_event WHERE source_snapshot_id=@id", new { id = s.SnapshotId }));
        Assert.Null(await c.ExecuteScalarAsync<long?>("SELECT upload_task_id FROM dbo.code_claim WHERE allocation_id=@id", new { id = s.AllocId }));
    }

    [Fact]
    public async Task Create_writes_task_part_outbox_event_and_binds_claim()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedInboundAsync(c, "PI-FACT-2", "81234567890123456712");
        TaskCreationResult r;
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            r = await NewFactory().CreateAsync(c, tx, Ctx(s, "81234567890123456712"), default);
            await tx.CommitAsync();
        }
        Assert.False(r.Reused);
        Assert.StartsWith("TF", r.BillCode);
        Assert.Equal("Ready", await c.ExecuteScalarAsync<string>("SELECT state FROM dbo.upload_task WHERE id=@id", new { id = r.TaskId }));
        Assert.Equal("COMPLETE", await c.ExecuteScalarAsync<string>("SELECT match_state FROM dbo.upload_task WHERE id=@id", new { id = r.TaskId }));
        Assert.Equal(64, (await c.ExecuteScalarAsync<string>("SELECT mapping_version_snapshot FROM dbo.upload_task WHERE id=@id", new { id = r.TaskId }))!.Length);
        Assert.Equal("PENDING", await c.ExecuteScalarAsync<string>("SELECT job_state FROM dbo.outbox_job WHERE job_kind=N'upload-dispatch' AND intent_key=@k", new { k = $"part:{r.PartId}" }));
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.code_event WHERE source_snapshot_id=@id AND event_type=N'PURCHASE_IN' AND direction=N'IN'", new { id = s.SnapshotId }));
        Assert.Equal(r.TaskId, await c.ExecuteScalarAsync<long>("SELECT upload_task_id FROM dbo.code_claim WHERE allocation_id=@id", new { id = s.AllocId }));
    }

    [Fact]
    public async Task Create_reuses_existing_task_unchanged()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedInboundAsync(c, "PI-FACT-3", "81234567890123456713");
        TaskCreationResult r1, r2;
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            r1 = await NewFactory().CreateAsync(c, tx, Ctx(s, "81234567890123456713"), default);
            await tx.CommitAsync();
        }
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            r2 = await NewFactory().CreateAsync(c, tx, Ctx(s, "81234567890123456713"), default);
            await tx.CommitAsync();
        }
        Assert.True(r2.Reused);
        Assert.Equal(r1.TaskId, r2.TaskId);
        Assert.Equal(r1.BillCode, r2.BillCode);
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT version FROM dbo.upload_task WHERE id=@id", new { id = r1.TaskId }));
    }

    [Fact]
    public async Task Cancelled_task_reactivates_same_row_with_stable_bill_code()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedInboundAsync(c, "PI-FACT-4", "81234567890123456714");
        TaskCreationResult r1, r2;
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            r1 = await NewFactory().CreateAsync(c, tx, Ctx(s, "81234567890123456714"), default);
            await tx.CommitAsync();
        }
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            Assert.True(await InboundCancelService.CancelPreSendAsync(c, tx, s.OrgId, "MOCK", s.SnapshotId, "tester", "上游单据作废", "LIVE", default));
            await tx.CommitAsync();
        }
        // 重新占用（取消已释放旧占用；模拟 T9 重新保存分配后的新 ACTIVE 占用）
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            await c.ExecuteAsync("UPDATE dbo.inbound_allocation SET state=N'RESERVED', version=version+1 WHERE id=@id", new { id = s.AllocId }, tx);
            await CodeClaimWriter.InsertActiveAsync(c, tx, s.OrgId, "MOCK", s.CodeId, "PURCHASE_IN", s.AllocId, null, default);
            r2 = await NewFactory().CreateAsync(c, tx, Ctx(s, "81234567890123456714"), default);
            await tx.CommitAsync();
        }
        Assert.False(r2.Reused);
        Assert.Equal(r1.TaskId, r2.TaskId);
        Assert.Equal(r1.BillCode, r2.BillCode); // IDEM-02：复活不换单号
        Assert.Equal("Ready", await c.ExecuteScalarAsync<string>("SELECT state FROM dbo.upload_task WHERE id=@id", new { id = r1.TaskId }));
        Assert.Equal(3, await c.ExecuteScalarAsync<int>("SELECT version FROM dbo.upload_task WHERE id=@id", new { id = r1.TaskId }));
        Assert.Equal("PENDING", await c.ExecuteScalarAsync<string>("SELECT job_state FROM dbo.outbox_job WHERE job_kind=N'upload-dispatch' AND intent_key=@k", new { k = $"part:{r2.PartId}" }));
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT attempt_count FROM dbo.outbox_job WHERE job_kind=N'upload-dispatch' AND intent_key=@k", new { k = $"part:{r2.PartId}" }));
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.code_event WHERE source_snapshot_id=@id", new { id = s.SnapshotId })); // 历史不可变，不重复
        Assert.Equal(r1.TaskId, await c.ExecuteScalarAsync<long>("SELECT upload_task_id FROM dbo.code_claim WHERE allocation_id=@id AND claim_state=N'ACTIVE'", new { id = s.AllocId }));
    }
}

[SupportedOSPlatform("windows")] // DpapiProtector 仅 Windows（CA1416）
// 每测试独立库（IAsyncLifetime）：本类会提交 top.billMaxLength/top.billPrefix，类共享库会与
// Default_contract_is_TF_32 产生顺序依赖（与 SettingsServiceTests「每测试独立库」同模式）。
public class BillContractTests : IAsyncLifetime
{
    private readonly Sql2008Fixture _fx = new();
    public async Task InitializeAsync() => await _fx.InitializeAsync();
    public async Task DisposeAsync() => await _fx.DisposeAsync();

    [Fact]
    public async Task Default_contract_is_TF_32()
    {
        var settings = new SettingsService(_fx.ConnectionString, new DpapiProtector());
        var contract = await settings.GetBillContractAsync(default);
        Assert.Equal("TF", contract.Prefix);
        Assert.Equal(32, contract.MaxLength);
    }

    [Fact]
    public async Task Prefix_setting_rejects_non_string()
    {
        await using var c = await _fx.OpenAsync();
        var settings = new SettingsService(_fx.ConnectionString, new DpapiProtector());
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
        await Assert.ThrowsAsync<SettingValidationException>(() =>
            settings.UpdateAsync(c, tx, "top.billPrefix", "123", null, "0", "测试非字符串拒绝", "test", default));
        await tx.RollbackAsync();
    }

    [Fact]
    public async Task Over_length_bill_code_throws_without_truncation()
    {
        await using var c = await _fx.OpenAsync();
        var settings = new SettingsService(_fx.ConnectionString, new DpapiProtector());
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            // 与 brief 的最小偏差：仅设 billMaxLength=8 永不可能超长——identity 从 1 起，bill_code = "TF" + Base35(1) 只有 3 字符；
            // 要真实触发「超长」必须让序号无法补齐，故同时把前缀设为超过上限的值（bill_code 长度 14 > 8）。
            await settings.UpdateAsync(c, tx, "top.billPrefix", "\"TFOVERLENGTH\"", null, "0", "测试超长拒绝", "test", default);
            await settings.UpdateAsync(c, tx, "top.billMaxLength", "8", null, "0", "测试超长拒绝", "test", default);
            await tx.CommitAsync();
        }
        var (orgId, sysId) = await TestSeed.EnsureAsync(c);
        var snapId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.source_snapshot(org_id,source_system_id,source_document_key,document_type,version,completeness_status,content_hash)
OUTPUT INSERTED.id VALUES(@orgId,@sysId,N'PI-LEN-1',N'PURCHASE_IN',1,N'COMPLETE',@hash)",
            new { orgId, sysId, hash = new string('0', 64) });
        var lineId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.source_line(snapshot_id,source_line_key,product_source_id,quantity_original,line_hash,event_kind)
OUTPUT INSERTED.id VALUES(@snapId,N'1',N'SP0001',1,@hash,N'PURCHASE_IN')", new { snapId, hash = new string('0', 64) });
        var billId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upstream_bill(org_id,env,upstream_ref_ent_id,platform_bill_id,completeness)
OUTPUT INSERTED.id VALUES(@orgId,N'MOCK',N'REF-SUP',N'BILL-LEN-1',N'COMPLETE')", new { orgId });
        var upLineId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upstream_bill_line(bill_id,platform_line_id) OUTPUT INSERTED.id VALUES(@billId,N'L1')", new { billId });
        await using var tx2 = (SqlTransaction)await c.BeginTransactionAsync();
        var stored = await TraceCodeStore.EnsureAsync(c, tx2, "81234567890123456721", default);
        var allocId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.inbound_allocation(org_id,env,source_line_id,upstream_line_id,quantity,unit_ratio_numerator,unit_ratio_denominator,state,created_by)
OUTPUT INSERTED.id VALUES(@orgId,N'MOCK',@lineId,@upLineId,1,1,1,N'RESERVED',N'test')", new { orgId, lineId, upLineId }, tx2);
        await CodeClaimWriter.InsertActiveAsync(c, tx2, orgId, "MOCK", stored.Id, "PURCHASE_IN", allocId, null, default);
        var ctx = new InboundTaskContext(orgId, "MOCK", snapId, "PI-LEN-1", new string('0', 64), null,
            "SUP001", "REF-SUP", "ENT-SUP",
            new[] { new AllocatedLine(allocId, lineId, "1", "SP0001", "DRUG-1", "B2026", 1m, "盒",
                new[] { new StoredCodeRef(stored.Id, stored.Normalized) }) },
            new[] { "product:SP0001:1" }, "test");
        var factory = new InboundTaskFactory(settings, new JobQueue(_fx.ConnectionString, new AuditLog()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => factory.CreateAsync(c, tx2, ctx, default));
        await tx2.RollbackAsync();
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.upload_task WHERE event_key=N'KSOA:PI-LEN-1:PURCHASE_IN'"));
    }
}
