using BackupPlatform.Application.Platform;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Application.Backups;
using BackupPlatform.Application.Lists;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Storage;
using BackupPlatform.Infrastructure.Backups;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.IntegrationTests;

/// <summary>Test seam over both RetentionStore stores: tenant calls run bound to the tenant the call names, agent and worker calls on the platform-operations store.</summary>
internal sealed class RetentionStoreFacade(DbContextOptions<PlatformDbContext> options, BackupRetentionOptions? retention = null) : IBackupRetentionStore, IBackupRetentionPlatformStore
{
    private readonly PostgreSqlBackupRetentionPlatformStore _platform = new(options, retention);
    private IBackupRetentionStore For(TenantId tenantId) => new PostgreSqlBackupRetentionStore(options, new MutableTenantContext(tenantId));

    public Task<BackupArtifactDeleteResult> RequestDeleteAsync(TenantId tenantId, Guid artifactId, Guid? requestedByUserId, DateTimeOffset now, CancellationToken cancellationToken) => For(tenantId).RequestDeleteAsync(tenantId, artifactId, requestedByUserId, now, cancellationToken);
    public Task<BackupArtifactCancelResult> CancelDeleteAsync(TenantId tenantId, Guid artifactId, DateTimeOffset now, CancellationToken cancellationToken, Guid? requestedByUserId = null) => For(tenantId).CancelDeleteAsync(tenantId, artifactId, now, cancellationToken, requestedByUserId);
    public Task<BackupArtifactRecord?> GetArtifactAsync(TenantId tenantId, Guid artifactId, CancellationToken cancellationToken) => For(tenantId).GetArtifactAsync(tenantId, artifactId, cancellationToken);
    public Task<IReadOnlyList<BackupArtifactRecord>> ListArtifactsAsync(TenantId tenantId, Guid? jobId, int limit, CancellationToken cancellationToken) => For(tenantId).ListArtifactsAsync(tenantId, jobId, limit, cancellationToken);
    public Task<PagedResult<BackupArtifactRecord>> QueryArtifactsAsync(TenantId tenantId, BackupArtifactQuery query, CancellationToken cancellationToken) => For(tenantId).QueryArtifactsAsync(tenantId, query, cancellationToken);
    public Task<BackupRetentionApplication> ApplyExpiredRetentionAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken) => _platform.ApplyExpiredRetentionAsync(now, limit, cancellationToken);
    public Task<IReadOnlyList<ClaimedReplicaDeletion>> ClaimDueDeletionsAsync(string owner, DateTimeOffset now, TimeSpan lease, int limit, CancellationToken cancellationToken, bool dispatchExternalToAgents = false) => _platform.ClaimDueDeletionsAsync(owner, now, lease, limit, cancellationToken, dispatchExternalToAgents);
    public Task<bool> CompleteDeletionAsync(Guid replicaId, Guid leaseToken, DateTimeOffset now, CancellationToken cancellationToken) => _platform.CompleteDeletionAsync(replicaId, leaseToken, now, cancellationToken);
    public Task<bool> FailDeletionAsync(Guid replicaId, Guid leaseToken, DateTimeOffset now, string failureCode, DateTimeOffset nextAttemptAt, CancellationToken cancellationToken) => _platform.FailDeletionAsync(replicaId, leaseToken, now, failureCode, nextAttemptAt, cancellationToken);
    public Task<IReadOnlySet<string>> FindReferencedKeysAsync(string storageProvider, IReadOnlyCollection<string> keys, CancellationToken cancellationToken) => _platform.FindReferencedKeysAsync(storageProvider, keys, cancellationToken);
    public Task<IReadOnlyDictionary<Guid, BackupAttemptActivity>> GetAttemptActivityAsync(IReadOnlyCollection<Guid> attemptIds, CancellationToken cancellationToken) => _platform.GetAttemptActivityAsync(attemptIds, cancellationToken);
    public Task<ClaimedStorageCleanup?> BeginSweptDeletionAsync(string storageProvider, string storageName, TenantId? storageScope, ManagedStorageKey key, string objectKey, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken) => _platform.BeginSweptDeletionAsync(storageProvider, storageName, storageScope, key, objectKey, now, leaseDuration, cancellationToken);
    public Task<IReadOnlyList<PendingStorageCleanup>> ListPendingSweptDeletionsAsync(string storageProvider, string storageName, TenantId? storageScope, DateTimeOffset now, int limit, CancellationToken cancellationToken) => _platform.ListPendingSweptDeletionsAsync(storageProvider, storageName, storageScope, now, limit, cancellationToken);
    public Task<ClaimedStorageCleanup?> ClaimPendingSweptDeletionAsync(Guid cleanupId, TenantId? storageScope, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken) => _platform.ClaimPendingSweptDeletionAsync(cleanupId, storageScope, now, leaseDuration, cancellationToken);
    public Task<bool> CompleteSweptDeletionAsync(Guid cleanupId, Guid leaseToken, DateTimeOffset now, CancellationToken cancellationToken) => _platform.CompleteSweptDeletionAsync(cleanupId, leaseToken, now, cancellationToken);
    public Task<int> PruneScheduleOccurrencesAsync(DateTimeOffset decidedBefore, int limit, CancellationToken cancellationToken) => _platform.PruneScheduleOccurrencesAsync(decidedBefore, limit, cancellationToken);
}
