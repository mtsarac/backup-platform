using BackupPlatform.Application.Agents;
using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Backups;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.IntegrationTests;

/// <summary>Test seam over both repair stores: tenant calls run bound to the tenant the call names, agent and worker calls on the platform-operations store.</summary>
internal sealed class ReplicaRepairStoreFacade(DbContextOptions<PlatformDbContext> options) : IReplicaRepairStore, IReplicaRepairPlatformStore
{
    private readonly PostgreSqlReplicaRepairPlatformStore _platform = new(options);
    private IReplicaRepairStore For(TenantId tenantId) => new PostgreSqlReplicaRepairStore(options, new MutableTenantContext(tenantId));
    public Task<bool> PinSourceAsync(TenantId tenantId, Guid operationId, Guid leaseToken, Guid sourceReplicaId, DateTimeOffset now, CancellationToken cancellationToken) => For(tenantId).PinSourceAsync(tenantId, operationId, leaseToken, sourceReplicaId, now, cancellationToken);

    public Task<ReplicaRepairOperation?> ClaimOperationAsync(TenantId tenantId, Guid operationId, Guid leaseToken, string leaseOwner, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken) => For(tenantId).ClaimOperationAsync(tenantId, operationId, leaseToken, leaseOwner, now, leaseDuration, cancellationToken);
    public Task<ReplicaRepairOperationView?> GetOperationAsync(TenantId tenantId, Guid artifactId, Guid operationId, CancellationToken cancellationToken) => For(tenantId).GetOperationAsync(tenantId, artifactId, operationId, cancellationToken);
    public Task<ReplicaRepairAdmissionResult> AdmitRepairAsync(TenantId tenantId, Guid artifactId, Guid? storageConnectionId, string? storageProvider, DateTimeOffset now, CancellationToken cancellationToken, Guid? excludeOperationId = null) => For(tenantId).AdmitRepairAsync(tenantId, artifactId, storageConnectionId, storageProvider, now, cancellationToken, excludeOperationId);
    public Task<bool> PinSourceAsync(TenantId tenantId, Guid operationId, Guid leaseToken, Guid sourceReplicaId, string targetKey, DateTimeOffset now, CancellationToken cancellationToken) => For(tenantId).PinSourceAsync(tenantId, operationId, leaseToken, sourceReplicaId, targetKey, now, cancellationToken);
    public Task<bool> CanCleanupTargetAsync(TenantId tenantId, Guid operationId, Guid leaseToken, string targetKey, DateTimeOffset now, CancellationToken cancellationToken) => For(tenantId).CanCleanupTargetAsync(tenantId, operationId, leaseToken, targetKey, now, cancellationToken);
    public Task<ReplicaRepairLeaseRenewal> RenewLeaseAsync(TenantId tenantId, Guid operationId, Guid leaseToken, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken) => For(tenantId).RenewLeaseAsync(tenantId, operationId, leaseToken, now, leaseDuration, cancellationToken);
    public Task<bool> UpdateProgressAsync(TenantId tenantId, Guid operationId, Guid leaseToken, long transferredBytes, long totalBytes, DateTimeOffset now, CancellationToken cancellationToken) => For(tenantId).UpdateProgressAsync(tenantId, operationId, leaseToken, transferredBytes, totalBytes, now, cancellationToken);
    public Task<BackupReplica> CompleteOperationAsync(TenantId tenantId, Guid operationId, Guid leaseToken, BackupReplica newReplica, DateTimeOffset now, CancellationToken cancellationToken) => For(tenantId).CompleteOperationAsync(tenantId, operationId, leaseToken, newReplica, now, cancellationToken);
    public Task<bool> FailOperationAsync(TenantId tenantId, Guid operationId, Guid leaseToken, string failureCode, string? reason, DateTimeOffset now, CancellationToken cancellationToken) => For(tenantId).FailOperationAsync(tenantId, operationId, leaseToken, failureCode, reason, now, cancellationToken);
    public Task<bool> FailTargetConflictAsync(TenantId tenantId, Guid operationId, Guid leaseToken, DateTimeOffset now, CancellationToken cancellationToken) => For(tenantId).FailTargetConflictAsync(tenantId, operationId, leaseToken, now, cancellationToken);
    public Task<bool> ScheduleRetryAsync(TenantId tenantId, Guid operationId, Guid leaseToken, string failureCode, string? reason, DateTimeOffset nextRetryAt, DateTimeOffset now, CancellationToken cancellationToken) => For(tenantId).ScheduleRetryAsync(tenantId, operationId, leaseToken, failureCode, reason, nextRetryAt, now, cancellationToken);
    public Task RecordRepairOrphanAsync(TenantId tenantId, Guid artifactId, Guid storageConnectionId, string storageProvider, string storageTarget, string key, long sizeBytes, string checksum, DateTimeOffset now, CancellationToken cancellationToken, bool quarantine = false) => For(tenantId).RecordRepairOrphanAsync(tenantId, artifactId, storageConnectionId, storageProvider, storageTarget, key, sizeBytes, checksum, now, cancellationToken, quarantine);
    public Task<BackupArtifact?> FindArtifactAsync(TenantId tenantId, Guid artifactId, CancellationToken cancellationToken) => For(tenantId).FindArtifactAsync(tenantId, artifactId, cancellationToken);
    public Task<bool> CancelOperationAsync(TenantId tenantId, Guid operationId, Guid leaseToken, DateTimeOffset now, CancellationToken cancellationToken) => For(tenantId).CancelOperationAsync(tenantId, operationId, leaseToken, now, cancellationToken);
    public Task<ReplicaRepairOperation?> ClaimDueRepairAsync(string leaseOwner, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken, TenantId? tenantId = null, bool backendExecutedOnly = false) => _platform.ClaimDueRepairAsync(leaseOwner, now, leaseDuration, cancellationToken, tenantId, backendExecutedOnly);
    public Task<ReplicaRepairOperation?> ClaimAgentRepairAsync(AgentIdentity identity, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken) => _platform.ClaimAgentRepairAsync(identity, now, leaseDuration, cancellationToken);
    public Task<ReplicaRepairOperation?> FindOperationAsync(TenantId tenantId, Guid operationId, CancellationToken cancellationToken) => _platform.FindOperationAsync(tenantId, operationId, cancellationToken);
    public Task<BackupDestination?> FindRunDestinationAsync(TenantId tenantId, Guid artifactId, Guid storageConnectionId, CancellationToken cancellationToken) => _platform.FindRunDestinationAsync(tenantId, artifactId, storageConnectionId, cancellationToken);
    public Task<int> RecoverStaleRepairsAsync(DateTimeOffset now, TimeSpan initialRetryDelay, CancellationToken cancellationToken) => _platform.RecoverStaleRepairsAsync(now, initialRetryDelay, cancellationToken);
    public Task<IReadOnlyList<ReplicaRepairOperation>> DiscoverEligibleRepairsAsync(DateTimeOffset now, int maxBatch, CancellationToken cancellationToken, TenantId? tenantId = null) => _platform.DiscoverEligibleRepairsAsync(now, maxBatch, cancellationToken, tenantId);
    public Task<ReplicaRepairAdmissionResult> CreateOperationAsync(ReplicaRepairOperation operation, CancellationToken cancellationToken) => For(operation.TenantId).CreateOperationAsync(operation, cancellationToken);
    public Task<bool> RequestCancellationAsync(TenantId tenantId, Guid artifactId, Guid operationId, DateTimeOffset now, CancellationToken cancellationToken) => For(tenantId).RequestCancellationAsync(tenantId, artifactId, operationId, now, cancellationToken);
    public Task<IReadOnlyList<ReplicaRepairOperationView>> ListOperationsAsync(TenantId tenantId, Guid artifactId, CancellationToken cancellationToken) => For(tenantId).ListOperationsAsync(tenantId, artifactId, cancellationToken);
}
