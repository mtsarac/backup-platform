using System.Security.Claims;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Identity;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;

namespace BackupPlatform.Api.Security;

/// <summary>Passkey relying party ID or origins are not configured: an operator state, not a server fault.</summary>
public sealed class PasskeysNotConfiguredException() : InvalidOperationException("Passkey relying party ID and origins must be configured.");

public sealed class PasskeyService(
    IIdentityStore identityStore,
    PasswordHashService passwordHashService,
    LoginThrottle throttle,
    TimeProvider timeProvider,
    IConfiguration configuration)
{
    public IFido2 CreateFido2(HttpContext context)
    {
        var rpId = configuration["Authentication:Passkeys:RelyingPartyId"];
        var configuredOrigins = configuration.GetSection("Authentication:Passkeys:Origins").Get<string[]>();
        if (string.IsNullOrWhiteSpace(rpId) || configuredOrigins is null || configuredOrigins.Length == 0)
            throw new PasskeysNotConfiguredException();
        var origins = new HashSet<string>(configuredOrigins, StringComparer.OrdinalIgnoreCase);

        var fidoConfig = new Fido2Configuration
        {
            RPID = rpId,
            RPName = configuration["Authentication:Passkeys:RelyingPartyName"] ?? "Backup Platform",
            Origins = origins,
            TimestampDriftTolerance = 300_000
        };

        return new Fido2(fidoConfig);
    }

    public async Task<(CredentialCreateOptions Options, Guid ChallengeId)> CreateRegistrationOptionsAsync(
        UserId userId,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!BrowserAuthentication.TryGetSession(context.User, out _, out var sessionId))
            throw new InvalidOperationException("A session is required to register a passkey.");
        var now = timeProvider.GetUtcNow();
        var existing = await identityStore.ListPasskeysAsync(userId, cancellationToken);
        var excludeCredentials = existing.Select(p => new PublicKeyCredentialDescriptor(p.CredentialId)).ToList();

        var passwordCred = await identityStore.FindCredentialByUserIdAsync(userId, cancellationToken);
        var displayName = await identityStore.GetUserDisplayNameAsync(userId, cancellationToken) ?? "User";
        var user = new Fido2User
        {
            Id = userId.Value.ToByteArray(),
            Name = passwordCred?.LoginIdentifier ?? userId.Value.ToString("D"),
            DisplayName = displayName
        };

        var fido2 = CreateFido2(context);
        var options = fido2.RequestNewCredential(new RequestNewCredentialParams
        {
            User = user,
            ExcludeCredentials = excludeCredentials,
            AuthenticatorSelection = new AuthenticatorSelection
            {
                ResidentKey = ResidentKeyRequirement.Preferred,
                UserVerification = UserVerificationRequirement.Preferred
            },
            AttestationPreference = AttestationConveyancePreference.None
        });

        var challengeId = Guid.NewGuid();
        var challenge = new PasskeyChallenge(
            challengeId,
            userId,
            sessionId,
            "registration",
            options.ToJson(),
            now,
            now.AddMinutes(5));

        await identityStore.SaveChallengeAsync(challenge, cancellationToken);
        return (options, challengeId);
    }

    public async Task<(PasskeyCredential Credential, string[] RecoveryCodes)?> RegisterPasskeyAsync(
        UserId userId,
        Guid challengeId,
        string name,
        AuthenticatorAttestationRawResponse response,
        string? currentPassword,
        Guid? verificationChallengeId,
        AuthenticatorAssertionRawResponse? verificationResponse,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Passkey name is required.", nameof(name));
        if (name.Length > 128) name = name[..128];

        var now = timeProvider.GetUtcNow();

        if (!await VerifyManagementAsync(userId, currentPassword, verificationChallengeId, verificationResponse, context, cancellationToken))
            return null;

        var challenge = await identityStore.ConsumeChallengeAsync(challengeId, "registration", now, cancellationToken);
        if (!BrowserAuthentication.TryGetSession(context.User, out _, out var sessionId)
            || challenge is null || challenge.UserId != userId || challenge.SessionId != sessionId)
            return null;

        var originalOptions = CredentialCreateOptions.FromJson(challenge.OptionsJson);
        var fido2 = CreateFido2(context);

        var result = await fido2.MakeNewCredentialAsync(new MakeNewCredentialParams
        {
            AttestationResponse = response,
            OriginalOptions = originalOptions,
            IsCredentialIdUniqueToUserCallback = async (args, ct) =>
                (await identityStore.FindPasskeyByCredentialIdAsync(args.CredentialId, ct)) is null
        }, cancellationToken);

        var passkey = new PasskeyCredential(
            Guid.NewGuid(),
            userId,
            name,
            result.Id,
            result.PublicKey,
            result.SignCount,
            result.AaGuid == Guid.Empty ? null : result.AaGuid,
            now);
        var recoveryCodes = Totp.GenerateRecoveryCodes(10);
        var hashes = recoveryCodes.Select(code => Totp.HashRecoveryCode(userId, code)).ToArray();
        var issued = await identityStore.AddPasskeyAsync(passkey, hashes, now, cancellationToken);
        return (passkey, issued ? recoveryCodes : []);
    }

    public async Task<(AssertionOptions Options, Guid ChallengeId)> CreateLoginOptionsAsync(
        string? loginIdentifier,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var allowedCredentials = new List<PublicKeyCredentialDescriptor>();
        UserId? targetUserId = null;

        var fido2 = CreateFido2(context);
        var options = fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = allowedCredentials,
            UserVerification = UserVerificationRequirement.Required
        });

        var challengeId = Guid.NewGuid();
        var challenge = new PasskeyChallenge(
            challengeId,
            targetUserId,
            GetClientFlowId(context),
            "login",
            options.ToJson(),
            now,
            now.AddMinutes(5));

        await identityStore.SaveChallengeAsync(challenge, cancellationToken);
        return (options, challengeId);
    }

    public async Task<UserSession?> LoginWithPasskeyAsync(
        Guid challengeId,
        AuthenticatorAssertionRawResponse response,
        bool rememberMe,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var ip = context.Connection.RemoteIpAddress?.ToString();

        if (await throttle.RetryAfterAsync("passkey_assertion", null, ip, cancellationToken) is not null)
        {
            return null;
        }

        var challenge = await identityStore.ConsumeChallengeAsync(challengeId, "login", now, cancellationToken);
        if (challenge is null || (challenge.SessionId != null && challenge.SessionId != GetClientFlowId(context)))
        {
            await throttle.FailedAsync("passkey_assertion", null, ip, cancellationToken);
            return null;
        }

        var passkey = await identityStore.FindPasskeyByCredentialIdAsync(response.RawId, cancellationToken);
        if (passkey is null || (challenge.UserId is not null && challenge.UserId != passkey.UserId))
        {
            var failedAccount = challenge.UserId is { } boundUser
                ? (await identityStore.FindCredentialByUserIdAsync(boundUser, cancellationToken))?.LoginIdentifier
                : null;
            await throttle.FailedAsync("passkey_assertion", failedAccount, ip, cancellationToken);
            return null;
        }

        var userCred = await identityStore.FindCredentialByUserIdAsync(passkey.UserId, cancellationToken);
        var accountIdentifier = userCred?.LoginIdentifier;

        if (accountIdentifier != null && await throttle.RetryAfterAsync("passkey_assertion", accountIdentifier, ip, cancellationToken) is { } waitAccount)
        {
            context.Response.Headers.RetryAfter = ((int)Math.Ceiling(waitAccount.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return null;
        }

        // Disabled account check
        if (await identityStore.IsUserDisabledAsync(passkey.UserId, cancellationToken))
        {
            await throttle.FailedAsync("passkey_assertion", accountIdentifier, ip, cancellationToken);
            await identityStore.RecordLoginFailureAsync(passkey.UserId, now, cancellationToken);
            return null;
        }

        var originalOptions = AssertionOptions.FromJson(challenge.OptionsJson);
        var fido2 = CreateFido2(context);

        VerifyAssertionResult result;
        try
        {
            result = await fido2.MakeAssertionAsync(new MakeAssertionParams
            {
                AssertionResponse = response,
                OriginalOptions = originalOptions,
                StoredPublicKey = passkey.PublicKey,
                StoredSignatureCounter = passkey.SignatureCounter,
                IsUserHandleOwnerOfCredentialIdCallback = (args, _) => Task.FromResult(true)
            }, cancellationToken);
        }
        catch (Exception)
        {
            await throttle.FailedAsync("passkey_assertion", accountIdentifier, ip, cancellationToken);
            await identityStore.RecordLoginFailureAsync(passkey.UserId, now, cancellationToken);
            return null;
        }

        // Passwordless login requires user verification
        var userVerified = response.Response?.AuthenticatorData != null
            && response.Response.AuthenticatorData.Length > 32
            && (response.Response.AuthenticatorData[32] & 0x04) != 0;
        if (!userVerified)
        {
            await throttle.FailedAsync("passkey_assertion", accountIdentifier, ip, cancellationToken);
            await identityStore.RecordLoginFailureAsync(passkey.UserId, now, cancellationToken);
            return null;
        }

        if (!await identityStore.UpdatePasskeyUsageAsync(passkey.Id, result.SignCount, now, cancellationToken))
        {
            await throttle.FailedAsync("passkey_assertion", accountIdentifier, ip, cancellationToken);
            return null;
        }
        await throttle.SucceededAsync("passkey_assertion", accountIdentifier, ip, cancellationToken);

        var device = context.Request.Headers.UserAgent.ToString();
        if (device.Length > 512) device = device[..512];

        var session = new UserSession(
            Guid.NewGuid(),
            passkey.UserId,
            now,
            rememberMe,
            device,
            ip ?? "",
            SessionAuthenticationState.FullyAuthenticated);

        await identityStore.CreateSessionAsync(session, now, cancellationToken);

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, session.UserId.Value.ToString("D")),
            new Claim(BrowserAuthentication.SessionClaimType, session.Id.ToString("D"))
        ], BrowserAuthentication.Scheme);

        await context.SignInAsync(BrowserAuthentication.Scheme, new ClaimsPrincipal(identity), new AuthenticationProperties
        {
            IsPersistent = rememberMe,
            AllowRefresh = false
        });

        return session;
    }

    public async Task<(AssertionOptions Options, Guid ChallengeId)> CreateSecondFactorOptionsAsync(
        UserId userId,
        Guid sessionId,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var passkeys = await identityStore.ListPasskeysAsync(userId, cancellationToken);
        if (passkeys.Count == 0)
            throw new InvalidOperationException("User has no registered passkeys.");

        var allowedCredentials = passkeys.Select(p => new PublicKeyCredentialDescriptor(p.CredentialId)).ToList();
        var fido2 = CreateFido2(context);
        var options = fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = allowedCredentials,
            UserVerification = UserVerificationRequirement.Preferred
        });

        var challengeId = Guid.NewGuid();
        var challenge = new PasskeyChallenge(
            challengeId,
            userId,
            sessionId,
            "second_factor",
            options.ToJson(),
            now,
            now.AddMinutes(5));

        await identityStore.SaveChallengeAsync(challenge, cancellationToken);
        return (options, challengeId);
    }

    public async Task<bool> VerifySecondFactorAsync(
        UserId userId,
        Guid sessionId,
        Guid challengeId,
        AuthenticatorAssertionRawResponse response,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var ip = context.Connection.RemoteIpAddress?.ToString();

        var userCred = await identityStore.FindCredentialByUserIdAsync(userId, cancellationToken);
        var accountIdentifier = userCred?.LoginIdentifier;

        if (await throttle.RetryAfterAsync("passkey_assertion", accountIdentifier, ip, cancellationToken) is { } wait)
        {
            context.Response.Headers.RetryAfter = ((int)Math.Ceiling(wait.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return false;
        }

        var challenge = await identityStore.ConsumeChallengeAsync(challengeId, "second_factor", now, cancellationToken);
        if (challenge is null || challenge.UserId != userId || challenge.SessionId != sessionId)
        {
            await throttle.FailedAsync("passkey_assertion", accountIdentifier, ip, cancellationToken);
            return false;
        }

        if (await identityStore.IsUserDisabledAsync(userId, cancellationToken))
        {
            await throttle.FailedAsync("passkey_assertion", accountIdentifier, ip, cancellationToken);
            return false;
        }

        var passkey = await identityStore.FindPasskeyByCredentialIdAsync(response.RawId, cancellationToken);
        if (passkey is null || passkey.UserId != userId)
        {
            await throttle.FailedAsync("passkey_assertion", accountIdentifier, ip, cancellationToken);
            return false;
        }

        var originalOptions = AssertionOptions.FromJson(challenge.OptionsJson);
        var fido2 = CreateFido2(context);

        VerifyAssertionResult result;
        try
        {
            result = await fido2.MakeAssertionAsync(new MakeAssertionParams
            {
                AssertionResponse = response,
                OriginalOptions = originalOptions,
                StoredPublicKey = passkey.PublicKey,
                StoredSignatureCounter = passkey.SignatureCounter,
                IsUserHandleOwnerOfCredentialIdCallback = (args, _) => Task.FromResult(true)
            }, cancellationToken);
        }
        catch (Exception)
        {
            await throttle.FailedAsync("passkey_assertion", accountIdentifier, ip, cancellationToken);
            return false;
        }

        if (!await identityStore.UpdatePasskeyUsageAsync(passkey.Id, result.SignCount, now, cancellationToken))
        {
            await throttle.FailedAsync("passkey_assertion", accountIdentifier, ip, cancellationToken);
            return false;
        }
        await identityStore.SetSessionStateAsync(sessionId, userId, SessionAuthenticationState.FullyAuthenticated, cancellationToken);
        await throttle.SucceededAsync("passkey_assertion", accountIdentifier, ip, cancellationToken);

        return true;
    }

    public async Task<bool> RemovePasskeyAsync(
        UserId userId,
        Guid credentialId,
        string? currentPassword,
        Guid? verificationChallengeId,
        AuthenticatorAssertionRawResponse? verificationResponse,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        if (!await VerifyManagementAsync(userId, currentPassword, verificationChallengeId, verificationResponse, context, cancellationToken))
            return false;

        return await identityStore.RemovePasskeyAsync(credentialId, userId, now, cancellationToken);
    }

    private async Task<bool> VerifyManagementAsync(UserId userId, string? password, Guid? verificationChallengeId, AuthenticatorAssertionRawResponse? verificationResponse, HttpContext context, CancellationToken cancellationToken)
    {
        if (verificationChallengeId is { } challengeId && verificationResponse is not null
            && BrowserAuthentication.TryGetSession(context.User, out _, out var sessionId))
            return await VerifySecondFactorAsync(userId, sessionId, challengeId, verificationResponse, context, cancellationToken);
        var credential = await identityStore.FindCredentialByUserIdAsync(userId, cancellationToken);
        if (credential is null) return false;
        var ip = context.Connection.RemoteIpAddress?.ToString();
        var account = credential.LoginIdentifier;
        if (await throttle.RetryAfterAsync("login", account, ip, cancellationToken) is not null) return false;
        if (string.IsNullOrEmpty(password) || passwordHashService.Verify(userId, credential.PasswordHash, password) == PasswordVerificationResult.Failed)
        {
            await throttle.FailedAsync("login", account, ip, cancellationToken);
            return false;
        }
        await throttle.SucceededAsync("login", account, ip, cancellationToken);
        return true;
    }
    private static Guid? GetClientFlowId(HttpContext context)
    {
        var cookie = context.Request.Cookies[BrowserAuthentication.CookieName]
            ?? context.Request.Cookies["__Host-BackupPlatform.Antiforgery"];
        if (string.IsNullOrWhiteSpace(cookie)) return null;
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(cookie));
        return new Guid(bytes.AsSpan(0, 16));
    }
}
