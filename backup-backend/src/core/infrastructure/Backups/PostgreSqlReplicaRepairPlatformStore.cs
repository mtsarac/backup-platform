using BackupPlatform.Application.Agents;
using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Infrastructure.Backups;

/// <summary>Agent-driven repair execution and the automatic repair worker, on the unfiltered platform-operations context.</summary>
public sealed class PostgreSqlReplicaRepairPlatformStore(DbContextOptions<PlatformDbContext> options) : IReplicaRepairPlatformStore
{
    private readonly ReplicaRepairStoreCore core = new(_ => PlatformOperationsDbContext.Create(options));

    public Task<ReplicaRepairOperation?> ClaimOperationAsync(TenantId tenantId, Guid operationId, Guid leaseToken, string leaseOwner, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken) => core.ClaimOperationAsync(tenantId, operationId, leaseToken, leaseOwner, now, leaseDuration, cancellationToken);
    public Task<ReplicaRepairOperationView?> GetOperationAsync(TenantId tenantId, Guid artifactId, Guid operationId, CancellationToken cancellationToken) => core.GetOperationAsync(tenantId, artifactId, operationId, cancellationToken);
    public Task<ReplicaRepairAdmissionResult> AdmitRepairAsync(TenantId tenantId, Guid artifactId, Guid? storageConnectionId, string? storageProvider, DateTimeOffset now, CancellationToken cancellationToken, Guid? excludeOperationId = null) => core.AdmitRepairAsync(tenantId, artifactId, storageConnectionId, storageProvider, now, cancellationToken, excludeOperationId);
    public Task<bool> PinSourceAsync(TenantId tenantId, Guid operationId, Guid leaseToken, Guid sourceReplicaId, string targetKey, DateTimeOffset now, CancellationToken cancellationToken) => core.PinSourceAsync(tenantId, operationId, leaseToken, sourceReplicaId, targetKey, now, cancellationToken);
    public Task<bool> CanCleanupTargetAsync(TenantId tenantId, Guid operationId, Guid leaseToken, string targetKey, DateTimeOffset now, CancellationToken cancellationToken) => core.CanCleanupTargetAsync(tenantId, operationId, leaseToken, targetKey, now, cancellationToken);
    public Task<ReplicaRepairLeaseRenewal> RenewLeaseAsync(TenantId tenantId, Guid operationId, Guid leaseToken, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken) => core.RenewLeaseAsync(tenantId, operationId, leaseToken, now, leaseDuration, cancellationToken);
    public Task<bool> UpdateProgressAsync(TenantId tenantId, Guid operationId, Guid leaseToken, long transferredBytes, long totalBytes, DateTimeOffset now, CancellationToken cancellationToken) => core.UpdateProgressAsync(tenantId, operationId, leaseToken, transferredBytes, totalBytes, now, cancellationToken);
    public Task<BackupReplica> CompleteOperationAsync(TenantId tenantId, Guid operationId, Guid leaseToken, BackupReplica newReplica, DateTimeOffset now, CancellationToken cancellationToken) => core.CompleteOperationAsync(tenantId, operationId, leaseToken, newReplica, now, cancellationToken);
    public Task<bool> FailOperationAsync(TenantId tenantId, Guid operationId, Guid leaseToken, string failureCode, string? reason, DateTimeOffset now, CancellationToken cancellationToken) => core.FailOperationAsync(tenantId, operationId, leaseToken, failureCode, reason, now, cancellationToken);
    public Task<bool> FailTargetConflictAsync(TenantId tenantId, Guid operationId, Guid leaseToken, DateTimeOffset now, CancellationToken cancellationToken) => core.FailTargetConflictAsync(tenantId, operationId, leaseToken, now, cancellationToken);
    public Task<bool> ScheduleRetryAsync(TenantId tenantId, Guid operationId, Guid leaseToken, string failureCode, string? reason, DateTimeOffset nextRetryAt, DateTimeOffset now, CancellationToken cancellationToken) => core.ScheduleRetryAsync(tenantId, operationId, leaseToken, failureCode, reason, nextRetryAt, now, cancellationToken);
    public Task RecordRepairOrphanAsync(TenantId tenantId, Guid artifactId, Guid storageConnectionId, string storageProvider, string storageTarget, string key, long sizeBytes, string checksum, DateTimeOffset now, CancellationToken cancellationToken, bool quarantine = false) => core.RecordRepairOrphanAsync(tenantId, artifactId, storageConnectionId, storageProvider, storageTarget, key, sizeBytes, checksum, now, cancellationToken, quarantine);
    public Task<BackupArtifact?> FindArtifactAsync(TenantId tenantId, Guid artifactId, CancellationToken cancellationToken) => core.FindArtifactAsync(tenantId, artifactId, cancellationToken);
    public Task<bool> CancelOperationAsync(TenantId tenantId, Guid operationId, Guid leaseToken, DateTimeOffset now, CancellationToken cancellationToken) => core.CancelOperationAsync(tenantId, operationId, leaseToken, now, cancellationToken);
    public Task<ReplicaRepairOperation?> ClaimDueRepairAsync(string leaseOwner, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken, TenantId? tenantId = null, bool backendExecutedOnly = false) => core.ClaimDueRepairAsync(leaseOwner, now, leaseDuration, cancellationToken, tenantId, backendExecutedOnly);
    public Task<ReplicaRepairOperation?> ClaimAgentRepairAsync(AgentIdentity identity, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken) => core.ClaimAgentRepairAsync(identity, now, leaseDuration, cancellationToken);
    public Task<ReplicaRepairOperation?> FindOperationAsync(TenantId tenantId, Guid operationId, CancellationToken cancellationToken) => core.FindOperationAsync(tenantId, operationId, cancellationToken);
    public Task<BackupDestination?> FindRunDestinationAsync(TenantId tenantId, Guid artifactId, Guid storageConnectionId, CancellationToken cancellationToken) => core.FindRunDestinationAsync(tenantId, artifactId, storageConnectionId, cancellationToken);
    public Task<int> RecoverStaleRepairsAsync(DateTimeOffset now, TimeSpan initialRetryDelay, CancellationToken cancellationToken) => core.RecoverStaleRepairsAsync(now, initialRetryDelay, cancellationToken);
    public Task<IReadOnlyList<ReplicaRepairOperation>> DiscoverEligibleRepairsAsync(DateTimeOffset now, int maxBatch, CancellationToken cancellationToken, TenantId? tenantId = null) => core.DiscoverEligibleRepairsAsync(now, maxBatch, cancellationToken, tenantId);
}
