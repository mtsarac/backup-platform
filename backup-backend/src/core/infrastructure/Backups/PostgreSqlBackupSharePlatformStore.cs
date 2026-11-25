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

namespace BackupPlatform.Infrastructure.Backups;


public sealed class PostgreSqlBackupSharePlatformStore(DbContextOptions<PlatformDbContext> options) : IBackupSharePlatformStore
{
    public async Task<IReadOnlyList<BackupShareView>> ListPlatformAsync(
        TenantId? tenantId,
        BackupShareStatus? status,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var context = PlatformOperationsDbContext.Create(options);
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

        var userNames = await BackupShareQueries.ResolveUserNamesAsync(context, userIds, cancellationToken);
        var artifactNames = await BackupShareQueries.ResolveArtifactNamesAsync(context, artifactIds, cancellationToken, tenantId);
        var tenantNames = await BackupShareQueries.ResolveTenantNamesAsync(context, tenantIds, cancellationToken);

        return shares.Select(share => BackupShareQueries.ToView(
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
        await using var context = PlatformOperationsDbContext.Create(options);
        var share = await context.BackupShares
            .AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == shareId, cancellationToken);

        if (share is null)
        {
            return null;
        }

        var userNames = await BackupShareQueries.ResolveUserNamesAsync(context, [share.CreatedByUserId], cancellationToken);
        var artifactNames = await BackupShareQueries.ResolveArtifactNamesAsync(context, [share.ArtifactId], cancellationToken, share.TenantId);
        var tenantNames = await BackupShareQueries.ResolveTenantNamesAsync(context, [share.TenantId], cancellationToken);

        return BackupShareQueries.ToView(
            share,
            now,
            artifactNames.GetValueOrDefault(share.ArtifactId),
            userNames.GetValueOrDefault(share.CreatedByUserId),
            tenantNames.GetValueOrDefault(share.TenantId));
    }

    private const string RetentionLock = "SELECT pg_advisory_xact_lock(hashtext('audit_retention'))";

    public async Task<AuditRetentionSettingsView> GetAuditRetentionAsync(CancellationToken cancellationToken)
    {
        await using var context = PlatformOperationsDbContext.Create(options);
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
        await using var context = PlatformOperationsDbContext.Create(options);
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

        context.RecordAudit(AuditEvent.Record(
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
        await using var context = PlatformOperationsDbContext.Create(options);
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
                && !(e.TargetType == "download" && protectedSessions.Any(d => d.TenantId == e.TenantId && d.Id.ToString() == e.TargetId))
                && !((e.TargetType == "backup_share" || (e.TargetType == "download" && e.Action == BackupShareQueries.RefusedAction))
                    && context.BackupShares.Any(s => s.RevokedAtUtc == null && s.ExpiresAtUtc > utcNow && s.TenantId == e.TenantId && s.Id.ToString() == e.TargetId)))
            .ExecuteDeleteAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return new AuditHistoryPruneResult(auditEventsRemoved, downloadSessionsRemoved, sharesRemoved);
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
        await using var context = PlatformOperationsDbContext.Create(options);
        return await BackupShareQueries.CreateCoreAsync(context, tenantId, artifactId, createdByUserId, createdByRole, duration, reason, now, cancellationToken);
    }

    public async Task<PagedResult<DownloadAttemptView>> ListPlatformDownloadAttemptsAsync(
        DownloadAttemptFilter filter,
        ListQuery query,
        CancellationToken cancellationToken)
    {
        await using var context = PlatformOperationsDbContext.Create(options);
        return await BackupShareQueries.ListDownloadAttemptsCoreAsync(context, filter, tenantScoped: false, query, cancellationToken);
    }
}
