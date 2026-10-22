using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Contracts.Backups;

namespace BackupPlatform.Agent.Core.Tasks;

/// <summary>Fenced task mutations, separate from connectivity so existing backend clients remain unchanged.</summary>
public interface IBackupTaskClient
{
    Task<AgentBackendResult<BackupTaskLeaseResponse>> StartAsync(AgentLocalIdentity identity, Guid attemptId, BackupTaskStartRequest request, CancellationToken cancellationToken);
    Task<AgentBackendResult<BackupTaskLeaseResponse>> RenewAsync(AgentLocalIdentity identity, Guid attemptId, BackupTaskLeaseRequest request, CancellationToken cancellationToken);
    /// <summary>The upload ceilings for this running attempt, and (while a tenant limit exists) renewal of its slice; <see cref="UploadPolicyRequest.Release"/> gives the slice back.</summary>
    Task<AgentBackendResult<UploadPolicyResponse>> GetUploadPolicyAsync(AgentLocalIdentity identity, Guid attemptId, UploadPolicyRequest request, CancellationToken cancellationToken);
    Task<AgentBackendResult<bool>> ReportProgressAsync(AgentLocalIdentity identity, Guid attemptId, BackupTaskProgressRequest request, CancellationToken cancellationToken);
    Task<AgentBackendResult<bool>> CompleteAsync(AgentLocalIdentity identity, Guid attemptId, BackupTaskCompletionRequest request, CancellationToken cancellationToken);
    Task<AgentBackendResult<bool>> CanReleaseRetainedTransferAsync(AgentLocalIdentity identity, Guid attemptId, CancellationToken cancellationToken);
}
