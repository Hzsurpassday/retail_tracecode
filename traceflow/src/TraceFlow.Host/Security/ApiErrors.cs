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
            AdapterNotCertifiedException => (422, "ERP_NOT_CERTIFIED", false),
            SettingValidationException => (422, "VALIDATION", false),
            SettingVersionException => (409, "VERSION_CONFLICT", false),
            ImportBoundsException => (422, "IMPORT_BOUNDS", false),
            TopRateLimitedException => (429, "TOP_RATE_LIMITED", true),
            ChannelPausedException => (503, "TOP_CHANNEL_PAUSED", false),
            TopNotCertifiedException => (422, "TOP_NOT_CERTIFIED", false),
            AllocationConflictException => (409, "ALLOCATION_CONFLICT", false),
            AllocationValidationException => (422, "ALLOCATION_INVALID", false),
            // M3 UI-06/07/09：未知资源 404；受控重试合同门禁 422（拒绝即零写入，见 UploadManualService）
            ResourceNotFoundException => (404, "NOT_FOUND", false),
            RetryPathNotCertifiedException => (422, "RETRY_PATH_NOT_CERTIFIED", false),
            FormatException => (422, "VALIDATION", false), // RowVer.FromHex 等格式校验（全局约束：FormatException→端点 422）
            Microsoft.AspNetCore.Antiforgery.AntiforgeryValidationException => (400, "CSRF_INVALID", false),
            BadHttpRequestException => (400, "BAD_REQUEST", false),
            // Task 12 将请求体反序列化移入 action：畸形 JSON 抛 JsonException，须 400 而非 500（定向补充 A）。
            System.Text.Json.JsonException => (400, "BAD_REQUEST", false),
            _ => (500, "INTERNAL", true),
        };
        // 动态错误码：409/422 的 code 由异常自带，如 CODE_CONTENDED/CANCEL_REFUSED/ALLOCATION_EXISTS/BIG_CODE_BLOCKED
        // 与 brief 的最小偏差：brief 的 `var fieldErrors = Array.Empty<object>()` 为 object[]，无法接收 IReadOnlyList<object>（CS0266）
        IReadOnlyList<object> fieldErrors = Array.Empty<object>();
        if (ex is AllocationConflictException ace) code = ace.ErrorCode;
        if (ex is AllocationValidationException ave) { code = ave.ErrorCode; fieldErrors = ave.FieldErrors ?? Array.Empty<object>(); }
        ctx.Response.StatusCode = status;
        // 日志记录完整异常（服务端），响应只给安全信息；禁连接串/堆栈（NFR-05）
        ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ApiErrors")
            .LogError(ex, "request failed {RequestId}", ctx.TraceIdentifier);
        await ctx.Response.WriteAsJsonAsync(new { code, message = ex switch
        {
            IdempotencyConflictException or SecretUndecryptableException
                or AdapterNotCertifiedException or SettingValidationException
                or SettingVersionException or ImportBoundsException or FormatException
                or TopRateLimitedException or ChannelPausedException or TopNotCertifiedException
                or AllocationConflictException or AllocationValidationException
                or ResourceNotFoundException or RetryPathNotCertifiedException => ex.Message,
            Microsoft.AspNetCore.Antiforgery.AntiforgeryValidationException => "CSRF 令牌缺失或无效",
            BadHttpRequestException or System.Text.Json.JsonException => "请求参数或格式无效",
            _ => "服务内部错误",
        }, fieldErrors, retryable, requestId = ctx.TraceIdentifier });
    }
}
