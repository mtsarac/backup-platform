using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Support;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Infrastructure.Support;

/// <summary>
/// Judges a support session inside the caller's transaction. The session row is locked <c>FOR SHARE</c>: any number of operations may hold it together,
/// while closing, revoking and expiring need it exclusively, so an operation that gets through the gate commits before the session can end, and one that
/// arrives after sees the end. Lock order everywhere: support session, then agent, then artifact, then replica or download row. Nothing about the
/// decision depends on a background worker: expiry is the comparison of the caller's server-clock instant with the session's stored expiry.
/// </summary>
internal static class SupportSessionGate
{
    /// <summary>
    /// The session as locked and read, and the denial (null when allowed). The session is null when none exists, or when <paramref name="tenantId"/> is given and it
    /// belongs to another tenant: a session of another tenant is indistinguishable from none.
    /// </summary>
    internal static async Task<(SupportAccessSession? Session, SupportAccessDenial? Denial)> LockAndEvaluateAsync(
        PlatformDbContext context, Guid sessionId, TenantId? tenantId, UserId actor, SupportPermissions required, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var locked = await context.Database.SqlQuery<Guid>(
            $"SELECT \"Id\" AS \"Value\" FROM support_access_sessions WHERE \"Id\" = {sessionId} FOR SHARE").ToListAsync(cancellationToken);
        if (locked.Count == 0) return (null, null);
        // A new statement under the lock: nothing can change the row until this transaction ends.
        var session = await context.SupportAccessSessions.AsNoTracking().SingleAsync(row => row.Id == sessionId, cancellationToken);
        if (tenantId is not null && session.TenantId != tenantId) return (null, null);
        return (session, session.Evaluate(actor, required, now));
    }

    /// <summary>Takes the exclusive lock an ending needs and returns the tracked session; null when unknown.</summary>
    internal static async Task<SupportAccessSession?> LockForEndAsync(PlatformDbContext context, Guid sessionId, CancellationToken cancellationToken)
    {
        var locked = await context.Database.SqlQuery<Guid>(
            $"SELECT \"Id\" AS \"Value\" FROM support_access_sessions WHERE \"Id\" = {sessionId} FOR UPDATE").ToListAsync(cancellationToken);
        return locked.Count == 0 ? null : await context.SupportAccessSessions.SingleAsync(row => row.Id == sessionId, cancellationToken);
    }
}
