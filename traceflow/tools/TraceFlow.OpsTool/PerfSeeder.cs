using System.Data;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Data.SqlClient;

namespace TraceFlow.OpsTool;

/// <summary>种子规模与时间窗，供报告/JSON 回显。</summary>
internal sealed record PerfSeedResult(
    long OrgId,
    long SysId,
    int Products,
    int Documents,
    int SaleLines,
    DateTime NewestBusinessUtc,
    DateTime OldestBusinessUtc);

/// <summary>
/// 性能基准种子（NFR-03：10000 商品 / 100000 销售行）：直写 SQL + SqlBulkCopy，全部落在专用
/// TraceFlow_Perf_* 库（绝不写生产库）。列以 db/migrations 0001–0009 为准：
/// organization/source_system（PERF）→ product → source_snapshot(SALE) → source_line + trace_code →
/// code_event(SALE/OUT, MOCK) → upload_task(Succeeded, COMPLETE) + verification(is_current=1)。
/// 身份键唯一且确定性（spid/barcode/码串/line_hash/content_hash），便于复现与计数自检。
/// </summary>
internal static class PerfSeeder
{
    private const string Env = "MOCK";
    private const int TimeoutSeconds = 600;

    public static async Task<PerfSeedResult> SeedAsync(string connectionString, int products, int documents,
        int saleLines, CancellationToken ct)
    {
        if (products <= 0) throw new CommandException("--products 必须 ≥1", 2);
        if (documents <= 0) throw new CommandException("--documents 必须 ≥1", 2);
        if (saleLines <= 0 || saleLines % documents != 0)
            throw new CommandException("--sale-lines 必须为正整数且能被 --documents 整除（每单行数一致）", 2);
        var linesPerDoc = saleLines / documents;

        var utcNow = DateTime.UtcNow;

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("SET NOCOUNT ON;", cancellationToken: ct));

