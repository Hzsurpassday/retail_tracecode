using System.Text.Json;
using TraceFlow.Application.Security;
using TraceFlow.Host.Security;
using TraceFlow.Infrastructure.Settings;

namespace TraceFlow.Host.Endpoints;

public static class SettingsEndpoints
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static void MapSettingsEndpoints(this WebApplication app, string connectionString)
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
    }

    public sealed record SettingUpdateRequest(string? ValueJson, string? SecretValue, string? ExpectedVersion, string? Reason);
    public sealed record ConnectionTestRequest(long ConfigId);
}
