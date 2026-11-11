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
            credential => credential.NormalizedLoginIdentifier == normalizedLoginIdentifier, cancellationToken);
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
}
