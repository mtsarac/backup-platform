using System.Net.Http.Json;
using System.Text.Json;
using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Agent.Core.Tasks;
using BackupPlatform.Contracts.Backups;

namespace BackupPlatform.Agent.Transport;

public sealed class HttpDeleteReplicaTaskClient : IDeleteReplicaTaskClient
{
    private readonly AgentHttpClient _client;

    public HttpDeleteReplicaTaskClient(HttpClient http, AgentBackendEndpoint endpoint, TimeProvider timeProvider)
    {
        _client = new AgentHttpClient(http, endpoint, timeProvider);
    }

    public Task<AgentBackendResult<DeleteReplicaLeaseResponse>> RenewAsync(AgentLocalIdentity identity, Guid replicaId,
        DeleteReplicaLeaseRequest request, CancellationToken cancellationToken) =>
        _client.SendAsync<DeleteReplicaLeaseResponse>(identity, HttpMethod.Post, Route(replicaId, "lease"), Content(request), cancellationToken);

    public Task<AgentBackendResult<bool>> CompleteAsync(AgentLocalIdentity identity, Guid replicaId,
        DeleteReplicaCompletionRequest request, CancellationToken cancellationToken) =>
        _client.SendEmptyAsync(identity, Route(replicaId, "complete"), Content(request), cancellationToken);

    private static JsonContent Content<T>(T request) where T : class
    {
        ArgumentNullException.ThrowIfNull(request);
        return JsonContent.Create(request, options: JsonSerializerOptions.Web);
    }

    private static string Route(Guid replicaId, string operation)
    {
        if (replicaId == Guid.Empty)
        {
            throw new ArgumentException("A replica identifier is required.", nameof(replicaId));
        }
        return $"api/agent/replica-deletions/{replicaId:D}/{operation}";
    }
}
