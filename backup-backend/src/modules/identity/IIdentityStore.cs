using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Identity;

public interface IIdentityStore
{
    Task<PasswordCredential?> FindCredentialAsync(string normalizedLoginIdentifier, CancellationToken cancellationToken);
    Task UpdatePasswordHashAsync(UserId userId, string passwordHash, CancellationToken cancellationToken);
    Task CreateSessionAsync(UserSession session, DateTimeOffset now, CancellationToken cancellationToken);
    Task<SessionAuthenticationState?> ValidateSessionAsync(Guid sessionId, UserId userId, DateTimeOffset now, CancellationToken cancellationToken);
    Task<IReadOnlyList<UserSession>> ListSessionsAsync(UserId userId, DateTimeOffset now, CancellationToken cancellationToken);
    Task<bool> EndSessionAsync(Guid sessionId, UserId userId, DateTimeOffset now, CancellationToken cancellationToken);
    Task EndAllSessionsAsync(UserId userId, DateTimeOffset now, CancellationToken cancellationToken);
    Task SetSessionStateAsync(Guid sessionId, UserId userId, SessionAuthenticationState state, CancellationToken cancellationToken);
    Task RecordLoginFailureAsync(UserId? targetUserId, DateTimeOffset now, CancellationToken cancellationToken);
}
