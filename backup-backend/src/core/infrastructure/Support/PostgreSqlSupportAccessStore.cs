using BackupPlatform.Application.Support;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Restores;
using BackupPlatform.Domain.Support;
using BackupPlatform.Infrastructure.Backups;
using BackupPlatform.Infrastructure.Downloads;
using BackupPlatform.Infrastructure.Persistence;
using BackupPlatform.Infrastructure.Restores;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Infrastructure.Support;

/// <summary>
/// Support session writes under explicit platform operations; the endpoints' PlatformAdmin policy and the session owner rules are the authorization. Every write
/// adds its audit event to the same SaveChanges. Lock order (support session, agent, artifact, replica or download row) is the order the gated operations take.
/// </summary>
public sealed class PostgreSqlSupportAccessStore(DbContextOptions<PlatformDbContext> options) : ISupportAccessStore
{
    private const int OpenRounds = 3;
    private const string ClosedFailure = "support_session_closed";
    private const string RevokedFailure = "support_session_revoked";

    public async Task<SupportOpenResult> OpenAsync(SupportOpenCommand command, CancellationToken cancellationToken)
    {
        for (var round = 0; round < OpenRounds; round++)
        {
            var result = await OpenOnceAsync(command, cancellationToken);
            if (result is not null) return result;
        }
        throw new InvalidOperationException("The support session could not be opened because of repeated conflicting writes.");
    }

    /// <summary>Null when a unique index or a concurrent change beat this attempt; the caller starts over and sees the winner.</summary>
    private async Task<SupportOpenResult?> OpenOnceAsync(SupportOpenCommand command, CancellationToken cancellationToken)
    {
        var (tenantId, admin) = (command.TenantId, command.Admin);
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var existing = await context.SupportAccessSessions.AsNoTracking()
            .SingleOrDefaultAsync(session => session.TenantId == tenantId && session.PlatformAdminId == admin && session.IdempotencyKey == command.IdempotencyKey, cancellationToken);
        if (existing is not null)
        {
            return string.Equals(existing.RequestFingerprint, command.Fingerprint, StringComparison.Ordinal)
                ? new SupportOpenResult(SupportOpenOutcome.Replayed, existing)
                : new SupportOpenResult(SupportOpenOutcome.IdempotencyConflict);
        }
        if (!await context.Tenants.AnyAsync(tenant => tenant.Id == tenantId, cancellationToken)) return new SupportOpenResult(SupportOpenOutcome.TenantNotFound);
        // The admin's previous window for this tenant is still Active in the table when its time ran out and nothing has recorded that yet: end it properly first.
        var live = await context.SupportAccessSessions.SingleOrDefaultAsync(session => session.TenantId == tenantId && session.PlatformAdminId == admin &&
            session.Status == SupportSessionStatus.Active, cancellationToken);
        if (live is not null)
        {
            if (!live.IsUsable(command.Now))
            {
                if (live.Expire(command.Now)) context.AuditEvents.Add(ExpiredAudit(live));
                try { await context.SaveChangesAsync(cancellationToken); }
                catch (DbUpdateConcurrencyException) { return null; }
            }
            else return new SupportOpenResult(SupportOpenOutcome.ActiveSessionExists, ExistingSessionId: live.Id);
        }
        var session = SupportAccessSession.Open(command.SessionId, tenantId, admin, command.Reason, command.Permissions, command.Now, command.Duration, command.IdempotencyKey, command.Fingerprint);
        context.SupportAccessSessions.Add(session);
        context.AuditEvents.Add(AuditEvent.Record(command.Now, admin, AuditActorKind.PlatformAdmin, SupportAuditActions.SessionOpened, tenantId,
            SupportAuditActions.SessionTargetType, session.Id.ToString("D"), new Dictionary<string, object?>
            {
                ["supportSessionId"] = session.Id, ["result"] = "opened", ["reason"] = session.Reason, ["permissions"] = SupportPermissionNames.ToNames(session.Permissions),
                ["startedAt"] = session.StartedAt, ["expiresAt"] = session.ExpiresAt
            }, session.Id));
        try { await context.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException exception) when (PostgreSqlBackupRunStore.IsUniqueViolation(exception)) { return null; }
        await transaction.CommitAsync(cancellationToken);
        return new SupportOpenResult(SupportOpenOutcome.Opened, session);
    }

