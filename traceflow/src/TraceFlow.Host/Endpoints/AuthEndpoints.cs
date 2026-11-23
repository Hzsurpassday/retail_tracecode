using System.Security.Claims;
using Dapper;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;

namespace TraceFlow.Host.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app, string connectionString)
    {
        var hasher = new PasswordHasher<string>();

        app.MapGet("/api/v1/auth/session", async (HttpContext ctx, IAntiforgery af, IPermissionService perms) =>
        {
            var tokens = af.GetAndStoreTokens(ctx);
            if (ctx.User.Identity?.IsAuthenticated != true)
                return Results.Ok(new { data = new { authenticated = false, csrfToken = tokens.RequestToken }, requestId = ctx.TraceIdentifier });
            var userId = long.Parse(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            return Results.Ok(new { data = new { authenticated = true, username = ctx.User.Identity!.Name,
                permissions = await perms.ForUserAsync(userId), csrfToken = tokens.RequestToken }, requestId = ctx.TraceIdentifier });
        });

        app.MapPost("/api/v1/auth/login", async (HttpContext ctx, LoginRequest req, IAntiforgery af, IAuditLog audit) =>
        {
            await af.ValidateRequestAsync(ctx);
            var username = req?.Username ?? "";
            var password = req?.Password ?? "";
            await using var conn = new SqlConnection(connectionString);
            var user = await conn.QuerySingleOrDefaultAsync<(long Id, string Hash, bool Active)?>(
                "SELECT id AS Id, password_hash AS Hash, is_active AS Active FROM dbo.app_user WHERE username=@u", new { u = username });
            if (user is null || !user.Value.Active ||
                hasher.VerifyHashedPassword(username, user.Value.Hash, password) == PasswordVerificationResult.Failed)
            {
                // audit_log.actor nvarchar(64)：空用户名记 "(empty)"，超长截断，失败审计永不抛（AUTH-02）。
                var actor = username.Length == 0 ? "(empty)" : username.Length > 64 ? username[..64] : username;
                await audit.AppendAsync(conn, null, new AuditEntry(actor, "User", "auth.login.failed", "app_user", null, "登录失败", null, null, ctx.TraceIdentifier, null, "LIVE"));
                return Results.Json(new { code = "AUTH_FAILED", message = "用户名或密码错误", retryable = false, requestId = ctx.TraceIdentifier }, statusCode: 401);
            }
            var claims = new[] { new Claim(ClaimTypes.NameIdentifier, user.Value.Id.ToString()), new Claim(ClaimTypes.Name, username) };
            await ctx.SignInAsync("tf", new ClaimsPrincipal(new ClaimsIdentity(claims, "tf")));
            await audit.AppendAsync(conn, null, new AuditEntry(username, "User", "auth.login", "app_user", user.Value.Id.ToString(), null, null, null, ctx.TraceIdentifier, null, "LIVE"));
            return Results.Ok(new { data = new { username }, requestId = ctx.TraceIdentifier });
        });

        app.MapPost("/api/v1/auth/logout", async (HttpContext ctx, IAntiforgery af, IAuditLog audit) =>
        {
            await af.ValidateRequestAsync(ctx);
            var name = ctx.User.Identity?.Name ?? "?";
            await ctx.SignOutAsync("tf");
            await using var conn = new SqlConnection(connectionString);
            await audit.AppendAsync(conn, null, new AuditEntry(name, "User", "auth.logout", "app_user", null, null, null, null, ctx.TraceIdentifier, null, "LIVE"));
            return Results.Ok(new { data = new { }, requestId = ctx.TraceIdentifier });
        });
    }

    public sealed record LoginRequest(string Username, string Password);
}
