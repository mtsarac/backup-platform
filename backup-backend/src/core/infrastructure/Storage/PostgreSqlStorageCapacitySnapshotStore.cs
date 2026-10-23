using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Storage;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Notifications;
using BackupPlatform.Infrastructure.Notifications;
using BackupPlatform.Notifications;
using BackupPlatform.Infrastructure.Persistence;
using BackupPlatform.Storage;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Infrastructure.Storage;

public sealed class PostgreSqlStorageCapacitySnapshotStore(DbContextOptions<PlatformDbContext> options, NotificationEngine notifications, TimeProvider time) : IStorageCapacitySnapshotStore
{
    public async Task<StorageCapacitySnapshot?> GetLatestAsync(TenantId tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        await using var db = PlatformDbContext.CreateForPlatformOperations(options);
        return await db.StorageCapacitySnapshots
            .Where(snapshot => snapshot.TenantId == tenantId && snapshot.ConnectionId == connectionId)
            .OrderByDescending(snapshot => snapshot.ReportedAt)
            .ThenByDescending(snapshot => snapshot.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StorageCapacitySnapshot>> GetHistoryAsync(TenantId tenantId, Guid connectionId, int limit, CancellationToken cancellationToken)
    {
        await using var db = PlatformDbContext.CreateForPlatformOperations(options);
        return await db.StorageCapacitySnapshots.AsNoTracking()
            .Where(snapshot => snapshot.TenantId == tenantId && snapshot.ConnectionId == connectionId)
            .OrderByDescending(snapshot => snapshot.ReportedAt).ThenByDescending(snapshot => snapshot.Id)
            .Take(Math.Clamp(limit, 1, StorageCapacityResponse.MaximumHistory)).ToListAsync(cancellationToken);
    }

    public async Task SaveAsync(StorageCapacitySnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        await using var db = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await SaveInTransactionAsync(db, snapshot, notifications, time, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    internal static async Task SaveInTransactionAsync(PlatformDbContext db, StorageCapacitySnapshot snapshot,
        NotificationEngine notifications, TimeProvider time, CancellationToken cancellationToken)
    {
        var session = await PostgreSqlNotificationStore.OpenLifecycleSessionAsync(db, snapshot.TenantId, time, cancellationToken);
        var latest = await db.StorageCapacitySnapshots.AsNoTracking()
            .Where(s => s.TenantId == snapshot.TenantId && s.ConnectionId == snapshot.ConnectionId)
            .OrderByDescending(s => s.ReportedAt).ThenByDescending(s => s.Id).FirstOrDefaultAsync(cancellationToken);
        var measured = snapshot.Status != StorageCapacityStatus.CapacityUnavailable;
        var previous = latest;
        if (measured && latest?.Status == StorageCapacityStatus.CapacityUnavailable)
            previous = await db.StorageCapacitySnapshots.AsNoTracking()
                .Where(s => s.TenantId == snapshot.TenantId && s.ConnectionId == snapshot.ConnectionId && s.Status != StorageCapacityStatus.CapacityUnavailable)
                .OrderByDescending(s => s.ReportedAt).ThenByDescending(s => s.Id).FirstOrDefaultAsync(cancellationToken);
        db.StorageCapacitySnapshots.Add(snapshot);
        await db.SaveChangesAsync(cancellationToken);
        await PruneAsync(db, snapshot, cancellationToken);
        if (!measured) return;
        var status = StorageCapacityStatus.Evaluate(snapshot.TotalBytes, snapshot.UsedBytes, snapshot.FreeBytes);
        var previousStatus = previous is null ? null : StorageCapacityStatus.Evaluate(previous.TotalBytes, previous.UsedBytes, previous.FreeBytes);
        if (status != previousStatus && status is StorageCapacityStatus.Warning or StorageCapacityStatus.Critical)
        {
            db.AuditEvents.Add(AuditEvent.Record(time.GetUtcNow(), UserId.System, AuditActorKind.System,
                status == StorageCapacityStatus.Critical ? "storage_capacity_critical" : "storage_capacity_warning",
                snapshot.TenantId, "storage_connection", snapshot.ConnectionId.ToString("D"),
                new Dictionary<string, object?>
                {
                    ["status"] = status, ["totalBytes"] = snapshot.TotalBytes,
                    ["usedBytes"] = snapshot.UsedBytes, ["freeBytes"] = snapshot.FreeBytes
                }));
        }
        var observations = await PostgreSqlNotificationObservationSource.CollectStorageCapacityAsync(db, snapshot.TenantId, cancellationToken);
        await notifications.ApplyAsync(session, [new ObservationSet(NotificationEventTypes.StorageCapacityCritical, true, observations)], time.GetUtcNow(), cancellationToken);
    }

    /// <summary>Drops this connection's snapshots past the retention window, always keeping the one just saved and the newest measured one.</summary>
    private static async Task PruneAsync(PlatformDbContext db, StorageCapacitySnapshot saved, CancellationToken cancellationToken)
    {
        var cutoff = saved.ReportedAt - StorageCapacityStatus.SnapshotRetention;
        var newestMeasured = saved.Status != StorageCapacityStatus.CapacityUnavailable ? saved.Id
            : await db.StorageCapacitySnapshots.AsNoTracking()
                .Where(s => s.TenantId == saved.TenantId && s.ConnectionId == saved.ConnectionId && s.Status != StorageCapacityStatus.CapacityUnavailable)
                .OrderByDescending(s => s.ReportedAt).ThenByDescending(s => s.Id).Select(s => s.Id).FirstOrDefaultAsync(cancellationToken);
        await db.StorageCapacitySnapshots
            .Where(s => s.TenantId == saved.TenantId && s.ConnectionId == saved.ConnectionId && s.ReportedAt < cutoff && s.Id != saved.Id && s.Id != newestMeasured)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
