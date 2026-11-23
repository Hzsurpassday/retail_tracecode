using System.Globalization;
using System.Runtime.Versioning; // DpapiProtector 仅 Windows（CA1416），与 InboundTaskFactoryTests 同标注
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Infrastructure.Jobs;
using TraceFlow.Infrastructure.Sales;
using TraceFlow.Infrastructure.Security;
using TraceFlow.Infrastructure.Settings;
using TraceFlow.IntegrationTests.Sources;
using Xunit;

namespace TraceFlow.IntegrationTests.Sales;

/// <summary>
/// T3 零售任务工厂（SALE-05/IDEM-03/DEF-01）：单据级事件键、分片（单行不可拆）、T2 占用、
/// code_event 方向与 payload 摘要。每测试独立库（IAsyncLifetime）：分片用例会直写 app_setting
/// 覆盖 upload.part.maxCodes，类共享库会产生顺序依赖（与 M3SettingsTests 同模式）。
/// </summary>
[SupportedOSPlatform("windows")]
public class RetailTaskTests : IAsyncLifetime
{
    private readonly Sql2008Fixture _fx = new();
    public async Task InitializeAsync() => await _fx.InitializeAsync();
    public async Task DisposeAsync() => await _fx.DisposeAsync();

    private RetailTaskFactory NewFactory() => new(new SettingsService(_fx.ConnectionString, new DpapiProtector()),
        new JobQueue(_fx.ConnectionString, new AuditLog()));

    private sealed record Seed(long OrgId, long SnapshotId, string DocKey, IReadOnlyList<long> LineIds);

