using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using TraceFlow.Application.Abstractions;

namespace TraceFlow.Host.Security;

public sealed class PermissionRequirement : IAuthorizationRequirement
{
    public string Permission { get; }
    public PermissionRequirement(string permission) => Permission = permission;
}

public sealed class PermissionHandler : AuthorizationHandler<PermissionRequirement>
{
    private readonly IPermissionService _perms;
    public PermissionHandler(IPermissionService perms) => _perms = perms;

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        var idValue = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (long.TryParse(idValue, out var userId) && await _perms.HasAsync(userId, requirement.Permission))
            context.Succeed(requirement); // 逐请求查库；隐藏按钮不能代替鉴权（AUTH-01）
    }
}