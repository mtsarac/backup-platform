using BackupPlatform.Domain.Identity;
using Microsoft.AspNetCore.Identity;

namespace BackupPlatform.Identity;

public sealed class AuthenticationService(IIdentityStore identityStore, PasswordHashService passwordHashService, TimeProvider timeProvider)
{
    public async Task<LoginSession?> LoginAsync(string loginIdentifier, string password, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string normalizedIdentifier;
        try
        {
            normalizedIdentifier = LoginIdentifierNormalizer.Normalize(loginIdentifier);
        }
        catch (ArgumentException)
        {
            passwordHashService.VerifyDummy(password);
            return null;
        }
        if (!PasswordHashService.IsValidPassword(password))
        {
            passwordHashService.VerifyDummy(password);
            return null;
        }
        var credential = await identityStore.FindCredentialAsync(normalizedIdentifier, cancellationToken).ConfigureAwait(false);
        if (credential is null)
        {
            passwordHashService.VerifyDummy(password);
            return null;
        }
        var result = passwordHashService.Verify(credential.UserId, credential.PasswordHash, password);
        if (result == PasswordVerificationResult.Failed)
        {
            return null;
        }
        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            var replacement = passwordHashService.Hash(credential.UserId, password);
            await identityStore.UpdatePasswordHashAsync(credential.UserId, replacement, cancellationToken).ConfigureAwait(false);
        }
        var session = new UserSession(Guid.NewGuid(), credential.UserId, timeProvider.GetUtcNow().AddHours(8));
        await identityStore.CreateSessionAsync(session, cancellationToken).ConfigureAwait(false);
        return new LoginSession(session.UserId, session.Id, session.ExpiresAt);
    }
}

public sealed record LoginSession(UserId UserId, Guid SessionId, DateTimeOffset ExpiresAt);
