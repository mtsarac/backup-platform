using BackupPlatform.Application.Downloads;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Downloads;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Support;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Infrastructure.Support;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Infrastructure.Downloads;

/// <summary>Replica locks serialize durable holds with deletion; session locks fence lifecycle writes and their audits.</summary>
public sealed class PostgreSqlDownloadStore(DbContextOptions<PlatformDbContext> options, TimeProvider? time = null) : IDownloadStore
{
    private const int MaxBatch = 500;
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<BackupArtifact?> GetArtifactAsync(TenantId tenantId, Guid artifactId, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        return await context.BackupArtifacts.AsNoTracking().Include(artifact => artifact.Replicas)
            .SingleOrDefaultAsync(artifact => artifact.TenantId == tenantId && artifact.Id == artifactId, cancellationToken);
    }

    public async Task<DownloadSession?> CreateAsync(DownloadCreateCommand command, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (command.SupportSessionId is { } supportId)
        {
            // The support session is locked first (shared), then artifact, then replica: the order closing a session uses against the downloads it cancels.
            var (session, denial) = await SupportSessionGate.LockAndEvaluateAsync(context, supportId, command.TenantId, new UserId(command.UserId), SupportPermissions.DownloadBackup,
                command.Now, cancellationToken);
            if (session is null) return null;
            if (denial is { } refused) throw new SupportAccessDeniedException(refused);
        }
        else if (!await IsAdministratorAsync(context, command.TenantId, command.UserId, cancellationToken)) return null;
        var artifactLocked = await context.Database.SqlQuery<Guid>($"""
            SELECT "Id" AS "Value" FROM backup_artifacts
            WHERE "TenantId" = {command.TenantId.Value} AND "Id" = {command.ArtifactId} FOR NO KEY UPDATE
            """).ToListAsync(cancellationToken);
        if (artifactLocked.Count == 0) return null;
        var replicaLocked = await context.Database.SqlQuery<Guid>($"""
            SELECT "Id" AS "Value" FROM backup_replicas
            WHERE "TenantId" = {command.TenantId.Value} AND "ArtifactId" = {command.ArtifactId} AND "Id" = {command.ReplicaId} FOR UPDATE
            """).ToListAsync(cancellationToken);
        if (replicaLocked.Count == 0) return null;
        // Fresh statements after both locks: neither a retention mutation nor a physical claim can invalidate eligibility before the hold commits.
        var artifact = await context.BackupArtifacts.AsNoTracking().SingleAsync(entity => entity.TenantId == command.TenantId && entity.Id == command.ArtifactId, cancellationToken);
        var replica = await context.BackupReplicas.AsNoTracking().SingleAsync(entity => entity.TenantId == command.TenantId && entity.ArtifactId == command.ArtifactId && entity.Id == command.ReplicaId, cancellationToken);
        if (replica.Status != BackupReplicaStatus.Available || replica.VerificationState != "Verified" ||
            replica.Checksum != artifact.ArtifactChecksum || replica.SizeBytes != artifact.SizeBytes || artifact.ChecksumAlgorithm != "sha256") return null;
        var download = new DownloadSession(Guid.NewGuid(), artifact, replica, command.UserId, command.Filename, command.Now, command.Lifetime, command.SupportSessionId);
        context.DownloadSessions.Add(download);
        AddAudit(context, download, command.Now, "download.requested");
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return download;
    }

    public async Task<DownloadSession?> GetAsync(TenantId tenantId, Guid sessionId, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        return await context.DownloadSessions.AsNoTracking().SingleOrDefaultAsync(session => session.TenantId == tenantId && session.Id == sessionId, cancellationToken);
    }

    public async Task<IReadOnlyList<DownloadSession>> ListPreparingAsync(TenantId tenantId, Guid agentId, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var now = _time.GetUtcNow();
        return await context.DownloadSessions.AsNoTracking().Where(session => session.TenantId == tenantId && session.AgentId == agentId &&
            session.State == DownloadState.Preparing && session.ReleasedAt == null && session.ExpiresAt > now &&
            (session.LeaseExpiresAt == null || session.LeaseExpiresAt > now))
            .OrderBy(session => session.CreatedAt).ThenBy(session => session.Id).Take(MaxBatch).ToListAsync(cancellationToken);
    }

