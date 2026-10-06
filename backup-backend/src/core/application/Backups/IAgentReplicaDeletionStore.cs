using BackupPlatform.Application.Agents;
using BackupPlatform.Contracts.Backups;

namespace BackupPlatform.Application.Backups;

/// <summary>Tenant-qualified, row-locked deletion claims and token-fenced mutations of agent-owned replicas.</summary>
public interface IAgentReplicaDeletionStore
{
    Task<DeleteReplicaTaskSnapshot?> ClaimAsync(AgentIdentity identity, DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken);
    Task<DeleteReplicaLeaseResponse?> RenewAsync(AgentIdentity identity, Guid replicaId, DeleteReplicaLeaseRequest request,
        DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken);
    Task<BackupMutationResult> CompleteAsync(AgentIdentity identity, Guid replicaId, DeleteReplicaCompletionRequest request,
        DateTimeOffset now, CancellationToken cancellationToken);
}
