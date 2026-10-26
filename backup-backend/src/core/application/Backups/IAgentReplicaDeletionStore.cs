using BackupPlatform.Application.Agents;
using BackupPlatform.Contracts.Backups;

namespace BackupPlatform.Application.Backups;

/// <summary>Tenant-qualified, row-locked deletion claims and token-fenced mutations of agent-owned replicas.</summary>
public interface IAgentReplicaDeletionStore
{
    Task<DeleteReplicaTaskSnapshot?> ClaimAsync(AgentIdentity identity, DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken);
    Task<DeleteReplicaLeaseResponse?> RenewAsync(AgentIdentity identity, Guid replicaId, DeleteReplicaLeaseRequest request,
        DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken);
    /// <summary>The external connection the live lease holder of a claimed removal may open, or null when the lease, owner or replica no longer permit it.</summary>
    Task<DeletionStorageTarget?> AuthorizeStorageAsync(AgentIdentity identity, Guid replicaId, Guid leaseToken, DateTimeOffset now, CancellationToken cancellationToken);
    Task<BackupMutationResult> CompleteAsync(AgentIdentity identity, Guid replicaId, DeleteReplicaCompletionRequest request,
        DateTimeOffset now, CancellationToken cancellationToken);
}

public sealed record DeletionStorageTarget(string StorageProvider, Guid StorageConnectionId);
