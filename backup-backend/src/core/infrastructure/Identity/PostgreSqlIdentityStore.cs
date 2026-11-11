using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Identity;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Infrastructure.Identity;

/// <summary>Narrow identity persistence operations; never returns credential data to a network endpoint.</summary>
public sealed class PostgreSqlIdentityStore(DbContextOptions<PlatformDbContext> options) : IIdentityStore
{
    public async Task<PasswordCredential?> FindCredentialAsync(string normalizedLoginIdentifier, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        return await context.PasswordCredentials.AsNoTracking().SingleOrDefaultAsync(
            credential => credential.NormalizedLoginIdentifier == normalizedLoginIdentifier || credential.NormalizedEmail == normalizedLoginIdentifier, cancellationToken);
    }

    public async Task UpdatePasswordHashAsync(UserId userId, string passwordHash, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var credential = await context.PasswordCredentials.SingleAsync(row => row.UserId == userId, cancellationToken);
        credential.ReplacePasswordHash(passwordHash);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task CreateSessionAsync(UserSession session, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        context.UserSessions.Add(session);
        context.AuditEvents.Add(AuditEvent.Record(
            now, session.UserId, AuditActorKind.User, AuditActions.UserLoginSucceeded, null,
            "session", session.Id.ToString("D"), new Dictionary<string, object?> { ["result"] = "authenticated" }));
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<SessionAuthenticationState?> ValidateSessionAsync(Guid sessionId, UserId userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var session = await context.UserSessions.AsNoTracking().SingleOrDefaultAsync(row => row.Id == sessionId && row.UserId == userId, cancellationToken);
        if (session is null || !session.IsActive(now)) return null;
        if (session.LastActivityAt <= now.AddMinutes(-1))
        {
            var updated = await context.UserSessions.Where(row => row.Id == sessionId && row.UserId == userId && row.LastActivityAt < now)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.LastActivityAt, now), cancellationToken);
            if (updated == 0 && !await context.UserSessions.AnyAsync(row => row.Id == sessionId && row.UserId == userId, cancellationToken)) return null;
        }
        return session.AuthenticationState;
    }

    public async Task<IReadOnlyList<UserSession>> ListSessionsAsync(UserId userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var sessions = await context.UserSessions.AsNoTracking().Where(row => row.UserId == userId).OrderByDescending(row => row.LastActivityAt).ToListAsync(cancellationToken);
        return sessions.Where(session => session.IsActive(now)).ToArray();
    }

