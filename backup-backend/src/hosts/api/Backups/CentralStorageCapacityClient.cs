using BackupPlatform.Domain.Storage;
using BackupPlatform.Storage;
using BackupPlatform.StorageProviders.Abstractions.FileSystem;
using Microsoft.Extensions.Options;

namespace BackupPlatform.Api.Backups;

public sealed class CentralStorageCapacityClient(IOptions<BackupStorageOptions> options) : IPlatformStorageCapacityClient
{
    public Task<(long? TotalBytes, long? UsedBytes, long? FreeBytes)> QueryCapacityAsync(StorageConnection connection, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var central = options.Value.Central;
        var scope = central?.TenantScopes.FirstOrDefault(entry => Guid.TryParse(entry.Key, out var tenant) && tenant == connection.TenantId.Value).Value;
        if (central?.RootDirectory is not { } root || scope is null)
            throw new InvalidOperationException("No authorized Central filesystem is configured for this tenant.");
        var configured = new BackupPlatform.StorageProviders.Central.CentralStorageOptions(root, scope);
        var (total, used, free) = FileSystemCapacity.Query(Path.Combine(configured.RootDirectory, "tenants", configured.TenantScope));
        return Task.FromResult<(long?, long?, long?)>((total, used, free));
    }
}
