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
}

public static class AuthenticationEndpoints
{
    public static IEndpointRouteBuilder MapAuthenticationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var auth = endpoints.MapGroup("/api/auth");
        auth.MapGet("/antiforgery", GetAntiforgeryToken).AllowAnonymous();
        auth.MapPost("/login", LoginAsync).AllowAnonymous();
        auth.MapPost("/logout", LogoutAsync).RequireAuthorization();
        return endpoints;
    }

    private static IResult GetAntiforgeryToken(HttpContext context, IAntiforgery antiforgery)
    {
        var tokens = antiforgery.GetAndStoreTokens(context);
        return Results.Ok(new { requestToken = tokens.RequestToken, headerName = "X-CSRF-TOKEN" });
    }

    private static async Task<IResult> LoginAsync(LoginRequest request, HttpContext context, AuthenticationService authenticationService, LoginThrottle throttle)
    {
        if (await throttle.RetryAfterAsync(request.LoginIdentifier, context.RequestAborted) is { } wait)
        {
            context.Response.Headers.RetryAfter = ((int)Math.Ceiling(wait.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Results.Json(new { error = "Too many failed login attempts. Try again later." }, statusCode: StatusCodes.Status429TooManyRequests);
        }
        var session = await authenticationService.LoginAsync(request.LoginIdentifier, request.Password, context.RequestAborted);
        if (session is null)
        {
            await throttle.FailedAsync(request.LoginIdentifier, context.RequestAborted);
            return Results.Json(new { error = "Invalid login identifier or password." }, statusCode: StatusCodes.Status401Unauthorized);
        }
        await throttle.SucceededAsync(request.LoginIdentifier, context.RequestAborted);
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, session.UserId.Value.ToString("D")),
            new Claim(BrowserAuthentication.SessionClaimType, session.SessionId.ToString("D"))
        ], BrowserAuthentication.Scheme);
        await context.SignInAsync(BrowserAuthentication.Scheme, new ClaimsPrincipal(identity), new AuthenticationProperties
        {
            IsPersistent = false,
            AllowRefresh = false,
            ExpiresUtc = session.ExpiresAt
        });
        return Results.NoContent();
    }

    private static async Task<IResult> LogoutAsync(HttpContext context, IIdentityStore identityStore)
    {
        if (!BrowserAuthentication.TryGetSession(context.User, out var userId, out var sessionId))
        {
            return Results.Unauthorized();
        }
        await identityStore.RevokeSessionAsync(sessionId, userId!, context.RequestAborted);
        await context.SignOutAsync(BrowserAuthentication.Scheme);
        return Results.NoContent();
    }
}
