using System.Text.Json;
using BackupPlatform.Domain.Downloads;
using BackupPlatform.Application.Backups;
using BackupPlatform.Application.Downloads;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Infrastructure.Downloads;
using BackupPlatform.Domain.Notifications;
using BackupPlatform.Infrastructure.Notifications;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Infrastructure.Backups;

public sealed class PostgreSqlBackupShareStore(DbContextOptions<PlatformDbContext> options, ActiveDownloadRegistry? activeDownloads = null) : IBackupShareStore
{
    public async Task<BackupShareCreateResult> CreateAsync(
        TenantId tenantId,
        Guid artifactId,
        UserId createdByUserId,
        UserRole createdByRole,
        TimeSpan duration,
        string? reason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var artifact = await context.BackupArtifacts
            .Include(a => a.Replicas)
            .SingleOrDefaultAsync(a => a.TenantId == tenantId && a.Id == artifactId, cancellationToken);

        if (artifact is null)
        {
            return new BackupShareCreateResult(
                BackupShareCreateOutcome.ArtifactNotFound,
                ErrorCode: "artifact_not_found",
                ErrorMessage: "Artifact was not found.");
        }

        var hasDownloadableReplica = artifact.Replicas.Any(r =>
            r.Status == BackupReplicaStatus.Available &&
            r.VerificationState == "Verified" &&
            r.Checksum == artifact.ArtifactChecksum &&
            r.SizeBytes == artifact.SizeBytes);

        if (!hasDownloadableReplica)
        {
            return new BackupShareCreateResult(
                BackupShareCreateOutcome.NoDownloadableReplica,
                ErrorCode: "no_downloadable_replica",
                ErrorMessage: "A share can be created only when the artifact has at least one downloadable replica that is Verified and Available.");
        }

        var createdAtUtc = now.ToUniversalTime();
        var expiresAtUtc = createdAtUtc + duration;
        var share = new BackupShare(
            Guid.NewGuid(),
            tenantId,
            artifactId,
            createdByUserId,
            createdByRole,
            createdAtUtc,
            expiresAtUtc,
            reason);

        context.BackupShares.Add(share);

        var actorKind = createdByRole == UserRole.PlatformAdmin
            ? AuditActorKind.PlatformAdmin
            : AuditActorKind.TenantAdministrator;

        context.AuditEvents.Add(AuditEvent.Record(
            createdAtUtc,
            createdByUserId,
            actorKind,
            AuditActions.BackupShareCreated,
            tenantId,
            "backup_share",
            share.Id.ToString("D"),
            new Dictionary<string, object?>
            {
                ["actor"] = createdByUserId.ToString(),
                ["role"] = createdByRole.ToString(),
                ["actorRole"] = createdByRole.ToString(),
                ["tenant"] = tenantId.Value.ToString("D"),
                ["artifact"] = artifactId.ToString("D"),
                ["expiry"] = share.ExpiresAtUtc.ToString("o"),
                ["reason"] = share.Reason,
                ["result"] = "created"
            }));

        var artifactNames = await ResolveArtifactNamesAsync(context, [artifactId], cancellationToken, tenantId);
        await RecordShareNotificationAsync(
            context,
            share,
            NotificationEventTypes.BackupShareCreated,
            $"{NotificationEventTypes.BackupShareCreated}:{share.Id:N}",
            artifactNames.GetValueOrDefault(artifactId),
            createdByUserId,
            createdByRole,
            platformAdmins: createdByRole == UserRole.UserAdmin,
            tenantAdmins: createdByRole == UserRole.PlatformAdmin,
            createdAtUtc,
            cancellationToken);

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var userNames = await ResolveUserNamesAsync(context, [createdByUserId], cancellationToken);
        var tenantNames = await ResolveTenantNamesAsync(context, [tenantId], cancellationToken);

        return new BackupShareCreateResult(
            BackupShareCreateOutcome.Success,
            Share: ToView(
                share,
                now,
                artifactNames.GetValueOrDefault(artifactId),
                userNames.GetValueOrDefault(createdByUserId),
                tenantNames.GetValueOrDefault(tenantId)));
    }

