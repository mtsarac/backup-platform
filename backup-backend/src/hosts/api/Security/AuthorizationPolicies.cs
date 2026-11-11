using BackupPlatform.Application.Identity;
using BackupPlatform.Application.Tenancy;
using BackupPlatform.Domain.Identity;
using Microsoft.AspNetCore.Authorization;

namespace BackupPlatform.Api.Security;

public static class AuthorizationPolicies
{
    public const string TenantAdministrator = "TenantAdministrator";
    public const string TenantOperator = "TenantOperator";
    public const string TenantViewer = "TenantViewer";
    public const string PlatformAdmin = "PlatformAdmin";
    public const string ArtifactContentAccess = "ArtifactContentAccess";
    /// <summary>Agent-scheme only; never satisfied by a browser session.</summary>
    public const string Agent = "Agent";
}

internal sealed class TenantAccessRequirement : IAuthorizationRequirement { }
internal sealed class PlatformAdminRequirement : IAuthorizationRequirement { }
internal sealed class ArtifactContentRequirement : IAuthorizationRequirement { }

internal sealed class TenantAccessAuthorizationHandler : AuthorizationHandler<TenantAccessRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, TenantAccessRequirement requirement)
    {
        var resolvedTenant = (context.Resource as HttpContext)?.Features.Get<ResolvedTenantContext>();
        if (context.User.Identity?.IsAuthenticated == true && resolvedTenant is not null)
        {
            context.Succeed(requirement);
        }
        return Task.CompletedTask;
    }
}

internal sealed class PlatformAdminAuthorizationHandler(ICurrentUser currentUser, ICentralAuthorizationService centralAuth)
    : AuthorizationHandler<PlatformAdminRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PlatformAdminRequirement requirement)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId || context.Resource is not HttpContext httpContext)
        {
            return;
        }
        if (await centralAuth.IsPlatformAdminAsync(userId, httpContext.RequestAborted))
        {
            context.Succeed(requirement);
        }
    }
}

internal sealed class ArtifactContentAuthorizationHandler(ICurrentUser currentUser, ICentralAuthorizationService centralAuth)
    : AuthorizationHandler<ArtifactContentRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, ArtifactContentRequirement requirement)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId || context.Resource is not HttpContext httpContext)
        {
            return;
        }
        var tenantId = httpContext.Features.Get<ResolvedTenantContext>()?.TenantId
            ?? (Guid.TryParse(httpContext.Request.RouteValues["tenantId"]?.ToString(), out var tid) ? new TenantId(tid) : null);
        if (tenantId is null)
        {
            return;
        }
        if (await centralAuth.EvaluateContentAccessAsync(userId, tenantId, httpContext.RequestAborted))
        {
            context.Succeed(requirement);
        }
    }
}
