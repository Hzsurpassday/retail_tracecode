using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Domain.Top;
using TraceFlow.Infrastructure.Codes;
using TraceFlow.Infrastructure.Top;

namespace TraceFlow.Infrastructure.Upstream;

public sealed record DownloadSummary(int BillsSeen, int BillsComplete, int BillsIncomplete, int ListPages, int DetailPages);

/// <summary>
/// SYNC-03/UP-01/UP-02：上游出库单分页下载。页数据与进度同事务；列表/详情进度分离；
/// 详情未全部成功→INCOMPLETE；重复页/总数不一致→FAILED，不伪造完成。
/// 每次运行从第 1 页重拉（upsert 幂等）；检查点逐页持久化供观测与下轮重拉。
/// </summary>
public sealed class UpstreamDownloadService
{
    private const int PageSize = 50; // 候选，待 G-TOP
    private const int MaxPages = 1000;
    private readonly string _cs;
    private readonly TopClientService _top;
    private readonly IJobQueue _queue;
    private readonly IAuditLog _audit;

    public UpstreamDownloadService(string connectionString, TopClientService top, IJobQueue queue, IAuditLog audit)
    {
        _cs = connectionString; _top = top; _queue = queue; _audit = audit;
    }

    public async Task<DownloadSummary> RunAsync(long orgId, string env, DateTime windowStartUtc, DateTime windowEndUtc, long? onlyBillId, CancellationToken ct)
    {
        if (env != "MOCK")
            throw new TopNotCertifiedException("G-TOP 未取证：上游下载仅 MOCK 可用");
        string? refEntId;
        await using (var c = new SqlConnection(_cs))
        {
            await c.OpenAsync(ct);
            refEntId = await c.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT ref_ent_id FROM dbo.organization WHERE id=@org", new { org = orgId }, cancellationToken: ct));
        }
        if (string.IsNullOrEmpty(refEntId))
        {
            await AuditAsync(env, "upstream.download.skip", orgId, "门店 ref_ent_id 未配置，上游下载跳过", ct);
            return new DownloadSummary(0, 0, 0, 0, 0);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var listPages = onlyBillId is null
            ? await DownloadListAsync(orgId, env, refEntId!, windowStartUtc, windowEndUtc, seen, ct)
            : 0;

        var detailPages = 0; var complete = 0; var incomplete = 0;
        List<BillRow> bills;
        await using (var c = new SqlConnection(_cs))
        {
            await c.OpenAsync(ct);
            bills = (await c.QueryAsync<BillRow>(new CommandDefinition(@"
SELECT id AS Id, platform_bill_id AS PlatformBillId, upstream_ref_ent_id AS UpstreamRefEntId, source_version AS SourceVersion
FROM dbo.upstream_bill
WHERE org_id=@org AND env=@env AND ((@only IS NULL AND completeness<>N'COMPLETE') OR id=@only)",
                new { org = orgId, env, only = onlyBillId }, cancellationToken: ct))).ToList();
        }
        foreach (var b in bills)
        {
            try
            {
                detailPages += await DownloadDetailAsync(orgId, env, b, ct);
                complete++;
            }
            catch (Exception ex) when (ex is not (TopRateLimitedException or ChannelPausedException or TopNotCertifiedException or OperationCanceledException))
            {
                await MarkBillIncompleteAsync(orgId, env, b.Id, ex.Message, ct);
                incomplete++;
            }
        }
        await AuditAsync(env, "upstream.download", orgId,
            $"列表页 {listPages}，详情页 {detailPages}，完整 {complete}，不完整 {incomplete}", ct);
        return new DownloadSummary(seen.Count, complete, incomplete, listPages, detailPages);
    }

    private async Task<int> DownloadListAsync(long orgId, string env, string refEntId, DateTime start, DateTime end, HashSet<string> seen, CancellationToken ct)
    {
        var pageNo = 1; int? totalPages = null; int? totalCount = null;
        HashSet<string>? prevPageIds = null;
        while (pageNo <= MaxPages)
        {
            var outcome = await _top.CallAsync(env, "listupout", new Dictionary<string, string>
            {
                ["ref_ent_id"] = refEntId,
                ["page_no"] = pageNo.ToString(CultureInfo.InvariantCulture),
                ["page_size"] = PageSize.ToString(CultureInfo.InvariantCulture),
                ["start_date"] = start.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                ["end_date"] = end.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            }, refEntId, "SYSTEM", ct);
            if (outcome.Kind != TopOutcomeKind.Success)
            {
                await SetProgressAsync(orgId, env, "upstream-list", 0, start, end, pageNo - 1, totalPages, totalCount, "FAILED", ct);
                throw new InvalidOperationException($"上游列表第 {pageNo} 页非成功：{outcome.Kind} {outcome.SubCode}");
            }
            using var doc = JsonDocument.Parse(outcome.ResultJson ?? "{}");
            var r = doc.RootElement;
            totalPages = r.TryGetProperty("total_pages", out var tp) ? tp.GetInt32() : totalPages;
            totalCount = r.TryGetProperty("total_count", out var tc) ? tc.GetInt32() : totalCount;
            var pageIds = new HashSet<string>(StringComparer.Ordinal);
            var bills = new List<ListBill>();
            if (r.TryGetProperty("bills", out var arr))
                foreach (var b in arr.EnumerateArray())
                {
                    var lb = ParseListBill(b);
                    bills.Add(lb);
                    pageIds.Add(lb.PlatformBillId);
                    seen.Add(lb.UpstreamRefEntId + "|" + lb.PlatformBillId);
                }
            if (prevPageIds is not null && pageIds.Count > 0 && pageIds.SetEquals(prevPageIds))
            {
                await SetProgressAsync(orgId, env, "upstream-list", 0, start, end, pageNo - 1, totalPages, totalCount, "FAILED", ct);
                throw new InvalidOperationException($"上游列表第 {pageNo} 页与上一页重复，禁止伪造完成");
            }
            prevPageIds = pageIds;
            await using (var c = new SqlConnection(_cs))
            {
                await c.OpenAsync(ct);
                await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
                foreach (var lb in bills)
                    await UpsertBillAsync(c, tx, orgId, env, lb, ct);
                await UpsertProgressAsync(c, tx, orgId, env, "upstream-list", 0, start, end, pageNo, totalPages, totalCount, "RUNNING", ct);
                await tx.CommitAsync(ct);
            }
            if (bills.Count == 0 || (totalPages is int t && pageNo >= t)) break;
            pageNo++;
        }
        if (totalCount is int expected && seen.Count != expected)
        {
            await SetProgressAsync(orgId, env, "upstream-list", 0, start, end, pageNo, totalPages, totalCount, "FAILED", ct);
            throw new InvalidOperationException($"上游列表总数不一致：期望 {expected}，实收 {seen.Count}");
        }
        await SetProgressAsync(orgId, env, "upstream-list", 0, start, end, pageNo, totalPages, totalCount, "COMPLETE", ct);
        return pageNo;
    }

    private async Task<int> DownloadDetailAsync(long orgId, string env, BillRow bill, CancellationToken ct)
    {
        var pageNo = 1; int? totalPages = null; var pages = 0;
        while (pageNo <= MaxPages)
        {
            var outcome = await _top.CallAsync(env, "listupout.detail", new Dictionary<string, string>
            {
                ["ref_ent_id"] = bill.UpstreamRefEntId,
                ["bill_id"] = bill.PlatformBillId,
                ["page_no"] = pageNo.ToString(CultureInfo.InvariantCulture),
                ["page_size"] = PageSize.ToString(CultureInfo.InvariantCulture),
            }, bill.UpstreamRefEntId, "SYSTEM", ct);
            if (outcome.Kind != TopOutcomeKind.Success)
                throw new InvalidOperationException($"上游详情第 {pageNo} 页非成功：{outcome.Kind} {outcome.SubCode}");
            using var doc = JsonDocument.Parse(outcome.ResultJson ?? "{}");
            var r = doc.RootElement;
            totalPages = r.TryGetProperty("total_pages", out var tp) ? tp.GetInt32() : totalPages;
            await using (var c = new SqlConnection(_cs))
            {
                await c.OpenAsync(ct);
                await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
                if (r.TryGetProperty("lines", out var lines))
                    foreach (var ln in lines.EnumerateArray())
                        await UpsertLineAsync(c, tx, bill.Id, ln, ct);
                await UpsertProgressAsync(c, tx, orgId, env, "upstream-detail", bill.Id, null, null, pageNo, totalPages, null, "RUNNING", ct);
                await tx.CommitAsync(ct);
            }
            pages++;
            if (totalPages is not int t || pageNo >= t) break;
            pageNo++;
        }
        // 全部详情页成功→COMPLETE + 匹配事件入队（同事务，SYNC-03）
        await using (var c = new SqlConnection(_cs))
        {
            await c.OpenAsync(ct);
            await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
            await c.ExecuteAsync(new CommandDefinition(
                "UPDATE dbo.upstream_bill SET completeness=N'COMPLETE', fetched_at=SYSUTCDATETIME() WHERE id=@id",
                new { id = bill.Id }, tx, cancellationToken: ct));
            await UpsertProgressAsync(c, tx, orgId, env, "upstream-detail", bill.Id, null, null, pageNo, totalPages, null, "COMPLETE", ct);
            var payload = JsonSerializer.Serialize(new { orgId, env, snapshotId = (long?)null, trigger = "upstream", billId = bill.Id });
            await _queue.EnqueueAsync(c, tx, orgId, env, "inbound-match", $"up:{bill.Id}:{bill.SourceVersion ?? "v0"}", payload, DateTime.UtcNow, ct);
            await tx.CommitAsync(ct);
        }
        return pages;
    }

    private sealed record BillRow(long Id, string PlatformBillId, string UpstreamRefEntId, string? SourceVersion);
    private sealed record ListBill(string PlatformBillId, string? BillNo, string UpstreamRefEntId, DateTime? BillDate,
        string? FromEntId, string? ToEntId, string? OwnerEntId, string? DeliverEntId, string? SourceVersion, string RawJson);
    private sealed record ExistingBill(long Id, string? SourceVersion);

    private static ListBill ParseListBill(JsonElement b)
    {
        var pid = GetString(b, "platform_bill_id") ?? throw new InvalidOperationException("上游单缺 platform_bill_id（候选合同不符）");
        var uref = GetString(b, "upstream_ref_ent_id") ?? throw new InvalidOperationException("上游单缺 upstream_ref_ent_id（候选合同不符）");
        DateTime? billDate = null;
        var bd = GetString(b, "bill_date");
        if (bd is not null && DateTime.TryParse(bd, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d))
            billDate = d;
        return new ListBill(pid, GetString(b, "bill_no"), uref, billDate,
            GetString(b, "from_ent_id"), GetString(b, "to_ent_id"), GetString(b, "owner_ent_id"), GetString(b, "deliver_ent_id"),
            GetString(b, "source_version"), b.GetRawText());
    }

    private static async Task UpsertBillAsync(SqlConnection conn, SqlTransaction tx, long orgId, string env, ListBill lb, CancellationToken ct)
    {
        var existing = await conn.QuerySingleOrDefaultAsync<ExistingBill>(new CommandDefinition(@"
SELECT id AS Id, source_version AS SourceVersion FROM dbo.upstream_bill WITH (UPDLOCK, HOLDLOCK)
WHERE org_id=@org AND env=@env AND upstream_ref_ent_id=@ref AND platform_bill_id=@pid",
            new { org = orgId, env, @ref = lb.UpstreamRefEntId, pid = lb.PlatformBillId }, tx, cancellationToken: ct));
        if (existing is null)
        {
            try
            {
                await conn.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.upstream_bill(org_id,env,upstream_ref_ent_id,platform_bill_id,bill_no,bill_date,from_ent_id,to_ent_id,owner_ent_id,deliver_ent_id,completeness,list_raw,source_version,fetched_at)
VALUES(@org,@env,@ref,@pid,@no,@date,@from,@to,@owner,@deliver,N'LIST_ONLY',@raw,@sv,SYSUTCDATETIME())",
                    new { org = orgId, env, @ref = lb.UpstreamRefEntId, pid = lb.PlatformBillId, no = lb.BillNo, date = lb.BillDate,
                          from = lb.FromEntId, to = lb.ToEntId, owner = lb.OwnerEntId, deliver = lb.DeliverEntId, raw = lb.RawJson, sv = lb.SourceVersion },
                    tx, cancellationToken: ct));
            }
            catch (SqlException e) when (e.Number is 2601 or 2627)
            {
                await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.upstream_bill SET list_raw=@raw, source_version=@sv, fetched_at=SYSUTCDATETIME(), completeness=N'LIST_ONLY'
WHERE org_id=@org AND env=@env AND upstream_ref_ent_id=@ref AND platform_bill_id=@pid",
                    new { org = orgId, env, @ref = lb.UpstreamRefEntId, pid = lb.PlatformBillId, raw = lb.RawJson, sv = lb.SourceVersion },
                    tx, cancellationToken: ct));
            }
        }
        else if (existing.SourceVersion != lb.SourceVersion)
        {
            // 版本变化→重拉详情（列表字段更新，完整性降级 LIST_ONLY）
            await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.upstream_bill SET bill_no=@no, bill_date=@date, from_ent_id=@from, to_ent_id=@to, owner_ent_id=@owner, deliver_ent_id=@deliver,
       list_raw=@raw, source_version=@sv, fetched_at=SYSUTCDATETIME(), completeness=N'LIST_ONLY'
WHERE id=@id",
                new { id = existing.Id, no = lb.BillNo, date = lb.BillDate, from = lb.FromEntId, to = lb.ToEntId,
                      owner = lb.OwnerEntId, deliver = lb.DeliverEntId, raw = lb.RawJson, sv = lb.SourceVersion },
                tx, cancellationToken: ct));
        }
        // 同版本已存在：不触碰（COMPLETE 保持，避免无谓重拉）
    }

    private static async Task UpsertLineAsync(SqlConnection conn, SqlTransaction tx, long billId, JsonElement ln, CancellationToken ct)
    {
        var lineKey = GetString(ln, "platform_line_id") ?? throw new InvalidOperationException("上游行缺 platform_line_id（候选合同不符）");
        var drugId = GetString(ln, "platform_drug_id");
        var batch = GetString(ln, "batch_no");
        decimal? qty = ln.TryGetProperty("quantity", out var q) && q.ValueKind == JsonValueKind.Number ? q.GetDecimal() : null;
        var unit = GetString(ln, "unit");
        var level = GetString(ln, "package_level");
        var raw = ln.GetRawText();
        var existingId = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT id FROM dbo.upstream_bill_line WITH (UPDLOCK, HOLDLOCK) WHERE bill_id=@bill AND platform_line_id=@lid",
            new { bill = billId, lid = lineKey }, tx, cancellationToken: ct));
        long id;
        if (existingId is null)
            id = await conn.ExecuteScalarAsync<long>(new CommandDefinition(@"
INSERT INTO dbo.upstream_bill_line(bill_id,platform_line_id,platform_drug_id,batch_no,quantity,unit,package_level,raw_json)
OUTPUT inserted.id VALUES(@bill,@lid,@drug,@batch,@qty,@unit,@level,@raw)",
                new { bill = billId, lid = lineKey, drug = drugId, batch, qty, unit, level, raw }, tx, cancellationToken: ct));
        else
        {
            id = existingId.Value;
            await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.upstream_bill_line SET platform_drug_id=@drug, batch_no=@batch, quantity=@qty, unit=@unit, package_level=@level, raw_json=@raw WHERE id=@id",
                new { drug = drugId, batch, qty, unit, level, raw, id }, tx, cancellationToken: ct));
        }
        if (!ln.TryGetProperty("codes", out var codes)) return;
        foreach (var cd in codes.EnumerateArray())
        {
            var rawCode = GetString(cd, "code");
            if (string.IsNullOrWhiteSpace(rawCode)) continue;
            var stored = await TraceCodeStore.EnsureAsync(conn, tx, rawCode, ct);
            var codeLevel = GetString(cd, "package_level");
            var parent = GetString(cd, "parent_code"); // UP-02：仅平台显式提供时落库，不由码拼接推导
            var rows = await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.upstream_bill_code SET line_id=@line, code_raw=@raw, package_level=@level, parent_code=@parent
WHERE bill_id=@bill AND code_normalized=@norm",
                new { line = id, raw = stored.Raw, level = codeLevel, parent, bill = billId, norm = stored.Normalized }, tx, cancellationToken: ct));
            if (rows == 0)
                await conn.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.upstream_bill_code(bill_id,line_id,code_normalized,code_raw,package_level,parent_code)