    /// <summary>直接 SQL 播种 SALE 快照 + 行（工厂不读产品映射，映射摘要由调用方以字符串给出）。</summary>
    private static async Task<Seed> SeedSaleAsync(SqlConnection c, string docKey, int lineCount)
    {
        var (orgId, sysId) = await TestSeed.EnsureAsync(c);
        var snapId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.source_snapshot(org_id,source_system_id,source_document_key,document_type,version,completeness_status,content_hash,business_datetime)
OUTPUT INSERTED.id VALUES(@orgId,@sysId,@key,N'SALE',1,N'COMPLETE',@hash,@biz)",
            new { orgId, sysId, key = docKey, hash = new string('0', 64), biz = new DateTime(2026, 11, 20, 6, 0, 0) });
        var lineIds = new List<long>(lineCount);
        for (var i = 1; i <= lineCount; i++)
        {
            lineIds.Add(await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.source_line(snapshot_id,source_line_key,product_source_id,quantity_original,unit_original,batch_raw,line_hash,event_kind)
OUTPUT INSERTED.id VALUES(@snapId,@key,N'SP0001',1,N'盒',N'B2026',@hash,N'SALE')",
                new { snapId, key = i.ToString(CultureInfo.InvariantCulture), hash = new string('0', 64) }));
        }
        return new Seed(orgId, snapId, docKey, lineIds);
    }

    /// <summary>码台账直插：8 开头 20 位数字，tag 区分行，seq 保证单内唯一。</summary>
    private static async Task<IReadOnlyList<(long CodeId, string CodeNormalized)>> CreateCodesAsync(
        SqlConnection c, int tag, int count)
    {
        var codes = new List<(long, string)>(count);
        for (var i = 0; i < count; i++)
        {
            var code = $"8{tag.ToString("D5", CultureInfo.InvariantCulture)}{i.ToString("D14", CultureInfo.InvariantCulture)}";
            var id = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.trace_code(code_normalized,code_raw,format) OUTPUT INSERTED.id VALUES(@n,@n,N'GS1')",
                new { n = code });
            codes.Add((id, code));
        }
        return codes;
    }

    private static RetailLine Line(long sourceLineId, string key, IReadOnlyList<(long CodeId, string CodeNormalized)> codes)
        => new(sourceLineId, key, "SP0001", "DRUG-1", codes.Count, "盒", "B2026", codes);

    private static RetailTaskContext Ctx(Seed s, IReadOnlyList<RetailLine> lines,
        string taskType = RetailTaskFactory.Retail321, IReadOnlyDictionary<long, long>? predecessors = null)
        => new(s.OrgId, "MOCK", s.SnapshotId, s.DocKey, new DateTime(2026, 11, 20, 6, 0, 0, DateTimeKind.Utc),
            new string('0', 64), "product:SP0001:1|product:SP0002:1", "M3-1", lines, taskType)
        {
            PredecessorEventByCodeId = predecessors,
        };

    /// <summary>模拟运维直写 app_setting（upload.part.maxCodes 不在 Catalog 内，无法走 UpdateAsync）。</summary>
    private async Task SetMaxCodesAsync(string value)
    {
        await using var c = new SqlConnection(_fx.ConnectionString);
        await c.OpenAsync();
        await c.ExecuteAsync(@"
IF NOT EXISTS (SELECT 1 FROM dbo.app_setting WHERE setting_key=N'upload.part.maxCodes')
    INSERT INTO dbo.app_setting(setting_key,value_json,updated_by) VALUES(N'upload.part.maxCodes',@v,N'test');
ELSE UPDATE dbo.app_setting SET value_json=@v, updated_by=N'test' WHERE setting_key=N'upload.part.maxCodes'",
            new { v = value });
    }

    private static async Task<RetailTaskResult> CreateAsync(SqlConnection c, RetailTaskFactory factory,
        RetailTaskContext ctx)
    {
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
        var result = await factory.CreateAsync(c, tx, ctx, default);
        await tx.CommitAsync();
        return result;
    }

    [Fact]
    public async Task Creates_one_task_one_part_and_dispatch_outbox()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedSaleAsync(c, "SALE-FACT-1", 1);
        var codes = await CreateCodesAsync(c, 1, 3);
        var ctx = Ctx(s, new[] { Line(s.LineIds[0], "1", codes) });

        var r = await CreateAsync(c, NewFactory(), ctx);

        Assert.False(r.Reused);
        Assert.Single(r.PartIds);
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.upload_task"));
        Assert.Equal("RETAIL_321", await c.ExecuteScalarAsync<string>(
            "SELECT task_type FROM dbo.upload_task WHERE id=@id", new { id = r.TaskId }));
        Assert.Equal("Ready", await c.ExecuteScalarAsync<string>(
            "SELECT state FROM dbo.upload_task WHERE id=@id", new { id = r.TaskId }));
        Assert.Equal("PASSED", await c.ExecuteScalarAsync<string>(
            "SELECT verification_state FROM dbo.upload_task WHERE id=@id", new { id = r.TaskId }));
        Assert.Equal($"KSOA:{s.DocKey}:RETAIL_321", await c.ExecuteScalarAsync<string>(
            "SELECT event_key FROM dbo.upload_task WHERE id=@id", new { id = r.TaskId }));
        var payloadHash = await c.ExecuteScalarAsync<string>(
            "SELECT payload_hash FROM dbo.upload_task WHERE id=@id", new { id = r.TaskId });
        Assert.Equal(64, payloadHash!.Length);
        Assert.Matches("^[0-9A-F]{64}$", payloadHash); // 大写 hex，不随区域变化
        Assert.Equal(64, (await c.ExecuteScalarAsync<string>(
            "SELECT mapping_version_snapshot FROM dbo.upload_task WHERE id=@id", new { id = r.TaskId }))!.Length);

        var billCode = await c.ExecuteScalarAsync<string>(
            "SELECT bill_code FROM dbo.upload_part WHERE task_id=@id", new { id = r.TaskId });
        Assert.StartsWith("TF", billCode);
        Assert.Equal(1, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.upload_part WHERE task_id=@id", new { id = r.TaskId }));

        Assert.Equal("PENDING", await c.ExecuteScalarAsync<string>(@"
SELECT job_state FROM dbo.outbox_job WHERE job_kind=N'upload-dispatch' AND intent_key=@k",
            new { k = $"part:{r.PartIds[0]}" }));
        Assert.Contains("sale-verify", await c.ExecuteScalarAsync<string>(
            "SELECT payload_json FROM dbo.outbox_job WHERE job_kind=N'upload-dispatch' AND intent_key=@k",
            new { k = $"part:{r.PartIds[0]}" }));

        Assert.Equal(3, await c.ExecuteScalarAsync<int>(@"
SELECT COUNT(*) FROM dbo.code_event WHERE upload_task_id=@id AND event_type=N'SALE' AND direction=N'OUT'",
            new { id = r.TaskId }));
        Assert.Equal(3, await c.ExecuteScalarAsync<int>(@"
SELECT COUNT(*) FROM dbo.code_claim WHERE upload_task_id=@id AND event_scope=N'RETAIL:321' AND claim_state=N'ACTIVE'",
            new { id = r.TaskId }));
    }

    [Fact]
    public async Task Splits_parts_at_max_codes_and_keeps_line_codes_together()
    {
        await SetMaxCodesAsync("200"); // brief 字面值：3 行 × 150 码，上限 200
        await using var c = await _fx.OpenAsync();
        var s = await SeedSaleAsync(c, "SALE-FACT-SPLIT", 3);
        var lines = new List<RetailLine>();
        for (var i = 0; i < 3; i++)
            lines.Add(Line(s.LineIds[i], (i + 1).ToString(CultureInfo.InvariantCulture), await CreateCodesAsync(c, i + 2, 150)));

        var r = await CreateAsync(c, NewFactory(), Ctx(s, lines));

        Assert.Equal(3, r.PartIds.Count);
        Assert.Equal(3, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.upload_part WHERE task_id=@id", new { id = r.TaskId }));
        var parts = (await c.QueryAsync<PartRow>(@"
SELECT id, bill_code, payload_json FROM dbo.upload_part WHERE task_id=@id ORDER BY part_index",
            new { id = r.TaskId })).ToList();
        Assert.Equal(r.PartIds, parts.Select(p => p.id).ToList());
        Assert.StartsWith("TF", parts[0].bill_code);
        Assert.EndsWith("P1", parts[1].bill_code);
        Assert.EndsWith("P2", parts[2].bill_code);

        var seen = new List<string>();
        foreach (var part in parts)
        {
            var (lineKeys, codeCount) = ReadPayload(part.payload_json);
            Assert.Single(lineKeys); // 每片只承载整行：同一行码不跨分片
            Assert.Equal(150, codeCount);
            Assert.DoesNotContain(lineKeys[0], seen);
            seen.Add(lineKeys[0]);
        }
        // 单片码数上限 200 生效（3×150 若不分片会是一片 450 码）
        Assert.Equal(3, seen.Count);
    }

    [Fact]
    public async Task Same_event_key_reuses_task_and_parts()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedSaleAsync(c, "SALE-FACT-REUSE", 1);
        var codes = await CreateCodesAsync(c, 3, 2);
        var ctx = Ctx(s, new[] { Line(s.LineIds[0], "1", codes) });
        var factory = NewFactory();

        var r1 = await CreateAsync(c, factory, ctx);
        var r2 = await CreateAsync(c, factory, ctx);

        Assert.True(r2.Reused);
        Assert.Equal(r1.TaskId, r2.TaskId);
        Assert.Equal(r1.PartIds, r2.PartIds);
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.upload_task"));
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.upload_part"));
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=N'upload-dispatch'"));
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT version FROM dbo.upload_task WHERE id=@id", new { id = r1.TaskId }));
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT attempt_count FROM dbo.outbox_job WHERE intent_key=@k",
            new { k = $"part:{r1.PartIds[0]}" })); // 复用路径不重排队列（未重复入队、未重试）
    }

    [Fact]
    public async Task Active_retail_claim_blocks_second_task()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedSaleAsync(c, "SALE-FACT-CONTEND", 1);
        var codes = await CreateCodesAsync(c, 4, 2);
        var factory = NewFactory();
        await CreateAsync(c, factory, Ctx(s, new[] { Line(s.LineIds[0], "1", codes) }));
        // 另一张单引入同一码：码已被 RETAIL:321 ACTIVE 占用
        var s2 = await SeedSaleAsync(c, "SALE-FACT-CONTEND-2", 1);

        await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
        var ex = await Assert.ThrowsAsync<AllocationValidationException>(() =>
            factory.CreateAsync(c, tx, Ctx(s2, new[] { Line(s2.LineIds[0], "1", codes) }), default));
        Assert.Equal("CODE_CONTENDED", ex.ErrorCode);
        await tx.RollbackAsync();

        Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.upload_task"));
        Assert.Equal(0, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.upload_task WHERE event_key=N'KSOA:SALE-FACT-CONTEND-2:RETAIL_321'"));
        Assert.Equal(0, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.code_event WHERE source_snapshot_id=@id", new { id = s2.SnapshotId }));
        Assert.Equal(1, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=N'upload-dispatch'")); // 争用任务未留下队列行
    }

    [Fact]
    public async Task Cancelled_task_reactivates_without_new_bill_code()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedSaleAsync(c, "SALE-FACT-REACT", 1);
        var codes = await CreateCodesAsync(c, 5, 2);
        var ctx = Ctx(s, new[] { Line(s.LineIds[0], "1", codes) });
        var factory = NewFactory();

        var r1 = await CreateAsync(c, factory, ctx);
        var billCode = await c.ExecuteScalarAsync<string>(
            "SELECT bill_code FROM dbo.upload_part WHERE id=@id", new { id = r1.PartIds[0] });
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            // 发送前取消的行政效果（零售路径复用同一 CK：upload_task/part/outbox Cancelled + 占用释放）
            await c.ExecuteAsync("UPDATE dbo.upload_task SET state=N'Cancelled', state_reason=N'测试取消', version=version+1 WHERE id=@id",
                new { id = r1.TaskId }, tx);
            await c.ExecuteAsync("UPDATE dbo.upload_part SET state=N'Cancelled' WHERE task_id=@id", new { id = r1.TaskId }, tx);
            await c.ExecuteAsync(@"UPDATE dbo.outbox_job SET job_state=N'CANCELLED', finished_at=SYSUTCDATETIME()
WHERE org_id=@orgId AND env=N'MOCK' AND job_kind=N'upload-dispatch' AND intent_key=@k",
                new { orgId = s.OrgId, k = $"part:{r1.PartIds[0]}" }, tx);
            await c.ExecuteAsync(@"UPDATE dbo.code_claim SET claim_state=N'RELEASED', released_at=SYSUTCDATETIME(), released_by=N'test'
WHERE upload_task_id=@id AND claim_state=N'ACTIVE'", new { id = r1.TaskId }, tx);
            await tx.CommitAsync();
        }

        var r2 = await CreateAsync(c, factory, ctx);

        Assert.False(r2.Reused);
        Assert.Equal(r1.TaskId, r2.TaskId);
        Assert.Equal(r1.PartIds, r2.PartIds);
        Assert.Equal(billCode, await c.ExecuteScalarAsync<string>(
            "SELECT bill_code FROM dbo.upload_part WHERE id=@id", new { id = r2.PartIds[0] })); // IDEM-03：复活不换单号
        Assert.Equal("Ready", await c.ExecuteScalarAsync<string>(
            "SELECT state FROM dbo.upload_task WHERE id=@id", new { id = r1.TaskId }));
        Assert.Equal("Ready", await c.ExecuteScalarAsync<string>(
            "SELECT state FROM dbo.upload_part WHERE id=@id", new { id = r2.PartIds[0] }));
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.upload_task"));
        Assert.Equal("PENDING", await c.ExecuteScalarAsync<string>(
            "SELECT job_state FROM dbo.outbox_job WHERE intent_key=@k", new { k = $"part:{r2.PartIds[0]}" }));
        Assert.Equal(0, await c.ExecuteScalarAsync<int>(
            "SELECT attempt_count FROM dbo.outbox_job WHERE intent_key=@k", new { k = $"part:{r2.PartIds[0]}" }));
        Assert.Equal(2, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.code_event WHERE source_snapshot_id=@id", new { id = s.SnapshotId })); // 历史不可变，不重复
        Assert.Equal(2, await c.ExecuteScalarAsync<int>(@"
SELECT COUNT(*) FROM dbo.code_claim WHERE upload_task_id=@id AND event_scope=N'RETAIL:321' AND claim_state=N'ACTIVE'",
            new { id = r1.TaskId }));
    }

    [Fact]
    public async Task Event_key_is_document_level_not_line_level()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedSaleAsync(c, "SALE-FACT-DOC", 2);
        var line1 = Line(s.LineIds[0], "1", await CreateCodesAsync(c, 6, 2));
        var line2 = Line(s.LineIds[1], "2", await CreateCodesAsync(c, 7, 1));

        var r = await CreateAsync(c, NewFactory(), Ctx(s, new[] { line1, line2 }));

        Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.upload_task")); // DEF-01：两行一任务
        Assert.Equal($"KSOA:{s.DocKey}:RETAIL_321", await c.ExecuteScalarAsync<string>(
            "SELECT event_key FROM dbo.upload_task WHERE id=@id", new { id = r.TaskId })); // 键只含单据，不含行键
        Assert.DoesNotContain(":1", await c.ExecuteScalarAsync<string>(
            "SELECT event_key FROM dbo.upload_task WHERE id=@id", new { id = r.TaskId }));
        var payload = await c.ExecuteScalarAsync<string>(
            "SELECT payload_json FROM dbo.upload_task WHERE id=@id", new { id = r.TaskId });
        var (lineKeys, codeCount) = ReadPayload(payload!);
        Assert.Equal(new[] { "1", "2" }, lineKeys);
        Assert.Equal(3, codeCount);
        Assert.Equal(3, await c.ExecuteScalarAsync<int>(@"
SELECT COUNT(*) FROM dbo.code_event WHERE upload_task_id=@id AND event_type=N'SALE' AND direction=N'OUT'",
            new { id = r.TaskId }));
    }

    /// <summary>
    /// 116 分支（Task 6 消费）：单据级键 + RETURN:116 占用 + CONSUMER_RETURN/IN 且带前序事件，
    /// 任务 predecessor_task_id 取前序 SALE 任务，载荷携带原销售单号。
    /// </summary>
    [Fact]
    public async Task Consumer_return_116_uses_return_scope_and_predecessor_lineage()
    {
        await using var c = await _fx.OpenAsync();
        var sale = await SeedSaleAsync(c, "SALE-FACT-116-SRC", 1);
        var codes = await CreateCodesAsync(c, 8, 2);
        var factory = NewFactory();
        var saleTask = await CreateAsync(c, factory, Ctx(sale, new[] { Line(sale.LineIds[0], "1", codes) }));
        var saleBillCode = (await c.ExecuteScalarAsync<string>(
            "SELECT bill_code FROM dbo.upload_part WHERE task_id=@id", new { id = saleTask.TaskId }))!;
        var saleEvents = (await c.QueryAsync<EventRow>(@"
SELECT code_id, id FROM dbo.code_event WHERE upload_task_id=@id AND event_type=N'SALE'",
            new { id = saleTask.TaskId })).ToDictionary(r => r.code_id, r => r.id);

        var ret = await SeedSaleAsync(c, "SALE-FACT-116", 1);
        var retLine = Line(ret.LineIds[0], "1", codes);
        var r = await CreateAsync(c, factory,
            Ctx(ret, new[] { retLine }, RetailTaskFactory.ConsumerReturn116, saleEvents));

        Assert.False(r.Reused);
        Assert.Equal("CONSUMER_RETURN_116", await c.ExecuteScalarAsync<string>(
            "SELECT task_type FROM dbo.upload_task WHERE id=@id", new { id = r.TaskId }));
        Assert.Equal($"KSOA:{ret.DocKey}:CONSUMER_RETURN_116", await c.ExecuteScalarAsync<string>(
            "SELECT event_key FROM dbo.upload_task WHERE id=@id", new { id = r.TaskId }));
        Assert.Equal(saleTask.TaskId, await c.ExecuteScalarAsync<long>(
            "SELECT predecessor_task_id FROM dbo.upload_task WHERE id=@id", new { id = r.TaskId }));
        Assert.Equal(2, await c.ExecuteScalarAsync<int>(@"
SELECT COUNT(*) FROM dbo.code_event WHERE upload_task_id=@id AND event_type=N'CONSUMER_RETURN' AND direction=N'IN'
   AND predecessor_event_id IS NOT NULL", new { id = r.TaskId }));
        Assert.Equal(2, await c.ExecuteScalarAsync<int>(@"
SELECT COUNT(*) FROM dbo.code_claim WHERE upload_task_id=@id AND event_scope=N'RETURN:116' AND claim_state=N'ACTIVE'",
            new { id = r.TaskId }));
        var payload = await c.ExecuteScalarAsync<string>(
            "SELECT payload_json FROM dbo.upload_task WHERE id=@id", new { id = r.TaskId });
        Assert.Contains(saleBillCode, payload); // RET-04：退货载荷携带原销售单号
    }

    /// <summary>读载荷 lines：行键顺序 + 码总数（用于分片/单据级断言）。</summary>
    private static (IReadOnlyList<string> LineKeys, int CodeCount) ReadPayload(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var lines = doc.RootElement.GetProperty("lines").EnumerateArray().ToList();
        return (lines.Select(l => l.GetProperty("sourceLineKey").GetString()!).ToList(),
            lines.Sum(l => l.GetProperty("codes").GetArrayLength()));
    }

    private sealed class PartRow
    {
        public long id { get; set; }
        public string bill_code { get; set; } = "";
        public string payload_json { get; set; } = "";
    }

    private sealed class EventRow
    {
        public long code_id { get; set; }
        public long id { get; set; }
    }
}
