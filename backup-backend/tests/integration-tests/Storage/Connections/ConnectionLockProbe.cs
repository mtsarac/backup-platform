using BackupPlatform.Application.Platform;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Storage;
using BackupPlatform.Storage;

namespace BackupPlatform.IntegrationTests.Storage.Connections;

/// <summary>Only coordinates requests; every lookup, lock, mutation and audit still uses the real PostgreSQL store.</summary>
internal sealed class ConnectionLockProbe(IStorageConnectionStore store) : IStorageConnectionStore, IDisposable
{
    private readonly ManualResetEventSlim _release = new(false);
    private Guid _target;
    public TaskCompletionSource PlatformLocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource TenantMutating { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Arm(Guid target) => _target = target;
    public void Release() => _release.Set();

    public Task<IReadOnlyList<StorageConnectionRecord>?> ListAsync(TenantId tenantId, CancellationToken cancellationToken) =>
        store.ListAsync(tenantId, cancellationToken);

    public Task<PagedResult<StorageConnectionRecord>> SearchAsync(StorageConnectionFilter filter, int page, int pageSize, CancellationToken cancellationToken) =>
        store.SearchAsync(filter, page, pageSize, cancellationToken);

    public Task<StorageConnectionRecord?> FindAsync(Guid id, TenantId? tenantScope, CancellationToken cancellationToken) =>
        store.FindAsync(id, tenantScope, cancellationToken);

    public Task<StorageConnectionResult> CreateAsync(
        StorageConnection connection, int? maxPerTenant, StorageActor actor, IReadOnlyDictionary<string, object?> auditDetails, DateTimeOffset now, CancellationToken cancellationToken) =>
        store.CreateAsync(connection, maxPerTenant, actor, auditDetails, now, cancellationToken);

    public Task<StorageConnectionResult> MutateAsync(
        Guid id, TenantId? tenantScope, StorageMutation mutation, StorageActor actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (id != _target) return store.MutateAsync(id, tenantScope, mutation, actor, now, cancellationToken);
        if (tenantScope is not null)
        {
            TenantMutating.TrySetResult();
            return store.MutateAsync(id, tenantScope, mutation, actor, now, cancellationToken);
        }
        return store.MutateAsync(id, tenantScope, (connection, changedAt) =>
        {
            // This delegate is invoked only after the real store has acquired the tenant transaction lock.
            PlatformLocked.TrySetResult();
            if (!_release.Wait(TimeSpan.FromSeconds(20), cancellationToken)) throw new TimeoutException("The platform mutation was not released.");
            return mutation(connection, changedAt);
        }, actor, now, cancellationToken);
    }

    public Task<StorageConnectionResult> DeleteAsync(Guid id, TenantId? tenantScope, StorageActor actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (id == _target && tenantScope is not null) TenantMutating.TrySetResult();
        return store.DeleteAsync(id, tenantScope, actor, now, cancellationToken);
    }

    public void Dispose()
    {
        _release.Set();
        _release.Dispose();
    }
}
