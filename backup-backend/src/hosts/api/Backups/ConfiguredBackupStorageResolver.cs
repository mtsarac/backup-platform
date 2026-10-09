using Microsoft.Extensions.DependencyInjection;
using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Storage;
using BackupPlatform.SecretProviders.Abstractions;
using BackupPlatform.Storage;
using BackupPlatform.StorageProviders.Abstractions;
using BackupPlatform.StorageProviders.Central;
using BackupPlatform.StorageProviders.Local;

namespace BackupPlatform.Api.Backups;

/// <summary>
/// Maps operator-configured Local/Central storage and tenant-bound persisted SFTP connections to providers. SFTP definitions and secrets are
/// reloaded in an operation scope, so neither a singleton resolver nor a retained provider caches a rotated credential or connection.
/// <para>
/// A "local" storage lives on the agent's host. The backend can only delete from it when the operator mounted that disk and configured
/// <c>Backups:Storage:Local</c>; otherwise it is simply not configured, deleting such a replica fails with <c>storage_not_configured</c>
/// and is retried with backoff. Agent-mediated deletion of agent-local storage is a later capability.
/// </para>
/// </summary>
public sealed class ConfiguredBackupStorageResolver : IBackupStorageResolver
{
    private const string DefaultTarget = "default";

    private readonly IStorageProvider? _local;
    private readonly Dictionary<Guid, IStorageProvider> _central = [];
    private readonly IServiceScopeFactory? _scopes;
    private readonly SftpConnectionProviderFactory? _sftp;
    private readonly GoogleDriveConnectionProviderFactory? _googleDrive;

    public ConfiguredBackupStorageResolver(BackupStorageOptions options, IServiceScopeFactory? scopes = null, SftpConnectionProviderFactory? sftp = null, GoogleDriveConnectionProviderFactory? googleDrive = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.ThrowIfInvalid();
        _scopes = scopes;
        _sftp = sftp;
        _googleDrive = googleDrive;
        var managed = new List<BackupManagedStorage>();
        if (options.Local?.RootDirectory is { } localRoot && !string.IsNullOrWhiteSpace(localRoot))
        {
            _local = new LocalStorageProvider(new LocalStorageOptions(localRoot));
            managed.Add(new BackupManagedStorage("local", StorageProviderId.Local, null, _local));
        }
        if (options.Central is { RootDirectory: { } centralRoot } central && !string.IsNullOrWhiteSpace(centralRoot))
        {
            foreach (var (tenantKey, scope) in central.TenantScopes.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                var tenant = Guid.Parse(tenantKey);
                var provider = new CentralStorageProvider(new CentralStorageOptions(centralRoot, scope));
                _central[tenant] = provider;
                managed.Add(new BackupManagedStorage($"central:{tenant:N}", StorageProviderId.Central, new TenantId(tenant), provider));
            }
        }
        ManagedStorages = managed;
    }

    public IReadOnlyList<BackupManagedStorage> ManagedStorages { get; }