    public async Task<BackupShareModifyResult> ExtendAsync(
        TenantId tenantId,
        Guid shareId,
        UserId actor,
        UserRole actorRole,
        ExtendBackupShareRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var locked = await context.Database.SqlQuery<Guid>($"""
            SELECT "Id" AS "Value" FROM backup_shares
            WHERE "TenantId" = {tenantId.Value} AND "Id" = {shareId}
            FOR UPDATE
            """).ToListAsync(cancellationToken);

        if (locked.Count == 0)
        {
            return new BackupShareModifyResult(
                BackupShareModifyOutcome.NotFound,
                ErrorCode: "share_not_found",
                ErrorMessage: "Backup share was not found.");
        }

        var share = await context.BackupShares.SingleAsync(s => s.TenantId == tenantId && s.Id == shareId, cancellationToken);

        if (share.CreatedByRole == UserRole.PlatformAdmin && actorRole != UserRole.PlatformAdmin)
        {
            return new BackupShareModifyResult(
                BackupShareModifyOutcome.Forbidden,
                ErrorCode: "forbidden",
                ErrorMessage: "A share created by a PlatformAdmin can only be extended by a PlatformAdmin.");
        }

        if (share.RevokedAtUtc is not null)
        {
            return new BackupShareModifyResult(
                BackupShareModifyOutcome.AlreadyRevoked,
                ErrorCode: "share_revoked",
                ErrorMessage: "Cannot extend a revoked share.");
        }

        if (share.ExpiresAtUtc <= now)
        {
            return new BackupShareModifyResult(
                BackupShareModifyOutcome.Expired,
                ErrorCode: "share_expired",
                ErrorMessage: "Cannot extend an expired share.");
        }

        DateTimeOffset newExpiresAtUtc;
        if (request.ExpiresAtUtc.HasValue)
        {
            newExpiresAtUtc = request.ExpiresAtUtc.Value.ToUniversalTime();
        }
        else if (request.DurationHours.HasValue)
        {
            if (request.DurationHours.Value <= 0 || request.DurationHours.Value > 72)
            {
                return new BackupShareModifyResult(
                    BackupShareModifyOutcome.InvalidExpiry,
                    ErrorCode: "invalid_duration",
                    ErrorMessage: "Duration must be positive and not exceed 72 hours.");
            }
            newExpiresAtUtc = share.CreatedAtUtc + TimeSpan.FromHours(request.DurationHours.Value);
        }
        else if (request.AdditionalHours.HasValue)
        {
            if (request.AdditionalHours.Value <= 0)
            {
                return new BackupShareModifyResult(
                    BackupShareModifyOutcome.InvalidExpiry,
                    ErrorCode: "invalid_duration",
                    ErrorMessage: "Additional hours must be positive.");
            }
            newExpiresAtUtc = share.ExpiresAtUtc + TimeSpan.FromHours(request.AdditionalHours.Value);
        }
        else if (request.Duration.HasValue)
        {
            if (request.Duration.Value <= TimeSpan.Zero || request.Duration.Value > BackupShare.MaximumDuration)
            {
                return new BackupShareModifyResult(
                    BackupShareModifyOutcome.InvalidExpiry,
                    ErrorCode: "invalid_duration",
                    ErrorMessage: "Duration must be positive and not exceed 72 hours.");
            }
            newExpiresAtUtc = share.CreatedAtUtc + request.Duration.Value;
        }
        else if (request.Extension.HasValue)
        {
            if (request.Extension.Value <= TimeSpan.Zero)
            {
                return new BackupShareModifyResult(
                    BackupShareModifyOutcome.InvalidExpiry,
                    ErrorCode: "invalid_duration",
                    ErrorMessage: "Extension must be positive.");
            }
            newExpiresAtUtc = share.ExpiresAtUtc + request.Extension.Value;
        }
        else
        {
            return new BackupShareModifyResult(
                BackupShareModifyOutcome.InvalidExpiry,
                ErrorCode: "missing_expiry",
                ErrorMessage: "New expiry or duration must be provided.");
        }

        if (newExpiresAtUtc <= share.ExpiresAtUtc)
        {
            return new BackupShareModifyResult(
                BackupShareModifyOutcome.InvalidExpiry,
                ErrorCode: "cannot_shorten",
                ErrorMessage: "New expiry must be later than the current expiry.");
        }

        if (newExpiresAtUtc > share.CreatedAtUtc + BackupShare.MaximumDuration)
        {
            return new BackupShareModifyResult(
                BackupShareModifyOutcome.InvalidExpiry,
                ErrorCode: "duration_exceeded",
                ErrorMessage: "Share duration cannot exceed 72 hours from creation.");
        }

        var oldExpiry = share.ExpiresAtUtc;
        share.Extend(newExpiresAtUtc, now);

        var actorKind = actorRole == UserRole.PlatformAdmin ? AuditActorKind.PlatformAdmin : AuditActorKind.TenantAdministrator;
        context.AuditEvents.Add(AuditEvent.Record(
            now.ToUniversalTime(),
            actor,
            actorKind,
            AuditActions.BackupShareExtended,
            tenantId,
            "backup_share",
            share.Id.ToString("D"),
            new Dictionary<string, object?>
            {
                ["actor"] = actor.ToString(),
                ["role"] = actorRole.ToString(),
                ["tenant"] = tenantId.Value.ToString("D"),
                ["artifact"] = share.ArtifactId.ToString("D"),
                ["oldExpiry"] = oldExpiry.ToString("o"),
                ["newExpiry"] = share.ExpiresAtUtc.ToString("o"),
                ["result"] = "extended"
            }));

        var occurredAtUtc = now.ToUniversalTime();
        var artifactNames = await ResolveArtifactNamesAsync(context, [share.ArtifactId], cancellationToken, tenantId);
        await RecordShareNotificationAsync(
            context,
            share,
            NotificationEventTypes.BackupShareExtended,
            $"{NotificationEventTypes.BackupShareExtended}:{share.Id:N}:{share.ExpiresAtUtc.UtcTicks}",
            artifactNames.GetValueOrDefault(share.ArtifactId),
            actor,
            actorRole,
            platformAdmins: true,
            tenantAdmins: true,
            occurredAtUtc,
            cancellationToken);

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var userNames = await ResolveUserNamesAsync(context, [share.CreatedByUserId], cancellationToken);
        var tenantNames = await ResolveTenantNamesAsync(context, [tenantId], cancellationToken);

        return new BackupShareModifyResult(
            BackupShareModifyOutcome.Success,
            Share: ToView(
                share,
                now,
                artifactNames.GetValueOrDefault(share.ArtifactId),
                userNames.GetValueOrDefault(share.CreatedByUserId),
                tenantNames.GetValueOrDefault(tenantId)));
    }

