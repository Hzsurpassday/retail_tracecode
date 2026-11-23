using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Application.Security;
using TraceFlow.Host.Security;

namespace TraceFlow.Host.Endpoints;

/// <summary>
/// 上游出库单 API（IN-08）：列表/详情只读（trace:read）；重下载（job:rematch）在 LIVE 通道须 G-TOP 取证门禁。
/// 重下载窗口显式取 now-90d..now（bill_date 可能早于缺省 7 天窗口），并按 billId 定向。
/// </summary>
public static class UpstreamEndpoints
{
    public static void MapUpstreamEndpoints(this WebApplication app, string connectionString, string env)
    {
        app.MapGet("/api/v1/upstream-bills", async (HttpContext ctx, int? page, int? pageSize, string? billNo, string? completeness) =>
        {
            var p = Math.Max(1, page ?? 1);
            var size = Math.Clamp(pageSize ?? 50, 1, 200);
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ctx.RequestAborted);
            var orgId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT id FROM dbo.organization WHERE is_active=1", cancellationToken: ctx.RequestAborted));
            var where = "WHERE org_id=@orgId AND env=@env";
            var args = new DynamicParameters();
            args.Add("orgId", orgId);
            args.Add("env", env);
            if (!string.IsNullOrEmpty(billNo)) { where += " AND bill_no=@billNo"; args.Add("billNo", billNo); }
            if (!string.IsNullOrEmpty(completeness)) { where += " AND completeness=@completeness"; args.Add("completeness", completeness); }
            var total = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                $"SELECT COUNT(*) FROM dbo.upstream_bill {where}", args, cancellationToken: ctx.RequestAborted));
            args.Add("lo", (p - 1) * size + 1);
            args.Add("hi", p * size);
            var rows = await conn.QueryAsync<BillListRow>(new CommandDefinition($@"
SELECT billId,platformBillId,billNo,billDate,upstreamRefEntId,toEntId,completeness,sourceVersion,fetchedAtUtc FROM (
    SELECT ROW_NUMBER() OVER (ORDER BY fetched_at DESC, id DESC) AS rn,
           id AS billId, platform_bill_id AS platformBillId, bill_no AS billNo, bill_date AS billDate,
           upstream_ref_ent_id AS upstreamRefEntId, to_ent_id AS toEntId, completeness, source_version AS sourceVersion, fetched_at AS fetchedAtUtc
    FROM dbo.upstream_bill {where}
) t WHERE rn BETWEEN @lo AND @hi ORDER BY rn", args, cancellationToken: ctx.RequestAborted));
            // API-01：外部 ID 用 JSON 字符串（billId 为 bigint，JS 精度）
            var records = rows.Select(r => new
            {
                billId = r.billId.ToString(),
                r.platformBillId, r.billNo, r.billDate, r.upstreamRefEntId, r.toEntId, r.completeness, r.sourceVersion, r.fetchedAtUtc,
            }).ToList();
            return Results.Ok(new { data = new { records, total, page = p, pageSize = size }, requestId = ctx.TraceIdentifier });
        }).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.TraceRead));

        app.MapGet("/api/v1/upstream-bills/{id:long}", async (HttpContext ctx, long id) =>
        {
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ctx.RequestAborted);
            var orgId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT id FROM dbo.organization WHERE is_active=1", cancellationToken: ctx.RequestAborted));
            var bill = await conn.QuerySingleOrDefaultAsync<BillListRow>(new CommandDefinition(@"
SELECT id AS billId, platform_bill_id AS platformBillId, bill_no AS billNo, bill_date AS billDate,
       upstream_ref_ent_id AS upstreamRefEntId, to_ent_id AS toEntId, completeness, source_version AS sourceVersion, fetched_at AS fetchedAtUtc
FROM dbo.upstream_bill WHERE id=@id AND org_id=@orgId AND env=@env", new { id, orgId, env }, cancellationToken: ctx.RequestAborted));
            if (bill is null)
                return Results.Json(new { code = "NOT_FOUND", message = "上游单据不存在", retryable = false, requestId = ctx.TraceIdentifier }, statusCode: 404);
            var lines = (await conn.QueryAsync<LineRow>(new CommandDefinition(@"
SELECT id AS lineId, platform_line_id AS platformLineId, platform_drug_id AS platformDrugId, batch_no AS batchNo, quantity, unit
FROM dbo.upstream_bill_line WHERE bill_id=@id ORDER BY id", new { id }, cancellationToken: ctx.RequestAborted))).ToList();
            var codes = lines.Count == 0 ? new List<CodeRow>() : (await conn.QueryAsync<CodeRow>(new CommandDefinition(@"
SELECT line_id AS lineId, code_normalized AS codeNormalized, package_level AS packageLevel
FROM dbo.upstream_bill_code WHERE line_id IN @ids", new { ids = lines.Select(l => l.lineId).ToList() }, cancellationToken: ctx.RequestAborted))).ToList();
            return Results.Ok(new { data = new { bill = new {
                    billId = bill.billId.ToString(), bill.platformBillId, bill.billNo, bill.billDate, bill.upstreamRefEntId,
                    bill.toEntId, bill.completeness, bill.sourceVersion, bill.fetchedAtUtc },
                lines = lines.Select(l => new { lineId = l.lineId.ToString(), l.platformLineId, l.platformDrugId, l.batchNo,
                    quantity = l.quantity is null ? null : Qty(l.quantity.Value), l.unit,
                    codes = codes.Where(k => k.lineId == l.lineId).Select(k => new { k.codeNormalized, k.packageLevel }) }) },
                requestId = ctx.TraceIdentifier });
        }).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.TraceRead));

        app.MapPost("/api/v1/upstream-bills/{id:long}/redownload", (HttpContext ctx, long id, IdempotencyExecutor idem, IJobQueue queue, IAuditLog audit) =>
            idem.RunAsync(ctx, async (conn, tx, ct) =>
            {
                if (env != "MOCK")
                    return Err(ctx, 422, "TOP_NOT_CERTIFIED", "LIVE 通道须 G-TOP 取证门禁通过后开启");
                var orgId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                    "SELECT id FROM dbo.organization WHERE is_active=1", transaction: tx, cancellationToken: ct));
                var exists = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                    "SELECT COUNT(*) FROM dbo.upstream_bill WHERE id=@id AND org_id=@orgId AND env=@env",
                    new { id, orgId, env }, tx, cancellationToken: ct));
                if (exists == 0) return Err(ctx, 404, "NOT_FOUND", "上游单据不存在");
                var now = DateTime.UtcNow;
                var payload = JsonSerializer.Serialize(new { orgId, env, start = now.AddDays(-90), end = now, billId = id, trigger = "redownload" });
                var jobId = await queue.EnqueueAsync(conn, tx, orgId, env, "upstream-download", $"redownload:{id}:{Guid.NewGuid():N}", payload, now, ct);
                await audit.AppendAsync(conn, tx, new AuditEntry(ctx.User.Identity!.Name!, "User", "upstream.redownload",
                    "upstream_bill", id.ToString(), null, null, null, ctx.TraceIdentifier, null, "LIVE"));
                return (202, JsonSerializer.Serialize(new { data = new { jobId = jobId.ToString() }, requestId = ctx.TraceIdentifier }));
            })).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.JobRematch));
    }

    private static (int, string) Err(HttpContext ctx, int status, string code, string message)
        => (status, JsonSerializer.Serialize(new { code, message, retryable = false, requestId = ctx.TraceIdentifier }));

    // decimal(18,6) 序列化为字符串时去掉尾随零（"2.000000"→"2"），保持 API-01 字符串约定与可读性
    private static string Qty(decimal v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    private sealed class BillListRow
    {
        public long billId { get; set; }
        public string platformBillId { get; set; } = "";
        public string? billNo { get; set; }
        public DateTime? billDate { get; set; }
        public string upstreamRefEntId { get; set; } = "";
        public string? toEntId { get; set; }
        public string completeness { get; set; } = "";
        public string? sourceVersion { get; set; }
        public DateTime fetchedAtUtc { get; set; }
    }
    private sealed class LineRow
    {
        public long lineId { get; set; }
        public string platformLineId { get; set; } = "";
        public string? platformDrugId { get; set; }
        public string? batchNo { get; set; }
        public decimal? quantity { get; set; }
        public string? unit { get; set; }
    }
    private sealed class CodeRow
    {
        public long lineId { get; set; }
        public string codeNormalized { get; set; } = "";
        public string? packageLevel { get; set; }
    }
}
