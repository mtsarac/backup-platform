using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Storage;

namespace BackupPlatform.Storage;

public interface IStorageCapacitySnapshotStore
{
    Task<StorageCapacitySnapshot?> GetLatestAsync(TenantId tenantId, Guid connectionId, CancellationToken cancellationToken);
    Task SaveAsync(StorageCapacitySnapshot snapshot, CancellationToken cancellationToken);
}

public interface ITenantAgentLiveness
{
    Task<Guid?> FindActiveAgentAsync(TenantId tenantId, CancellationToken cancellationToken);
}

public interface IGoogleDriveCapacityClient
{
    Task<(long? TotalBytes, long? UsedBytes, long? FreeBytes)> QueryCapacityAsync(StorageConnection connection, CancellationToken cancellationToken);
}

public interface IStorageCapacityNotificationSink
{
    Task NotifyCapacityEvaluatedAsync(TenantId tenantId, CancellationToken cancellationToken);
}

public sealed record StorageCapacityResponse
{
    public Guid ConnectionId { get; init; }
    public long? TotalBytes { get; init; }
    public long? UsedBytes { get; init; }
    public long? FreeBytes { get; init; }
    public string Status { get; init; } = StorageCapacityStatus.CapacityUnavailable;
    public DateTimeOffset ReportedAt { get; init; }
    public string? ErrorMessage { get; init; }

    public static StorageCapacityResponse From(StorageCapacitySnapshot snapshot) =>
        new()
        {
            ConnectionId = snapshot.ConnectionId,
            TotalBytes = snapshot.TotalBytes,
            UsedBytes = snapshot.UsedBytes,
            FreeBytes = snapshot.FreeBytes,
            Status = snapshot.Status == StorageCapacityStatus.CapacityUnavailable
                ? StorageCapacityStatus.CapacityUnavailable
                : StorageCapacityStatus.Evaluate(snapshot.TotalBytes, snapshot.UsedBytes, snapshot.FreeBytes),
            ReportedAt = snapshot.ReportedAt,
            ErrorMessage = snapshot.ErrorMessage
        };
}
