using BackupPlatform.Application.Identity;
using BackupPlatform.Application.Tenancy;
using BackupPlatform.Domain.Tenancy;
using Microsoft.AspNetCore.Authorization;

namespace BackupPlatform.Api.Security;

public static class AuthorizationPolicies
{
    public const string TenantAdministrator = "TenantAdministrator";
    public const string TenantOperator = "TenantOperator";
    public const string TenantViewer = "TenantViewer";
    public const string PlatformAdmin = "PlatformAdmin";
    /// <summary>Agent-scheme only; never satisfied by a browser session.</summary>
    public const string Agent = "Agent";
}

internal sealed record TenantRoleRequirement(TenantRole MinimumRole) : IAuthorizationRequirement;
internal sealed class PlatformAdminRequirement : IAuthorizationRequirement { }

internal sealed class TenantRoleAuthorizationHandler : AuthorizationHandler<TenantRoleRequirement>
{
    public TenantRoleAuthorizationHandler() { }

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, TenantRoleRequirement requirement)
    {
        var membership = (context.Resource as HttpContext)?.Features.Get<ResolvedTenantContext>()?.Membership;
        if (context.User.Identity?.IsAuthenticated == true && membership is not null && GrantsAccess(membership.Role, requirement.MinimumRole))
        {
            context.Succeed(requirement);
        }
        return Task.CompletedTask;
    }

    private static bool GrantsAccess(TenantRole role, TenantRole minimumRole) => minimumRole switch
    {
        TenantRole.Admin => role is TenantRole.Owner or TenantRole.Admin,
        TenantRole.Operator => role is TenantRole.Owner or TenantRole.Admin or TenantRole.Operator,
        TenantRole.Viewer => role is TenantRole.Owner or TenantRole.Admin or TenantRole.Operator or TenantRole.Viewer,
        _ => false
    };
}

internal sealed class PlatformAdminAuthorizationHandler : AuthorizationHandler<PlatformAdminRequirement>
{
    private readonly ICurrentUser _currentUser;
    private readonly IUserAccessStore _userAccessStore;

    public PlatformAdminAuthorizationHandler(ICurrentUser currentUser, IUserAccessStore userAccessStore)
    {
        _currentUser = currentUser;
        _userAccessStore = userAccessStore;
    }

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PlatformAdminRequirement requirement)
    {
        if (!_currentUser.IsAuthenticated || _currentUser.UserId is not { } userId || context.Resource is not HttpContext httpContext)
        {
            return;
        }
        if (await _userAccessStore.IsPlatformAdminAsync(userId, httpContext.RequestAborted))
        {
            context.Succeed(requirement);
        }
    }
}
