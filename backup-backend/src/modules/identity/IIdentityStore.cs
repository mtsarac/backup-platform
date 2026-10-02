using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Identity;

public interface IIdentityStore
{
    Task<PasswordCredential?> FindCredentialAsync(string normalizedLoginIdentifier, CancellationToken cancellationToken);
    Task UpdatePasswordHashAsync(UserId userId, string passwordHash, CancellationToken cancellationToken);
    Task CreateSessionAsync(UserSession session, CancellationToken cancellationToken);
    Task<bool> IsSessionActiveAsync(Guid sessionId, UserId userId, DateTimeOffset now, CancellationToken cancellationToken);
    Task RevokeSessionAsync(Guid sessionId, UserId userId, CancellationToken cancellationToken);
}
