using BackupPlatform.Application.Agents;
using BackupPlatform.Contracts.Backups;

namespace BackupPlatform.Application.Backups;

/// <summary>The backend clock, never an agent clock, owns deletion lease expiry.</summary>
public sealed class AgentReplicaDeletionService(IAgentReplicaDeletionStore store, BackupRetentionOptions options, TimeProvider time)
{
    public Task<DeleteReplicaTaskSnapshot?> ClaimAsync(AgentIdentity identity, CancellationToken cancellationToken) =>
        store.ClaimAsync(identity, time.GetUtcNow(), options.DeleteLeaseDuration, cancellationToken);

    public Task<DeleteReplicaLeaseResponse?> RenewAsync(AgentIdentity identity, Guid replicaId, DeleteReplicaLeaseRequest request,
        CancellationToken cancellationToken) =>
        store.RenewAsync(identity, replicaId, request, time.GetUtcNow(), options.DeleteLeaseDuration, cancellationToken);

    public Task<DeletionStorageTarget?> AuthorizeStorageAsync(AgentIdentity identity, Guid replicaId, Guid leaseToken, CancellationToken cancellationToken) =>
        store.AuthorizeStorageAsync(identity, replicaId, leaseToken, time.GetUtcNow(), cancellationToken);

    public Task<BackupMutationResult> CompleteAsync(AgentIdentity identity, Guid replicaId, DeleteReplicaCompletionRequest request,
        CancellationToken cancellationToken) =>
        store.CompleteAsync(identity, replicaId, request, time.GetUtcNow(), cancellationToken);
}
