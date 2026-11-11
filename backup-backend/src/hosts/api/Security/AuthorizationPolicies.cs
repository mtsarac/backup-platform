using BackupPlatform.Application.Identity;
using BackupPlatform.Application.Tenancy;
using BackupPlatform.Domain.Identity;
using Microsoft.AspNetCore.Authorization;
using BackupPlatform.Application.Downloads;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace BackupPlatform.Api.Security;

public static class AuthorizationPolicies
{
    public const string TenantAccess = "TenantAccess";
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
            return;
        }
        if (await centralAuth.IsPlatformAdminAsync(userId, httpContext.RequestAborted))
        {
            var path = httpContext.Request.Path.Value ?? "";
            if (HttpMethods.IsPost(httpContext.Request.Method) && path.EndsWith("/downloads", StringComparison.OrdinalIgnoreCase))
            {
                context.Succeed(requirement);
                return;
            }

            Guid artifactId = Guid.TryParse(httpContext.Request.RouteValues["artifactId"]?.ToString(), out var aid) ? aid : Guid.Empty;
            Guid? shareId = null;
            if (Guid.TryParse(httpContext.Request.Query["shareId"], out var qSid))
            {
                shareId = qSid;
            }
            else if (Guid.TryParse(httpContext.Request.Headers["X-Share-Id"], out var hSid))
            {
                shareId = hSid;
            }
            bool allowExpiredShare = false;
            if (shareId is null && httpContext.Request.RouteValues.TryGetValue("sessionId", out var sVal) && Guid.TryParse(sVal?.ToString(), out var sessionId))
            {
                var downloadStore = httpContext.RequestServices.GetService<IDownloadStore>();
                if (downloadStore is not null)
                {
                    var session = await downloadStore.GetAsync(tenantId, sessionId, httpContext.RequestAborted);
                    if (session is not null && session.UserId == userId.Value && session.ArtifactId == artifactId)
                    {
                        shareId = session.ShareId;
                        allowExpiredShare = session.State == BackupPlatform.Domain.Downloads.DownloadState.Streaming || session.IsTerminal;
                    }
                }
            }

            var auth = await centralAuth.EvaluateDownloadAccessAsync(userId, tenantId, artifactId, shareId, httpContext.RequestAborted, allowExpiredShare);
            if (auth.Authorized)
            {
                context.Succeed(requirement);
            }
        }
    }
}
