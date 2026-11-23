namespace TraceFlow.Application.Abstractions;

// M1 服务层异常→HTTP 契约（ApiErrors 统一映射；端点不各自写错误臂）。
public sealed class AdapterNotCertifiedException : Exception
{
    public AdapterNotCertifiedException(string message) : base(message) { }
}
public sealed class SettingValidationException : Exception
{
    public SettingValidationException(string message) : base(message) { }
}
public sealed class SettingVersionException : Exception
{
    public SettingVersionException(string message) : base(message) { }
}
public sealed class ImportBoundsException : Exception
{
    public ImportBoundsException(string message) : base(message) { }
}
public sealed class TopRateLimitedException : Exception
{
    public TimeSpan Wait { get; }
    public TopRateLimitedException(TimeSpan wait) : base($"TOP 限流，需等待 {wait.TotalSeconds:F0} 秒") => Wait = wait;
}
public sealed class ChannelPausedException : Exception
{
    public string ScopeKey { get; }
    public ChannelPausedException(string scopeKey, string message) : base(message) => ScopeKey = scopeKey;
}
public sealed class TopNotCertifiedException : Exception
{
    public TopNotCertifiedException(string message) : base(message) { }
}

public sealed class AllocationConflictException : Exception
{
    public string ErrorCode { get; }
    public AllocationConflictException(string errorCode, string message) : base(message) => ErrorCode = errorCode;
}

public sealed class AllocationValidationException : Exception
{
    public string ErrorCode { get; }
    public IReadOnlyList<object>? FieldErrors { get; }
    public AllocationValidationException(string errorCode, string message, IReadOnlyList<object>? fieldErrors = null) : base(message)
    {
        ErrorCode = errorCode;
        FieldErrors = fieldErrors;
    }
}

/// <summary>读数/命令目标不存在：端点与服务统一映射 404 NOT_FOUND（不用 KeyNotFoundException 掩盖字典缺陷）。</summary>
public sealed class ResourceNotFoundException : Exception
{
    public ResourceNotFoundException(string message) : base(message) { }
}

/// <summary>
/// 受控重试合同门禁未取证（top.retryPathCertified=false）：422 RETRY_PATH_NOT_CERTIFIED。
/// 调用方必须在任何写入前抛出（事务回滚 + 不落幂等记录），保证「拒绝即零写入」。
/// </summary>
public sealed class RetryPathNotCertifiedException : Exception
{
    public RetryPathNotCertifiedException(string message) : base(message) { }
}
