using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Contracts.Backups;

namespace BackupPlatform.Agent.Core.Tasks;

public interface IDeleteReplicaTaskClient
{
    Task<AgentBackendResult<DeleteReplicaLeaseResponse>> RenewAsync(AgentLocalIdentity identity, Guid replicaId,
        DeleteReplicaLeaseRequest request, CancellationToken cancellationToken);

    Task<AgentBackendResult<bool>> CompleteAsync(AgentLocalIdentity identity, Guid replicaId,
        DeleteReplicaCompletionRequest request, CancellationToken cancellationToken);
}
