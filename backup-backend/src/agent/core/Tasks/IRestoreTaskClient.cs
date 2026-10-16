using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Contracts.Restores;

namespace BackupPlatform.Agent.Core.Tasks;

/// <summary>
/// Fenced restore-attempt mutations. Start, lease renewal and progress are the same generic messages a backup attempt uses; only the
/// terminal message differs, because a restore completes with a target state and a result instead of an artifact.
/// </summary>
public interface IRestoreTaskClient
{
    Task<AgentBackendResult<BackupTaskLeaseResponse>> StartAsync(AgentLocalIdentity identity, Guid attemptId, BackupTaskStartRequest request, CancellationToken cancellationToken);
    Task<AgentBackendResult<BackupTaskLeaseResponse>> RenewAsync(AgentLocalIdentity identity, Guid attemptId, BackupTaskLeaseRequest request, CancellationToken cancellationToken);
    Task<AgentBackendResult<bool>> ReportProgressAsync(AgentLocalIdentity identity, Guid attemptId, BackupTaskProgressRequest request, CancellationToken cancellationToken);
    Task<AgentBackendResult<bool>> CompleteAsync(AgentLocalIdentity identity, Guid attemptId, RestoreTaskCompletionRequest request, CancellationToken cancellationToken);

    /// <summary>Reports the outcome of a leased database removal, fenced by the lease token inside <paramref name="request"/>.</summary>
    Task<AgentBackendResult<bool>> CompleteCleanupAsync(AgentLocalIdentity identity, Guid operationId, RestoreCleanupCompletionRequest request, CancellationToken cancellationToken);
}
