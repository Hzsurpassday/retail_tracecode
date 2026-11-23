using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Security;
using TraceFlow.Host.Security;
using TraceFlow.Infrastructure.Settings;
using TraceFlow.Infrastructure.Top;

namespace TraceFlow.Host.Endpoints;

public static class SettingsEndpoints
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static void MapSettingsEndpoints(this WebApplication app, string connectionString, string env)
    {
        // 用 Web（camelCase）序列化，与 PUT 返回的 SettingView  casing 一致（Host 无全局 camelCase 策略，Results.Ok 会输出 PascalCase 记录属性）
        app.MapGet("/api/v1/settings", async (HttpContext ctx, SettingsService svc) =>
            Results.Json(new { data = await svc.ListAsync(ctx.RequestAborted), requestId = ctx.TraceIdentifier }, Web))
            .WithMetadata(new RequirePermissionAttribute(PermissionCatalog.SystemManage));

        app.MapPut("/api/v1/settings/{key}", (HttpContext ctx, string key, IdempotencyExecutor idem, SettingsService svc) =>
            idem.RunAsync(ctx, async (conn, tx, ct) =>
            {
                ctx.Request.Body.Position = 0;
                var req = await JsonSerializer.DeserializeAsync<SettingUpdateRequest>(ctx.Request.Body, Web, ct);
                if (req is null || string.IsNullOrEmpty(req.ExpectedVersion))
                    return (422, JsonSerializer.Serialize(new { code = "VALIDATION", message = "expectedVersion 必填（新建传 \"0\"）", retryable = false, requestId = ctx.TraceIdentifier }));
                // §4.7：账单码合同耦合（top.billPrefix × top.billMaxLength 必然超长的组合在写入面即拒绝，422）
                await svc.EnsureBillContractCouplingAsync(conn, tx, key, req.ValueJson, ct);
                // 服务异常（SettingValidationException/SettingVersionException）直接抛出→事务回滚→ApiErrors 422/409
                var view = await svc.UpdateAsync(conn, tx, key, req.ValueJson, req.SecretValue, req.ExpectedVersion,
                    req.Reason ?? "", ctx.User.Identity!.Name!, ct);
                // brief 原文此处 Serialize 未传 Web，会输出 PascalCase 记录属性（与 GET/注释意图及全局 camelCase 契约矛盾，T6 偏差 3）；补 Web
                return (200, JsonSerializer.Serialize(new { data = view, requestId = ctx.TraceIdentifier }, Web));
            })).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.SystemManage));

        // 连接测试无副作用，不需要幂等键；Web camelCase 输出 {ok,message}
        app.MapPost("/api/v1/settings/connections/test", async (HttpContext ctx, ConnectionTestRequest req, SettingsService svc) =>
            Results.Json(new { data = await svc.TestErpConnectionAsync(req.ConfigId, ctx.RequestAborted), requestId = ctx.TraceIdentifier }, Web))
            .WithMetadata(new RequirePermissionAttribute(PermissionCatalog.SystemManage));

        // §4.7：显示服务运行账号（进程身份）与机器名；换机/恢复备份须重新录入凭证（protectionScope 绑定 CurrentUser）
        app.MapGet("/api/v1/settings/service-account", (HttpContext ctx) =>
            Results.Json(new { data = new { serviceAccount = Environment.UserDomainName + "\\" + Environment.UserName,
                machineName = Environment.MachineName }, requestId = ctx.TraceIdentifier }, Web))
            .WithMetadata(new RequirePermissionAttribute(PermissionCatalog.SystemManage));

        // §4.7：限流预算用量与授权暂停只读视图（限值 NULL=本地不设帽，G-TOP 未取证不虚构）
        app.MapGet("/api/v1/top-budgets", async (HttpContext ctx, ChannelPauseService pauses) =>
        {
            await using var conn = new SqlConnection(connectionString);
            var budgets = await conn.QueryAsync<BudgetRow>(new CommandDefinition(
                "SELECT budget_key AS budgetKey, day_count AS dayCount, day_limit AS dayLimit, minute_count AS minuteCount, minute_limit AS minuteLimit, second_count AS secondCount, second_limit AS secondLimit, next_allowed_at AS nextAllowedAtUtc FROM dbo.top_rate_budget WHERE env=@env ORDER BY budget_key",
                new { env }, cancellationToken: ctx.RequestAborted));
            var pauseList = await pauses.ListAsync(conn, env, ctx.RequestAborted);
            // M2 约定：实体 id 以字符串出网（PauseView.Id 为 bigint）
            return Results.Json(new { data = new { budgets, pauses = pauseList.Select(p => new { id = p.Id.ToString(),
                p.ScopeKey, p.Reason, p.IsActive, p.PausedAtUtc, p.PausedBy, p.ResumedAtUtc, p.ResumedBy }) },
                requestId = ctx.TraceIdentifier }, Web);
        }).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.SystemManage));
    }

    public sealed record SettingUpdateRequest(string? ValueJson, string? SecretValue, string? ExpectedVersion, string? Reason);
    public sealed record ConnectionTestRequest(long ConfigId);

    private sealed class BudgetRow
    {
        public string budgetKey { get; set; } = "";
        public int dayCount { get; set; }
        public int? dayLimit { get; set; }
        public int minuteCount { get; set; }
        public int? minuteLimit { get; set; }
        public int secondCount { get; set; }
        public int? secondLimit { get; set; }
        public DateTime? nextAllowedAtUtc { get; set; }
    }
}
