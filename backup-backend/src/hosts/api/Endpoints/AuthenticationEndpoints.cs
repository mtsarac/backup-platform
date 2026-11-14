using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using BackupPlatform.Api.Security;
using BackupPlatform.Identity;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using QRCoder;
using AuthenticationService = BackupPlatform.Identity.AuthenticationService;

namespace BackupPlatform.Api.Endpoints;

public sealed class LoginRequest
{
    public string LoginIdentifier { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public bool RememberMe { get; init; }
}

public sealed class TotpVerificationRequest
{
    public string Code { get; init; } = string.Empty;
}

public sealed class RecoveryCodeVerificationRequest
{
    public string RecoveryCode { get; init; } = string.Empty;
}

public sealed class EnrollTotpRequest
{
    public string SetupToken { get; init; } = string.Empty;
    public string Code { get; init; } = string.Empty;
    public string CurrentPassword { get; init; } = string.Empty;
}

public sealed class RemoveTotpRequest
{
    public string? CurrentPassword { get; init; }
    public string? Code { get; init; }
}

public sealed class RegenerateRecoveryCodesRequest
{
    public string? CurrentPassword { get; init; }
    public string? Code { get; init; }
}

public sealed class RegisterPasskeyRequest
{
    public Guid ChallengeId { get; init; }
    public string Name { get; init; } = string.Empty;
    public Fido2NetLib.AuthenticatorAttestationRawResponse Response { get; init; } = default!;
    public string? CurrentPassword { get; init; }
    public Guid? VerificationChallengeId { get; init; }
    public Fido2NetLib.AuthenticatorAssertionRawResponse? VerificationResponse { get; init; }
}

public sealed class RemovePasskeyRequest
{
    public string? CurrentPassword { get; init; }
    public Guid? VerificationChallengeId { get; init; }
    public Fido2NetLib.AuthenticatorAssertionRawResponse? VerificationResponse { get; init; }
}

public sealed class PasskeyLoginOptionsRequest
{
    public string? LoginIdentifier { get; init; }
}

public sealed class PasskeyLoginRequest
{
    public Guid ChallengeId { get; init; }
    public Fido2NetLib.AuthenticatorAssertionRawResponse Response { get; init; } = default!;
    public bool RememberMe { get; init; }
}

public sealed class SecondFactorVerifyRequest
{
    public Guid ChallengeId { get; init; }
    public Fido2NetLib.AuthenticatorAssertionRawResponse Response { get; init; } = default!;
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

        // Second factor verification at sign-in (RestrictedSessionAllowed)
        auth.MapPost("/second-factor/totp", VerifyTotpSecondFactorAsync).RequireAuthorization().WithMetadata(new RestrictedSessionAllowed());
        auth.MapPost("/second-factor/recovery-code", VerifyRecoveryCodeSecondFactorAsync).RequireAuthorization().WithMetadata(new RestrictedSessionAllowed());

        // Security settings & factor management (FullyAuthenticated)
        auth.MapGet("/security", GetSecurityStatusAsync).RequireAuthorization();
        auth.MapPost("/security/totp/setup", SetupTotpAsync).RequireAuthorization();
        auth.MapPost("/security/totp/enroll", EnrollTotpAsync).RequireAuthorization();
        auth.MapPost("/security/totp/remove", RemoveTotpAsync).RequireAuthorization();
        auth.MapPost("/security/recovery-codes/regenerate", RegenerateRecoveryCodesAsync).RequireAuthorization();
        // Passkey endpoints
        auth.MapGet("/passkeys", ListPasskeysAsync).RequireAuthorization();
        auth.MapPost("/passkeys/register/options", CreatePasskeyRegistrationOptionsAsync).RequireAuthorization();
        auth.MapPost("/passkeys/register", RegisterPasskeyAsync).RequireAuthorization();
        auth.MapDelete("/passkeys/{id:guid}", RemovePasskeyAsync).RequireAuthorization();
        auth.MapPost("/passkeys/login/options", CreatePasskeyLoginOptionsAsync).AllowAnonymous().RequireRateLimiting(AgentRateLimitOptions.Policies.PasskeyLoginOptions);
        auth.MapPost("/passkeys/login", PasskeyLoginAsync).AllowAnonymous();
        auth.MapPost("/passkeys/second-factor/options", CreateSecondFactorOptionsAsync).RequireAuthorization().WithMetadata(new RestrictedSessionAllowed());
        auth.MapPost("/passkeys/second-factor", VerifySecondFactorAsync).RequireAuthorization().WithMetadata(new RestrictedSessionAllowed());
        auth.MapPost("/change-password", ChangePasswordAsync).RequireAuthorization().WithMetadata(new RestrictedSessionAllowed());
        auth.MapGet("/email-recovery", GetEmailRecoveryAsync).AllowAnonymous();
        auth.MapPost("/password-reset/request", RequestPasswordResetAsync).AllowAnonymous();
        auth.MapPost("/password-reset/complete", CompletePasswordResetAsync).AllowAnonymous();
        auth.MapPost("/email-verification/request", RequestEmailVerificationAsync).RequireAuthorization();
        auth.MapPost("/email-verification/confirm", ConfirmEmailVerificationAsync).AllowAnonymous();
        return endpoints;
    }
    private static IResult GetAntiforgeryToken(HttpContext context, IAntiforgery antiforgery)
    {
        var tokens = antiforgery.GetAndStoreTokens(context);
        return Results.Ok(new { requestToken = tokens.RequestToken, headerName = "X-CSRF-TOKEN" });
    }

