using System.Security.Claims;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace BackupPlatform.Api.Security;

/// <summary>Browser-only authentication boundary with protected user/session references, not tenant role claims.</summary>
/// <remarks>
/// The database session must remain active on every request; deleting it on logout also rejects cookie replay.
/// Membership and platform grants are read independently from the authoritative database, never trusted from a cookie.
/// Production assumes HTTPS and a persisted, protected ASP.NET Data Protection key ring managed by deployment.
/// </remarks>
public static class BrowserAuthentication
{
    public const string Scheme = "BackupPlatform.Browser";
    public const string SessionClaimType = "backup:session";
    public const string CookieName = "__Host-BackupPlatform.Auth";

    public static bool TryGetSession(ClaimsPrincipal? principal, out UserId? userId, out Guid sessionId)
    {
        userId = null;
        sessionId = Guid.Empty;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return false;
        }
        if (!TryGetClaimGuid(principal, ClaimTypes.NameIdentifier, out var userValue)
            || !TryGetClaimGuid(principal, SessionClaimType, out sessionId))
        {
            return false;
        }
        userId = new UserId(userValue);
        return true;
    }

    private static bool TryGetClaimGuid(ClaimsPrincipal principal, string claimType, out Guid value)
    {
        value = Guid.Empty;
        var found = false;
        foreach (var claim in principal.FindAll(claimType))
        {
            if (found || !Guid.TryParse(claim.Value, out value) || value == Guid.Empty)
            {
                return false;
            }
            found = true;
        }
        return found;
    }
}

public sealed class BrowserCookieEvents(IIdentityStore identityStore, TimeProvider timeProvider) : CookieAuthenticationEvents
{
    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        if (BrowserAuthentication.TryGetSession(context.Principal, out var userId, out var sessionId)
            && await identityStore.IsSessionActiveAsync(sessionId, userId!, timeProvider.GetUtcNow(), context.HttpContext.RequestAborted))
        {
            return;
        }
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(BrowserAuthentication.Scheme);
    }

    public override Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }

    public override Task RedirectToAccessDenied(RedirectContext<CookieAuthenticationOptions> context)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }
}
