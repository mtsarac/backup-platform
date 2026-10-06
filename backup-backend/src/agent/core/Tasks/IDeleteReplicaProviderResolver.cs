using BackupPlatform.StorageProviders.Abstractions;

namespace BackupPlatform.Agent.Core.Tasks;

/// <summary>Resolves host-configured target references; task input cannot configure a provider or its root.</summary>
public interface IDeleteReplicaProviderResolver
{
    DeleteReplicaProviderResolution Resolve(string provider, string target);
}

public sealed record DeleteReplicaProviderResolution(IStorageProvider? Provider, string? FailureCode);
