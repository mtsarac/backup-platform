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

    public async Task<bool> IsSessionActiveAsync(Guid sessionId, UserId userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        return await context.UserSessions.AnyAsync(session =>
            session.Id == sessionId && session.UserId == userId && session.ExpiresAt > now, cancellationToken);
    }

    public async Task RevokeSessionAsync(Guid sessionId, UserId userId, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var deleted = await context.UserSessions.Where(session => session.Id == sessionId && session.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);
        if (deleted > 0)
        {
            context.AuditEvents.Add(AuditEvent.Record(
                DateTimeOffset.UtcNow, userId, AuditActorKind.User, AuditActions.UserLogout, null,
                "session", sessionId.ToString("D"), new Dictionary<string, object?> { ["result"] = "revoked" }));
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
