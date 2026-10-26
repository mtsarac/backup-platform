using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Contracts.Backups;

namespace BackupPlatform.Agent.Core.Tasks;

/// <summary>The lease-fenced backend calls of one agent-executed repair. A refused pin, progress or completion surfaces as a non-retryable failure.</summary>
public interface IReplicaRepairTaskClient
{
    Task<AgentBackendResult<ReplicaRepairLeaseResponse>> RenewAsync(AgentLocalIdentity identity, Guid operationId,
        ReplicaRepairLeaseRequest request, CancellationToken cancellationToken);

    Task<AgentBackendResult<bool>> PinAsync(AgentLocalIdentity identity, Guid operationId,
        ReplicaRepairPinRequest request, CancellationToken cancellationToken);

    Task<AgentBackendResult<bool>> ProgressAsync(AgentLocalIdentity identity, Guid operationId,
        ReplicaRepairProgressRequest request, CancellationToken cancellationToken);

    Task<AgentBackendResult<ReplicaRepairCleanupResponse>> CleanupCheckAsync(AgentLocalIdentity identity, Guid operationId,
        ReplicaRepairCleanupRequest request, CancellationToken cancellationToken);

    Task<AgentBackendResult<bool>> CompleteAsync(AgentLocalIdentity identity, Guid operationId,
        ReplicaRepairCompletionRequest request, CancellationToken cancellationToken);
}
