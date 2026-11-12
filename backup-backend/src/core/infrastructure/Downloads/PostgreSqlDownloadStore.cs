using BackupPlatform.Application.Backups;
using BackupPlatform.Application.Downloads;
using BackupPlatform.Application.Tenancy;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Downloads;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Infrastructure.Backups;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Infrastructure.Downloads;

/// <summary>Replica locks serialize durable holds with deletion; session locks fence lifecycle writes and their audits.</summary>
public sealed class PostgreSqlDownloadStore(DbContextOptions<PlatformDbContext> options, ICentralAuthorizationService centralAuth, TimeProvider? time = null) : IDownloadStore
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
        var downloadId = Guid.NewGuid();
        if (command.ShareId is { } shareId)
        {
            if (!await centralAuth.IsPlatformAdminAsync(new UserId(command.UserId), cancellationToken))
            {
                await RecordRefusalInternalAsync(context, new DownloadRefusalCommand(command.TenantId, command.ArtifactId, command.UserId, shareId, "forbidden", command.Now, false), cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return null;
            }
            var shareLocked = await context.Database.SqlQuery<Guid>($"""
                SELECT "Id" AS "Value" FROM backup_shares
                WHERE "TenantId" = {command.TenantId.Value} AND "Id" = {shareId} FOR UPDATE
                """).ToListAsync(cancellationToken);
            if (shareLocked.Count == 0)
            {
                await RecordRefusalInternalAsync(context, new DownloadRefusalCommand(command.TenantId, command.ArtifactId, command.UserId, shareId, "share_not_found", command.Now, true), cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return null;
            }
            var share = await context.BackupShares.AsNoTracking().SingleAsync(s => s.TenantId == command.TenantId && s.Id == shareId, cancellationToken);
            if (share.ArtifactId != command.ArtifactId)
            {
                await RecordRefusalInternalAsync(context, new DownloadRefusalCommand(command.TenantId, command.ArtifactId, command.UserId, shareId, "share_artifact_mismatch", command.Now, true), cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return null;
            }
            if (share.RevokedAtUtc is not null)
            {
                await RecordRefusalInternalAsync(context, new DownloadRefusalCommand(command.TenantId, command.ArtifactId, command.UserId, shareId, "share_revoked", command.Now, true), cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return null;
            }
            if (share.ExpiresAtUtc <= command.Now)
            {
                await RecordRefusalInternalAsync(context, new DownloadRefusalCommand(command.TenantId, command.ArtifactId, command.UserId, shareId, "share_expired", command.Now, true), cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return null;
            }
        }
        else if (!await centralAuth.EvaluateContentAccessAsync(new UserId(command.UserId), command.TenantId, cancellationToken))
        {
            await RecordRefusalInternalAsync(context, new DownloadRefusalCommand(command.TenantId, command.ArtifactId, command.UserId, null, "forbidden", command.Now, false), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        var artifactLocked = await context.Database.SqlQuery<Guid>($"""
            SELECT "Id" AS "Value" FROM backup_artifacts
            WHERE "TenantId" = {command.TenantId.Value} AND "Id" = {command.ArtifactId} FOR NO KEY UPDATE
            """).ToListAsync(cancellationToken);
        if (artifactLocked.Count == 0)
        {
            await RecordRefusalInternalAsync(context, new DownloadRefusalCommand(command.TenantId, command.ArtifactId, command.UserId, command.ShareId, "artifact_not_found", command.Now, command.ShareId != null), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        var replicaLocked = await context.Database.SqlQuery<Guid>($"""
            SELECT "Id" AS "Value" FROM backup_replicas
            WHERE "TenantId" = {command.TenantId.Value} AND "ArtifactId" = {command.ArtifactId} AND "Id" = {command.ReplicaId} FOR UPDATE
            """).ToListAsync(cancellationToken);
        if (replicaLocked.Count == 0)
        {
            await RecordRefusalInternalAsync(context, new DownloadRefusalCommand(command.TenantId, command.ArtifactId, command.UserId, command.ShareId, "replica_not_found", command.Now, command.ShareId != null), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        // Fresh statements after both locks: neither a retention mutation nor a physical claim can invalidate eligibility before the hold commits.
        var artifact = await context.BackupArtifacts.AsNoTracking().SingleAsync(entity => entity.TenantId == command.TenantId && entity.Id == command.ArtifactId, cancellationToken);
        var replica = await context.BackupReplicas.AsNoTracking().SingleAsync(entity => entity.TenantId == command.TenantId && entity.ArtifactId == command.ArtifactId && entity.Id == command.ReplicaId, cancellationToken);
        if (replica.Status != BackupReplicaStatus.Available || replica.VerificationState != "Verified" ||
            replica.Checksum != artifact.ArtifactChecksum || replica.SizeBytes != artifact.SizeBytes || artifact.ChecksumAlgorithm != "sha256")
        {
            await RecordRefusalInternalAsync(context, new DownloadRefusalCommand(command.TenantId, command.ArtifactId, command.UserId, command.ShareId, "replica_unavailable", command.Now, command.ShareId != null), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        var download = new DownloadSession(downloadId, artifact, replica, command.UserId, command.Filename, command.Now, command.Lifetime, command.Decrypted, command.DirectAgentId, command.ShareId);
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
        var requiresActiveAuthorization = transition.State is not (DownloadState.Failed or DownloadState.Cancelled);
        var target = await context.DownloadSessions.AsNoTracking().Where(entity => entity.Id == sessionId)
            .Select(entity => new { entity.TenantId, entity.ArtifactId, entity.AgentId, entity.ShareId }).SingleOrDefaultAsync(cancellationToken);
        if (target is null) return null;
        if (requiresActiveAuthorization && target.ShareId is { } preLockShareId)
        {
            var shareLocked = await context.Database.SqlQuery<Guid>($"""
                SELECT "Id" AS "Value" FROM backup_shares
                WHERE "TenantId" = {target.TenantId.Value} AND "Id" = {preLockShareId} FOR UPDATE
                """).ToListAsync(cancellationToken);
            if (shareLocked.Count == 0) return null;
        }
        if (transition.ExpectedState == DownloadState.Preparing && transition.State == DownloadState.Preparing &&
            transition.ExpectedOwner is null && transition.Owner is not null)
        {
            if (target.AgentId is not { } agentId ||
                !await BackupPlatform.Infrastructure.Backups.PostgreSqlBackupRunStore.LockAgentAsync(context, target.TenantId, new AgentId(agentId), cancellationToken)) return null;
            // Lock the shared agent before any session, so concurrent polls cannot claim different downloads for its single worker.
            var busy = await context.DownloadSessions.AnyAsync(entity => entity.TenantId == target.TenantId && entity.AgentId == agentId &&
                entity.State == DownloadState.Preparing && entity.LeaseOwner != null && entity.ReleasedAt == null &&
                entity.ExpiresAt > transition.Now && entity.LeaseExpiresAt > transition.Now, cancellationToken);
            if (busy) return null;
        }
        var session = await LockSessionAsync(context, sessionId, cancellationToken);
        if (session is null) return null;
        if (requiresActiveAuthorization)
        {
            if (session.ShareId is { } shareId)
            {
                var share = await context.BackupShares.AsNoTracking().SingleOrDefaultAsync(s => s.TenantId == session.TenantId && s.Id == shareId, cancellationToken);
                if (share is null || share.ArtifactId != session.ArtifactId || share.RevokedAtUtc is not null)
                {
                    if (startsStreaming && session.AgentId is null)
                    {
                        var reason = share?.RevokedAtUtc is not null ? "share_revoked" : "share_not_found";
                        await RecordRefusalInternalAsync(context, new DownloadRefusalCommand(session.TenantId, session.ArtifactId, session.UserId, shareId, reason, transition.Now, true), cancellationToken);
                        await transaction.CommitAsync(cancellationToken);
                    }
                    else if (share?.RevokedAtUtc is not null && session.AgentId is not null && !session.IsTerminal)
                    {
                        if (session.CancelByRevocation(transition.Now))
                        {
                            AddAudit(context, session, transition.Now, "download.cancelled");
                            await context.SaveChangesAsync(cancellationToken);
                            await transaction.CommitAsync(cancellationToken);
                        }
                    }
                    return null;
                }
                if (session.State != DownloadState.Streaming && share.ExpiresAtUtc <= transition.Now)
                {
                    if (startsStreaming)
                    {
                        await RecordRefusalInternalAsync(context, new DownloadRefusalCommand(session.TenantId, session.ArtifactId, session.UserId, shareId, "share_expired", transition.Now, true), cancellationToken);
                        await transaction.CommitAsync(cancellationToken);
                    }
                    return null;
                }
            }
            else if (startsStreaming && !await centralAuth.EvaluateContentAccessAsync(new UserId(session.UserId), session.TenantId, cancellationToken))
            {
                await RecordRefusalInternalAsync(context, new DownloadRefusalCommand(session.TenantId, session.ArtifactId, session.UserId, null, "forbidden", transition.Now, false), cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return null;
            }
        }
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
        var shareIds = sessions.Where(s => s.ShareId is not null).Select(s => s.ShareId!.Value).Distinct().ToList();
        var revokedShares = shareIds.Count == 0
            ? []
            : (await context.BackupShares.AsNoTracking()
                .Where(s => shareIds.Contains(s.Id) && s.RevokedAtUtc != null)
                .Select(s => s.Id)
                .ToListAsync(cancellationToken)).ToHashSet();
        var expired = 0;
        foreach (var session in sessions)
        {
            if (session.ShareId is { } sid && revokedShares.Contains(sid) && session.State is DownloadState.Streaming or DownloadState.Preparing)
            {
                if (!session.CancelByRevocation(now)) continue;
                AddAudit(context, session, now, "download.cancelled");
                expired++;
                continue;
            }
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
        if (session.ShareId is { } sid && session.State is DownloadState.Streaming or DownloadState.Preparing
            && await context.BackupShares.AsNoTracking().AnyAsync(s => s.TenantId == tenantId && s.Id == sid && s.RevokedAtUtc != null, cancellationToken))
        {
            if (session.CancelByRevocation(now))
            {
                AddAudit(context, session, now, "download.cancelled");
                await context.SaveChangesAsync(cancellationToken);
            }
        }
        else if (session.Expire(now))
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


    public async Task<bool> IsShareRevokedAsync(TenantId tenantId, Guid shareId, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var share = await context.BackupShares.AsNoTracking()
            .SingleOrDefaultAsync(s => s.TenantId == tenantId && s.Id == shareId, cancellationToken);
        return share is null || share.RevokedAtUtc is not null;
    }

    internal static void RecordSessionAudit(PlatformDbContext context, DownloadSession session, DateTimeOffset now, string action) =>
        AddAudit(context, session, now, action);

    private static void AddAudit(PlatformDbContext context, DownloadSession session, DateTimeOffset now, string action)
    {
        var result = session.State == DownloadState.Cancelled && session.FailureCode == "cancelled_by_revocation"
            ? "cancelled by revocation"
            : session.IsTerminal ? session.State.ToString() : (action == "download.streaming" ? "streaming" : "requested");
        var details = new Dictionary<string, object?>
        {
            ["shareId"] = session.ShareId,
            ["artifactId"] = session.ArtifactId,
            ["replicaId"] = session.ReplicaId,
            ["sessionId"] = session.Id,
            ["tenantId"] = session.TenantId.Value,
            ["initiatingUser"] = session.UserId,
            ["transferredBytes"] = session.TransferredBytes,
            ["bytesTransferred"] = session.TransferredBytes,
            ["status"] = session.State.ToString(),
            ["failure"] = session.FailureCode,
            ["failureReason"] = session.FailureCode,
            ["startedAt"] = action == "download.streaming" ? now : (DateTimeOffset?)null,
            ["startTime"] = action == "download.streaming" ? now : session.CreatedAt,
            ["completedAt"] = session.ReleasedAt,
            ["endTime"] = session.ReleasedAt,
            ["result"] = result,
            ["decrypted"] = session.Decrypted
        };
        var actorKind = session.ShareId is not null ? AuditActorKind.PlatformAdmin : AuditActorKind.TenantAdministrator;
        context.AuditEvents.Add(AuditEvent.Record(now, new UserId(session.UserId), actorKind, action, session.TenantId,
            "download", session.Id.ToString("D"), details));
    }

    public async Task RecordRefusalAsync(DownloadRefusalCommand command, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await RecordRefusalInternalAsync(context, command, cancellationToken);
    }

    private static async Task RecordRefusalInternalAsync(PlatformDbContext context, DownloadRefusalCommand command, CancellationToken cancellationToken)
    {
        var details = new Dictionary<string, object?>
        {
            ["shareId"] = command.ShareId,
            ["artifactId"] = command.ArtifactId,
            ["tenantId"] = command.TenantId.Value,
            ["initiatingUser"] = command.UserId,
            ["startTime"] = command.Now,
            ["startedAt"] = command.Now,
            ["endTime"] = command.Now,
            ["completedAt"] = command.Now,
            ["result"] = "refused",
            ["status"] = "Refused",
            ["bytesTransferred"] = 0L,
            ["transferredBytes"] = 0L,
            ["failureReason"] = command.Reason,
            ["failure"] = command.Reason
        };
        var actorKind = command.IsPlatformAdmin ? AuditActorKind.PlatformAdmin : AuditActorKind.TenantAdministrator;
        context.AuditEvents.Add(AuditEvent.Record(command.Now, new UserId(command.UserId), actorKind, "download.refused", command.TenantId,
            "download", command.ShareId?.ToString("D") ?? command.ArtifactId.ToString("D"), details));
        await context.SaveChangesAsync(cancellationToken);
    }

}
