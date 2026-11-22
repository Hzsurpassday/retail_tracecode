using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Security;
using TraceFlow.Domain.Versioning;
using TraceFlow.Host.Security;
using TraceFlow.Infrastructure.Master;

namespace TraceFlow.Host.Endpoints;

public static class ImportEndpoints
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static void MapImportEndpoints(this WebApplication app, string connectionString, string env)
    {
        app.MapPost("/api/v1/imports", (HttpContext ctx, IdempotencyExecutor idem, ImportService svc) =>
            idem.RunAsync(ctx, async (conn, tx, ct) =>
            {
                ctx.Request.Body.Position = 0;
                var req = await JsonSerializer.DeserializeAsync<ImportRequest>(ctx.Request.Body, Web, ct);
                if (req is null || string.IsNullOrEmpty(req.FileKind) || string.IsNullOrEmpty(req.FileHash) || string.IsNullOrEmpty(req.Content))
                    return (422, Err(ctx, "VALIDATION", "fileKind/fileHash/content 必填"));
                var orgId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                    "SELECT id FROM dbo.organization WHERE is_active=1", null, tx, cancellationToken: ct));
                if (orgId == 0) return (422, Err(ctx, "VALIDATION", "无启用组织"));
                var (batchId, jobId) = await svc.StageAsync(conn, tx, orgId, env, req.FileKind, req.FileName,
                    req.FileHash, req.Content, ctx.User.Identity!.Name!, ct);
                return (202, JsonSerializer.Serialize(new { data = new { jobId = jobId.ToString(), batchId = batchId.ToString() }, requestId = ctx.TraceIdentifier }));
            })).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.MasterMatch));

        // 预览定位：按 (fileKind,fileHash) 找批次（UI 上传后拿 batchId 也可直接查详情）
        app.MapGet("/api/v1/imports", async (HttpContext ctx, string? fileKind, string? fileHash) =>
        {
            if (string.IsNullOrEmpty(fileKind) || string.IsNullOrEmpty(fileHash))
                return Results.Json(new { code = "VALIDATION", message = "fileKind/fileHash 必填", retryable = false, requestId = ctx.TraceIdentifier }, statusCode: 422);
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ctx.RequestAborted);
            var b = await conn.QuerySingleOrDefaultAsync<BatchView>(new CommandDefinition(@"
SELECT id,file_kind,file_name,file_hash,total_rows,success_rows,failed_rows,status,staged_by,staged_at,committed_by,committed_at,ver
FROM dbo.import_batch
WHERE org_id=(SELECT id FROM dbo.organization WHERE is_active=1) AND file_kind=@kind AND file_hash=@hash",
                new { kind = fileKind, hash = fileHash }, cancellationToken: ctx.RequestAborted));
            if (b is null)
                return Results.Json(new { code = "NOT_FOUND", message = "批次不存在", retryable = false, requestId = ctx.TraceIdentifier }, statusCode: 404);
            return Results.Ok(new { data = ToView(b), requestId = ctx.TraceIdentifier });
        }).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.MasterMatch));

        app.MapGet("/api/v1/imports/{id:long}", async (HttpContext ctx, long id, int? page, int? pageSize) =>
        {
            var p = Math.Max(1, page ?? 1);
            var ps = Math.Clamp(pageSize ?? 50, 1, 200);
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ctx.RequestAborted);
            var b = await conn.QuerySingleOrDefaultAsync<BatchView>(new CommandDefinition(
                "SELECT id,file_kind,file_name,file_hash,total_rows,success_rows,failed_rows,status,staged_by,staged_at,committed_by,committed_at,ver FROM dbo.import_batch WHERE id=@id",
                new { id }, cancellationToken: ctx.RequestAborted));
            if (b is null)
                return Results.Json(new { code = "NOT_FOUND", message = "批次不存在", retryable = false, requestId = ctx.TraceIdentifier }, statusCode: 404);
            var total = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM dbo.import_row WHERE batch_id=@id", new { id }, cancellationToken: ctx.RequestAborted));
            var rows = await conn.QueryAsync<RowView>(new CommandDefinition(@"
SELECT row_number,status,error,raw_json FROM (
  SELECT row_number,status,error,raw_json, ROW_NUMBER() OVER (ORDER BY row_number) AS rn
  FROM dbo.import_row WHERE batch_id=@id) t WHERE rn BETWEEN @lo AND @hi",
                new { id, lo = (p - 1) * ps + 1, hi = p * ps }, cancellationToken: ctx.RequestAborted));
            return Results.Ok(new
            {
                data = new
                {
                    batch = ToView(b),
                    rows = rows.Select(r => new { rowNumber = r.row_number, status = r.status, error = r.error, rawJson = r.raw_json }),
                    total, page = p, pageSize = ps,
                },
                requestId = ctx.TraceIdentifier,
            });
        }).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.MasterMatch));

        app.MapPost("/api/v1/imports/{id:long}/commit", (HttpContext ctx, long id, IdempotencyExecutor idem, ImportService svc) =>
            idem.RunAsync(ctx, async (conn, tx, ct) =>
            {
                ctx.Request.Body.Position = 0;
                var req = await JsonSerializer.DeserializeAsync<CommitImportRequest>(ctx.Request.Body, Web, ct);
                if (req is null || string.IsNullOrEmpty(req.ExpectedVersion))
                    return (422, Err(ctx, "VALIDATION", "expectedVersion 必填"));
                var exists = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                    "SELECT COUNT(*) FROM dbo.import_batch WHERE id=@id", new { id }, tx, cancellationToken: ct));
                if (exists == 0) return (404, Err(ctx, "NOT_FOUND", "批次不存在"));
                var applied = await svc.CommitAsync(conn, tx, id, env, req.ExpectedVersion, req.Reason, ctx.User.Identity!.Name!, ct);
                return (200, JsonSerializer.Serialize(new { data = new { appliedRows = applied, status = "COMMITTED" }, requestId = ctx.TraceIdentifier }));
            })).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.MasterMatch));
    }

    private static string Err(HttpContext ctx, string code, string message) =>
        JsonSerializer.Serialize(new { code, message, retryable = false, requestId = ctx.TraceIdentifier });

    private static object ToView(BatchView b) => new
    {
        batchId = b.id.ToString(), fileKind = b.file_kind, fileName = b.file_name, fileHash = b.file_hash,
        totalRows = b.total_rows, successRows = b.success_rows, failedRows = b.failed_rows, status = b.status,
        version = RowVer.ToHex(b.ver), stagedBy = b.staged_by, stagedAt = b.staged_at,
        committedBy = b.committed_by, committedAt = b.committed_at,
    };

    public sealed record ImportRequest(string? FileKind, string? FileHash, string? Content, string? FileName);
    public sealed record CommitImportRequest(string? ExpectedVersion, string? Reason);
    public sealed record BatchView(long id, string file_kind, string file_name, string file_hash, int total_rows,
        int success_rows, int failed_rows, string status, string staged_by, DateTime staged_at,
        string? committed_by, DateTime? committed_at, byte[] ver);
    public sealed record RowView(int row_number, string status, string? error, string raw_json);
}
