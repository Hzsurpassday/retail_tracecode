using TraceFlow.Application.Abstractions;

namespace TraceFlow.Host.Security;

public static class ApiErrors
{
    public static async Task HandleAsync(HttpContext ctx)
    {
        var ex = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
        var (status, code, retryable) = ex switch
        {
            IdempotencyConflictException => (409, "IDEMPOTENCY_CONFLICT", false),
            SecretUndecryptableException => (503, "SECRET_UNAVAILABLE", false),
            Microsoft.AspNetCore.Antiforgery.AntiforgeryValidationException => (400, "CSRF_INVALID", false),
            BadHttpRequestException => (400, "BAD_REQUEST", false),
            // Task 12 将请求体反序列化移入 action：畸形 JSON 抛 JsonException，须 400 而非 500（定向补充 A）。
            System.Text.Json.JsonException => (400, "BAD_REQUEST", false),
            _ => (500, "INTERNAL", true),
        };
        ctx.Response.StatusCode = status;
        // 日志记录完整异常（服务端），响应只给安全信息；禁连接串/堆栈（NFR-05）
        ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ApiErrors")
            .LogError(ex, "request failed {RequestId}", ctx.TraceIdentifier);
        await ctx.Response.WriteAsJsonAsync(new { code, message = ex switch
        {
            IdempotencyConflictException or SecretUndecryptableException => ex.Message,
            Microsoft.AspNetCore.Antiforgery.AntiforgeryValidationException => "CSRF 令牌缺失或无效",
            _ => "服务内部错误",
        }, fieldErrors = Array.Empty<object>(), retryable, requestId = ctx.TraceIdentifier });
    }
}