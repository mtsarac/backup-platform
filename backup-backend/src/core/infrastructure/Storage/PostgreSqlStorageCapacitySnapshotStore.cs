using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Storage;
using BackupPlatform.Infrastructure.Persistence;
using BackupPlatform.Storage;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Infrastructure.Storage;

public sealed class PostgreSqlStorageCapacitySnapshotStore(DbContextOptions<PlatformDbContext> options) : IStorageCapacitySnapshotStore
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
        db.StorageCapacitySnapshots.Add(snapshot);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
