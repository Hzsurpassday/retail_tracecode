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
/// 来源同步（SYNC-01/02/03、SRC-01/02/04/05、DB-02）：
/// 抓取在事务外；落库整体一个事务——任何失败（含 docKey 超宽 SqlException）回滚全部，检查点不动（AT-006）。
/// 不可变版本：同 docKey 摘要相同→稳定期轮次推进（不产生新版本）；摘要变化→新版本（旧版本字节不动）。
/// 摘要去重是问题感知的（T7 评审修复，必须保留）：content_hash 不含 KeyIssues/document_type，
/// 异常计算先于去重判定；命中时比对台账、只补记未记录的键质量异常（含跨类型 KEY_REUSE）；
/// 完全相同重跑→零写入。补记的 OPEN 异常同样阻塞 COMPLETE 晋升。
/// 稳定期（SYNC-02）：两轮摘要一致且轮间隔≥roundIntervalSeconds；行数>0、docKey 无 OPEN 异常、
/// rounds≥2、距 observed_at≥stabilizeSeconds → COMPLETE。零行永不 COMPLETE。
/// 已 COMPLETE 摘要变化→旧 SOURCE_CHANGED + 台账标记（立即 RESOLVED，证据=新版本）；任务 payload 不可变（AT-057）。
/// 迟到明细→新版本 + EMPTY_DETAIL 自动 RESOLVED（AT-005）。
/// 异常单据 INVALID 但 raw 完整保存。M1 仅 MOCK：LIVE 一律 AdapterNotCertifiedException（G-ERP 门禁）。
/// </summary>
public sealed class SourceSyncService
{
    private readonly string _cs;
    private readonly ISourceAdapter _adapter;
    private readonly SettingsService _settings;
    private readonly IAuditLog _audit;

    public SourceSyncService(string connectionString, ISourceAdapter adapter, SettingsService settings, IAuditLog audit)
    {
        _cs = connectionString;
        _adapter = adapter;
        _settings = settings;
        _audit = audit;
    }