    private static async Task<IResult> LoginAsync(LoginRequest request, HttpContext context, AuthenticationService authenticationService, IIdentityStore identityStore, LoginThrottle throttle, TimeProvider clock)
    {
        var ip = context.Connection.RemoteIpAddress?.ToString();
        if (await throttle.RetryAfterAsync("login", null, ip, context.RequestAborted) is { } wait)
        {
            context.Response.Headers.RetryAfter = ((int)Math.Ceiling(wait.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Results.Json(new { error = "Too many failed login attempts. Try again later." }, statusCode: StatusCodes.Status429TooManyRequests);
        }
        string? normalized = null;
        try
        {
            normalized = request.LoginIdentifier?.Contains('@') == true
                ? LoginIdentifierNormalizer.NormalizeEmail(request.LoginIdentifier)
                : LoginIdentifierNormalizer.TryNormalize(request.LoginIdentifier, out var login) ? login : null;
        }
        catch (ArgumentException) { }
        var credential = normalized is null ? null : await identityStore.FindCredentialAsync(normalized, context.RequestAborted);
        var account = credential?.NormalizedLoginIdentifier ?? normalized;
        if (await throttle.RetryAfterAsync("login", account, null, context.RequestAborted) is not null)
            return Results.Json(new { error = "Invalid login identifier or password." }, statusCode: StatusCodes.Status401Unauthorized);
        var device = context.Request.Headers.UserAgent.ToString();
        if (device.Length > 512) device = device[..512];
        var session = await authenticationService.LoginAsync(request.LoginIdentifier ?? "", request.Password, request.RememberMe, device, ip ?? "", context.RequestAborted);
        if (session is null)
        {
            await throttle.FailedAsync("login", account, ip, context.RequestAborted);
            // The attempt that trips the progressive limiter for a real account is reported to its owner.
            if (credential is not null && await throttle.RetryAfterAsync("login", account, null, context.RequestAborted) is not null)
                await identityStore.RecordRepeatedLoginFailuresAsync(credential.UserId, clock.GetUtcNow(), context.RequestAborted);
            return Results.Json(new { error = "Invalid login identifier or password." }, statusCode: StatusCodes.Status401Unauthorized);
        }
        await throttle.SucceededAsync("login", account, ip, context.RequestAborted);
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
        if (session.PasswordChangeRequired)
        {
            return Results.Ok(new { passwordChangeRequired = true, requiresPasswordChange = true });
        }
        if (session.SecondFactorRequired)
        {
            return Results.Ok(new { secondFactorRequired = true, requiresSecondFactor = true });
        }
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

    private static async Task<IResult> VerifyTotpSecondFactorAsync(
        TotpVerificationRequest request,
        HttpContext context,
        IIdentityStore identityStore,
        LoginThrottle throttle,
        IDataProtectionProvider dataProtection,
        TimeProvider clock)
    {
        if (!BrowserAuthentication.TryGetSession(context.User, out var userId, out var sessionId))
            return Results.Unauthorized();

        if (context.Features.Get<BrowserSessionState>() is not { Value: SessionAuthenticationState.SecondFactorPending })
            return Results.BadRequest(new { error = "Second factor verification is not pending." });

        var credential = await identityStore.FindCredentialByUserIdAsync(userId!, context.RequestAborted);
        var account = credential?.NormalizedLoginIdentifier;
        var ip = context.Connection.RemoteIpAddress?.ToString();

        if (await throttle.RetryAfterAsync("totp", account, ip, context.RequestAborted) is { } wait)
        {
            context.Response.Headers.RetryAfter = ((int)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            return Results.Json(new { error = "Too many failed attempts. Try again later." }, statusCode: StatusCodes.Status429TooManyRequests);
        }

        var totpCred = await identityStore.FindTotpCredentialAsync(userId!, context.RequestAborted);
        if (totpCred is null)
        {
            await throttle.FailedAsync("totp", account, ip, context.RequestAborted);
            return Results.Json(new { error = "TOTP is not enrolled." }, statusCode: StatusCodes.Status400BadRequest);
        }

        var protector = dataProtection.CreateProtector("BackupPlatform.Identity.Totp.v1");
        string secretBase32;
        try
        {
            secretBase32 = protector.Unprotect(totpCred.EncryptedSecret);
        }
        catch
        {
            return Results.Json(new { error = "Failed to decrypt TOTP secret." }, statusCode: StatusCodes.Status500InternalServerError);
        }

        var secretBytes = Totp.DecodeBase32(secretBase32);
        if (!Totp.VerifyCode(secretBytes, request.Code, clock.GetUtcNow(), totpCred.LastUsedEpochStep, out var matchedStep))
        {
            await throttle.FailedAsync("totp", account, ip, context.RequestAborted);
            return Results.Json(new { error = "Invalid verification code." }, statusCode: StatusCodes.Status401Unauthorized);
        }

        if (!await identityStore.TryUseTotpStepAsync(userId!, matchedStep, context.RequestAborted))
        {
            await throttle.FailedAsync("totp", account, ip, context.RequestAborted);
            return Results.Json(new { error = "Verification code has already been used." }, statusCode: StatusCodes.Status401Unauthorized);
        }

        await throttle.SucceededAsync("totp", account, ip, context.RequestAborted);
        await identityStore.SetSessionStateAsync(sessionId, userId!, SessionAuthenticationState.FullyAuthenticated, context.RequestAborted);
        return Results.NoContent();
    }

    private static async Task<IResult> VerifyRecoveryCodeSecondFactorAsync(
        RecoveryCodeVerificationRequest request,
        HttpContext context,
        IIdentityStore identityStore,
        LoginThrottle throttle,
        TimeProvider clock)
    {
        if (!BrowserAuthentication.TryGetSession(context.User, out var userId, out var sessionId))
            return Results.Unauthorized();

        if (context.Features.Get<BrowserSessionState>() is not { Value: SessionAuthenticationState.SecondFactorPending })
            return Results.BadRequest(new { error = "Second factor verification is not pending." });

        var credential = await identityStore.FindCredentialByUserIdAsync(userId!, context.RequestAborted);
        var account = credential?.NormalizedLoginIdentifier;
        var ip = context.Connection.RemoteIpAddress?.ToString();

        if (await throttle.RetryAfterAsync("recovery", account, ip, context.RequestAborted) is { } wait)
        {
            context.Response.Headers.RetryAfter = ((int)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            return Results.Json(new { error = "Too many failed recovery code attempts. Try again later." }, statusCode: StatusCodes.Status429TooManyRequests);
        }

        if (string.IsNullOrWhiteSpace(request.RecoveryCode))
        {
            await throttle.FailedAsync("recovery", account, ip, context.RequestAborted);
            return Results.Json(new { error = "Invalid recovery code." }, statusCode: StatusCodes.Status401Unauthorized);
        }

        var hash = Totp.HashRecoveryCode(userId!, request.RecoveryCode);
        var redeemed = await identityStore.TryRedeemRecoveryCodeAsync(userId!, hash, clock.GetUtcNow(), context.RequestAborted);
        if (!redeemed)
        {
            await throttle.FailedAsync("recovery", account, ip, context.RequestAborted);
            return Results.Json(new { error = "Invalid or already used recovery code." }, statusCode: StatusCodes.Status401Unauthorized);
        }

        await throttle.SucceededAsync("recovery", account, ip, context.RequestAborted);
        await identityStore.SetSessionStateAsync(sessionId, userId!, SessionAuthenticationState.FullyAuthenticated, context.RequestAborted);
        return Results.NoContent();
    }

    public sealed record PasswordResetRequest(string? LoginIdentifier);
    public sealed record PasswordResetCompletionRequest(string? Token, string? NewPassword);
    public sealed record EmailVerificationConfirmationRequest(string? Token);

    private static readonly IResult EmailRecoveryUnavailable = Results.Json(new { error = "email_recovery_unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);

    private static async Task<IResult> GetEmailRecoveryAsync(AccountEmailService service, CancellationToken cancellationToken) =>
        Results.Ok(new { available = await service.IsAvailableAsync(cancellationToken) });

    /// <summary>Answers the same way for known, unverified and unknown accounts; every request counts against the account and IP limits.</summary>
    private static async Task<IResult> RequestPasswordResetAsync(PasswordResetRequest? request, HttpContext context, AccountEmailService service, LoginThrottle throttle)
    {
        if (!await service.IsAvailableAsync(context.RequestAborted)) return EmailRecoveryUnavailable;
        var ip = context.Connection.RemoteIpAddress?.ToString();
        var identifier = request?.LoginIdentifier;
        if (await throttle.RetryAfterAsync("password-reset", identifier, ip, context.RequestAborted) is { } wait)
        {
            context.Response.Headers.RetryAfter = ((int)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            return Results.Json(new { error = "Too many requests. Try again later." }, statusCode: StatusCodes.Status429TooManyRequests);
        }
        await throttle.FailedAsync("password-reset", identifier, ip, context.RequestAborted);
        await service.RequestPasswordResetAsync(identifier, context.RequestAborted);
        return Results.Accepted(null, new { accepted = true });
    }

    private static async Task<IResult> CompletePasswordResetAsync(PasswordResetCompletionRequest? request, AccountEmailService service, CancellationToken cancellationToken)
    {
        var (result, failureReason) = await service.CompletePasswordResetAsync(request?.Token, request?.NewPassword, cancellationToken);
        return result switch
        {
            PasswordResetResult.Completed => Results.NoContent(),
            PasswordResetResult.InvalidPassword => Results.BadRequest(new { error = failureReason, message = "The new password does not meet the password policy." }),
            _ => Results.BadRequest(new { error = "invalid_token", message = "The reset link is invalid or has expired." })
        };
    }

    private static async Task<IResult> RequestEmailVerificationAsync(HttpContext context, AccountEmailService service)
    {
        if (!BrowserAuthentication.TryGetSession(context.User, out var userId, out _)) return Results.Unauthorized();
        if (!await service.IsAvailableAsync(context.RequestAborted)) return EmailRecoveryUnavailable;
        return await service.SendVerificationAsync(userId!, context.RequestAborted)
            ? Results.Accepted(null, new { accepted = true })
            : Results.Conflict(new { error = "email_not_verifiable" });
    }

    private static async Task<IResult> ConfirmEmailVerificationAsync(EmailVerificationConfirmationRequest? request, AccountEmailService service, CancellationToken cancellationToken) =>
        await service.ConfirmEmailAsync(request?.Token, cancellationToken)
            ? Results.NoContent()
            : Results.BadRequest(new { error = "invalid_token", message = "The verification link is invalid or has expired." });

    public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

    private static async Task<IResult> ChangePasswordAsync(
        ChangePasswordRequest request, HttpContext context, IIdentityStore identityStore, PasswordHashService passwordHasher, TimeProvider clock)
    {
        if (!BrowserAuthentication.TryGetSession(context.User, out var userId, out var sessionId))
        {
            return Results.Unauthorized();
        }
        if (string.IsNullOrWhiteSpace(request?.CurrentPassword) || string.IsNullOrWhiteSpace(request?.NewPassword))
        {
            return Results.BadRequest(new { error = "invalid_request", message = "Both current and new passwords are required." });
        }
        var credential = await identityStore.FindCredentialByUserIdAsync(userId!, context.RequestAborted);
        if (credential is null) return Results.Unauthorized();
        if (passwordHasher.Verify(userId!, credential.PasswordHash, request.CurrentPassword) != Microsoft.AspNetCore.Identity.PasswordVerificationResult.Success)
        {
            return Results.BadRequest(new { error = "invalid_password", message = "Current password is incorrect." });
        }
        if (string.Equals(request.CurrentPassword.Trim(), request.NewPassword.Trim(), StringComparison.Ordinal))
        {
            return Results.BadRequest(new { error = "password_same_as_current", message = "The new password cannot equal the current password." });
        }
        if (!PasswordPolicy.Validate(request.NewPassword, out var failureReason))
        {
            return Results.BadRequest(new { error = failureReason, message = "The new password does not meet the password policy." });
        }
        var newHash = passwordHasher.Hash(userId!, request.NewPassword);
        var now = clock.GetUtcNow();
        await identityStore.ChangePasswordAsync(userId!, newHash, now, context.RequestAborted);
        var hasSecondFactor = await identityStore.HasSecondFactorAsync(userId!, context.RequestAborted);
        var nextState = hasSecondFactor ? SessionAuthenticationState.SecondFactorPending : SessionAuthenticationState.FullyAuthenticated;
        await identityStore.SetSessionStateAsync(sessionId, userId!, nextState, context.RequestAborted);
        return Results.Ok(new { requiresSecondFactor = hasSecondFactor, secondFactorRequired = hasSecondFactor });
    }
    private static async Task<IResult> GetSecurityStatusAsync(
        HttpContext context,
        IIdentityStore identityStore)
    {
        if (!BrowserAuthentication.TryGetSession(context.User, out var userId, out _))
            return Results.Unauthorized();

        var hasTotp = await identityStore.FindTotpCredentialAsync(userId!, context.RequestAborted) is not null;
        var hasPasskeys = await identityStore.HasPasskeysAsync(userId!, context.RequestAborted);
        var recoveryCodesCount = await identityStore.GetRemainingRecoveryCodesCountAsync(userId!, context.RequestAborted);
        var credential = await identityStore.FindCredentialByUserIdAsync(userId!, context.RequestAborted);
        return Results.Ok(new { hasTotp, hasPasskeys, remainingRecoveryCodes = recoveryCodesCount, hasEmail = credential?.Email is not null, emailVerified = credential?.EmailVerifiedAt is not null });
    }

    private static async Task<IResult> SetupTotpAsync(
        HttpContext context,
        IIdentityStore identityStore,
        IDataProtectionProvider dataProtection,
        TimeProvider clock)
    {
        if (!BrowserAuthentication.TryGetSession(context.User, out var userId, out _))
            return Results.Unauthorized();

        var credential = await identityStore.FindCredentialByUserIdAsync(userId!, context.RequestAborted);
        if (credential is null) return Results.Unauthorized();

        var secretBytes = RandomNumberGenerator.GetBytes(20);
        var secretBase32 = Totp.EncodeBase32(secretBytes);
        var label = Uri.EscapeDataString(credential.LoginIdentifier);
        var qrUri = $"otpauth://totp/BackupPlatform:{label}?secret={secretBase32}&issuer=BackupPlatform&algorithm=SHA1&digits=6&period=30";

        using var qrGenerator = new QRCodeGenerator();
        using var qrData = qrGenerator.CreateQrCode(qrUri, QRCodeGenerator.ECCLevel.M);
        var qrSvg = new SvgQRCode(qrData).GetGraphic(20);

        var setupProtector = dataProtection.CreateProtector("BackupPlatform.Identity.TotpSetup.v1");
        var expiresAt = clock.GetUtcNow().AddMinutes(15).ToUnixTimeSeconds();
        var payload = $"{userId!.Value:D}|{secretBase32}|{expiresAt}";
        var setupToken = setupProtector.Protect(payload);

        return Results.Ok(new
        {
            setupToken,
            manualEntryKey = secretBase32,
            qrUri,
            qrSvg
        });
    }

    private static async Task<IResult> EnrollTotpAsync(
        EnrollTotpRequest request,
        HttpContext context,
        IIdentityStore identityStore,
        LoginThrottle throttle,
        PasswordHashService passwordHasher,
        IDataProtectionProvider dataProtection,
        TimeProvider clock)
    {
        if (!BrowserAuthentication.TryGetSession(context.User, out var userId, out _))
            return Results.Unauthorized();

        var credential = await identityStore.FindCredentialByUserIdAsync(userId!, context.RequestAborted);
        if (credential is null) return Results.Unauthorized();

        var account = credential.NormalizedLoginIdentifier;
        var ip = context.Connection.RemoteIpAddress?.ToString();

        if (await throttle.RetryAfterAsync("login", account, ip, context.RequestAborted) is { } waitLogin)
        {
            context.Response.Headers.RetryAfter = ((int)Math.Ceiling(waitLogin.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            return Results.Json(new { error = "Too many failed login attempts. Try again later." }, statusCode: StatusCodes.Status429TooManyRequests);
        }

        if (await throttle.RetryAfterAsync("totp", account, ip, context.RequestAborted) is { } waitTotp)
        {
            context.Response.Headers.RetryAfter = ((int)Math.Ceiling(waitTotp.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            return Results.Json(new { error = "Too many failed attempts. Try again later." }, statusCode: StatusCodes.Status429TooManyRequests);
        }

        // Security verification: current password required
        if (string.IsNullOrWhiteSpace(request.CurrentPassword))
        {
            return Results.Json(new { error = "Current password is required." }, statusCode: StatusCodes.Status400BadRequest);
        }
        var passwordCheck = passwordHasher.Verify(userId!, credential.PasswordHash, request.CurrentPassword);
        if (passwordCheck == Microsoft.AspNetCore.Identity.PasswordVerificationResult.Failed)
        {
            await throttle.FailedAsync("login", account, ip, context.RequestAborted);
            return Results.Json(new { error = "Current password is incorrect." }, statusCode: StatusCodes.Status400BadRequest);
        }
        await throttle.SucceededAsync("login", account, ip, context.RequestAborted);

        var existingTotp = await identityStore.FindTotpCredentialAsync(userId!, context.RequestAborted);
        if (existingTotp is not null)
        {
            return Results.Json(new { error = "TOTP is already enrolled." }, statusCode: StatusCodes.Status409Conflict);
        }

        // Unpack setup token
        var setupProtector = dataProtection.CreateProtector("BackupPlatform.Identity.TotpSetup.v1");
        string secretBase32;
        try
        {
            var unpacked = setupProtector.Unprotect(request.SetupToken);
            var parts = unpacked.Split('|');
            if (parts.Length != 3 || parts[0] != userId!.Value.ToString("D"))
                return Results.Json(new { error = "Invalid setup token." }, statusCode: StatusCodes.Status400BadRequest);

            var expiresAtUnix = long.Parse(parts[2], CultureInfo.InvariantCulture);
            if (clock.GetUtcNow().ToUnixTimeSeconds() > expiresAtUnix)
                return Results.Json(new { error = "Setup token has expired. Please restart enrollment." }, statusCode: StatusCodes.Status400BadRequest);

            secretBase32 = parts[1];
        }
        catch
        {
            return Results.Json(new { error = "Invalid setup token." }, statusCode: StatusCodes.Status400BadRequest);
        }

        var secretBytes = Totp.DecodeBase32(secretBase32);
        if (!Totp.VerifyCode(secretBytes, request.Code, clock.GetUtcNow(), null, out var matchedStep))
        {
            await throttle.FailedAsync("totp", account, ip, context.RequestAborted);
            return Results.Json(new { error = "Invalid verification code." }, statusCode: StatusCodes.Status400BadRequest);
        }
        await throttle.SucceededAsync("totp", account, ip, context.RequestAborted);

        var protector = dataProtection.CreateProtector("BackupPlatform.Identity.Totp.v1");
        var encryptedSecret = protector.Protect(secretBase32);
        var now = clock.GetUtcNow();
        var totpCredential = new TotpCredential(userId!, encryptedSecret, now, matchedStep);
        var recoveryCodes = Totp.GenerateRecoveryCodes(10);
        var hashes = recoveryCodes.Select(code => Totp.HashRecoveryCode(userId!, code)).ToArray();
        var outcome = await identityStore.SaveTotpCredentialAsync(totpCredential, hashes, now, context.RequestAborted);
        if (outcome == TotpEnrollmentOutcome.Conflict)
        {
            return Results.Json(new { error = "TOTP is already enrolled." }, statusCode: StatusCodes.Status409Conflict);
        }
        if (outcome == TotpEnrollmentOutcome.EnrolledWithoutRecoveryCodes)
        {
            recoveryCodes = [];
        }

        return Results.Ok(new { recoveryCodes });
    }

    private static async Task<IResult> RemoveTotpAsync(
        RemoveTotpRequest request,
        HttpContext context,
        IIdentityStore identityStore,
        LoginThrottle throttle,
        PasswordHashService passwordHasher,
        IDataProtectionProvider dataProtection,
        TimeProvider clock)
    {
        if (!BrowserAuthentication.TryGetSession(context.User, out var userId, out _))
            return Results.Unauthorized();

        var credential = await identityStore.FindCredentialByUserIdAsync(userId!, context.RequestAborted);
        if (credential is null) return Results.Unauthorized();

        var account = credential.NormalizedLoginIdentifier;
        var ip = context.Connection.RemoteIpAddress?.ToString();

        // Security verification: current password OR existing factor code
        var verified = false;
        if (!string.IsNullOrWhiteSpace(request.CurrentPassword))
        {
            if (await throttle.RetryAfterAsync("login", account, ip, context.RequestAborted) is { } waitLogin)
            {
                context.Response.Headers.RetryAfter = ((int)Math.Ceiling(waitLogin.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                return Results.Json(new { error = "Too many failed login attempts. Try again later." }, statusCode: StatusCodes.Status429TooManyRequests);
            }
            var passwordCheck = passwordHasher.Verify(userId!, credential.PasswordHash, request.CurrentPassword);
            if (passwordCheck != Microsoft.AspNetCore.Identity.PasswordVerificationResult.Failed)
            {
                verified = true;
                await throttle.SucceededAsync("login", account, ip, context.RequestAborted);
            }
            else
            {
                await throttle.FailedAsync("login", account, ip, context.RequestAborted);
            }
        }
        if (!verified && !string.IsNullOrWhiteSpace(request.Code))
        {
            if (await throttle.RetryAfterAsync("totp", account, ip, context.RequestAborted) is { } waitTotp)
            {
                context.Response.Headers.RetryAfter = ((int)Math.Ceiling(waitTotp.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                return Results.Json(new { error = "Too many failed attempts. Try again later." }, statusCode: StatusCodes.Status429TooManyRequests);
            }
            var totpCred = await identityStore.FindTotpCredentialAsync(userId!, context.RequestAborted);
            if (totpCred is not null)
            {
                var protector = dataProtection.CreateProtector("BackupPlatform.Identity.Totp.v1");
                var secretBase32 = protector.Unprotect(totpCred.EncryptedSecret);
                var secretBytes = Totp.DecodeBase32(secretBase32);
                if (Totp.VerifyCode(secretBytes, request.Code, clock.GetUtcNow(), totpCred.LastUsedEpochStep, out var matchedStep)
                    && await identityStore.TryUseTotpStepAsync(userId!, matchedStep, context.RequestAborted))
                {
                    verified = true;
                    await throttle.SucceededAsync("totp", account, ip, context.RequestAborted);
                }
                else
                {
                    await throttle.FailedAsync("totp", account, ip, context.RequestAborted);
                }
            }
            else
            {
                await throttle.FailedAsync("totp", account, ip, context.RequestAborted);
            }
        }

        if (!verified)
        {
            return Results.Json(new { error = "Security verification failed. Please provide your current password or TOTP code." }, statusCode: StatusCodes.Status400BadRequest);
        }

        var now = clock.GetUtcNow();
        await identityStore.RemoveTotpCredentialAsync(userId!, now, context.RequestAborted);
        return Results.NoContent();
    }

    private static async Task<IResult> RegenerateRecoveryCodesAsync(
        RegenerateRecoveryCodesRequest request,
        HttpContext context,
        IIdentityStore identityStore,
        LoginThrottle throttle,
        PasswordHashService passwordHasher,
        IDataProtectionProvider dataProtection,
        TimeProvider clock)
    {
        if (!BrowserAuthentication.TryGetSession(context.User, out var userId, out _))
            return Results.Unauthorized();

        var credential = await identityStore.FindCredentialByUserIdAsync(userId!, context.RequestAborted);
        if (credential is null) return Results.Unauthorized();

        var account = credential.NormalizedLoginIdentifier;
        var ip = context.Connection.RemoteIpAddress?.ToString();

        // Security verification: current password OR existing factor code
        var verified = false;
        if (!string.IsNullOrWhiteSpace(request.CurrentPassword))
        {
            if (await throttle.RetryAfterAsync("login", account, ip, context.RequestAborted) is { } waitLogin)
            {
                context.Response.Headers.RetryAfter = ((int)Math.Ceiling(waitLogin.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                return Results.Json(new { error = "Too many failed login attempts. Try again later." }, statusCode: StatusCodes.Status429TooManyRequests);
            }
            var passwordCheck = passwordHasher.Verify(userId!, credential.PasswordHash, request.CurrentPassword);
            if (passwordCheck != Microsoft.AspNetCore.Identity.PasswordVerificationResult.Failed)
            {
                verified = true;
                await throttle.SucceededAsync("login", account, ip, context.RequestAborted);
            }
            else
            {
                await throttle.FailedAsync("login", account, ip, context.RequestAborted);
            }
        }
        if (!verified && !string.IsNullOrWhiteSpace(request.Code))
        {
            if (await throttle.RetryAfterAsync("totp", account, ip, context.RequestAborted) is { } waitTotp)
            {
                context.Response.Headers.RetryAfter = ((int)Math.Ceiling(waitTotp.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                return Results.Json(new { error = "Too many failed attempts. Try again later." }, statusCode: StatusCodes.Status429TooManyRequests);
            }
            var totpCred = await identityStore.FindTotpCredentialAsync(userId!, context.RequestAborted);
            if (totpCred is not null)
            {
                var protector = dataProtection.CreateProtector("BackupPlatform.Identity.Totp.v1");
                var secretBase32 = protector.Unprotect(totpCred.EncryptedSecret);
                var secretBytes = Totp.DecodeBase32(secretBase32);
                if (Totp.VerifyCode(secretBytes, request.Code, clock.GetUtcNow(), totpCred.LastUsedEpochStep, out var matchedStep)
                    && await identityStore.TryUseTotpStepAsync(userId!, matchedStep, context.RequestAborted))
                {
                    verified = true;
                    await throttle.SucceededAsync("totp", account, ip, context.RequestAborted);
                }
                else
                {
                    await throttle.FailedAsync("totp", account, ip, context.RequestAborted);
                }
            }
            else
            {
                await throttle.FailedAsync("totp", account, ip, context.RequestAborted);
            }
        }

        if (!verified)
        {
            return Results.Json(new { error = "Security verification failed. Please provide your current password or TOTP code." }, statusCode: StatusCodes.Status400BadRequest);
        }

        var now = clock.GetUtcNow();
        var recoveryCodes = Totp.GenerateRecoveryCodes(10);
        var hashes = recoveryCodes.Select(code => Totp.HashRecoveryCode(userId!, code));
        await identityStore.SaveRecoveryCodesAsync(userId!, hashes, now, context.RequestAborted);

        return Results.Ok(new { recoveryCodes });
    }
    private static IResult PasskeysNotConfigured => Results.Json(new { error = "passkeys_not_configured" }, statusCode: StatusCodes.Status503ServiceUnavailable);

    private static async Task<IResult> ListPasskeysAsync(HttpContext context, IIdentityStore store)
    {
        if (!BrowserAuthentication.TryGetSession(context.User, out var userId, out _)) return Results.Unauthorized();
        var passkeys = await store.ListPasskeysAsync(userId!, context.RequestAborted);
        return Results.Ok(passkeys.Select(p => new
        {
            id = p.Id,
            name = p.Name,
            createdAt = p.CreatedAt,
            lastUsedAt = p.LastUsedAt
        }));
    }

    private static async Task<IResult> CreatePasskeyRegistrationOptionsAsync(HttpContext context, PasskeyService passkeyService)
    {
        if (!BrowserAuthentication.TryGetSession(context.User, out var userId, out _)) return Results.Unauthorized();
        try
        {
            var (options, challengeId) = await passkeyService.CreateRegistrationOptionsAsync(userId!, context, context.RequestAborted);
            return Results.Ok(new { options, challengeId });
        }
        catch (PasskeysNotConfiguredException) { return PasskeysNotConfigured; }
    }

    private static async Task<IResult> RegisterPasskeyAsync(RegisterPasskeyRequest request, HttpContext context, PasskeyService passkeyService)
    {
        if (!BrowserAuthentication.TryGetSession(context.User, out var userId, out _)) return Results.Unauthorized();
        try
        {
            var passkey = await passkeyService.RegisterPasskeyAsync(
                userId!,
                request.ChallengeId,
                request.Name,
                request.Response,
                request.CurrentPassword,
                request.VerificationChallengeId,
                request.VerificationResponse,
                context,
                context.RequestAborted);

            if (passkey is null)
            {
                return Results.Json(new { error = "Security verification failed or invalid challenge." }, statusCode: StatusCodes.Status401Unauthorized);
            }

            return Results.Json(new { id = passkey.Value.Credential.Id, name = passkey.Value.Credential.Name, createdAt = passkey.Value.Credential.CreatedAt, recoveryCodes = passkey.Value.RecoveryCodes }, statusCode: StatusCodes.Status201Created);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static async Task<IResult> RemovePasskeyAsync(Guid id, RemovePasskeyRequest request, HttpContext context, PasskeyService passkeyService, IIdentityStore store)
    {
        if (!BrowserAuthentication.TryGetSession(context.User, out var userId, out _)) return Results.Unauthorized();
        var existing = await store.FindPasskeyByIdAsync(id, userId!, context.RequestAborted);
        if (existing is null) return Results.NotFound();

        var success = await passkeyService.RemovePasskeyAsync(userId!, id, request.CurrentPassword, request.VerificationChallengeId, request.VerificationResponse, context, context.RequestAborted);
        if (!success)
        {
            return Results.Json(new { error = "Security verification failed." }, statusCode: StatusCodes.Status401Unauthorized);
        }
        return Results.NoContent();
    }

    private static async Task<IResult> CreatePasskeyLoginOptionsAsync(PasskeyLoginOptionsRequest? request, HttpContext context, PasskeyService passkeyService)
    {
        try
        {
            var (options, challengeId) = await passkeyService.CreateLoginOptionsAsync(request?.LoginIdentifier, context, context.RequestAborted);
            return Results.Ok(new { options, challengeId });
        }
        catch (PasskeysNotConfiguredException) { return PasskeysNotConfigured; }
    }

    private static async Task<IResult> PasskeyLoginAsync(PasskeyLoginRequest request, HttpContext context, PasskeyService passkeyService, LoginThrottle throttle)
    {
        var ip = context.Connection.RemoteIpAddress?.ToString();
        if (await throttle.RetryAfterAsync("passkey_assertion", null, ip, context.RequestAborted) is { } wait)
        {
            context.Response.Headers.RetryAfter = ((int)Math.Ceiling(wait.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Results.Json(new { error = "Too many failed attempts. Try again later." }, statusCode: StatusCodes.Status429TooManyRequests);
        }

        var session = await passkeyService.LoginWithPasskeyAsync(
            request.ChallengeId,
            request.Response,
            request.RememberMe,
            context,
            context.RequestAborted);

        if (session is null)
        {
            return context.Response.Headers.RetryAfter.Count > 0
                ? Results.Json(new { error = "Too many failed attempts. Try again later." }, statusCode: StatusCodes.Status429TooManyRequests)
                : Results.Json(new { error = "Invalid passkey assertion or account disabled." }, statusCode: StatusCodes.Status401Unauthorized);
        }

        return Results.NoContent();
    }

    private static async Task<IResult> CreateSecondFactorOptionsAsync(HttpContext context, PasskeyService passkeyService)
    {
        if (!BrowserAuthentication.TryGetSession(context.User, out var userId, out var sessionId)) return Results.Unauthorized();
        try
        {
            var (options, challengeId) = await passkeyService.CreateSecondFactorOptionsAsync(userId!, sessionId, context, context.RequestAborted);
            return Results.Ok(new { options, challengeId });
        }
        catch (PasskeysNotConfiguredException) { return PasskeysNotConfigured; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static async Task<IResult> VerifySecondFactorAsync(SecondFactorVerifyRequest request, HttpContext context, PasskeyService passkeyService, LoginThrottle throttle)
    {
        if (!BrowserAuthentication.TryGetSession(context.User, out var userId, out var sessionId)) return Results.Unauthorized();
        var ip = context.Connection.RemoteIpAddress?.ToString();

        if (await throttle.RetryAfterAsync("passkey_assertion", null, ip, context.RequestAborted) is { } wait)
        {
            context.Response.Headers.RetryAfter = ((int)Math.Ceiling(wait.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Results.Json(new { error = "Too many failed attempts. Try again later." }, statusCode: StatusCodes.Status429TooManyRequests);
        }

        var success = await passkeyService.VerifySecondFactorAsync(
            userId!,
            sessionId,
            request.ChallengeId,
            request.Response,
            context,
            context.RequestAborted);

        if (!success)
        {
            return context.Response.Headers.RetryAfter.Count > 0
                ? Results.Json(new { error = "Too many failed attempts. Try again later." }, statusCode: StatusCodes.Status429TooManyRequests)
                : Results.Json(new { error = "Invalid passkey assertion." }, statusCode: StatusCodes.Status401Unauthorized);
        }

        return Results.NoContent();
    }
}
