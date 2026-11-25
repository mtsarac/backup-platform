using System.Text.Json;
using BackupPlatform.Domain.Downloads;
using BackupPlatform.Application.Backups;
using BackupPlatform.Application.Downloads;
using BackupPlatform.Application.Lists;
using BackupPlatform.Application.Platform;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Infrastructure.Downloads;
using BackupPlatform.Infrastructure.Lists;
using BackupPlatform.Domain.Notifications;
using BackupPlatform.Infrastructure.Notifications;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using BackupPlatform.Application.Tenancy;

namespace BackupPlatform.Infrastructure.Backups;

/// <summary>Shares and download attempts of the request's tenant on the filtered context. Platform-wide administration, audit retention and pruning are <see cref="PostgreSqlBackupSharePlatformStore"/>.</summary>
public sealed class PostgreSqlBackupShareStore(DbContextOptions<PlatformDbContext> options, ITenantContext tenant, ActiveDownloadRegistry? activeDownloads = null) : IBackupShareStore
{
    private PlatformDbContext Open(TenantId tenantId) => PlatformDbContext.ForTenant(options, tenant, tenantId);

    public async Task<BackupShareModifyResult> ExtendAsync(
        TenantId tenantId,
        Guid shareId,
        UserId actor,
        UserRole actorRole,
        ExtendBackupShareRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var context = Open(tenantId);
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

        var actorKind = actorRole == UserRole.PlatformAdmin ? AuditActorKind.PlatformAdmin : AuditActorKind.UserAdmin;
        context.RecordAudit(AuditEvent.Record(
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
        var artifactNames = await BackupShareQueries.ResolveArtifactNamesAsync(context, [share.ArtifactId], cancellationToken, tenantId);
        await BackupShareQueries.RecordShareNotificationAsync(
            context.PlatformSide(),
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

        var userNames = await BackupShareQueries.ResolveUserNamesAsync(context, [share.CreatedByUserId], cancellationToken);
        var tenantNames = await BackupShareQueries.ResolveTenantNamesAsync(context, [tenantId], cancellationToken);

        return new BackupShareModifyResult(
            BackupShareModifyOutcome.Success,
            Share: BackupShareQueries.ToView(
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
        await using var context = Open(tenantId);
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

        var actorKind = actorRole == UserRole.PlatformAdmin ? AuditActorKind.PlatformAdmin : AuditActorKind.UserAdmin;
        context.RecordAudit(AuditEvent.Record(
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
                    DownloadStoreCore.RecordSessionAudit(context, agentSession, revokedAtUtc, "download.cancelled");
                }
            }
        }

        await context.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_notify('backup_share_revoked', {share.Id.ToString("D")})",
            cancellationToken);

        var occurredAtUtc = now.ToUniversalTime();
        var artifactNames = await BackupShareQueries.ResolveArtifactNamesAsync(context, [share.ArtifactId], cancellationToken, tenantId);
        await BackupShareQueries.RecordShareNotificationAsync(
            context.PlatformSide(),
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
        var userNames = await BackupShareQueries.ResolveUserNamesAsync(context, [share.CreatedByUserId], cancellationToken);
        var tenantNames = await BackupShareQueries.ResolveTenantNamesAsync(context, [tenantId], cancellationToken);

        return new BackupShareModifyResult(
            BackupShareModifyOutcome.Success,
            Share: BackupShareQueries.ToView(
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
        await using var context = Open(tenantId);
        var share = await context.BackupShares
            .AsNoTracking()
            .SingleOrDefaultAsync(s => s.TenantId == tenantId && s.Id == shareId, cancellationToken);

        if (share is null)
        {
            return null;
        }

        var userNames = await BackupShareQueries.ResolveUserNamesAsync(context, [share.CreatedByUserId], cancellationToken);
        var artifactNames = await BackupShareQueries.ResolveArtifactNamesAsync(context, [share.ArtifactId], cancellationToken, tenantId);
        var tenantNames = await BackupShareQueries.ResolveTenantNamesAsync(context, [tenantId], cancellationToken);

        return BackupShareQueries.ToView(
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
        await using var context = Open(tenantId);
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

        var userNames = await BackupShareQueries.ResolveUserNamesAsync(context, userIds, cancellationToken);
        var artifactNames = await BackupShareQueries.ResolveArtifactNamesAsync(context, artifactIds, cancellationToken, tenantId);
        var tenantNames = await BackupShareQueries.ResolveTenantNamesAsync(context, [tenantId], cancellationToken);

        return shares.Select(share => BackupShareQueries.ToView(
            share,
            now,
            artifactNames.GetValueOrDefault(share.ArtifactId),
            userNames.GetValueOrDefault(share.CreatedByUserId),
            tenantNames.GetValueOrDefault(share.TenantId))).ToList();
    }

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
        await using var context = Open(tenantId);
        return await BackupShareQueries.CreateCoreAsync(context, tenantId, artifactId, createdByUserId, createdByRole, duration, reason, now, cancellationToken);
    }

    public async Task<PagedResult<DownloadAttemptView>> ListDownloadAttemptsAsync(
        DownloadAttemptFilter filter,
        ListQuery query,
        CancellationToken cancellationToken)
    {
        if (filter.TenantId is not { } tenantId) return new PagedResult<DownloadAttemptView>([], 0, query.Page, query.PageSize);
        await using var context = Open(tenantId);
        return await BackupShareQueries.ListDownloadAttemptsCoreAsync(context, filter, tenantScoped: true, query, cancellationToken);
    }
}
