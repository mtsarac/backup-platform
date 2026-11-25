using BackupPlatform.Application.Agents;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Application.Backups;

/// <summary>Concurrency ceilings both claim paths enforce.</summary>
public static class ReplicaRepairLimits
{
    public const int MaxGlobalConcurrentRepairs = 4;
    public const int MaxTenantConcurrentRepairs = 2;
}

/// <summary>The repair steps the tenant's manual repair and the agent-driven repair run alike; each flow passes its own store.</summary>
public interface IReplicaRepairOperations
{
    Task<ReplicaRepairOperation?> ClaimOperationAsync(
        TenantId tenantId,
        Guid operationId,
        Guid leaseToken,
        string leaseOwner,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);
    Task<ReplicaRepairOperationView?> GetOperationAsync(
        TenantId tenantId,
        Guid artifactId,
        Guid operationId,
        CancellationToken cancellationToken);
    Task<ReplicaRepairAdmissionResult> AdmitRepairAsync(
        TenantId tenantId,
        Guid artifactId,
        Guid? storageConnectionId,
        string? storageProvider,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        Guid? excludeOperationId = null);
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
    /// <summary>Atomically records an unowned conflicting target in quarantine and fails the lease holder.</summary>
    Task<bool> FailTargetConflictAsync(
        TenantId tenantId,
        Guid operationId,
        Guid leaseToken,
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
        CancellationToken cancellationToken,
        bool quarantine = false);
    /// <summary>The artifact with its replicas, read-only; null when it does not exist in this tenant.</summary>
    Task<BackupArtifact?> FindArtifactAsync(TenantId tenantId, Guid artifactId, CancellationToken cancellationToken);
    Task<bool> CancelOperationAsync(
        TenantId tenantId,
        Guid operationId,
        Guid leaseToken,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}

/// <summary>Manual replica repair of the request's tenant, on the filtered tenant context.</summary>
public interface IReplicaRepairStore : IReplicaRepairOperations
{
    /// <summary>Persists a new operation together with its quota reservation; a denied reservation creates nothing.</summary>
    Task<ReplicaRepairAdmissionResult> CreateOperationAsync(
        ReplicaRepairOperation operation,
        CancellationToken cancellationToken);
    Task<bool> RequestCancellationAsync(
        TenantId tenantId,
        Guid artifactId,
        Guid operationId,
        DateTimeOffset now,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<ReplicaRepairOperationView>> ListOperationsAsync(
        TenantId tenantId,
        Guid artifactId,
        CancellationToken cancellationToken);
}

/// <summary>Agent-driven repair execution and the automatic repair worker, unfiltered by tenant (the agent's identity or the row worked on names the tenant).</summary>
public interface IReplicaRepairPlatformStore : IReplicaRepairOperations
{
    Task<ReplicaRepairOperation?> ClaimDueRepairAsync(
        string leaseOwner,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken,
        TenantId? tenantId = null,
        bool backendExecutedOnly = false);
    /// <summary>
    /// Leases one due repair of the agent's own tenant: an external-to-external repair with an SFTP or Google Drive target, or one whose source is a replica on this
    /// agent's local store (any external target), under the same global and tenant caps
    /// as <see cref="ClaimDueRepairAsync"/>. Null when the agent lacks the lifecycle capability, already holds a live repair, or nothing is due.
    /// </summary>
    Task<ReplicaRepairOperation?> ClaimAgentRepairAsync(
        AgentIdentity identity,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);
    Task<ReplicaRepairOperation?> FindOperationAsync(TenantId tenantId, Guid operationId, CancellationToken cancellationToken);
    /// <summary>The destination of the artifact's run snapshot that uses this connection; null when the snapshot has none.</summary>
    Task<BackupDestination?> FindRunDestinationAsync(TenantId tenantId, Guid artifactId, Guid storageConnectionId, CancellationToken cancellationToken);
    Task<int> RecoverStaleRepairsAsync(
        DateTimeOffset now,
        TimeSpan initialRetryDelay,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<ReplicaRepairOperation>> DiscoverEligibleRepairsAsync(
        DateTimeOffset now,
        int maxBatch,
        CancellationToken cancellationToken,
        TenantId? tenantId = null);
}
