using BackupPlatform.Agent.Core.Tasks;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.StorageProviders.Abstractions;
using BackupPlatform.StorageProviders.Local;

namespace BackupPlatform.Agent.Host;

/// <summary>The backup pipeline persists the provider identifier as its target reference, not a root path.</summary>
public sealed class DeleteReplicaProviderResolver(IEnumerable<IStorageProvider> providers) : IDeleteReplicaProviderResolver
{
    private readonly LocalStorageProvider[] _local = providers.OfType<LocalStorageProvider>().ToArray();

    public DeleteReplicaProviderResolution Resolve(string provider, string target)
    {
        if (provider != StorageProviderId.Local.Value || target != StorageProviderId.Local.Value)
        {
            return new(null, DeleteReplicaFailureCodes.StorageTargetUnknown);
        }
        return _local.Length == 1
            ? new(_local[0], null)
            : new(null, DeleteReplicaFailureCodes.InvalidConfiguration);
    }
}
