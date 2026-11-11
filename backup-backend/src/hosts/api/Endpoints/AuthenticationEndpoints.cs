using System.Security.Claims;
using BackupPlatform.Api.Security;
using BackupPlatform.Identity;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using AuthenticationService = BackupPlatform.Identity.AuthenticationService;

namespace BackupPlatform.Api.Endpoints;

public sealed class LoginRequest
{
    public string LoginIdentifier { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public bool RememberMe { get; init; }
}

public static class AuthenticationEndpoints
{
    public static IEndpointRouteBuilder MapAuthenticationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var auth = endpoints.MapGroup("/api/auth");
        auth.MapGet("/antiforgery", GetAntiforgeryToken).AllowAnonymous().WithMetadata(new RestrictedSessionAllowed());
        auth.MapPost("/login", LoginAsync).AllowAnonymous();
        auth.MapPost("/logout", LogoutAsync).RequireAuthorization().WithMetadata(new RestrictedSessionAllowed());
        auth.MapGet("/sessions", ListSessionsAsync).RequireAuthorization();
        auth.MapDelete("/sessions/{id:guid}", EndSessionAsync).RequireAuthorization();
        auth.MapDelete("/sessions", EndAllSessionsAsync).RequireAuthorization();
        return endpoints;
    }

    private static IResult GetAntiforgeryToken(HttpContext context, IAntiforgery antiforgery)
    {
        var tokens = antiforgery.GetAndStoreTokens(context);
        return Results.Ok(new { requestToken = tokens.RequestToken, headerName = "X-CSRF-TOKEN" });
    }

    private static async Task<IResult> LoginAsync(LoginRequest request, HttpContext context, AuthenticationService authenticationService, LoginThrottle throttle)
    {
        var ip = context.Connection.RemoteIpAddress?.ToString();
        if (await throttle.RetryAfterAsync("login", request.LoginIdentifier, ip, context.RequestAborted) is { } wait)
        {
            context.Response.Headers.RetryAfter = ((int)Math.Ceiling(wait.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Results.Json(new { error = "Too many failed login attempts. Try again later." }, statusCode: StatusCodes.Status429TooManyRequests);
        }
        var device = context.Request.Headers.UserAgent.ToString();
        if (device.Length > 512) device = device[..512];
        var session = await authenticationService.LoginAsync(request.LoginIdentifier, request.Password, request.RememberMe, device, ip ?? "", context.RequestAborted);
        if (session is null)
        {
            await throttle.FailedAsync("login", request.LoginIdentifier, ip, context.RequestAborted);
            return Results.Json(new { error = "Invalid login identifier or password." }, statusCode: StatusCodes.Status401Unauthorized);
        }
        await throttle.SucceededAsync("login", request.LoginIdentifier, ip, context.RequestAborted);
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, session.UserId.Value.ToString("D")),
            new Claim(BrowserAuthentication.SessionClaimType, session.SessionId.ToString("D"))
        ], BrowserAuthentication.Scheme);
        await context.SignInAsync(BrowserAuthentication.Scheme, new ClaimsPrincipal(identity), new AuthenticationProperties
        {
            IsPersistent = request.RememberMe,
            AllowRefresh = false
        });
        return Results.NoContent();
    }

    private static async Task<IResult> LogoutAsync(HttpContext context, IIdentityStore identityStore, TimeProvider clock)
    {
        if (!BrowserAuthentication.TryGetSession(context.User, out var userId, out var sessionId))
        {
            return Results.Unauthorized();
        }
        await identityStore.EndSessionAsync(sessionId, userId!, clock.GetUtcNow(), context.RequestAborted);
        await context.SignOutAsync(BrowserAuthentication.Scheme);
        return Results.NoContent();
    }

    private static async Task<IResult> ListSessionsAsync(HttpContext context, IIdentityStore store, TimeProvider clock)
    {
        if (!BrowserAuthentication.TryGetSession(context.User, out var userId, out var current)) return Results.Unauthorized();
        var sessions = await store.ListSessionsAsync(userId!, clock.GetUtcNow(), context.RequestAborted);
        return Results.Ok(sessions.Select(session => new { session.Id, session.Device, session.IpAddress, session.LastActivityAt, isCurrent = session.Id == current }));
    }

    private static async Task<IResult> EndSessionAsync(Guid id, HttpContext context, IIdentityStore store, TimeProvider clock)
    {
        if (!BrowserAuthentication.TryGetSession(context.User, out var userId, out var current)) return Results.Unauthorized();
        if (!await store.EndSessionAsync(id, userId!, clock.GetUtcNow(), context.RequestAborted)) return Results.NotFound();
        if (id == current) await context.SignOutAsync(BrowserAuthentication.Scheme);
        return Results.NoContent();
    }

    private static async Task<IResult> EndAllSessionsAsync(HttpContext context, IIdentityStore store, TimeProvider clock)
    {
        if (!BrowserAuthentication.TryGetSession(context.User, out var userId, out _)) return Results.Unauthorized();
        await store.EndAllSessionsAsync(userId!, clock.GetUtcNow(), context.RequestAborted);
        await context.SignOutAsync(BrowserAuthentication.Scheme);
        return Results.NoContent();
    }
}
