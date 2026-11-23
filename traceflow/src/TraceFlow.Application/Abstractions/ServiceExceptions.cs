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
