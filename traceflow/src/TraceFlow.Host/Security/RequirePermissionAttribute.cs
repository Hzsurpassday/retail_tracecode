using Microsoft.AspNetCore.Authorization;
namespace TraceFlow.Host.Security;
public sealed class RequirePermissionAttribute : AuthorizeAttribute
{
    public RequirePermissionAttribute(string permission) => Policy = "perm:" + permission;
}
