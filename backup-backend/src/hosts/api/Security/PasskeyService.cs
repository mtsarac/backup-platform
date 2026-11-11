using System.Security.Claims;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Identity;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;

namespace BackupPlatform.Api.Security;

public sealed class PasskeyService(
    IIdentityStore identityStore,
    PasswordHashService passwordHashService,
    LoginThrottle throttle,
    TimeProvider timeProvider,
    IConfiguration configuration)
{
    public IFido2 CreateFido2(HttpContext context)
    {
        var configRp = configuration["Authentication:Passkeys:RelyingPartyId"] ?? configuration["Authentication:WebAuthn:RelyingPartyId"];
        var requestHost = context.Request.Host.Host;
        var rpId = !string.IsNullOrWhiteSpace(configRp) ? configRp : (string.IsNullOrWhiteSpace(requestHost) ? "localhost" : requestHost);

        var origins = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "https://localhost",
            "http://localhost",
            "https://localhost:5001",
            "http://localhost:5000",
            "http://localhost:5173",
            "https://127.0.0.1",
            "http://127.0.0.1"
        };

        if (!string.IsNullOrWhiteSpace(context.Request.Scheme) && context.Request.Host.HasValue)
        {
            origins.Add($"{context.Request.Scheme}://{context.Request.Host.Value}");
        }

        if (context.Request.Headers.TryGetValue("Origin", out var originHeader) && !string.IsNullOrWhiteSpace(originHeader))
        {
            if (Uri.TryCreate(originHeader.ToString(), UriKind.Absolute, out var originUri))
            {
                if (originUri.Host.Equals(requestHost, StringComparison.OrdinalIgnoreCase)
                    || originUri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                    || originUri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase))
                {
                    origins.Add(originHeader.ToString().TrimEnd('/'));
                }
            }
        }

        var configOrigins = configuration.GetSection("Authentication:Passkeys:Origins").Get<string[]>()
            ?? configuration.GetSection("Authentication:WebAuthn:Origins").Get<string[]>();
        if (configOrigins != null)
        {
            foreach (var origin in configOrigins)
            {
                if (!string.IsNullOrWhiteSpace(origin))
                {
                    origins.Add(origin.TrimEnd('/'));
                }
            }
        }

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
            null,
            "registration",
            options.ToJson(),
            now,
            now.AddMinutes(5));

        await identityStore.SaveChallengeAsync(challenge, cancellationToken);
        return (options, challengeId);
    }

    public async Task<PasskeyCredential?> RegisterPasskeyAsync(
        UserId userId,
        Guid challengeId,
        string name,
        AuthenticatorAttestationRawResponse response,
        string? currentPassword,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Passkey name is required.", nameof(name));
        if (name.Length > 128) name = name[..128];

        var now = timeProvider.GetUtcNow();

        // Security verification: current password required
        if (string.IsNullOrEmpty(currentPassword))
            return null;

        var cred = await identityStore.FindCredentialByUserIdAsync(userId, cancellationToken);
        if (cred is null)
            return null;

        var verifyResult = passwordHashService.Verify(userId, cred.PasswordHash, currentPassword);
        if (verifyResult == PasswordVerificationResult.Failed)
            return null;

        var challenge = await identityStore.ConsumeChallengeAsync(challengeId, "registration", now, cancellationToken);
        if (challenge is null || challenge.UserId != userId)
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
        await identityStore.AddPasskeyAsync(passkey, now, cancellationToken);
        return passkey;
    }

    public async Task<(AssertionOptions Options, Guid ChallengeId)> CreateLoginOptionsAsync(
        string? loginIdentifier,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var allowedCredentials = new List<PublicKeyCredentialDescriptor>();
        UserId? targetUserId = null;

        if (!string.IsNullOrWhiteSpace(loginIdentifier) && LoginIdentifierNormalizer.TryNormalize(loginIdentifier, out var normalized))
        {
            var cred = await identityStore.FindCredentialAsync(normalized, cancellationToken);
            if (cred != null)
            {
                targetUserId = cred.UserId;
                var passkeys = await identityStore.ListPasskeysAsync(cred.UserId, cancellationToken);
                allowedCredentials.AddRange(passkeys.Select(p => new PublicKeyCredentialDescriptor(p.CredentialId)));
            }
        }

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
            null,
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
        if (challenge is null)
        {
            await throttle.FailedAsync("passkey_assertion", null, ip, cancellationToken);
            return null;
        }

        var passkey = await identityStore.FindPasskeyByCredentialIdAsync(response.RawId, cancellationToken);
        if (passkey is null)
        {
            await throttle.FailedAsync("passkey_assertion", null, ip, cancellationToken);
            return null;
        }

        var userCred = await identityStore.FindCredentialByUserIdAsync(passkey.UserId, cancellationToken);
        var accountIdentifier = userCred?.LoginIdentifier;

        if (accountIdentifier != null && await throttle.RetryAfterAsync("passkey_assertion", accountIdentifier, ip, cancellationToken) is not null)
        {
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

        await identityStore.UpdatePasskeyUsageAsync(passkey.Id, result.SignCount, now, cancellationToken);
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

        if (await throttle.RetryAfterAsync("passkey_assertion", accountIdentifier, ip, cancellationToken) is not null)
        {
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

        await identityStore.UpdatePasskeyUsageAsync(passkey.Id, result.SignCount, now, cancellationToken);
        await identityStore.SetSessionStateAsync(sessionId, userId, SessionAuthenticationState.FullyAuthenticated, cancellationToken);
        await throttle.SucceededAsync("passkey_assertion", accountIdentifier, ip, cancellationToken);

        return true;
    }

    public async Task<bool> RemovePasskeyAsync(
        UserId userId,
        Guid credentialId,
        string? currentPassword,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        // Security verification: current password required
        if (string.IsNullOrEmpty(currentPassword))
            return false;

        var cred = await identityStore.FindCredentialByUserIdAsync(userId, cancellationToken);
        if (cred is null)
            return false;

        var verifyResult = passwordHashService.Verify(userId, cred.PasswordHash, currentPassword);
        if (verifyResult == PasswordVerificationResult.Failed)
            return false;

        return await identityStore.RemovePasskeyAsync(credentialId, userId, now, cancellationToken);
    }
}
