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
    Task<PasswordCredential?> FindCredentialByUserIdAsync(UserId userId, CancellationToken cancellationToken);
    Task<bool> HasSecondFactorAsync(UserId userId, CancellationToken cancellationToken);
    Task<TotpCredential?> FindTotpCredentialAsync(UserId userId, CancellationToken cancellationToken);
    Task SaveTotpCredentialAsync(TotpCredential credential, DateTimeOffset now, CancellationToken cancellationToken);
    Task<bool> RemoveTotpCredentialAsync(UserId userId, DateTimeOffset now, CancellationToken cancellationToken);
    Task<bool> TryUseTotpStepAsync(UserId userId, long step, CancellationToken cancellationToken);
    Task SaveRecoveryCodesAsync(UserId userId, IEnumerable<string> codeHashes, DateTimeOffset now, CancellationToken cancellationToken);
    Task<bool> TryRedeemRecoveryCodeAsync(UserId userId, string codeHash, DateTimeOffset now, CancellationToken cancellationToken);
    Task<int> GetRemainingRecoveryCodesCountAsync(UserId userId, CancellationToken cancellationToken);
}