        // 1) 组织 + 来源系统（单活组织唯一索引要求先于宿主 OrgBootstrap 落库）。
        var orgId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(@"
INSERT INTO dbo.organization(org_code, name) VALUES(N'PERF', N'性能基准门店');
SELECT CAST(SCOPE_IDENTITY() AS bigint);", cancellationToken: ct));
        var sysId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(@"
INSERT INTO dbo.source_system(system_code, name, adapter_contract_version, config_json)
VALUES(N'PERF', N'PERF 性能基准来源', N'perf-1', N'{""mode"":""perf""}');
SELECT CAST(SCOPE_IDENTITY() AS bigint);", cancellationToken: ct));

        // 2) 商品：spid=P0000001…、barcode=69…（14 位，唯一）。
        BulkCopy(conn, "dbo.product", BuildProducts(orgId, sysId, products));

        // 3) SALE 单据快照：business_datetime 分布在最近 30 天。
        var docKeys = Enumerable.Range(1, documents)
            .Select(d => "PERF-SALE-" + d.ToString("D8")).ToArray();
        BulkCopy(conn, "dbo.source_snapshot", BuildSnapshots(orgId, sysId, docKeys, documents, utcNow));

        // 快照 id 回读（2000 行）供 verification / upload_task 关联。
        var snapshotIds = (await conn.QueryAsync<(long Id, string Key)>(new CommandDefinition(@"
SELECT id AS Id, source_document_key AS [Key] FROM dbo.source_snapshot WHERE source_system_id=@sysId",
            new { sysId }, cancellationToken: ct))).ToDictionary(x => x.Key, x => x.Id, StringComparer.Ordinal);

        // 4) 每单 1 个 Succeeded + match_state=COMPLETE 的上传任务（列表 LEFT JOIN 分支被真实执行）。
        BulkCopy(conn, "dbo.upload_task", BuildTasks(orgId, docKeys, utcNow));

        // 5) 每单 1 条当前核验（is_current=1，指向该单任务）。
        var taskIds = (await conn.QueryAsync<(long Id, string Key)>(new CommandDefinition(@"
SELECT id AS Id, event_key AS [Key] FROM dbo.upload_task WHERE org_id=@orgId AND env=N'MOCK'",
            new { orgId }, cancellationToken: ct))).ToDictionary(x => x.Key, x => x.Id, StringComparer.Ordinal);
        BulkCopy(conn, "dbo.verification", BuildVerifications(orgId, docKeys, snapshotIds, taskIds, utcNow));

        // 6) 明细行 + 码台账（含行级码串与唯一 line_hash）。
        await conn.ExecuteAsync(new CommandDefinition(@"
CREATE TABLE #perf_line(
  doc_key nvarchar(128) COLLATE Latin1_General_BIN2 NOT NULL,
  line_key nvarchar(128) COLLATE Latin1_General_BIN2 NOT NULL,
  product_spid nvarchar(64) COLLATE Latin1_General_BIN2 NOT NULL,
  qty decimal(18,6) NOT NULL,
  code nvarchar(200) COLLATE Latin1_General_BIN2 NOT NULL,
  line_hash char(64) COLLATE Latin1_General_BIN2 NOT NULL,
  event_key nvarchar(200) COLLATE Latin1_General_BIN2 NOT NULL,
  occurred_at datetime2(3) NOT NULL,
  business_dt datetime2(3) NOT NULL);", cancellationToken: ct));

        var lines = BuildLines(docKeys, documents, linesPerDoc, products, utcNow);
        BulkCopy(conn, "#perf_line", lines.Table);

        await conn.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.source_line(snapshot_id, source_line_key, product_source_id, quantity_original,
    unit_original, trace_code_raw, trace_code_normalized, code_format, line_hash, event_kind)
SELECT s.id, l.line_key, l.product_spid, l.qty, N'盒', l.code, l.code, N'S8P20', l.line_hash, N'SALE'
FROM #perf_line l
JOIN dbo.source_snapshot s
  ON s.source_system_id=@sysId AND s.source_document_key=l.doc_key AND s.version=1;",
            new { sysId }, commandTimeout: TimeoutSeconds, cancellationToken: ct));

        BulkCopy(conn, "dbo.trace_code", BuildTraceCodes(lines, utcNow));

        await conn.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.code_event(org_id, env, code_id, event_type, direction, source_snapshot_id,
    source_line_id, upload_task_id, occurred_at, business_datetime)
SELECT @orgId, N'MOCK', tc.id, N'SALE', N'OUT', s.id, sl.id, t.id, l.occurred_at, l.business_dt
FROM #perf_line l
JOIN dbo.source_snapshot s
  ON s.source_system_id=@sysId AND s.source_document_key=l.doc_key AND s.version=1
JOIN dbo.source_line sl ON sl.snapshot_id=s.id AND sl.source_line_key=l.line_key
JOIN dbo.trace_code tc ON tc.code_normalized=l.code
JOIN dbo.upload_task t ON t.org_id=@orgId AND t.env=N'MOCK' AND t.event_key=l.event_key;",
            new { orgId, sysId }, commandTimeout: TimeoutSeconds, cancellationToken: ct));

        // 7) 规模自检：任何一张表少了行都视为种子失败（不带着假数据跑基准）。
        var actualProducts = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM dbo.product WHERE org_id=@orgId", new { orgId }, cancellationToken: ct));
        var actualDocs = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM dbo.source_snapshot WHERE org_id=@orgId AND document_type=N'SALE'",
            new { orgId }, cancellationToken: ct));
        var actualLines = await conn.ExecuteScalarAsync<int>(new CommandDefinition(@"
SELECT COUNT(*) FROM dbo.source_line l JOIN dbo.source_snapshot s ON s.id=l.snapshot_id
WHERE s.org_id=@orgId AND l.event_kind=N'SALE'", new { orgId }, cancellationToken: ct));
        var actualCodes = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM dbo.code_event WHERE org_id=@orgId AND env=N'MOCK' AND event_type=N'SALE'",
            new { orgId }, cancellationToken: ct));
        var actualTasks = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM dbo.upload_task WHERE org_id=@orgId AND env=N'MOCK'",
            new { orgId }, cancellationToken: ct));
        var actualVerifications = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM dbo.verification WHERE org_id=@orgId AND env=N'MOCK' AND is_current=1",
            new { orgId }, cancellationToken: ct));

        if (actualProducts != products || actualDocs != documents || actualLines != saleLines
            || actualCodes != saleLines || actualTasks != documents || actualVerifications != documents)
            throw new InvalidOperationException(
                $"种子计数不符：product={actualProducts}/{products}, snapshot={actualDocs}/{documents}, " +
                $"source_line={actualLines}/{saleLines}, code_event={actualCodes}/{saleLines}, " +
                $"upload_task={actualTasks}/{documents}, verification={actualVerifications}/{documents}");

        // 实际业务时间窗（报告用真实 MIN/MAX，不按构造推算）。
        var newest = await conn.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            "SELECT MAX(business_datetime) FROM dbo.source_snapshot WHERE org_id=@orgId AND document_type=N'SALE'",
            new { orgId }, cancellationToken: ct)) ?? utcNow;
        var oldest = await conn.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            "SELECT MIN(business_datetime) FROM dbo.source_snapshot WHERE org_id=@orgId AND document_type=N'SALE'",
            new { orgId }, cancellationToken: ct)) ?? utcNow;

        return new PerfSeedResult(orgId, sysId, products, documents, saleLines, newest, oldest);
    }

    // ---------------------------------------------------------------- DataTable 构造

    private static DataTable BuildProducts(long orgId, long sysId, int products)
    {
        var dt = new DataTable();
        dt.Columns.Add("org_id", typeof(long));
        dt.Columns.Add("source_system_id", typeof(long));
        dt.Columns.Add("spid", typeof(string));
        dt.Columns.Add("barcode", typeof(string));
        dt.Columns.Add("name", typeof(string));
        dt.Columns.Add("trace_classification", typeof(string));
        dt.Columns.Add("updated_at", typeof(DateTime));
        var updated = DateTime.UtcNow;
        for (var p = 1; p <= products; p++)
            dt.Rows.Add(orgId, sysId, Spid(p), Barcode(p), "性能商品" + p, "REQUIRED", updated);
        return dt;
    }

    private static DataTable BuildSnapshots(long orgId, long sysId, string[] docKeys, int documents, DateTime utcNow)
    {
        var dt = new DataTable();
        dt.Columns.Add("org_id", typeof(long));
        dt.Columns.Add("source_system_id", typeof(long));
        dt.Columns.Add("source_document_key", typeof(string));
        dt.Columns.Add("document_type", typeof(string));
        dt.Columns.Add("version", typeof(int));
        dt.Columns.Add("business_datetime", typeof(DateTime));
        dt.Columns.Add("completeness_status", typeof(string));
        dt.Columns.Add("content_hash", typeof(string));
        dt.Columns.Add("observed_at", typeof(DateTime));
        for (var d = 1; d <= documents; d++)
        {
            var business = BusinessTime(d, utcNow);
            dt.Rows.Add(orgId, sysId, docKeys[d - 1], "SALE", 1, business, "COMPLETE",
                Sha("DOC|" + docKeys[d - 1]), business);
        }
        return dt;
    }

    private static DataTable BuildTasks(long orgId, string[] docKeys, DateTime utcNow)
    {
        var dt = new DataTable();
        dt.Columns.Add("org_id", typeof(long));
        dt.Columns.Add("env", typeof(string));
        dt.Columns.Add("event_key", typeof(string));
        dt.Columns.Add("task_type", typeof(string));
        dt.Columns.Add("source_hash", typeof(string));
        dt.Columns.Add("payload_hash", typeof(string));
        dt.Columns.Add("state", typeof(string));
        dt.Columns.Add("match_state", typeof(string));
        dt.Columns.Add("created_at", typeof(DateTime));
        foreach (var key in docKeys)
            dt.Rows.Add(orgId, Env, EventKey(key), "RETAIL_321", Sha("SRC|" + key), Sha("PAYLOAD|" + key),
                "Succeeded", "COMPLETE", utcNow);
        return dt;
    }

    private static DataTable BuildVerifications(long orgId, string[] docKeys,
        IReadOnlyDictionary<string, long> snapshotIds, IReadOnlyDictionary<string, long> taskIds,
        DateTime utcNow)
    {
        var dt = new DataTable();
        dt.Columns.Add("org_id", typeof(long));
        dt.Columns.Add("env", typeof(string));
        dt.Columns.Add("event_key", typeof(string));
        dt.Columns.Add("upload_task_id", typeof(long));
        dt.Columns.Add("source_snapshot_id", typeof(long));
        dt.Columns.Add("source_hash", typeof(string));
        dt.Columns.Add("code_set_hash", typeof(string));
        dt.Columns.Add("rule_version", typeof(string));
        dt.Columns.Add("state", typeof(string));
        dt.Columns.Add("verified_at", typeof(DateTime));
        dt.Columns.Add("is_current", typeof(bool));
        foreach (var key in docKeys)
            dt.Rows.Add(orgId, Env, EventKey(key), taskIds[EventKey(key)], snapshotIds[key],
                Sha("SRC|" + key), Sha("CODES|" + key), "perf-1", "PASSED", utcNow, true);
        return dt;
    }

    /// <summary>行级种子：每单 linesPerDoc 行；码串 = 商品条码 + 6 位全局序号（以 69 开头、全局唯一）。</summary>
    private static (DataTable Table, List<string> Codes) BuildLines(string[] docKeys, int documents,
        int linesPerDoc, int products, DateTime utcNow)
    {
        var dt = new DataTable();
        dt.Columns.Add("doc_key", typeof(string));
        dt.Columns.Add("line_key", typeof(string));
        dt.Columns.Add("product_spid", typeof(string));
        dt.Columns.Add("qty", typeof(decimal));
        dt.Columns.Add("code", typeof(string));
        dt.Columns.Add("line_hash", typeof(string));
        dt.Columns.Add("event_key", typeof(string));
        dt.Columns.Add("occurred_at", typeof(DateTime));
        dt.Columns.Add("business_dt", typeof(DateTime));

        var codes = new List<string>(documents * linesPerDoc);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var n = 0;
        for (var d = 1; d <= documents; d++)
        {
            var docKey = docKeys[d - 1];
            var eventKey = EventKey(docKey);
            var business = BusinessTime(d, utcNow);
            for (var j = 1; j <= linesPerDoc; j++)
            {
                n++;
                var productIndex = ((n - 1) % products) + 1;
                var code = Barcode(productIndex) + n.ToString("D6");
                if (!seen.Add(code))
                    throw new InvalidOperationException("生成的码串不唯一（种子构造错误）: " + code);
                codes.Add(code);
                var lineKey = "L" + j.ToString("D4");
                dt.Rows.Add(docKey, lineKey, Spid(productIndex), 1m, code, Sha("LINE|" + docKey + "|" + lineKey),
                    eventKey, business, business);
            }
        }
        return (dt, codes);
    }

    private static DataTable BuildTraceCodes((DataTable Table, List<string> Codes) lines, DateTime utcNow)
    {
        var dt = new DataTable();
        dt.Columns.Add("code_normalized", typeof(string));
        dt.Columns.Add("code_raw", typeof(string));
        dt.Columns.Add("format", typeof(string));
        dt.Columns.Add("first_seen_at", typeof(DateTime));
        foreach (var code in lines.Codes)
            dt.Rows.Add(code, code, "S8P20", utcNow);
        return dt;
    }

    // ---------------------------------------------------------------- 辅助

    private static void BulkCopy(SqlConnection conn, string destination, DataTable table)
    {
        using var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.Default, null)
        {
            DestinationTableName = destination,
            BulkCopyTimeout = TimeoutSeconds,
            BatchSize = 5000,
        };
        foreach (DataColumn column in table.Columns)
            bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
        bulk.WriteToServer(table);
    }

    private static string Spid(int p) => "P" + p.ToString("D7");

    private static string Barcode(int p) => "69" + p.ToString("D12");

    private static string EventKey(string docKey) => "PERF:" + docKey;

    /// <summary>业务时间：最近 30 天内按单号错开（同一天多单，跨 30 天分层）。</summary>
    private static DateTime BusinessTime(int documentIndex, DateTime utcNow)
        => utcNow.AddDays(-(documentIndex % 30)).AddMinutes(-(documentIndex % 600));

    private static string Sha(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}