    public async Task<SyncResult> RunAsync(long orgId, string env, string entity, DateTime startUtc, DateTime endUtc, CancellationToken ct)
    {
        if (env != "MOCK")
            throw new AdapterNotCertifiedException("M1 来源同步仅允许 MOCK 通道；LIVE 须 G-ERP 取证门禁通过后开启");
        var policy = await _settings.GetSyncPolicyAsync(ct);
        var docs = await _adapter.FetchAsync(entity, startUtc, endUtc, ct); // 事务外抓取（DB-01 只读来源库）
        var now = DateTime.UtcNow;
        int newVersions = 0, completed = 0, issuesTotal = 0;

        await using var c = new SqlConnection(_cs);
        await c.OpenAsync(ct);
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
        var sysId = await c.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT id FROM dbo.source_system WHERE system_code=N'KSOA'", transaction: tx, cancellationToken: ct));

        // 异常单点写入：新版本路径与去重补记共用同一 detail 序列化，保证重跑比对逐字节一致。
        async Task InsertIssueAsync(string docKey, (string? LineKey, string Type, string Detail, string Raw) issue)
        {
            await c.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.ingest_exception(org_id,source_system_id,source_document_key,source_line_key,exception_type,detail)
VALUES(@org,@sys,@key,@lineKey,@type,@detail)",
                new { org = orgId, sys = sysId, key = docKey, lineKey = issue.LineKey,
                      type = issue.Type, detail = JsonSerializer.Serialize(new { detail = issue.Detail, raw = issue.Raw }) }, tx, cancellationToken: ct));
            issuesTotal++;
        }

        foreach (var doc in docs)
        {
            ct.ThrowIfCancellationRequested();
            var lineHashes = doc.Lines.Select(l => SourceHasher.LineHash(
                l.SourceLineKey ?? "", l.ProductSourceId, l.QuantityOriginal, l.UnitOriginal,
                l.BatchRaw, l.ExpiryRaw, l.TraceCodeRaw, l.EventKind)).ToList();
            var sorted = lineHashes.OrderBy(h => h, StringComparer.Ordinal).ToList();
            var docHash = SourceHasher.DocumentHash(doc.SourceDocumentKey, doc.BusinessDateTimeUtc, sorted);

            var latest = await c.QuerySingleOrDefaultAsync<SnapshotRow>(new CommandDefinition(@"
SELECT TOP 1 id, version, content_hash, completeness_status, business_datetime, document_type,
       observed_at, stabilize_rounds, last_round_at
FROM dbo.source_snapshot WITH (UPDLOCK, HOLDLOCK)
WHERE source_system_id=@sys AND source_document_key=@key ORDER BY version DESC",
                new { sys = sysId, key = doc.SourceDocumentKey }, tx, cancellationToken: ct));

            // 异常计算先于去重判定（T7 评审修复）：同摘要不代表键质量异常已记录。
            var issues = new List<(string? LineKey, string Type, string Detail, string Raw)>();
            foreach (var ki in doc.KeyIssues)
                issues.Add((ki.SourceLineKey, ki.IssueKind, ki.Detail, ki.RawLineJson));

            // DB 级跨表键碰撞（KEY_REUSE）：同 docKey 出现过不同单据类型。
            // 摘要命中时也必须执行：content_hash 不含 document_type，跨类型同内容否则被去重吞掉。
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
            for (var i = 0; i < doc.Lines.Count; i++)
            {
                var l = doc.Lines[i];
                try { _ = new Qty(l.QuantityOriginal); }
                catch (ArgumentOutOfRangeException)
                {
                    issues.Add((l.SourceLineKey, "QTY_RANGE", $"数量 {l.QuantityOriginal} 超出 decimal(18,6) 域", l.RawLineJson));
                    continue;
                }
                insertable.Add((l, lineHashes[i]));
            }

            var hasFatal = issues.Count > 0;
            if (!hasFatal && insertable.Count == 0)
                issues.Add((null, "EMPTY_DETAIL", "单据无明细行（迟到明细由后续新版本补全，AT-005）", doc.RawHeaderJson));

            if (latest is not null && latest.content_hash == docHash)
            {
                // 去重补记（T7 评审修复）：比对台账 (lineKey,type,detail)，只补记缺失异常；
                // 不产生新版本、不动既有版本字节；完全相同重跑→全部已记录→零写入。
                var recorded = await c.QueryAsync<ExceptionRow>(new CommandDefinition(@"
SELECT source_line_key, exception_type, detail FROM dbo.ingest_exception
WHERE source_system_id=@sys AND source_document_key=@key",
                    new { sys = sysId, key = doc.SourceDocumentKey }, tx, cancellationToken: ct));
                var recordedSet = new HashSet<(string? LineKey, string Type, string? Detail)>(
                    recorded.Select(r => (r.source_line_key, r.exception_type, r.detail)));
                foreach (var issue in issues)
                {
                    var payload = JsonSerializer.Serialize(new { detail = issue.Detail, raw = issue.Raw });
                    if (!recordedSet.Add((issue.LineKey, issue.Type, payload)))
                        continue; // 同 (lineKey,type,detail) 已在台账→可证已记录，不重复
                    await InsertIssueAsync(doc.SourceDocumentKey, issue);
                }

                // 摘要一致：仅 STABILIZING 推进稳定期；DISCOVERED/INVALID/COMPLETE/SOURCE_CHANGED 无操作。
                // 补记先于晋升判定：新记录的 OPEN 异常立即阻塞 COMPLETE。
                if (latest.completeness_status == "STABILIZING")
                {
                    var rounds = latest.stabilize_rounds;
                    if (latest.last_round_at is null || (now - latest.last_round_at.Value).TotalSeconds >= policy.RoundIntervalSeconds)
                    {
                        rounds++;
                        await c.ExecuteAsync(new CommandDefinition(
                            "UPDATE dbo.source_snapshot SET stabilize_rounds=@r, last_round_at=@now WHERE id=@id",
                            new { r = rounds, now, id = latest.id }, tx, cancellationToken: ct));
                    }
                    var lineCount = await c.ExecuteScalarAsync<int>(new CommandDefinition(
                        "SELECT COUNT(*) FROM dbo.source_line WHERE snapshot_id=@id", new { id = latest.id }, tx, cancellationToken: ct));
                    var openIssues = await c.ExecuteScalarAsync<int>(new CommandDefinition(
                        "SELECT COUNT(*) FROM dbo.ingest_exception WHERE source_system_id=@sys AND source_document_key=@key AND status=N'OPEN'",
                        new { sys = sysId, key = doc.SourceDocumentKey }, tx, cancellationToken: ct));
                    if (lineCount > 0 && openIssues == 0 && rounds >= 2 &&
                        (now - latest.observed_at).TotalSeconds >= policy.StabilizeSeconds)
                    {
                        await c.ExecuteAsync(new CommandDefinition(
                            "UPDATE dbo.source_snapshot SET completeness_status=N'COMPLETE' WHERE id=@id",
                            new { id = latest.id }, tx, cancellationToken: ct));
                        completed++;
                    }
                }
                continue; // 摘要去重：重复轮询不产生新版本
            }

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

            foreach (var issue in issues)
                await InsertIssueAsync(doc.SourceDocumentKey, issue);

            // AT-057：已 COMPLETE 的单据来源变化→旧版本 SOURCE_CHANGED；台账标记立即 RESOLVED（证据=新版本），
            // 新版本走自己的稳定期。旧版本关联 upload_task 的 payload 由 M2 任务生成逻辑保证不可变，此处不触碰任务表。
            if (latest is not null && latest.completeness_status == "COMPLETE")
            {
                await c.ExecuteAsync(new CommandDefinition(
                    "UPDATE dbo.source_snapshot SET completeness_status=N'SOURCE_CHANGED' WHERE id=@id",
                    new { id = latest.id }, tx, cancellationToken: ct));
                await c.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.ingest_exception(org_id,source_system_id,source_document_key,exception_type,detail,status,resolved_by,resolved_at,resolution_evidence)
VALUES(@org,@sys,@key,N'SOURCE_CHANGED',@detail,N'RESOLVED',N'SYSTEM',@now,@evidence)",
                    new { org = orgId, sys = sysId, key = doc.SourceDocumentKey,
                          detail = JsonSerializer.Serialize(new { oldVersion = latest.version, oldSnapshotId = latest.id, newHash = docHash }),
                          now, evidence = JsonSerializer.Serialize(new { snapshotId = snapId }) }, tx, cancellationToken: ct));
                issuesTotal++;
            }

            // AT-005：迟到明细到达→旧 EMPTY_DETAIL 自动 RESOLVED（证据=新版本），解除 COMPLETE 阻塞
            if (insertable.Count > 0)
            {
                await c.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.ingest_exception SET status=N'RESOLVED', resolved_by=N'SYSTEM', resolved_at=@now,
       resolution_evidence=@evidence
WHERE source_system_id=@sys AND source_document_key=@key AND exception_type=N'EMPTY_DETAIL' AND status=N'OPEN'",
                    new { sys = sysId, key = doc.SourceDocumentKey, now,
                          evidence = JsonSerializer.Serialize(new { snapshotId = snapId }) }, tx, cancellationToken: ct));
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
            JsonSerializer.Serialize(new { entity, documentsSeen = docs.Count, newVersions, completed, issues = issuesTotal, windowEnd = endUtc }),
            null, null, env));
        await tx.CommitAsync(ct);
        return new SyncResult(docs.Count, newVersions, completed, issuesTotal, endUtc);
    }

    private sealed class SnapshotRow
    {
        public long id { get; set; }
        public int version { get; set; }
        public string content_hash { get; set; } = "";
        public string completeness_status { get; set; } = "";
        public DateTime? business_datetime { get; set; }
        public string document_type { get; set; } = "";
        public DateTime observed_at { get; set; }
        public int stabilize_rounds { get; set; }
        public DateTime? last_round_at { get; set; }
    }

    // 去重补记的台账比对行：detail 为入库时的完整 JSON 载荷（与 InsertIssueAsync 序列化一致）。
    private sealed class ExceptionRow
    {
        public string? source_line_key { get; set; }
        public string exception_type { get; set; } = "";
        public string? detail { get; set; }
    }
}
