using System.Net.Http.Json;
using System.Text.Json;
using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Agent.Core.Tasks;
using BackupPlatform.Contracts.Backups;

namespace BackupPlatform.Agent.Transport;

public sealed class HttpReplicaRepairTaskClient : IReplicaRepairTaskClient
{
    private readonly AgentHttpClient _client;

    public HttpReplicaRepairTaskClient(HttpClient http, AgentBackendEndpoint endpoint, TimeProvider timeProvider)
    {
        _client = new AgentHttpClient(http, endpoint, timeProvider);
    }

    public Task<AgentBackendResult<ReplicaRepairLeaseResponse>> RenewAsync(AgentLocalIdentity identity, Guid operationId,
        ReplicaRepairLeaseRequest request, CancellationToken cancellationToken) =>
        _client.SendAsync<ReplicaRepairLeaseResponse>(identity, HttpMethod.Post, Route(operationId, "lease"), Content(request), cancellationToken);

    public Task<AgentBackendResult<bool>> PinAsync(AgentLocalIdentity identity, Guid operationId,
        ReplicaRepairPinRequest request, CancellationToken cancellationToken) =>
        _client.SendEmptyAsync(identity, Route(operationId, "pin"), Content(request), cancellationToken);

    public Task<AgentBackendResult<bool>> ProgressAsync(AgentLocalIdentity identity, Guid operationId,
        ReplicaRepairProgressRequest request, CancellationToken cancellationToken) =>
        _client.SendEmptyAsync(identity, Route(operationId, "progress"), Content(request), cancellationToken);

    public Task<AgentBackendResult<ReplicaRepairCleanupResponse>> CleanupCheckAsync(AgentLocalIdentity identity, Guid operationId,
        ReplicaRepairCleanupRequest request, CancellationToken cancellationToken) =>
        _client.SendAsync<ReplicaRepairCleanupResponse>(identity, HttpMethod.Post, Route(operationId, "cleanup-check"), Content(request), cancellationToken);

    public Task<AgentBackendResult<bool>> CompleteAsync(AgentLocalIdentity identity, Guid operationId,
        ReplicaRepairCompletionRequest request, CancellationToken cancellationToken) =>
        _client.SendEmptyAsync(identity, Route(operationId, "complete"), Content(request), cancellationToken);

    private static JsonContent Content<T>(T request) where T : class
    {
        ArgumentNullException.ThrowIfNull(request);
        return JsonContent.Create(request, options: JsonSerializerOptions.Web);
    }

    private static string Route(Guid operationId, string operation)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("An operation identifier is required.", nameof(operationId));
        }
        return $"api/agent/replica-repairs/{operationId:D}/{operation}";
    }
}
