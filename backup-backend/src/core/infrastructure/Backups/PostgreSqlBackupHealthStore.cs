using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Restores;
using BackupPlatform.Domain.Storage;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Infrastructure.Backups;

public sealed class PostgreSqlBackupHealthStore(DbContextOptions<PlatformDbContext> options) : IBackupHealthStore
{
    public async Task<TenantId?> FindJobTenantAsync(Guid jobId, CancellationToken cancellationToken)
    {
        await using var db = PlatformDbContext.CreateForPlatformOperations(options);
        return await db.BackupJobs.AsNoTracking().Where(job => job.Id == jobId).Select(job => job.TenantId).SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<BackupHealthEvidence>> ReadAsync(TenantId tenantId, Guid? jobId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var db = PlatformDbContext.CreateForPlatformOperations(options);
        var rows = await db.BackupJobs.AsNoTracking().Where(job => job.TenantId == tenantId && (jobId == null || job.Id == jobId))
            .OrderBy(job => job.Name).ThenBy(job => job.Id).Select(job => new
            {
                Job = job,
                LastSuccess = db.BackupRuns.Where(run => run.TenantId == tenantId && run.JobId == job.Id && run.Status == BackupStatus.Succeeded && run.CompletedAt <= now)
                    .OrderByDescending(run => run.CompletedAt).ThenByDescending(run => run.Id)
                    .Select(run => new { run.CompletedAt, AttemptId = run.Attempts.Where(attempt => attempt.Number == run.CurrentAttemptNumber).Select(attempt => attempt.Id).First() }).FirstOrDefault(),
                LastVerification = (from restore in db.RestoreOperations
                    join artifact in db.BackupArtifacts on restore.ArtifactId equals artifact.Id
                    join attempt in db.BackupRunAttempts on artifact.AttemptId equals attempt.Id
                    join run in db.BackupRuns on attempt.RunId equals run.Id
                    where restore.TenantId == tenantId && artifact.TenantId == tenantId && attempt.TenantId == tenantId && run.TenantId == tenantId && run.JobId == job.Id &&
                        restore.Purpose == RestorePurpose.Verification && restore.CompletedAt != null && restore.CompletedAt <= now
                    orderby restore.CompletedAt descending, restore.Id descending
                    select new { restore.Status, restore.CompletedAt }).FirstOrDefault()
            }).ToListAsync(cancellationToken);
        if (rows.Count == 0) return [];
        var attemptIds = rows.Where(row => row.LastSuccess is not null).Select(row => row.LastSuccess!.AttemptId).ToArray();
        // Read copies only for the newest successful attempt; old copies cannot inflate replication health.
        var copies = await (from artifact in db.BackupArtifacts
            join replica in db.BackupReplicas on artifact.Id equals replica.ArtifactId
            where artifact.TenantId == tenantId && replica.TenantId == tenantId && attemptIds.Contains(artifact.AttemptId)
            select new { artifact.AttemptId, Replica = replica }).AsNoTracking().ToListAsync(cancellationToken);
        var verifiedByAttempt = copies.Where(copy => copy.Replica.IsRestorable(now)).GroupBy(copy => copy.AttemptId)
            .ToDictionary(group => group.Key, group => group.Select(copy => copy.Replica.StorageConnectionId).Distinct().Count());
        var connections = await db.StorageConnections.AsNoTracking().Where(connection => connection.TenantId == tenantId)
            .Select(connection => new { connection.Id, connection.ProviderType, connection.IsDefault }).ToListAsync(cancellationToken);
        var snapshots = await LatestSnapshotsAsync(db, tenantId, cancellationToken);
        return rows.Select(row =>
        {
            var verified = row.LastSuccess is null ? 0 : verifiedByAttempt.GetValueOrDefault(row.LastSuccess.AttemptId);
            var capacity = row.Job.Configuration.Destinations.Select(destination =>
            {
                var connectionId = destination.StorageConnectionId ?? connections.FirstOrDefault(connection => connection.IsDefault && connection.ProviderType == destination.StorageProvider)?.Id;
                return connectionId is { } id ? snapshots.GetValueOrDefault(id) : null;
            }).ToArray();
            return new BackupHealthEvidence(row.Job, row.LastSuccess?.CompletedAt, verified, row.LastVerification?.Status, row.LastVerification?.CompletedAt, capacity);
        }).ToArray();
    }

    public async Task<IReadOnlyList<BackupCapacityRisk>> ReadCapacityRisksAsync(TenantId tenantId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var db = PlatformDbContext.CreateForPlatformOperations(options);
        var snapshots = await LatestSnapshotsAsync(db, tenantId, cancellationToken);
        var connections = await db.StorageConnections.AsNoTracking().Where(connection => connection.TenantId == tenantId)
            .Select(connection => new { connection.Id, connection.Name }).ToListAsync(cancellationToken);
        return connections.Where(connection => snapshots.TryGetValue(connection.Id, out var snapshot) && BackupHealthEvaluator.HasKnownCapacity(snapshot, now))
            .Select(connection =>
            {
                var snapshot = snapshots[connection.Id];
                return new BackupCapacityRisk(connection.Id, connection.Name, StorageCapacityStatus.Evaluate(snapshot.TotalBytes, snapshot.UsedBytes, snapshot.FreeBytes),
                    snapshot.TotalBytes, snapshot.UsedBytes, snapshot.FreeBytes, snapshot.ReportedAt);
            }).Where(risk => risk.Status is StorageCapacityStatus.Warning or StorageCapacityStatus.Critical)
            .OrderBy(risk => risk.Status == StorageCapacityStatus.Critical ? 0 : 1).ThenBy(risk => risk.ConnectionName).ThenBy(risk => risk.ConnectionId).ToArray();
    }

    public async Task<(IReadOnlyList<BackupRecentSizeAnomaly> Items, int Total)> ReadRecentSizeAnomaliesAsync(TenantId tenantId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var db = PlatformDbContext.CreateForPlatformOperations(options);
        var since = now.AddDays(-7);
        var warnings = from run in db.BackupRuns.AsNoTracking()
            join job in db.BackupJobs on run.JobId equals job.Id
            where run.TenantId == tenantId && job.TenantId == tenantId && run.Status == BackupStatus.Succeeded &&
                run.SizeAnomalyStatus == BackupSizeAnomalyStatuses.SizeAnomalyWarning && run.CompletedAt >= since && run.CompletedAt <= now
            select new { Run = run, JobName = job.Name };
        var total = await warnings.CountAsync(cancellationToken);
        var items = await warnings.OrderByDescending(row => row.Run.CompletedAt).ThenByDescending(row => row.Run.Id).Take(20)
            .Select(row => new BackupRecentSizeAnomaly(row.Run.Id, row.Run.JobId, row.JobName, row.Run.CompletedAt!.Value,
                row.Run.Attempts.Where(attempt => attempt.TenantId == tenantId && attempt.Number == row.Run.CurrentAttemptNumber)
                    .SelectMany(attempt => attempt.Artifacts).Where(artifact => artifact.TenantId == tenantId).Sum(artifact => artifact.SizeBytes),
                row.Run.SizeAnomalyBaselineBytes!.Value, row.Run.SizeAnomalyDeviationRatio!.Value))
            .ToListAsync(cancellationToken);
        return (items, total);
    }

    private static async Task<Dictionary<Guid, StorageCapacitySnapshot>> LatestSnapshotsAsync(PlatformDbContext db, TenantId tenantId, CancellationToken cancellationToken) =>
        (await db.StorageCapacitySnapshots.AsNoTracking().Where(snapshot => snapshot.TenantId == tenantId)
            .GroupBy(snapshot => snapshot.ConnectionId).Select(group => group.OrderByDescending(snapshot => snapshot.ReportedAt).ThenByDescending(snapshot => snapshot.Id).First())
            .ToListAsync(cancellationToken)).ToDictionary(snapshot => snapshot.ConnectionId);
}
