using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Contracts.Downloads;

namespace BackupPlatform.Agent.Core.Tasks;

public interface IDownloadTaskClient
{
    Task<AgentBackendResult<BackupTaskLeaseResponse>> StartAsync(AgentLocalIdentity identity, Guid sessionId, BackupTaskStartRequest request, CancellationToken cancellationToken);
    Task<AgentBackendResult<BackupTaskLeaseResponse>> RenewAsync(AgentLocalIdentity identity, Guid sessionId, BackupTaskLeaseRequest request, CancellationToken cancellationToken);
    Task<AgentBackendResult<bool>> UploadAsync(AgentLocalIdentity identity, DownloadTaskSnapshot task, Stream content, CancellationToken cancellationToken);
    Task<AgentBackendResult<bool>> CompleteAsync(AgentLocalIdentity identity, Guid sessionId, DownloadTaskCompletionRequest request, CancellationToken cancellationToken);
}