    public async Task<BackupShareModifyResult> RevokeAsync(
        TenantId tenantId,
        Guid shareId,
        UserId actor,
        UserRole actorRole,
        string? reason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var locked = await context.Database.SqlQuery<Guid>($"""
            SELECT "Id" AS "Value" FROM backup_shares
            WHERE "TenantId" = {tenantId.Value} AND "Id" = {shareId}
            FOR UPDATE
            """).ToListAsync(cancellationToken);

        if (locked.Count == 0)
        {
            return new BackupShareModifyResult(
                BackupShareModifyOutcome.NotFound,
                ErrorCode: "share_not_found",
                ErrorMessage: "Backup share was not found.");
        }

        var share = await context.BackupShares.SingleAsync(s => s.TenantId == tenantId && s.Id == shareId, cancellationToken);

        if (share.CreatedByRole == UserRole.PlatformAdmin && actorRole != UserRole.PlatformAdmin)
        {
            return new BackupShareModifyResult(
                BackupShareModifyOutcome.Forbidden,
                ErrorCode: "forbidden",
                ErrorMessage: "A share created by a PlatformAdmin can only be revoked by a PlatformAdmin.");
        }

        if (share.RevokedAtUtc is not null)
        {
            return new BackupShareModifyResult(
                BackupShareModifyOutcome.AlreadyRevoked,
                ErrorCode: "already_revoked",
                ErrorMessage: "Share is already revoked.");
        }

        if (share.ExpiresAtUtc <= now)
        {
            return new BackupShareModifyResult(
                BackupShareModifyOutcome.Expired,
                ErrorCode: "share_expired",
                ErrorMessage: "Cannot revoke an expired share.");
        }

        var oldExpiry = share.ExpiresAtUtc;
        var revokedAtUtc = now.ToUniversalTime();
        share.Revoke(actor, revokedAtUtc);

        var actorKind = actorRole == UserRole.PlatformAdmin ? AuditActorKind.PlatformAdmin : AuditActorKind.TenantAdministrator;
        context.AuditEvents.Add(AuditEvent.Record(
            revokedAtUtc,
            actor,
            actorKind,
            AuditActions.BackupShareRevoked,
            tenantId,
            "backup_share",
            share.Id.ToString("D"),
            new Dictionary<string, object?>
            {
                ["actor"] = actor.ToString(),
                ["role"] = actorRole.ToString(),
                ["tenant"] = tenantId.Value.ToString("D"),
                ["artifact"] = share.ArtifactId.ToString("D"),
                ["oldExpiry"] = oldExpiry.ToString("o"),
                ["revokedAt"] = share.RevokedAtUtc?.ToString("o"),
                ["reason"] = reason,
                ["result"] = "revoked"
            }));

        var agentSessionIds = await context.Database.SqlQuery<Guid>($"""
            SELECT "Id" AS "Value" FROM download_sessions
            WHERE "TenantId" = {tenantId.Value} AND "ShareId" = {share.Id} AND "AgentId" IS NOT NULL AND "ReleasedAt" IS NULL
            ORDER BY "Id"
            FOR UPDATE
            """).ToListAsync(cancellationToken);

        if (agentSessionIds.Count > 0)
        {
            var agentSessions = await context.DownloadSessions
                .Where(s => agentSessionIds.Contains(s.Id))
                .ToListAsync(cancellationToken);
            foreach (var agentSession in agentSessions)
            {
                if (agentSession.CancelByRevocation(revokedAtUtc))
                {
                    PostgreSqlDownloadStore.RecordSessionAudit(context, agentSession, revokedAtUtc, "download.cancelled");
                }
            }
        }

        await context.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_notify('backup_share_revoked', {share.Id.ToString("D")})",
            cancellationToken);