    public async Task<DownloadSession?> TransitionAsync(Guid sessionId, DownloadTransition transition, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var startsStreaming = transition.State == DownloadState.Streaming && transition.ExpectedState != DownloadState.Streaming;
        SupportAccessDenial? supportDenial = null;
        if (startsStreaming)
        {
            // A support download re-asks its support session at the moment bytes would start flowing, under the session's shared lock and before the download row's
            // lock (the order a close uses): a session that ended or expired since the claim was prepared does not stream. Lease renewals are not re-judged: a transfer
            // that began is governed by its lease and pin, and a close or revoke cancels it through the row instead.
            var link = await context.DownloadSessions.AsNoTracking().Where(entity => entity.Id == sessionId)
                .Select(entity => new { entity.TenantId, entity.UserId, entity.SupportSessionId }).SingleOrDefaultAsync(cancellationToken);
            if (link?.SupportSessionId is { } supportId)
            {
                var (found, denial) = await SupportSessionGate.LockAndEvaluateAsync(context, supportId, link.TenantId, new UserId(link.UserId), SupportPermissions.DownloadBackup,
                    transition.Now, cancellationToken);
                if (found is null) return null;
                supportDenial = denial;
            }
        }
        if (supportDenial is not null) return null;
        if (transition.ExpectedState == DownloadState.Preparing && transition.State == DownloadState.Preparing &&
            transition.ExpectedOwner is null && transition.Owner is not null)
        {
            var target = await context.DownloadSessions.AsNoTracking().Where(entity => entity.Id == sessionId)
                .Select(entity => new { entity.TenantId, entity.AgentId }).SingleOrDefaultAsync(cancellationToken);
            if (target?.AgentId is not { } agentId ||
                !await BackupPlatform.Infrastructure.Backups.PostgreSqlBackupRunStore.LockAgentAsync(context, target.TenantId, new AgentId(agentId), cancellationToken)) return null;
            // Lock the shared agent before any session, so concurrent polls cannot claim different downloads for its single worker.
            var busy = await context.DownloadSessions.AnyAsync(entity => entity.TenantId == target.TenantId && entity.AgentId == agentId &&
                entity.State == DownloadState.Preparing && entity.LeaseOwner != null && entity.ReleasedAt == null &&
                entity.ExpiresAt > transition.Now && entity.LeaseExpiresAt > transition.Now, cancellationToken);
            if (busy) return null;
        }
        var session = await LockSessionAsync(context, sessionId, cancellationToken);
        if (session is null) return null;
        if (startsStreaming && session.SupportSessionId is null &&
            !await IsAdministratorAsync(context, session.TenantId, session.UserId, cancellationToken)) return null;
        var previous = session.State;
        if (!session.Transition(transition.ExpectedState, transition.ExpectedOwner, transition.State, transition.Owner,
            transition.Now, transition.LeaseDuration, transition.Bytes, transition.FailureCode)) return null;
        if (previous != session.State || (transition.ExpectedOwner is null && session.LeaseOwner is not null))
            AddAudit(context, session, transition.Now, $"download.{session.State.ToString().ToLowerInvariant()}");
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return session;
    }