    public async Task<SupportAuthorization> AuthorizeAsync(SupportAuthorizationCommand command, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var (session, denial) = await SupportSessionGate.LockAndEvaluateAsync(context, command.SessionId, null, command.Actor, command.Required, command.Now, cancellationToken);
        if (session is null) return new SupportAuthorization(null, null);
        if (denial is { } refused) return new SupportAuthorization(null, refused, session.TenantId.Value);
        if (command.Audit is { } audit)
        {
            // The audit is part of the decision: it commits with it, and when it cannot be written nothing is allowed.
            var details = new Dictionary<string, object?>(audit.Details ?? new Dictionary<string, object?>()) { ["supportSessionId"] = session.Id };
            details.TryAdd("result", "allowed");
            context.AuditEvents.Add(AuditEvent.Record(command.Now, command.Actor, AuditActorKind.PlatformAdmin, audit.Action, session.TenantId, audit.TargetType, audit.TargetId, details, session.Id));
            await context.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new SupportAuthorization(session, null);
    }

    public Task<SupportEndResult> CloseAsync(Guid sessionId, UserId by, DateTimeOffset now, CancellationToken cancellationToken) =>
        EndAsync(sessionId, by, null, now, cancellationToken);

    public Task<SupportEndResult> RevokeAsync(Guid sessionId, UserId by, string reason, DateTimeOffset now, CancellationToken cancellationToken) =>
        EndAsync(sessionId, by, reason, now, cancellationToken);

    private async Task<SupportEndResult> EndAsync(Guid sessionId, UserId by, string? revokeReason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var revoking = revokeReason is not null;
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        // Exclusive: waits for every operation that already passed the gate (they hold it shared), and everything after sees the end.
        var session = await SupportSessionGate.LockForEndAsync(context, sessionId, cancellationToken);
        if (session is null) return new SupportEndResult(SupportEndOutcome.NotFound);
        if (!revoking && session.PlatformAdminId != by) return new SupportEndResult(SupportEndOutcome.NotOwner);
        if (session.Expire(now))
        {
            // The window had already run out: record that truth rather than pretending this call ended it.
            context.AuditEvents.Add(ExpiredAudit(session));
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new SupportEndResult(SupportEndOutcome.AlreadyEnded, session);
        }
        if (!session.IsUsable(now)) return new SupportEndResult(SupportEndOutcome.AlreadyEnded, session);
        if (revoking) session.Revoke(by, revokeReason!, now);
        else session.Close(by, now);
        var failure = revoking ? RevokedFailure : ClosedFailure;
        var downloads = await DownloadsEnding(context, session.Id, now, failure, cancellationToken);
        var restores = await CancelRestoresAsync(context, session, by, revoking, now, cancellationToken);
        context.AuditEvents.Add(AuditEvent.Record(now, by, AuditActorKind.PlatformAdmin, revoking ? SupportAuditActions.SessionRevoked : SupportAuditActions.SessionClosed, session.TenantId,
            SupportAuditActions.SessionTargetType, session.Id.ToString("D"), new Dictionary<string, object?>
            {
                ["supportSessionId"] = session.Id, ["result"] = revoking ? "revoked" : "closed", ["reason"] = session.CloseReason, ["ownerId"] = session.PlatformAdminId.Value,
                ["downloadsCancelled"] = downloads, ["restoresAffected"] = restores
            }, session.Id));
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new SupportEndResult(SupportEndOutcome.Ended, session);
    }

    private static Task<int> DownloadsEnding(PlatformDbContext context, Guid sessionId, DateTimeOffset now, string failure, CancellationToken cancellationToken) =>
        PostgreSqlDownloadStore.CancelForSupportSessionAsync(context, sessionId, now, failure, cancellationToken);

    /// <summary>
    /// Restores of the session that have not started (Pending) are withdrawn, as M5's cancellation does. When revoking, running ones are asked to cancel through
    /// the same cooperative request. A close leaves a started restore alone: ending SQL work only because a person stepped away would leave the target in a state
    /// nobody chose. Returns the operations touched.
    /// </summary>
    private static async Task<int> CancelRestoresAsync(PlatformDbContext context, SupportAccessSession session, UserId by, bool includeStarted, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var sessionId = session.Id;
        var candidates = await context.RestoreOperations.AsNoTracking()
            .Where(operation => operation.SupportSessionId == sessionId &&
                (operation.Status == RestoreStatus.Pending || (includeStarted && (operation.Status == RestoreStatus.Leased || operation.Status == RestoreStatus.Running))))
            .Select(operation => new { operation.AgentId, operation.Id }).ToListAsync(cancellationToken);
        if (candidates.Count == 0) return 0;
        var touched = 0;
        foreach (var agent in candidates.Select(candidate => candidate.AgentId).Distinct().OrderBy(agentId => agentId.Value))
        {
            if (!await PostgreSqlBackupRunStore.LockAgentAsync(context, session.TenantId, agent, cancellationToken)) continue;
            var ids = candidates.Where(candidate => candidate.AgentId == agent).Select(candidate => candidate.Id).ToArray();
            // Loaded under the agent's lock: a claim, failure or expiry that committed first is already visible and the cancellation applies to what really happened.
            var operations = await context.RestoreOperations.Include(operation => operation.Attempts).Include(operation => operation.Pin).AsSplitQuery()
                .Where(operation => operation.TenantId == session.TenantId && operation.AgentId == agent && ids.Contains(operation.Id)).ToListAsync(cancellationToken);
            foreach (var operation in operations)
            {
                if (operation.IsTerminal || operation.CancellationRequestedAt is not null) continue;
                if (operation.Status != RestoreStatus.Pending && !includeStarted) continue;
                operation.RequestCancellation(now);
                touched++;
                context.AuditEvents.Add(AuditEvent.Record(now, by, AuditActorKind.PlatformAdmin, SupportAuditActions.RestoreCancelRequested, session.TenantId,
                    SupportAuditActions.RestoreTargetType, operation.Id.ToString("D"), new Dictionary<string, object?>
                    {
                        ["supportSessionId"] = sessionId, ["artifactId"] = operation.ArtifactId, ["result"] = "accepted",
                        ["cause"] = includeStarted ? "session_revoked" : "session_closed", ["purpose"] = operation.Purpose.ToString()
                    }, sessionId));
            }
        }
        PostgreSqlRestoreStore.AddTerminalAudits(context);
        return touched;
    }

    public async Task<int> ExpireDueAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken)
    {
        now = now.ToUniversalTime();
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var ids = await context.Database.SqlQuery<Guid>($"""
            SELECT "Id" AS "Value" FROM support_access_sessions WHERE "Status" = 'Active' AND "ExpiresAt" <= {now}
            ORDER BY "ExpiresAt", "Id" LIMIT {limit} FOR UPDATE SKIP LOCKED
            """).ToListAsync(cancellationToken);
        if (ids.Count == 0) return 0;
        var sessions = await context.SupportAccessSessions.Where(session => ids.Contains(session.Id)).ToListAsync(cancellationToken);
        var expired = 0;
        foreach (var session in sessions)
        {
            // Expiry blocks new operations (judged from the clock on every request); it deliberately does not cancel running downloads or restores.
            if (!session.Expire(now)) continue;
            context.AuditEvents.Add(ExpiredAudit(session));
            expired++;
        }
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return expired;
    }

    public async Task AppendAuditAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        context.AuditEvents.Add(auditEvent);
        await context.SaveChangesAsync(cancellationToken);
    }

    private static AuditEvent ExpiredAudit(SupportAccessSession session) =>
        AuditEvent.Record(session.ClosedAt ?? session.ExpiresAt, session.PlatformAdminId, AuditActorKind.PlatformAdmin, SupportAuditActions.SessionExpired, session.TenantId,
            SupportAuditActions.SessionTargetType, session.Id.ToString("D"), new Dictionary<string, object?>
            {
                ["supportSessionId"] = session.Id, ["result"] = "expired", ["expiresAt"] = session.ExpiresAt, ["cause"] = "time_elapsed"
            }, session.Id);
}
