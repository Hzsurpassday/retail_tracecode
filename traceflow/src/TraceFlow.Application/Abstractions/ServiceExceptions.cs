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
