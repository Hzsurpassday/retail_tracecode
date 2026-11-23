namespace TraceFlow.Application.Security;

public static class PermissionCatalog
{
    public const string TraceRead = "trace:read";
    public const string MasterMatch = "master:match";
    public const string MasterClassify = "master:classify";
    public const string InboundAllocate = "inbound:allocate";
    public const string JobRematch = "job:rematch";
    public const string UploadQuery = "upload:query";
    public const string UploadRetry = "upload:retry";
    public const string ReturnConfirm = "return:confirm";
    public const string DataExport = "data:export";
    public const string AuditRead = "audit:read";
    public const string AuditExport = "audit:export";
    public const string SystemManage = "system:manage";
    public const string CodeRelease = "code:release"; // 设计 §4.2：占用释放独立权限

    public static readonly IReadOnlyList<string> All = new[]
    {
        TraceRead, MasterMatch, MasterClassify, InboundAllocate, JobRematch, UploadQuery,
        UploadRetry, ReturnConfirm, DataExport, AuditRead, AuditExport, SystemManage, CodeRelease
    };
}
