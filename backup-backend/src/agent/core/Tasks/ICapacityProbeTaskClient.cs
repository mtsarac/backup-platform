using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Contracts.Storage;

namespace BackupPlatform.Agent.Core.Tasks;

public interface ICapacityProbeTaskClient
{
    Task<AgentBackendResult<bool>> AuthorizeAsync(AgentLocalIdentity identity, Guid taskId, CapacityProbeCompletionRequest request, CancellationToken cancellationToken);
    Task<AgentBackendResult<bool>> CompleteAsync(AgentLocalIdentity identity, Guid taskId, CapacityProbeCompletionRequest request, CancellationToken cancellationToken);
}
