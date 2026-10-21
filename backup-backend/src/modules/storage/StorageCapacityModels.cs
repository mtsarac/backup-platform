using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Storage;

namespace BackupPlatform.Storage;

public interface IStorageCapacitySnapshotStore
{
    Task<StorageCapacitySnapshot?> GetLatestAsync(TenantId tenantId, Guid connectionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<StorageCapacitySnapshot>> GetHistoryAsync(TenantId tenantId, Guid connectionId, int limit, CancellationToken cancellationToken);
    Task SaveAsync(StorageCapacitySnapshot snapshot, CancellationToken cancellationToken);
}

public interface IStorageCapacityTaskQueue
{
    Task<bool> HasCompatibleAgentAsync(TenantId tenantId, DateTimeOffset now, CancellationToken cancellationToken);
    Task<StorageCapacityResponse> EnqueueAsync(TenantId tenantId, Guid connectionId, DateTimeOffset now, CancellationToken cancellationToken);
    Task<StorageCapacityResponse?> GetPendingAsync(TenantId tenantId, Guid connectionId, DateTimeOffset now, CancellationToken cancellationToken);
    Task<BackupPlatform.Contracts.Storage.CapacityProbeTaskRequest?> ClaimAsync(BackupPlatform.Application.Agents.AgentIdentity identity, DateTimeOffset now, CancellationToken cancellationToken);
    Task<bool> IsLiveAsync(BackupPlatform.Application.Agents.AgentIdentity identity, Guid taskId, Guid token, DateTimeOffset now, CancellationToken cancellationToken);
    Task<BackupPlatform.Application.Backups.BackupMutationResult> CompleteAsync(BackupPlatform.Application.Agents.AgentIdentity identity, Guid taskId, BackupPlatform.Contracts.Storage.CapacityProbeCompletionRequest request, DateTimeOffset now, CancellationToken cancellationToken);
}


public interface IGoogleDriveCapacityClient
{
    Task<(long? TotalBytes, long? UsedBytes, long? FreeBytes)> QueryCapacityAsync(StorageConnection connection, CancellationToken cancellationToken);
}

public interface IPlatformStorageCapacityClient
{
    Task<(long? TotalBytes, long? UsedBytes, long? FreeBytes)> QueryCapacityAsync(StorageConnection connection, CancellationToken cancellationToken);
}

public sealed record StorageCapacityHistoryEntry(DateTimeOffset ReportedAt, long? TotalBytes, long? UsedBytes, long? FreeBytes, string Status);


public sealed record StorageCapacityResponse
{
    public const int MaximumHistory = 30;
    public int HistoryLimit => MaximumHistory;
    public IReadOnlyList<StorageCapacityHistoryEntry> History { get; init; } = [];
    public Guid ConnectionId { get; init; }
    public Guid? TaskId { get; init; }
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
