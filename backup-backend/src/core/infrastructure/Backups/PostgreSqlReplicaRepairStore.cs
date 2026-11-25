using BackupPlatform.Application.Backups;
using BackupPlatform.Application.Tenancy;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Infrastructure.Backups;

/// <summary>Manual replica repair of the request's tenant. Every context it opens is the filtered one of that tenant.</summary>
public sealed class PostgreSqlReplicaRepairStore(DbContextOptions<PlatformDbContext> options, ITenantContext tenant) : IReplicaRepairStore
{
    private readonly ReplicaRepairStoreCore core = new(tenantId => PlatformDbContext.ForTenant(options, tenant, tenantId));

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
    public Task<ReplicaRepairAdmissionResult> CreateOperationAsync(ReplicaRepairOperation operation, CancellationToken cancellationToken) => core.CreateOperationAsync(operation, cancellationToken);
    public Task<bool> RequestCancellationAsync(TenantId tenantId, Guid artifactId, Guid operationId, DateTimeOffset now, CancellationToken cancellationToken) => core.RequestCancellationAsync(tenantId, artifactId, operationId, now, cancellationToken);
    public Task<IReadOnlyList<ReplicaRepairOperationView>> ListOperationsAsync(TenantId tenantId, Guid artifactId, CancellationToken cancellationToken) => core.ListOperationsAsync(tenantId, artifactId, cancellationToken);
}