        var occurredAtUtc = now.ToUniversalTime();
        var artifactNames = await ResolveArtifactNamesAsync(context, [share.ArtifactId], cancellationToken, tenantId);
        await RecordShareNotificationAsync(
            context,
            share,
            NotificationEventTypes.BackupShareRevoked,
            $"{NotificationEventTypes.BackupShareRevoked}:{share.Id:N}",
            artifactNames.GetValueOrDefault(share.ArtifactId),
            actor,
            actorRole,
            platformAdmins: true,
            tenantAdmins: true,
            occurredAtUtc,
            cancellationToken);

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        activeDownloads?.CancelShare(share.Id);
        var userNames = await ResolveUserNamesAsync(context, [share.CreatedByUserId], cancellationToken);
        var tenantNames = await ResolveTenantNamesAsync(context, [tenantId], cancellationToken);

        return new BackupShareModifyResult(
            BackupShareModifyOutcome.Success,
            Share: ToView(
                share,
                now,
                artifactNames.GetValueOrDefault(share.ArtifactId),
                userNames.GetValueOrDefault(share.CreatedByUserId),
                tenantNames.GetValueOrDefault(tenantId)));
    }

    public async Task<BackupShareView?> GetAsync(
        TenantId tenantId,
        Guid shareId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var share = await context.BackupShares
            .AsNoTracking()
            .SingleOrDefaultAsync(s => s.TenantId == tenantId && s.Id == shareId, cancellationToken);

        if (share is null)
        {
            return null;
        }

        var userNames = await ResolveUserNamesAsync(context, [share.CreatedByUserId], cancellationToken);
        var artifactNames = await ResolveArtifactNamesAsync(context, [share.ArtifactId], cancellationToken, tenantId);
        var tenantNames = await ResolveTenantNamesAsync(context, [tenantId], cancellationToken);

        return ToView(
            share,
            now,
            artifactNames.GetValueOrDefault(share.ArtifactId),
            userNames.GetValueOrDefault(share.CreatedByUserId),
            tenantNames.GetValueOrDefault(tenantId));
    }

    public async Task<IReadOnlyList<BackupShareView>> ListAsync(
        TenantId tenantId,
        Guid? artifactId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var shares = await context.BackupShares
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId && (artifactId == null || s.ArtifactId == artifactId))
            .OrderByDescending(s => s.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        if (shares.Count == 0)
        {
            return [];
        }

        var userIds = shares.Select(s => s.CreatedByUserId).Distinct().ToArray();
        var artifactIds = shares.Select(s => s.ArtifactId).Distinct().ToArray();

        var userNames = await ResolveUserNamesAsync(context, userIds, cancellationToken);
        var artifactNames = await ResolveArtifactNamesAsync(context, artifactIds, cancellationToken, tenantId);
        var tenantNames = await ResolveTenantNamesAsync(context, [tenantId], cancellationToken);

        return shares.Select(share => ToView(
            share,
            now,
            artifactNames.GetValueOrDefault(share.ArtifactId),
            userNames.GetValueOrDefault(share.CreatedByUserId),
            tenantNames.GetValueOrDefault(share.TenantId))).ToList();
    }

    public async Task<IReadOnlyList<BackupShareView>> ListPlatformAsync(
        TenantId? tenantId,
        BackupShareStatus? status,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var query = context.BackupShares.AsNoTracking();

        if (tenantId != null)
        {
            query = query.Where(s => s.TenantId == tenantId);
        }

        var utcNow = now.ToUniversalTime();
        if (status.HasValue)
        {
            query = status.Value switch
            {
                BackupShareStatus.Revoked => query.Where(s => s.RevokedAtUtc != null),
                BackupShareStatus.Expired => query.Where(s => s.RevokedAtUtc == null && s.ExpiresAtUtc <= utcNow),
                BackupShareStatus.Active => query.Where(s => s.RevokedAtUtc == null && s.ExpiresAtUtc > utcNow),
                _ => query
            };
        }

        var shares = await query
            .OrderByDescending(s => s.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        if (shares.Count == 0)
        {
            return [];
        }

        var userIds = shares.Select(s => s.CreatedByUserId).Distinct().ToArray();
        var artifactIds = shares.Select(s => s.ArtifactId).Distinct().ToArray();
        var tenantIds = shares.Select(s => s.TenantId).Distinct().ToArray();

        var userNames = await ResolveUserNamesAsync(context, userIds, cancellationToken);
        var artifactNames = await ResolveArtifactNamesAsync(context, artifactIds, cancellationToken, tenantId);
        var tenantNames = await ResolveTenantNamesAsync(context, tenantIds, cancellationToken);

        return shares.Select(share => ToView(
            share,
            now,
            artifactNames.GetValueOrDefault(share.ArtifactId),
            userNames.GetValueOrDefault(share.CreatedByUserId),
            tenantNames.GetValueOrDefault(share.TenantId))).ToList();
    }

    public async Task<BackupShareView?> GetPlatformAsync(
        Guid shareId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var share = await context.BackupShares
            .AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == shareId, cancellationToken);

        if (share is null)
        {
            return null;
        }

        var userNames = await ResolveUserNamesAsync(context, [share.CreatedByUserId], cancellationToken);
        var artifactNames = await ResolveArtifactNamesAsync(context, [share.ArtifactId], cancellationToken, share.TenantId);
        var tenantNames = await ResolveTenantNamesAsync(context, [share.TenantId], cancellationToken);

        return ToView(
            share,
            now,
            artifactNames.GetValueOrDefault(share.ArtifactId),
            userNames.GetValueOrDefault(share.CreatedByUserId),
            tenantNames.GetValueOrDefault(share.TenantId));
    }

    private const string RefusedAction = "download.refused";
    private const string RetentionLock = "SELECT pg_advisory_xact_lock(hashtext('audit_retention'))";

    /// <summary>
    /// One page of download attempts, newest first. An attempt is a download session, or a refusal that happened before any session existed.
    /// A refusal of a prepared session carries that session's id and is shown as the session's outcome, never matched by time, user or artifact.
    /// Both sources are filtered and keyset-limited in the database, so every attempt is reachable by following the cursor.
    /// </summary>
    public async Task<DownloadAttemptPage> ListDownloadAttemptsAsync(
        DownloadAttemptFilter filter,
        bool tenantScoped,
        DownloadAttemptCursor? after,
        int limit,
        CancellationToken cancellationToken)
    {
        var empty = new DownloadAttemptPage([], null);
        if (tenantScoped && filter.TenantId is null) return empty;
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);

        var result = string.IsNullOrWhiteSpace(filter.Result) ? null : filter.Result.Trim();
        var refusedOnly = string.Equals(result, "Refused", StringComparison.OrdinalIgnoreCase);
        DownloadState? state = result is null || refusedOnly ? null : Enum.Parse<DownloadState>(result, ignoreCase: true);
        var refusals = context.AuditEvents.AsNoTracking().Where(e => e.Action == RefusedAction && e.TargetType == "download");

        var sessionQuery = context.DownloadSessions.AsNoTracking().AsQueryable();
        var refusalQuery = refusals.Where(e => !EF.Functions.JsonExists(e.DetailsJson, "sessionId"));
        if (filter.TenantId is { } tenantId)
        {
            sessionQuery = sessionQuery.Where(s => s.TenantId == tenantId);
            refusalQuery = refusalQuery.Where(e => e.TenantId == tenantId);
        }
        if (filter.ShareId is { } shareId)
        {
            sessionQuery = sessionQuery.Where(s => s.ShareId == shareId);
            refusalQuery = refusalQuery.Where(e => EF.Functions.JsonContains(e.DetailsJson, JsonSerializer.Serialize(new { shareId })));
        }
        if (filter.ArtifactId is { } artifactId)
        {
            sessionQuery = sessionQuery.Where(s => s.ArtifactId == artifactId);
            refusalQuery = refusalQuery.Where(e => EF.Functions.JsonContains(e.DetailsJson, JsonSerializer.Serialize(new { artifactId })));
        }
        if (filter.InitiatingUserId is { } initiatingUserId)
        {
            var actor = new UserId(initiatingUserId);
            sessionQuery = sessionQuery.Where(s => s.UserId == initiatingUserId);
            refusalQuery = refusalQuery.Where(e => e.ActorUserId == actor);
        }
        if (after is not null)
        {
            sessionQuery = sessionQuery.Where(s => s.CreatedAt < after.StartedAtUtc || (s.CreatedAt == after.StartedAtUtc && s.Id.CompareTo(after.Id) < 0));
            refusalQuery = refusalQuery.Where(e => e.At < after.StartedAtUtc || (e.At == after.StartedAtUtc && e.Id.CompareTo(after.Id) < 0));
        }

        // A prepared session that was refused when its stream started is reported as Refused while it is still unstarted.
        var candidates = sessionQuery.Select(s => new
        {
            Session = s,
            Refused = s.State == DownloadState.Ready && refusals.Any(e => e.TargetId == s.Id.ToString())
        });
        if (refusedOnly) candidates = candidates.Where(row => row.Refused);
        else if (state is not null) candidates = candidates.Where(row => row.Session.State == state && !row.Refused);

        var sessions = await candidates.OrderByDescending(row => row.Session.CreatedAt).ThenByDescending(row => row.Session.Id)
            .Take(limit + 1).ToListAsync(cancellationToken);
        var sessionless = result is null || refusedOnly
            ? await refusalQuery.OrderByDescending(e => e.At).ThenByDescending(e => e.Id).Take(limit + 1).ToListAsync(cancellationToken)
            : [];

        var refusedSessionIds = sessions.Where(row => row.Refused).Select(row => row.Session.Id.ToString("D")).ToArray();
        var sessionRefusals = refusedSessionIds.Length == 0 ? [] : (await refusals.Where(e => refusedSessionIds.Contains(e.TargetId)).ToListAsync(cancellationToken))
            .GroupBy(e => e.TargetId).ToDictionary(group => group.Key, group => group.MaxBy(e => e.At)!);

        var merged = sessions.Select(row =>
            {
                var s = row.Session;
                var refusal = row.Refused ? sessionRefusals.GetValueOrDefault(s.Id.ToString("D")) : null;
                return new Attempt(s.Id, s.ShareId, s.TenantId, s.ArtifactId, s.UserId, s.CreatedAt, refusal?.At ?? s.ReleasedAt,
                    refusal is null ? s.State.ToString() : "Refused", s.TransferredBytes, refusal is null ? s.FailureCode : RefusalReason(refusal), s.Decrypted);
            })
            .Concat(sessionless.Select(ToAttempt).OfType<Attempt>())
            // PostgreSQL orders uuid bytewise, which is the ordinal order of its hex form; the in-memory merge must agree with the keyset predicate.
            .OrderByDescending(attempt => attempt.StartedAtUtc).ThenByDescending(attempt => attempt.Id.ToString("N"), StringComparer.Ordinal)
            .ToList();
        var page = merged.Take(limit).ToList();
        if (page.Count == 0) return empty;

        HashSet<Guid>? tenantShareIds = tenantScoped
            ? [.. await context.BackupShares.AsNoTracking().Where(s => s.TenantId == filter.TenantId).Select(s => s.Id).ToListAsync(cancellationToken)]
            : null;
        var userNames = await ResolveUserNamesAsync(context, page.Select(a => new UserId(a.UserId)).Distinct().ToArray(), cancellationToken);
        var tenantNames = await ResolveTenantNamesAsync(context, page.Select(a => a.TenantId).Distinct().ToArray(), cancellationToken);
        var artifactNames = await ResolveArtifactNamesAsync(context, page.Select(a => a.ArtifactId).Where(id => id != Guid.Empty).Distinct().ToArray(), cancellationToken, filter.TenantId);

        var items = page.Select(a => new DownloadAttemptView(
            a.Id,
            // A tenant never learns the id of a share that is not its own, even when someone used it against its artifact.
            a.ShareId is { } id && tenantShareIds is not null && !tenantShareIds.Contains(id) ? null : a.ShareId,
            a.TenantId.Value, tenantNames.GetValueOrDefault(a.TenantId), a.ArtifactId, artifactNames.GetValueOrDefault(a.ArtifactId),
            a.UserId, userNames.GetValueOrDefault(new UserId(a.UserId)), a.StartedAtUtc, a.EndedAtUtc, a.Result, a.Bytes, a.FailureReason, a.Decrypted)).ToList();
        var last = page[^1];
        return new DownloadAttemptPage(items, merged.Count > limit ? new DownloadAttemptCursor(last.StartedAtUtc, last.Id).ToString() : null);
    }

    private sealed record Attempt(Guid Id, Guid? ShareId, TenantId TenantId, Guid ArtifactId, Guid UserId, DateTimeOffset StartedAtUtc, DateTimeOffset? EndedAtUtc,
        string Result, long Bytes, string? FailureReason, bool Decrypted);

    private static string RefusalReason(AuditEvent audit)
    {
        using var details = JsonDocument.Parse(audit.DetailsJson);
        return TryGetString(details.RootElement, "failureReason") ?? "refused";
    }

    private static Attempt? ToAttempt(AuditEvent audit)
    {
        if (audit.TenantId is not { } tenantId) return null;
        using var details = JsonDocument.Parse(audit.DetailsJson);
        var root = details.RootElement;
        return new Attempt(audit.Id, TryGetGuid(root, "shareId"), tenantId, TryGetGuid(root, "artifactId") ?? Guid.Empty, audit.ActorUserId.Value,
            audit.At, audit.At, "Refused", 0, TryGetString(root, "failureReason") ?? "refused", false);
    }

    public async Task<AuditRetentionSettingsView> GetAuditRetentionAsync(CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var row = await context.PlatformAuditRetentionSettings
            .AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == PlatformAuditRetentionSettings.SingletonId, cancellationToken);

        return new AuditRetentionSettingsView(
            row?.RetentionDays ?? PlatformAuditRetentionSettings.DefaultRetentionDays,
            PlatformAuditRetentionSettings.DefaultRetentionDays,
            PlatformAuditRetentionSettings.MinimumRetentionDays,
            PlatformAuditRetentionSettings.MaximumRetentionDays,
            row?.UpdatedAt,
            row?.UpdatedBy.Value);
    }

    public async Task<AuditRetentionSettingsView> SaveAuditRetentionAsync(
        int retentionDays,
        UserId actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var utcNow = now.ToUniversalTime();
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        // One lock for the setting and the pruning on every API node: a prune always runs with a committed setting, never across a change of it.
        await context.Database.ExecuteSqlRawAsync(RetentionLock, cancellationToken);

        var row = await context.PlatformAuditRetentionSettings
            .SingleOrDefaultAsync(s => s.Id == PlatformAuditRetentionSettings.SingletonId, cancellationToken);

        var previousRetentionDays = row?.RetentionDays ?? PlatformAuditRetentionSettings.DefaultRetentionDays;
        if (row is null)
        {
            row = new PlatformAuditRetentionSettings(retentionDays, actor, utcNow);
            context.PlatformAuditRetentionSettings.Add(row);
        }
        else
        {
            row.Set(retentionDays, actor, utcNow);
        }

        context.AuditEvents.Add(AuditEvent.Record(
            utcNow,
            actor,
            AuditActorKind.PlatformAdmin,
            AuditActions.AuditRetentionUpdated,
            tenantId: null,
            "audit_retention",
            "singleton",
            new Dictionary<string, object?>
            {
                ["previousRetentionDays"] = previousRetentionDays,
                ["retentionDays"] = retentionDays
            }));

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new AuditRetentionSettingsView(
            row.RetentionDays,
            PlatformAuditRetentionSettings.DefaultRetentionDays,
            PlatformAuditRetentionSettings.MinimumRetentionDays,
            PlatformAuditRetentionSettings.MaximumRetentionDays,
            row.UpdatedAt,
            row.UpdatedBy.Value);
    }

    public async Task<AuditHistoryPruneResult> PruneHistoryAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var utcNow = now.ToUniversalTime();
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        // One lock for the setting and the pruning on every API node: a prune always runs with a committed setting, never across a change of it.
        await context.Database.ExecuteSqlRawAsync(RetentionLock, cancellationToken);

        var retentionDays = (await context.PlatformAuditRetentionSettings
                .AsNoTracking()
                .SingleOrDefaultAsync(s => s.Id == PlatformAuditRetentionSettings.SingletonId, cancellationToken))?.RetentionDays
            ?? PlatformAuditRetentionSettings.DefaultRetentionDays;
        var cutoff = utcNow - TimeSpan.FromDays(retentionDays);

        var activeShareIdsQuery = context.BackupShares
            .Where(s => s.RevokedAtUtc == null && s.ExpiresAtUtc > utcNow)
            .Select(s => s.Id);

        var downloadSessionsRemoved = await context.DownloadSessions
            .Where(s => s.ReleasedAt != null
                && s.ReleasedAt < cutoff
                && (s.ShareId == null || !activeShareIdsQuery.Contains(s.ShareId.Value)))
            .ExecuteDeleteAsync(cancellationToken);

        var sharesRemoved = await context.BackupShares
            .Where(s =>
                ((s.RevokedAtUtc != null && s.RevokedAtUtc < cutoff) || (s.RevokedAtUtc == null && s.ExpiresAtUtc <= utcNow && s.ExpiresAtUtc < cutoff))
                && !context.DownloadSessions.Any(d => d.ShareId == s.Id))
            .ExecuteDeleteAsync(cancellationToken);

        // Sessions that survived the step above and are older than the cutoff are exactly the protected ones (running, or of an active share);
        // their audit trail, including refusals recorded against them, stays with them.
        var protectedSessions = context.DownloadSessions.Where(d => d.ReleasedAt == null || (d.ShareId != null && activeShareIdsQuery.Contains(d.ShareId.Value)));
        var auditEventsRemoved = await context.AuditEvents
            .Where(e => e.At < cutoff
                && !(e.TargetType == "download" && protectedSessions.Any(d => d.Id.ToString() == e.TargetId))
                && !((e.TargetType == "backup_share" || (e.TargetType == "download" && e.Action == RefusedAction))
                    && activeShareIdsQuery.Any(id => id.ToString() == e.TargetId)))
            .ExecuteDeleteAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return new AuditHistoryPruneResult(auditEventsRemoved, downloadSessionsRemoved, sharesRemoved);
    }

    private static Guid? TryGetGuid(JsonElement root, string property)
    {
        if (root.TryGetProperty(property, out var elem) && elem.ValueKind == JsonValueKind.String && Guid.TryParse(elem.GetString(), out var g))
        {
            return g;
        }
        return null;
    }

    private static string? TryGetString(JsonElement root, string property)
    {
        if (root.TryGetProperty(property, out var elem) && elem.ValueKind == JsonValueKind.String)
        {
            return elem.GetString();
        }
        return null;
    }

    private static BackupShareView ToView(
        BackupShare share,
        DateTimeOffset now,
        string? artifactName,
        string? createdByDisplayName,
        string? tenantName = null)
    {
        return new BackupShareView(
            share.Id,
            share.TenantId.Value,
            share.ArtifactId,
            share.CreatedByUserId.Value,
            share.CreatedByRole.ToString(),
            share.Reason,
            share.CreatedAtUtc,
            share.ExpiresAtUtc,
            share.RevokedAtUtc,
            share.RevokedByUserId?.Value,
            share.GetStatus(now).ToString(),
            artifactName,
            createdByDisplayName,
            tenantName);
    }

    private static async Task RecordShareNotificationAsync(
        PlatformDbContext context,
        BackupShare share,
        string eventType,
        string dedupKey,
        string? artifactName,
        UserId actor,
        UserRole actorRole,
        bool platformAdmins,
        bool tenantAdmins,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        var subjectName = !string.IsNullOrWhiteSpace(artifactName) ? artifactName : "Backup share";
        var notificationEvent = new NotificationEvent(
            Guid.NewGuid(),
            share.TenantId,
            eventType,
            NotificationPhase.Raised,
            NotificationSeverity.Info,
            new NotificationSubject("backup_share", share.Id, subjectName),
            new Dictionary<string, string>
            {
                ["shareId"] = share.Id.ToString("D"),
                ["artifactId"] = share.ArtifactId.ToString("D"),
                ["actorRole"] = actorRole.ToString(),
                ["expiresAtUtc"] = share.ExpiresAtUtc.ToString("o")
            },
            dedupKey,
            occurredAt,
            occurredAt);

        await UserNotificationWriter.RecordAsync(
            context,
            notificationEvent,
            [],
            platformAdmins: platformAdmins,
            tenantAdmins: tenantAdmins,
            except: actor,
            cancellationToken);
    }

    private static async Task<Dictionary<UserId, string>> ResolveUserNamesAsync(
        PlatformDbContext context,
        IReadOnlyCollection<UserId> userIds,
        CancellationToken cancellationToken)
    {
        if (userIds.Count == 0) return [];
        return await context.Users
            .AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, cancellationToken);
    }

    private static async Task<Dictionary<TenantId, string>> ResolveTenantNamesAsync(
        PlatformDbContext context,
        IReadOnlyCollection<TenantId> tenantIds,
        CancellationToken cancellationToken)
    {
        if (tenantIds.Count == 0) return [];
        return await context.Tenants
            .AsNoTracking()
            .Where(t => tenantIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Name, cancellationToken);
    }

    private static async Task<Dictionary<Guid, string>> ResolveArtifactNamesAsync(
        PlatformDbContext context,
        IReadOnlyCollection<Guid> artifactIds,
        CancellationToken cancellationToken,
        TenantId? tenantId = null)
    {
        if (artifactIds.Count == 0) return [];

        var query = from artifact in context.BackupArtifacts.AsNoTracking()
                    join attempt in context.BackupRunAttempts.AsNoTracking() on new { artifact.AttemptId, artifact.TenantId } equals new { AttemptId = attempt.Id, attempt.TenantId }
                    join run in context.BackupRuns.AsNoTracking() on new { attempt.RunId, attempt.TenantId } equals new { RunId = run.Id, run.TenantId }
                    join job in context.BackupJobs.AsNoTracking() on new { run.JobId, run.TenantId } equals new { JobId = job.Id, job.TenantId }
                    where artifactIds.Contains(artifact.Id)
                    select new
                    {
                        ArtifactId = artifact.Id,
                        TenantId = artifact.TenantId,
                        DatabaseName = run.Configuration.Database,
                        JobName = job.Name
                    };

        if (tenantId != null)
        {
            query = query.Where(r => r.TenantId == tenantId);
        }

        var rows = await query.ToListAsync(cancellationToken);

        return rows
            .DistinctBy(r => r.ArtifactId)
            .ToDictionary(
                r => r.ArtifactId,
                r => !string.IsNullOrWhiteSpace(r.DatabaseName) ? r.DatabaseName : r.JobName);
    }
}
