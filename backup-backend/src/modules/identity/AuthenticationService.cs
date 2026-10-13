using BackupPlatform.Domain.Identity;
using Microsoft.AspNetCore.Identity;

namespace BackupPlatform.Identity;

public sealed class AuthenticationService(IIdentityStore identityStore, PasswordHashService passwordHashService, TimeProvider timeProvider)
{
    public async Task<LoginSession?> LoginAsync(string loginIdentifier, string password, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = timeProvider.GetUtcNow();
        string normalizedIdentifier;
        try
        {
            normalizedIdentifier = LoginIdentifierNormalizer.Normalize(loginIdentifier);
        }
        catch (ArgumentException)
        {
            passwordHashService.VerifyDummy(password);
            await identityStore.RecordLoginFailureAsync(null, now, cancellationToken).ConfigureAwait(false);
            return null;
        }
        if (!PasswordHashService.IsValidPassword(password))
        {
            passwordHashService.VerifyDummy(password);
            await identityStore.RecordLoginFailureAsync(null, now, cancellationToken).ConfigureAwait(false);
            return null;
        }
        var credential = await identityStore.FindCredentialAsync(normalizedIdentifier, cancellationToken).ConfigureAwait(false);
        if (credential is null)
        {
            passwordHashService.VerifyDummy(password);
            await identityStore.RecordLoginFailureAsync(null, now, cancellationToken).ConfigureAwait(false);
            return null;
        }
        var result = passwordHashService.Verify(credential.UserId, credential.PasswordHash, password);
        if (result == PasswordVerificationResult.Failed)
        {
            await identityStore.RecordLoginFailureAsync(credential.UserId, now, cancellationToken).ConfigureAwait(false);
            return null;
        }
        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            var replacement = passwordHashService.Hash(credential.UserId, password);
            await identityStore.UpdatePasswordHashAsync(credential.UserId, replacement, cancellationToken).ConfigureAwait(false);
        }
        var session = new UserSession(Guid.NewGuid(), credential.UserId, now.AddHours(8));
        await identityStore.CreateSessionAsync(session, now, cancellationToken).ConfigureAwait(false);
        return new LoginSession(session.UserId, session.Id, session.ExpiresAt);
    }
}

public sealed record LoginSession(UserId UserId, Guid SessionId, DateTimeOffset ExpiresAt);