    public async Task<int> ExpireAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        now = now.ToUniversalTime();
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var ids = await context.Database.SqlQuery<Guid>($"""
            SELECT "Id" AS "Value" FROM download_sessions
            WHERE "ReleasedAt" IS NULL AND ("ExpiresAt" <= {now} OR "LeaseExpiresAt" <= {now})
            ORDER BY "ExpiresAt", "Id" LIMIT {MaxBatch} FOR UPDATE SKIP LOCKED
            """).ToListAsync(cancellationToken);
        var sessions = await context.DownloadSessions.Where(session => ids.Contains(session.Id)).ToListAsync(cancellationToken);
        var expired = 0;
        foreach (var session in sessions)
        {
            if (!session.Expire(now)) continue;
            AddAudit(context, session, now, "download.expired");
            expired++;
        }
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return expired;
    }

    public async Task<DownloadSession?> ExpireSessionAsync(TenantId tenantId, Guid sessionId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var ids = await context.Database.SqlQuery<Guid>($"""
            SELECT "Id" AS "Value" FROM download_sessions
            WHERE "TenantId" = {tenantId.Value} AND "Id" = {sessionId} FOR UPDATE
            """).ToListAsync(cancellationToken);
        if (ids.Count == 0) return null;
        var session = await context.DownloadSessions.SingleAsync(entity => entity.TenantId == tenantId && entity.Id == sessionId, cancellationToken);
        if (session.Expire(now))
        {
            AddAudit(context, session, now, "download.expired");
            await context.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return session;
    }

    public async Task<DownloadSession?> SwitchReplicaAsync(Guid sessionId, Guid currentReplicaId, string? expectedOwner, BackupReplica newReplica, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var link = await context.DownloadSessions.AsNoTracking().Where(entity => entity.Id == sessionId)
            .Select(entity => new { entity.TenantId, entity.ArtifactId }).SingleOrDefaultAsync(cancellationToken);
        if (link is null) return null;
        var artifacts = await context.Database.SqlQuery<Guid>($"""
            SELECT "Id" AS "Value" FROM backup_artifacts
            WHERE "TenantId" = {link.TenantId.Value} AND "Id" = {link.ArtifactId} FOR NO KEY UPDATE
            """).ToListAsync(cancellationToken);
        if (artifacts.Count == 0) return null;
        var ids = await context.Database.SqlQuery<Guid>($"""
            SELECT "Id" AS "Value" FROM download_sessions
            WHERE "Id" = {sessionId} FOR UPDATE
            """).ToListAsync(cancellationToken);
        if (ids.Count == 0) return null;
        var session = await context.DownloadSessions.SingleAsync(s => s.Id == sessionId, cancellationToken);
        if (session.ReplicaId != currentReplicaId || session.IsTerminal) return null;

        var replicaLocked = await context.Database.SqlQuery<Guid>($"""
            SELECT "Id" AS "Value" FROM backup_replicas
            WHERE "TenantId" = {session.TenantId.Value} AND "ArtifactId" = {session.ArtifactId} AND "Id" = {newReplica.Id} FOR UPDATE
            """).ToListAsync(cancellationToken);
        if (replicaLocked.Count == 0) return null;

        var replica = await context.BackupReplicas.AsNoTracking().SingleAsync(r => r.TenantId == session.TenantId && r.ArtifactId == session.ArtifactId && r.Id == newReplica.Id, cancellationToken);
        if (replica.Status != BackupReplicaStatus.Available || replica.VerificationState != "Verified" ||
            replica.Checksum != session.Checksum || replica.SizeBytes != session.SizeBytes) return null;

        var now = _time.GetUtcNow();
        if (!session.SwitchReplica(replica, expectedOwner, now)) return null;
        AddAudit(context, session, now, "download.replica_fallback");
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return session;
    }

    private static async Task<DownloadSession?> LockSessionAsync(PlatformDbContext context, Guid sessionId, CancellationToken cancellationToken)
    {
        var ids = await context.Database.SqlQuery<Guid>($"SELECT \"Id\" AS \"Value\" FROM download_sessions WHERE \"Id\" = {sessionId} FOR UPDATE")
            .ToListAsync(cancellationToken);
        return ids.Count == 0 ? null : await context.DownloadSessions.SingleAsync(session => session.Id == sessionId, cancellationToken);
    }

    private static Task<bool> IsAdministratorAsync(PlatformDbContext context, TenantId tenantId, Guid userId, CancellationToken cancellationToken) =>
        context.TenantMemberships.AnyAsync(membership => membership.TenantId == tenantId && membership.UserId == new UserId(userId) &&
            (membership.Role == TenantRole.Owner || membership.Role == TenantRole.Admin), cancellationToken);

    private static void AddAudit(PlatformDbContext context, DownloadSession session, DateTimeOffset now, string action)
    {
        var details = new Dictionary<string, object?>
        {
            ["artifactId"] = session.ArtifactId, ["replicaId"] = session.ReplicaId, ["sessionId"] = session.Id,
            ["transferredBytes"] = session.TransferredBytes, ["status"] = session.State.ToString(), ["failure"] = session.FailureCode,
            ["startedAt"] = action == "download.streaming" ? now : (DateTimeOffset?)null,
            ["completedAt"] = session.ReleasedAt, ["result"] = session.IsTerminal ? session.State.ToString() : null
        };
        if (session.SupportSessionId is not { } support)
        {
            context.AuditEvents.Add(AuditEvent.Record(now, new UserId(session.UserId), AuditActorKind.TenantAdministrator, action, session.TenantId,
                "download", session.Id.ToString("D"), details));
            return;
        }
        // Agent preparation states are plumbing between the backend and the agent; the request and the transfer are what support access records.
        if (action is "download.preparing" or "download.ready") return;
        // A support download is audited as support access: the real PlatformAdmin as actor, the support session, and the artifact as the resource.
        details["downloadId"] = session.Id;
        details["supportSessionId"] = support;
        details["result"] ??= action == "download.requested" ? "requested" : "started";
        context.AuditEvents.Add(AuditEvent.Record(now, new UserId(session.UserId), AuditActorKind.PlatformAdmin, SupportAction(action), session.TenantId,
            SupportAuditActions.ArtifactTargetType, session.ArtifactId.ToString("D"), details, support));
    }

    private static string SupportAction(string action) => action switch
    {
        "download.requested" => SupportAuditActions.DownloadRequested,
        "download.streaming" or "download.replica_fallback" => SupportAuditActions.DownloadStarted,
        "download.completed" => SupportAuditActions.DownloadCompleted,
        _ => SupportAuditActions.DownloadFailed
    };

    /// <summary>
    /// Ends the not yet terminal downloads of a support session that is closing, in the caller's transaction (which already holds the session exclusively).
    /// A transfer in progress notices on its next lease renewal and stops; a download never started can no longer be streamed. Returns how many ended.
    /// </summary>
    internal static async Task<int> CancelForSupportSessionAsync(PlatformDbContext context, Guid supportSessionId, DateTimeOffset now, string failureCode, CancellationToken cancellationToken)
    {
        var ids = await context.Database.SqlQuery<Guid>($"""
            SELECT "Id" AS "Value" FROM download_sessions WHERE "SupportSessionId" = {supportSessionId} AND "ReleasedAt" IS NULL ORDER BY "Id" FOR UPDATE
            """).ToListAsync(cancellationToken);
        if (ids.Count == 0) return 0;
        var sessions = await context.DownloadSessions.Where(entity => ids.Contains(entity.Id)).ToListAsync(cancellationToken);
        var cancelled = 0;
        foreach (var session in sessions)
        {
            if (!session.CancelForSupport(now, failureCode)) continue;
            AddAudit(context, session, now, "download.cancelled");
            cancelled++;
        }
        return cancelled;
    }
}