    public async Task SetSessionStateAsync(Guid sessionId, UserId userId, SessionAuthenticationState state, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await context.UserSessions.Where(row => row.Id == sessionId && row.UserId == userId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.AuthenticationState, state), cancellationToken);
    }

    public async Task<bool> EndSessionAsync(Guid sessionId, UserId userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var deleted = await context.UserSessions.Where(row => row.Id == sessionId && row.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        if (deleted > 0)
        {
            context.AuditEvents.Add(AuditEvent.Record(now, userId, AuditActorKind.User, AuditActions.UserLogout, null,
                "session", sessionId.ToString("D"), new Dictionary<string, object?> { ["result"] = "revoked" }));
            await context.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return deleted > 0;
    }

    public async Task EndAllSessionsAsync(UserId userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var deleted = await context.UserSessions.Where(row => row.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        if (deleted > 0)
        {
            context.AuditEvents.Add(AuditEvent.Record(now, userId, AuditActorKind.User, AuditActions.UserLogout, null,
                "user", userId.ToString(), new Dictionary<string, object?> { ["result"] = "all_sessions_revoked" }));
            await context.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RecordLoginFailureAsync(UserId? targetUserId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        context.AuditEvents.Add(AuditEvent.Record(
            now, UserId.Anonymous, AuditActorKind.Anonymous, AuditActions.UserLoginFailed, null,
            "user", targetUserId?.ToString() ?? "unknown", new Dictionary<string, object?> { ["result"] = "denied", ["code"] = "invalid_credentials" }));
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<PasswordCredential?> FindCredentialByUserIdAsync(UserId userId, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        return await context.PasswordCredentials.AsNoTracking().SingleOrDefaultAsync(
            credential => credential.UserId == userId, cancellationToken);
    }

    public async Task<bool> HasSecondFactorAsync(UserId userId, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        return await context.TotpCredentials.AsNoTracking().AnyAsync(row => row.UserId == userId, cancellationToken)
            || await context.PasskeyCredentials.AsNoTracking().AnyAsync(row => row.UserId == userId, cancellationToken);
    }

    public async Task<TotpCredential?> FindTotpCredentialAsync(UserId userId, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        return await context.TotpCredentials.AsNoTracking().SingleOrDefaultAsync(row => row.UserId == userId, cancellationToken);
    }

    public async Task SaveTotpCredentialAsync(TotpCredential credential, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var existing = await context.TotpCredentials.SingleOrDefaultAsync(row => row.UserId == credential.UserId, cancellationToken);
        if (existing is not null)
        {
            context.TotpCredentials.Remove(existing);
            await context.SaveChangesAsync(cancellationToken);
        }
        context.TotpCredentials.Add(credential);
        context.AuditEvents.Add(AuditEvent.Record(
            now, credential.UserId, AuditActorKind.User, AuditActions.TotpEnrolled, null,
            "user", credential.UserId.ToString(), new Dictionary<string, object?> { ["result"] = "enrolled" }));
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<bool> RemoveTotpCredentialAsync(UserId userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var deleted = await context.TotpCredentials.Where(row => row.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        if (deleted > 0)
        {
            context.AuditEvents.Add(AuditEvent.Record(
                now, userId, AuditActorKind.User, AuditActions.TotpRemoved, null,
                "user", userId.ToString(), new Dictionary<string, object?> { ["result"] = "removed" }));
            await context.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return deleted > 0;
    }

    public async Task<bool> TryUseTotpStepAsync(UserId userId, long step, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var updated = await context.TotpCredentials
            .Where(row => row.UserId == userId && (row.LastUsedEpochStep == null || row.LastUsedEpochStep < step))
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.LastUsedEpochStep, step), cancellationToken);
        return updated > 0;
    }

    public async Task SaveRecoveryCodesAsync(UserId userId, IEnumerable<string> codeHashes, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var hashList = codeHashes.ToList();
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await context.RecoveryCodes.Where(row => row.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        foreach (var hash in hashList)
        {
            context.RecoveryCodes.Add(new RecoveryCode(Guid.NewGuid(), userId, hash, now));
        }
        context.AuditEvents.Add(AuditEvent.Record(
            now, userId, AuditActorKind.User, AuditActions.RecoveryCodesRegenerated, null,
            "user", userId.ToString(), new Dictionary<string, object?> { ["result"] = "regenerated", ["count"] = hashList.Count }));
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<bool> TryRedeemRecoveryCodeAsync(UserId userId, string codeHash, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var updated = await context.RecoveryCodes
            .Where(row => row.UserId == userId && row.CodeHash == codeHash && row.RedeemedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.RedeemedAt, now), cancellationToken);
        if (updated > 0)
        {
            context.AuditEvents.Add(AuditEvent.Record(
                now, userId, AuditActorKind.User, AuditActions.RecoveryCodeConsumed, null,
                "user", userId.ToString(), new Dictionary<string, object?> { ["result"] = "consumed" }));
            await context.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return updated > 0;
    }

    public async Task<int> GetRemainingRecoveryCodesCountAsync(UserId userId, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        return await context.RecoveryCodes.AsNoTracking().CountAsync(row => row.UserId == userId && row.RedeemedAt == null, cancellationToken);
    }

    public async Task<bool> HasPasskeysAsync(UserId userId, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        return await context.PasskeyCredentials.AnyAsync(row => row.UserId == userId, cancellationToken);
    }

    public async Task<IReadOnlyList<PasskeyCredential>> ListPasskeysAsync(UserId userId, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        return await context.PasskeyCredentials.AsNoTracking().Where(row => row.UserId == userId).OrderByDescending(row => row.CreatedAt).ToListAsync(cancellationToken);
    }

    public async Task<PasskeyCredential?> FindPasskeyByCredentialIdAsync(byte[] credentialId, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        return await context.PasskeyCredentials.SingleOrDefaultAsync(row => row.CredentialId == credentialId, cancellationToken);
    }

    public async Task<PasskeyCredential?> FindPasskeyByIdAsync(Guid id, UserId userId, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        return await context.PasskeyCredentials.SingleOrDefaultAsync(row => row.Id == id && row.UserId == userId, cancellationToken);
    }

    public async Task AddPasskeyAsync(PasskeyCredential passkey, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        context.PasskeyCredentials.Add(passkey);
        context.AuditEvents.Add(AuditEvent.Record(
            now, passkey.UserId, AuditActorKind.User, AuditActions.PasskeyRegistered, null,
            "passkey", passkey.Id.ToString("D"), new Dictionary<string, object?> { ["name"] = passkey.Name }));
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> RemovePasskeyAsync(Guid id, UserId userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var cred = await context.PasskeyCredentials.SingleOrDefaultAsync(row => row.Id == id && row.UserId == userId, cancellationToken);
        if (cred is null) return false;
        context.PasskeyCredentials.Remove(cred);
        context.AuditEvents.Add(AuditEvent.Record(
            now, userId, AuditActorKind.User, AuditActions.PasskeyRemoved, null,
            "passkey", id.ToString("D"), new Dictionary<string, object?> { ["name"] = cred.Name }));
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task UpdatePasskeyUsageAsync(Guid id, uint newCounter, DateTimeOffset usedAt, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var cred = await context.PasskeyCredentials.SingleOrDefaultAsync(row => row.Id == id, cancellationToken);
        if (cred is null) return;
        cred.RecordUsage(newCounter, usedAt);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveChallengeAsync(PasskeyChallenge challenge, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        context.PasskeyChallenges.Add(challenge);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<PasskeyChallenge?> ConsumeChallengeAsync(Guid challengeId, string purpose, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var challenge = await context.PasskeyChallenges.SingleOrDefaultAsync(c => c.Id == challengeId && c.Purpose == purpose && c.ExpiresAt > now, cancellationToken);
        if (challenge is null) return null;
        context.PasskeyChallenges.Remove(challenge);
        await context.SaveChangesAsync(cancellationToken);
        return challenge;
    }

    public async Task<bool> IsUserDisabledAsync(UserId userId, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        return await context.Users.AsNoTracking().Where(row => row.Id == userId).Select(row => row.IsDisabled).SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<string?> GetUserDisplayNameAsync(UserId userId, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        return await context.Users.AsNoTracking().Where(row => row.Id == userId).Select(row => row.DisplayName).SingleOrDefaultAsync(cancellationToken);
    }
}
