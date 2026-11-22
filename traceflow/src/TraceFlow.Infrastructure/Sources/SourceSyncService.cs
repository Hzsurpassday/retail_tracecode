using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Domain.Codes;
using TraceFlow.Domain.Quantities;
using TraceFlow.Domain.Sources;
using TraceFlow.Infrastructure.Settings;

namespace TraceFlow.Infrastructure.Sources;

public sealed record SyncResult(int DocumentsSeen, int NewVersions, int Completed, int Issues, DateTime WindowEndUtc);

/// <summary>
/// 来源同步（SYNC-01/03、SRC-01/02/04/05、DB-02）：
/// 抓取在事务外；落库整体一个事务——任何失败（含 docKey 超宽 SqlException）回滚全部，检查点不动（AT-006）。
/// 不可变版本：同 docKey 摘要相同→无操作；摘要变化→新版本（旧版本字节不动）。
/// 异常单据 INVALID 但 raw 完整保存；零行 DISCOVERED+EMPTY_DETAIL；正常 STABILIZING（Task 8 续稳定期）。
/// M1 仅 MOCK：LIVE 一律 AdapterNotCertifiedException（G-ERP 门禁）。
/// </summary>
public sealed class SourceSyncService
{
    private readonly string _cs;
    private readonly ISourceAdapter _adapter;
    private readonly IAuditLog _audit;

    // settings 参数本任务不消费（Task 8 引入稳定期字段时再存 _settings）；
    // 不存未读字段——TreatWarningsAsErrors 下 CS0414 会编译失败。
    public SourceSyncService(string connectionString, ISourceAdapter adapter, SettingsService settings, IAuditLog audit)
    {
        _cs = connectionString;
        _adapter = adapter;
        _audit = audit;
    }

    public async Task<SyncResult> RunAsync(long orgId, string env, string entity, DateTime startUtc, DateTime endUtc, CancellationToken ct)
    {
        if (env != "MOCK")
            throw new AdapterNotCertifiedException("M1 来源同步仅允许 MOCK 通道；LIVE 须 G-ERP 取证门禁通过后开启");
        var docs = await _adapter.FetchAsync(entity, startUtc, endUtc, ct); // 事务外抓取（DB-01 只读来源库）
        var now = DateTime.UtcNow;
        int newVersions = 0, issuesTotal = 0;

        await using var c = new SqlConnection(_cs);
        await c.OpenAsync(ct);
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
        var sysId = await c.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT id FROM dbo.source_system WHERE system_code=N'KSOA'", transaction: tx, cancellationToken: ct));

        foreach (var doc in docs)
        {
            ct.ThrowIfCancellationRequested();
            var lineHashes = doc.Lines.Select(l => SourceHasher.LineHash(
                l.SourceLineKey ?? "", l.ProductSourceId, l.QuantityOriginal, l.UnitOriginal,
                l.BatchRaw, l.ExpiryRaw, l.TraceCodeRaw, l.EventKind)).ToList();
            var sorted = lineHashes.OrderBy(h => h, StringComparer.Ordinal).ToList();
            var docHash = SourceHasher.DocumentHash(doc.SourceDocumentKey, doc.BusinessDateTimeUtc, sorted);

            var latest = await c.QuerySingleOrDefaultAsync<SnapshotRow>(new CommandDefinition(@"
SELECT TOP 1 id, version, content_hash, completeness_status, business_datetime, document_type
FROM dbo.source_snapshot WITH (UPDLOCK, HOLDLOCK)
WHERE source_system_id=@sys AND source_document_key=@key ORDER BY version DESC",
                new { sys = sysId, key = doc.SourceDocumentKey }, tx, cancellationToken: ct));
            if (latest is not null && latest.content_hash == docHash)
                continue; // 摘要去重：重复轮询不产生新版本

            var issues = new List<(string? LineKey, string Type, string Detail, string Raw)>();
            foreach (var ki in doc.KeyIssues)
                issues.Add((ki.SourceLineKey, ki.IssueKind, ki.Detail, ki.RawLineJson));

            // DB 级跨表键碰撞（KEY_REUSE）：同 docKey 出现过不同单据类型
            var otherType = await c.ExecuteScalarAsync<string?>(new CommandDefinition(@"
SELECT TOP 1 document_type FROM dbo.source_snapshot
WHERE source_system_id=@sys AND source_document_key=@key AND document_type<>@t",
                new { sys = sysId, key = doc.SourceDocumentKey, t = doc.DocumentType }, tx, cancellationToken: ct));
            if (otherType is not null)
                issues.Add((null, "KEY_REUSE", $"docKey={doc.SourceDocumentKey} 已用于 {otherType}，跨单据类型碰撞", doc.RawHeaderJson));

            // DB 级跨日复用（CROSS_DAY_REUSE）：摘要变化且中国业务日不同（适配器批内已查，这里查跨轮次）
            if (latest is not null && latest.business_datetime is not null && doc.BusinessDateTimeUtc is not null)
            {
                var oldDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(latest.business_datetime.Value, BusinessTimeParser.Shanghai));
                var newDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(doc.BusinessDateTimeUtc.Value, BusinessTimeParser.Shanghai));
                if (oldDay != newDay)
                    issues.Add((null, "CROSS_DAY_REUSE", $"docKey={doc.SourceDocumentKey} 跨业务日复用 {oldDay:yyyy-MM-dd}→{newDay:yyyy-MM-dd}", doc.RawHeaderJson));
            }