VALUES(@bill,@line,@norm,@raw,@level,@parent)",
                    new { bill = billId, line = id, norm = stored.Normalized, raw = stored.Raw, level = codeLevel, parent }, tx, cancellationToken: ct));
        }
    }

    private async Task MarkBillIncompleteAsync(long orgId, string env, long billId, string reason, CancellationToken ct)
    {
        await using var c = new SqlConnection(_cs);
        await c.OpenAsync(ct);
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.upstream_bill SET completeness=N'INCOMPLETE' WHERE id=@id", new { id = billId }, tx, cancellationToken: ct));
        await UpsertProgressAsync(c, tx, orgId, env, "upstream-detail", billId, null, null, 0, null, null, "FAILED", ct);
        await _audit.AppendAsync(c, tx, new AuditEntry("SYSTEM", "System", "upstream.detail.incomplete", "upstream_bill",
            billId.ToString(CultureInfo.InvariantCulture), reason, null, null, null, null, env));
        await tx.CommitAsync(ct);
    }

    private async Task SetProgressAsync(long orgId, string env, string kind, long billId, DateTime? ws, DateTime? we,
        int done, int? totalPages, int? totalCount, string status, CancellationToken ct)
    {
        await using var c = new SqlConnection(_cs);
        await c.OpenAsync(ct);
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
        await UpsertProgressAsync(c, tx, orgId, env, kind, billId, ws, we, done, totalPages, totalCount, status, ct);
        await tx.CommitAsync(ct);
    }

    private static async Task UpsertProgressAsync(SqlConnection conn, SqlTransaction tx, long orgId, string env, string kind, long billId,
        DateTime? ws, DateTime? we, int done, int? totalPages, int? totalCount, string status, CancellationToken ct)
    {
        var p = new { org = orgId, env, kind, bill = billId, cursor = done.ToString(CultureInfo.InvariantCulture),
                      done, tp = totalPages, tc = totalCount, status, ws, we };
        var rows = await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.download_progress
SET page_cursor=@cursor, pages_done=@done, total_pages=@tp, total_count_expected=@tc, status=@status,
    last_success_at=SYSUTCDATETIME(), window_start=@ws, window_end=@we
WHERE org_id=@org AND env=@env AND job_kind=@kind AND bill_id=@bill", p, tx, cancellationToken: ct));
        if (rows == 0)
        {
            try
            {
                await conn.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.download_progress(org_id,env,job_kind,bill_id,page_cursor,pages_done,total_pages,total_count_expected,status,last_success_at,window_start,window_end)
VALUES(@org,@env,@kind,@bill,@cursor,@done,@tp,@tc,@status,SYSUTCDATETIME(),@ws,@we)", p, tx, cancellationToken: ct));
            }
            catch (SqlException e) when (e.Number is 2601 or 2627)
            {
                await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.download_progress
SET page_cursor=@cursor, pages_done=@done, total_pages=@tp, total_count_expected=@tc, status=@status,
    last_success_at=SYSUTCDATETIME(), window_start=@ws, window_end=@we
WHERE org_id=@org AND env=@env AND job_kind=@kind AND bill_id=@bill", p, tx, cancellationToken: ct));
            }
        }
    }

    private async Task AuditAsync(string env, string action, long orgId, string reason, CancellationToken ct)
    {
        await using var c = new SqlConnection(_cs);
        await c.OpenAsync(ct);
        await _audit.AppendAsync(c, null, new AuditEntry("SYSTEM", "System", action, "upstream_download",
            orgId.ToString(CultureInfo.InvariantCulture), reason, null, null, null, null, env));
    }

    private static string? GetString(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