    public IStorageProvider? Resolve(TenantId tenantId, string storageProvider, string storageTarget)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        if (storageProvider == StorageProviderTypes.Sftp)
        {
            return _scopes is not null && _sftp is not null && Guid.TryParse(storageTarget, out var connectionId) && connectionId != Guid.Empty
                ? new ScopedSftpProvider(tenantId, connectionId, _scopes, _sftp)
                : null;
        }
        if (storageProvider == StorageProviderTypes.GoogleDrive)
        {
            return _scopes is not null && _googleDrive is not null && Guid.TryParse(storageTarget, out var connectionId) && connectionId != Guid.Empty
                ? new ScopedGoogleDriveProvider(tenantId, connectionId, _scopes, _googleDrive)
                : null;
        }
        if (storageTarget != storageProvider && storageTarget != DefaultTarget) return null;
        return storageProvider switch
        {
            "local" => _local,
            "central" => _central.GetValueOrDefault(tenantId.Value),
            _ => null
        };
    }

    private sealed class ScopedSftpProvider(
        TenantId tenantId, Guid connectionId, IServiceScopeFactory scopes, SftpConnectionProviderFactory factory) : IStorageProvider
    {
        private static readonly StorageCapabilities Supported = new([
            StorageCapabilityIds.Read, StorageCapabilityIds.Delete, StorageCapabilityIds.AtomicPublish, StorageCapabilityIds.VerifyAbsence]);

        public StorageProviderId Id { get; } = new(StorageProviderTypes.Sftp);
        public StorageTopology Topology => StorageTopology.BackendRelayed;
        public StorageCapabilities Capabilities => Supported;

        public Task<StorageObjectMetadata> PutAsync(StorageObjectKey key, Stream content, StoragePutOptions options, IProgress<long>? progress, CancellationToken cancellationToken) =>
            ExecuteAsync((provider, token) => provider.PutAsync(key, content, options, progress, token), cancellationToken);

        public Task<bool> ExistsAsync(StorageObjectKey key, CancellationToken cancellationToken) =>
            ExecuteAsync((provider, token) => provider.ExistsAsync(key, token), cancellationToken);

        public Task<StorageObjectMetadata?> GetMetadataAsync(StorageObjectKey key, CancellationToken cancellationToken) =>
            ExecuteAsync((provider, token) => provider.GetMetadataAsync(key, token), cancellationToken);

        public Task<StorageVerificationResult> VerifyAsync(StorageObjectKey key, StorageVerificationExpectation expected, CancellationToken cancellationToken) =>
            ExecuteAsync((provider, token) => provider.VerifyAsync(key, expected, token), cancellationToken);

        public Task<Stream> OpenReadAsync(StorageObjectKey key, CancellationToken cancellationToken) =>
            ExecuteAsync((provider, token) => provider.OpenReadAsync(key, token), cancellationToken);

        public Task<bool> DeleteAsync(StorageObjectKey key, CancellationToken cancellationToken) =>
            ExecuteAsync((provider, token) => provider.DeleteAsync(key, token), cancellationToken);
        public IAsyncEnumerable<StorageEntry> EnumerateAsync(StorageObjectKey? prefix, DateTimeOffset modifiedBefore, CancellationToken cancellationToken) =>
            throw new StorageException(StorageFailureKind.NotSupported, "SFTP enumeration is not supported.");

        private async Task<T> ExecuteAsync<T>(Func<IStorageProvider, CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var scope = scopes.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IStorageConnectionStore>();
            var record = await store.FindAsync(connectionId, tenantId, cancellationToken);
            if (record is null || record.Connection.Id != connectionId || record.Connection.TenantId != tenantId
                || record.Connection.ProviderType != StorageProviderTypes.Sftp)
                throw new StorageException(StorageFailureKind.InvalidConfiguration, "The SFTP storage connection is not available.");
            // Availability fences new admissions, not access to replicas already stored on a disabled connection.
            var secrets = scope.ServiceProvider.GetRequiredService<ISecretProvider>();
            var provider = factory.Create(record.Connection, secrets);
            return await operation(provider, cancellationToken);
        }
    }

    private sealed class ScopedGoogleDriveProvider(
        TenantId tenantId, Guid connectionId, IServiceScopeFactory scopes, GoogleDriveConnectionProviderFactory factory) : IStorageProvider
    {
        private static readonly StorageCapabilities Supported = new([
            StorageCapabilityIds.Read, StorageCapabilityIds.Delete, StorageCapabilityIds.AtomicPublish, StorageCapabilityIds.VerifyAbsence]);

        public StorageProviderId Id { get; } = new(StorageProviderTypes.GoogleDrive);
        public StorageTopology Topology => StorageTopology.BackendRelayed;
        public StorageCapabilities Capabilities => Supported;

        public Task<StorageObjectMetadata> PutAsync(StorageObjectKey key, Stream content, StoragePutOptions options, IProgress<long>? progress, CancellationToken cancellationToken) =>
            ExecuteAsync((provider, token) => provider.PutAsync(key, content, options, progress, token), cancellationToken);

        public Task<bool> ExistsAsync(StorageObjectKey key, CancellationToken cancellationToken) =>
            ExecuteAsync((provider, token) => provider.ExistsAsync(key, token), cancellationToken);

        public Task<StorageObjectMetadata?> GetMetadataAsync(StorageObjectKey key, CancellationToken cancellationToken) =>
            ExecuteAsync((provider, token) => provider.GetMetadataAsync(key, token), cancellationToken);

        public Task<StorageVerificationResult> VerifyAsync(StorageObjectKey key, StorageVerificationExpectation expected, CancellationToken cancellationToken) =>
            ExecuteAsync((provider, token) => provider.VerifyAsync(key, expected, token), cancellationToken);

        public Task<Stream> OpenReadAsync(StorageObjectKey key, CancellationToken cancellationToken) =>
            ExecuteAsync((provider, token) => provider.OpenReadAsync(key, token), cancellationToken);

        public Task<bool> DeleteAsync(StorageObjectKey key, CancellationToken cancellationToken) =>
            ExecuteAsync((provider, token) => provider.DeleteAsync(key, token), cancellationToken);

        public IAsyncEnumerable<StorageEntry> EnumerateAsync(StorageObjectKey? prefix, DateTimeOffset modifiedBefore, CancellationToken cancellationToken) =>
            throw new StorageException(StorageFailureKind.NotSupported, "Google Drive enumeration is not supported.");

        private async Task<T> ExecuteAsync<T>(Func<IStorageProvider, CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var scope = scopes.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IStorageConnectionStore>();
            var record = await store.FindAsync(connectionId, tenantId, cancellationToken);
            if (record is null || record.Connection.Id != connectionId || record.Connection.TenantId != tenantId
                || record.Connection.ProviderType != StorageProviderTypes.GoogleDrive)
                throw new StorageException(StorageFailureKind.InvalidConfiguration, "The Google Drive storage connection is not available.");
            var secrets = scope.ServiceProvider.GetRequiredService<ISecretProvider>();
            var provider = factory.Create(record.Connection, secrets);
            return await operation(provider, cancellationToken);
        }
    }
}
