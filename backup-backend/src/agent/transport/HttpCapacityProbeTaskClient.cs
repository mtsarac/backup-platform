using System.Net.Http.Json;
using System.Text.Json;
using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Agent.Core.Tasks;
using BackupPlatform.Contracts.Storage;

namespace BackupPlatform.Agent.Transport;

public sealed class HttpCapacityProbeTaskClient(HttpClient http, AgentBackendEndpoint endpoint, TimeProvider timeProvider) : ICapacityProbeTaskClient
{
    private readonly AgentHttpClient _client = new(http, endpoint, timeProvider);

    public Task<AgentBackendResult<bool>> AuthorizeAsync(AgentLocalIdentity identity, Guid taskId,
        CapacityProbeCompletionRequest request, CancellationToken cancellationToken) => SendAsync(identity, taskId, "lease", request, cancellationToken);

    public Task<AgentBackendResult<bool>> CompleteAsync(AgentLocalIdentity identity, Guid taskId,
        CapacityProbeCompletionRequest request, CancellationToken cancellationToken) => SendAsync(identity, taskId, "complete", request, cancellationToken);

    private Task<AgentBackendResult<bool>> SendAsync(AgentLocalIdentity identity, Guid taskId, string operation,
        CapacityProbeCompletionRequest request, CancellationToken cancellationToken)
    {
        if (taskId == Guid.Empty) throw new ArgumentException("A task identifier is required.", nameof(taskId));
        return _client.SendEmptyAsync(identity, $"api/agent/capacity-probes/{taskId:D}/{operation}",
            JsonContent.Create(request, options: JsonSerializerOptions.Web), cancellationToken);
    }
}
