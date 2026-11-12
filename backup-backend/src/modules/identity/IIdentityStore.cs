using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Identity;

public enum TotpEnrollmentOutcome
{
    EnrolledWithRecoveryCodes,
    EnrolledWithoutRecoveryCodes,
    Conflict,
}
public interface IIdentityStore
{
    Task<PasswordCredential?> FindCredentialAsync(string normalizedLoginIdentifier, CancellationToken cancellationToken);
    Task UpdatePasswordHashAsync(UserId userId, string passwordHash, CancellationToken cancellationToken);
    Task<bool> ChangePasswordAsync(UserId userId, string newPasswordHash, DateTimeOffset now, CancellationToken cancellationToken);
    Task CreateSessionAsync(UserSession session, DateTimeOffset now, CancellationToken cancellationToken);
    Task<SessionAuthenticationState?> ValidateSessionAsync(Guid sessionId, UserId userId, DateTimeOffset now, CancellationToken cancellationToken);
    Task<IReadOnlyList<UserSession>> ListSessionsAsync(UserId userId, DateTimeOffset now, CancellationToken cancellationToken);
    Task<bool> EndSessionAsync(Guid sessionId, UserId userId, DateTimeOffset now, CancellationToken cancellationToken);
    Task EndAllSessionsAsync(UserId userId, DateTimeOffset now, CancellationToken cancellationToken);
    Task SetSessionStateAsync(Guid sessionId, UserId userId, SessionAuthenticationState state, CancellationToken cancellationToken);
    /// <summary>The failed sign-ins on this account tripped the progressive limiter; reports it to the user (at most once an hour).</summary>
    Task RecordRepeatedLoginFailuresAsync(UserId userId, DateTimeOffset now, CancellationToken cancellationToken);
    Task RecordLoginFailureAsync(UserId? targetUserId, DateTimeOffset now, CancellationToken cancellationToken);
    Task<PasswordCredential?> FindCredentialByUserIdAsync(UserId userId, CancellationToken cancellationToken);
    Task<bool> HasSecondFactorAsync(UserId userId, CancellationToken cancellationToken);
    Task<TotpCredential?> FindTotpCredentialAsync(UserId userId, CancellationToken cancellationToken);
    Task<TotpEnrollmentOutcome> SaveTotpCredentialAsync(TotpCredential credential, IReadOnlyList<string> recoveryCodeHashes, DateTimeOffset now, CancellationToken cancellationToken);
    Task<bool> RemoveTotpCredentialAsync(UserId userId, DateTimeOffset now, CancellationToken cancellationToken);
    Task<bool> TryUseTotpStepAsync(UserId userId, long step, CancellationToken cancellationToken);
    Task SaveRecoveryCodesAsync(UserId userId, IEnumerable<string> codeHashes, DateTimeOffset now, CancellationToken cancellationToken);
    Task<bool> TryRedeemRecoveryCodeAsync(UserId userId, string codeHash, DateTimeOffset now, CancellationToken cancellationToken);
    Task<int> GetRemainingRecoveryCodesCountAsync(UserId userId, CancellationToken cancellationToken);
    Task<bool> HasPasskeysAsync(UserId userId, CancellationToken cancellationToken);
    Task<IReadOnlyList<PasskeyCredential>> ListPasskeysAsync(UserId userId, CancellationToken cancellationToken);
    Task<PasskeyCredential?> FindPasskeyByCredentialIdAsync(byte[] credentialId, CancellationToken cancellationToken);
    Task<PasskeyCredential?> FindPasskeyByIdAsync(Guid id, UserId userId, CancellationToken cancellationToken);
    Task<bool> AddPasskeyAsync(PasskeyCredential passkey, IReadOnlyList<string> recoveryCodeHashes, DateTimeOffset now, CancellationToken cancellationToken);
    Task<bool> RemovePasskeyAsync(Guid id, UserId userId, DateTimeOffset now, CancellationToken cancellationToken);
    Task<bool> UpdatePasskeyUsageAsync(Guid id, uint newCounter, DateTimeOffset usedAt, CancellationToken cancellationToken);
    Task SaveChallengeAsync(PasskeyChallenge challenge, CancellationToken cancellationToken);
    Task<PasskeyChallenge?> ConsumeChallengeAsync(Guid challengeId, string purpose, DateTimeOffset now, CancellationToken cancellationToken);
    Task<bool> IsUserDisabledAsync(UserId userId, CancellationToken cancellationToken);
    /// <summary>Replaces any unused token of the same purpose and stores this one, auditing the request.</summary>
    Task IssueEmailTokenAsync(AccountEmailToken token, CancellationToken cancellationToken);
    /// <summary>The token's owner when it is unused, unexpired, still bound to the account's current address (and, for a reset, that address is verified).</summary>
    Task<UserId?> FindUsableEmailTokenAsync(string tokenHash, AccountEmailPurpose purpose, DateTimeOffset now, CancellationToken cancellationToken);
    Task<bool> ConfirmEmailAsync(string tokenHash, DateTimeOffset now, CancellationToken cancellationToken);
    /// <summary>Consumes a reset token, sets the password, clears a forced change and ends every session of the user, atomically.</summary>
    Task<bool> CompletePasswordResetAsync(string tokenHash, string newPasswordHash, DateTimeOffset now, CancellationToken cancellationToken);
    Task<string?> GetUserDisplayNameAsync(UserId userId, CancellationToken cancellationToken);
}
