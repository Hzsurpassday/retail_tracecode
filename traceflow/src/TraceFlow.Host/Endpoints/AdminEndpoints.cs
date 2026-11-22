using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Application.Security;
using TraceFlow.Host.Security;

namespace TraceFlow.Host.Endpoints;

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app, string connectionString)
    {
        app.MapPost("/api/v1/admin/users", async (HttpContext ctx, CreateUserRequest req, IAuditLog audit) =>
        {
            if (string.IsNullOrWhiteSpace(req.Username) || req.Password.Length < 10)
                return Results.Json(new { code = "VALIDATION", message = "用户名必填且密码至少 10 位", retryable = false, requestId = ctx.TraceIdentifier }, statusCode: 422);
            var hasher = new PasswordHasher<string>();
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();
            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
            long id;
            try
            {
                id = await conn.ExecuteScalarAsync<long>(
                    "INSERT INTO dbo.app_user(username,display_name,password_hash) OUTPUT inserted.id VALUES(@u,@d,@h)",
                    new { u = req.Username, d = req.DisplayName ?? req.Username, h = hasher.HashPassword(req.Username, req.Password) }, tx);
            }
            catch (SqlException ex) when (ex.Number is 2601 or 2627)
            {
                await tx.RollbackAsync();
                return Results.Json(new { code = "CONFLICT", message = "用户名已存在", retryable = false, requestId = ctx.TraceIdentifier }, statusCode: 409);
            }
            await audit.AppendAsync(conn, tx, new AuditEntry(ctx.User.Identity!.Name!, "User", "admin.user.create",
                "app_user", id.ToString(), req.Reason, null, JsonSerializer.Serialize(new { username = req.Username }), ctx.TraceIdentifier, null, "LIVE"));
            await tx.CommitAsync();
            return Results.Ok(new { data = new { id }, requestId = ctx.TraceIdentifier });
        }).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.SystemManage));

        app.MapPost("/api/v1/admin/users/{id:long}/permissions", async (HttpContext ctx, long id, GrantRequest req, IPermissionService perms, IAuditLog audit) =>
        {
            if (!PermissionCatalog.All.Contains(req.Permission))
                return Results.Json(new { code = "VALIDATION", message = "未知权限项", retryable = false, requestId = ctx.TraceIdentifier }, statusCode: 422);
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();
            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
            if (req.Grant) await perms.GrantAsync(conn, tx, id, req.Permission, ctx.User.Identity!.Name!);
            else await perms.RevokeAsync(conn, tx, id, req.Permission, ctx.User.Identity!.Name!);
            await audit.AppendAsync(conn, tx, new AuditEntry(ctx.User.Identity!.Name!, "User",
                req.Grant ? "admin.permission.grant" : "admin.permission.revoke",
                "app_user", id.ToString(), req.Reason, null, JsonSerializer.Serialize(new { permission = req.Permission }), ctx.TraceIdentifier, null, "LIVE"));
            await tx.CommitAsync();
            return Results.Ok(new { data = new { }, requestId = ctx.TraceIdentifier });
        }).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.SystemManage));
    }

    public sealed record CreateUserRequest(string Username, string Password, string? DisplayName, string? Reason);
    public sealed record GrantRequest(string Permission, bool Grant, string? Reason);
}