            // 数量域校验（SRC-05）：越界行不落库（decimal(18,6) 放不下），raw 进异常，不静默舍入
            var insertable = new List<(SourceLineDto Line, string Hash)>();
            var skippedKeys = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < doc.Lines.Count; i++)
            {
                var l = doc.Lines[i];
                try { _ = new Qty(l.QuantityOriginal); }
                catch (ArgumentOutOfRangeException)
                {
                    issues.Add((l.SourceLineKey, "QTY_RANGE", $"数量 {l.QuantityOriginal} 超出 decimal(18,6) 域", l.RawLineJson));
                    if (l.SourceLineKey is not null) skippedKeys.Add(l.SourceLineKey);
                    continue;
                }
                insertable.Add((l, lineHashes[i]));
            }

            var hasFatal = issues.Count > 0;
            if (!hasFatal && insertable.Count == 0)
                issues.Add((null, "EMPTY_DETAIL", "单据无明细行（迟到明细由后续新版本补全，AT-005）", doc.RawHeaderJson));
            var status = hasFatal ? "INVALID" : insertable.Count == 0 ? "DISCOVERED" : "STABILIZING";
            var version = (latest?.version ?? 0) + 1;

            var snapId = await c.ExecuteScalarAsync<long>(new CommandDefinition(@"
INSERT INTO dbo.source_snapshot(org_id,source_system_id,source_document_key,document_type,version,
    business_datetime,source_time_raw,source_time_zone,completeness_status,content_hash,raw_header,stabilize_rounds,last_round_at)
OUTPUT inserted.id
VALUES(@org,@sys,@key,@type,@ver,@bdt,@str,@stz,@status,@hash,@raw,@rounds,@roundAt)",
                new { org = orgId, sys = sysId, key = doc.SourceDocumentKey, type = doc.DocumentType, ver = version,
                      bdt = doc.BusinessDateTimeUtc, str = doc.SourceTimeRaw, stz = doc.SourceTimeZone,
                      status, hash = docHash, raw = doc.RawHeaderJson,
                      rounds = status == "STABILIZING" ? 1 : 0,
                      roundAt = status == "STABILIZING" ? now : (DateTime?)null }, tx, cancellationToken: ct));
            newVersions++;

            foreach (var (l, hash) in insertable)
            {
                var norm = TraceCodeNormalizer.Normalize(l.TraceCodeRaw);
                await c.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.source_line(snapshot_id,source_line_key,product_source_id,quantity_original,unit_original,
    batch_raw,expiry_raw,expiry_precision,trace_code_raw,trace_code_normalized,code_format,line_hash,event_kind,raw_line)
VALUES(@snap,@key,@pid,@qty,@unit,@batch,@expiry,@prec,@raw,@norm,@fmt,@hash,@evt,@rawLine)",
                    new { snap = snapId, key = l.SourceLineKey, pid = l.ProductSourceId, qty = l.QuantityOriginal,
                          unit = l.UnitOriginal, batch = l.BatchRaw, expiry = l.ExpiryRaw,
                          prec = ExpiryParser.ParsePrecision(l.ExpiryRaw), raw = l.TraceCodeRaw,
                          norm = norm.Normalized, fmt = CodeFormatStorage.ToToken(norm.Format),
                          hash, evt = l.EventKind, rawLine = l.RawLineJson }, tx, cancellationToken: ct));
            }

            foreach (var (lineKey, type, detail, raw) in issues)
            {
                await c.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.ingest_exception(org_id,source_system_id,source_document_key,source_line_key,exception_type,detail)
VALUES(@org,@sys,@key,@lineKey,@type,@detail)",
                    new { org = orgId, sys = sysId, key = doc.SourceDocumentKey, lineKey,
                          type, detail = JsonSerializer.Serialize(new { detail, raw }) }, tx, cancellationToken: ct));
                issuesTotal++;
            }
        }

        // 检查点 UPSERT（同事务；失败回滚则检查点不动，AT-007 窗口不丢）
        await c.ExecuteAsync(new CommandDefinition(@"
IF EXISTS (SELECT 1 FROM dbo.sync_checkpoint WHERE source_system_id=@sys AND entity=@entity AND checkpoint_kind=N'WINDOW')
    UPDATE dbo.sync_checkpoint SET window_start=@start, window_end=@end, last_success_at=@now
    WHERE source_system_id=@sys AND entity=@entity AND checkpoint_kind=N'WINDOW'
ELSE
    INSERT INTO dbo.sync_checkpoint(source_system_id,entity,checkpoint_kind,window_start,window_end,last_success_at)
    VALUES(@sys,@entity,N'WINDOW',@start,@end,@now)",
            new { sys = sysId, entity, start = startUtc, end = endUtc, now }, tx, cancellationToken: ct));

        await _audit.AppendAsync(c, tx, new AuditEntry("SYSTEM", "Worker", "source.sync", "sync_checkpoint",
            entity, null, null,
            JsonSerializer.Serialize(new { entity, documentsSeen = docs.Count, newVersions, issues = issuesTotal, windowEnd = endUtc }),
            null, null, env));
        await tx.CommitAsync(ct);
        return new SyncResult(docs.Count, newVersions, 0, issuesTotal, endUtc);
    }

    private sealed class SnapshotRow
    {
        public long id { get; set; }
        public int version { get; set; }
        public string content_hash { get; set; } = "";
        public string completeness_status { get; set; } = "";
        public DateTime? business_datetime { get; set; }
        public string document_type { get; set; } = "";
    }
}
