using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Infrastructure.Backups;

public sealed class PostgreSqlBackupShareStore(DbContextOptions<PlatformDbContext> options) : IBackupShareStore
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

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var userNames = await ResolveUserNamesAsync(context, [createdByUserId], cancellationToken);
        var artifactNames = await ResolveArtifactNamesAsync(context, [artifactId], cancellationToken, tenantId);
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
