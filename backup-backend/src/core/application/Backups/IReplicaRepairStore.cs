using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Application.Backups;

public interface IReplicaRepairStore
{
    const int MaxGlobalConcurrentRepairs = 4;
    const int MaxTenantConcurrentRepairs = 2;

    Task<ReplicaRepairAdmissionResult> AdmitRepairAsync(
        TenantId tenantId,
        Guid artifactId,
        Guid? storageConnectionId,
        string? storageProvider,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        Guid? excludeOperationId = null);
    /// <summary>Persists a new operation together with its quota reservation; a denied reservation creates nothing.</summary>
    Task<ReplicaRepairAdmissionResult> CreateOperationAsync(
        ReplicaRepairOperation operation,
        CancellationToken cancellationToken);

    Task<ReplicaRepairOperation?> ClaimOperationAsync(
        TenantId tenantId,
        Guid operationId,
        Guid leaseToken,
        string leaseOwner,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    Task<bool> PinSourceAsync(TenantId tenantId, Guid operationId, Guid leaseToken, Guid sourceReplicaId, string targetKey, DateTimeOffset now, CancellationToken cancellationToken);

    Task<bool> PinSourceAsync(TenantId tenantId, Guid operationId, Guid leaseToken, Guid sourceReplicaId, DateTimeOffset now, CancellationToken cancellationToken) =>
        PinSourceAsync(tenantId, operationId, leaseToken, sourceReplicaId, "default.bak", now, cancellationToken);
    Task<bool> CanCleanupTargetAsync(
        TenantId tenantId,
        Guid operationId,
        Guid leaseToken,
        string targetKey,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<ReplicaRepairLeaseRenewal> RenewLeaseAsync(
        TenantId tenantId,
        Guid operationId,
        Guid leaseToken,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    Task<bool> UpdateProgressAsync(
        TenantId tenantId,
        Guid operationId,
        Guid leaseToken,
        long transferredBytes,
        long totalBytes,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<BackupReplica> CompleteOperationAsync(
        TenantId tenantId,
        Guid operationId,
        Guid leaseToken,
        BackupReplica newReplica,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<bool> FailOperationAsync(
        TenantId tenantId,
        Guid operationId,
        Guid leaseToken,
        string failureCode,
        string? reason,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<bool> ScheduleRetryAsync(
        TenantId tenantId,
        Guid operationId,
        Guid leaseToken,
        string failureCode,
        string? reason,
        DateTimeOffset nextRetryAt,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task RecordRepairOrphanAsync(
        TenantId tenantId,
        Guid artifactId,
        Guid storageConnectionId,
        string storageProvider,
        string storageTarget,
        string key,
        long sizeBytes,
        string checksum,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<ReplicaRepairOperation?> ClaimDueRepairAsync(
        string leaseOwner,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken,
        TenantId? tenantId = null);

    Task<int> RecoverStaleRepairsAsync(
        DateTimeOffset now,
        TimeSpan initialRetryDelay,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ReplicaRepairOperation>> DiscoverEligibleRepairsAsync(
        DateTimeOffset now,
        int maxBatch,
        CancellationToken cancellationToken,
        TenantId? tenantId = null);

    Task<bool> RequestCancellationAsync(
        TenantId tenantId,
        Guid operationId,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<bool> CancelOperationAsync(
        TenantId tenantId,
        Guid operationId,
        Guid leaseToken,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<ReplicaRepairOperationView?> GetOperationAsync(
        TenantId tenantId,
        Guid artifactId,
        Guid operationId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ReplicaRepairOperationView>> ListOperationsAsync(
        TenantId tenantId,
        Guid artifactId,
        CancellationToken cancellationToken);
}
