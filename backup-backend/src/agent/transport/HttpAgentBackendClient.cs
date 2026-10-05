using System.Net.Http.Json;
using System.Text.Json;
using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Contracts.Agents;

namespace BackupPlatform.Agent.Transport;

/// <summary>
/// <see cref="IAgentBackendClient"/> over HTTPS. Every request carries the credential of the identity passed in and the
/// protocol header; secrets never reach exception messages, failure descriptions or logs. Network errors, timeouts and
/// unreadable answers become failures; only cancellation throws.
/// </summary>
public sealed class HttpAgentBackendClient : IAgentBackendClient
{
    private const string HeartbeatPath = "api/agent/heartbeat";
    private const string PollPath = "api/agent/tasks";

    private readonly AgentHttpClient _client;
    private readonly AgentBackendEndpoint _endpoint;

    public HttpAgentBackendClient(HttpClient http, AgentBackendEndpoint endpoint, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _client = new AgentHttpClient(http, endpoint, timeProvider);
        _endpoint = endpoint;
    }

    public Uri BackendBaseUri => _endpoint.BaseUri;

    public Task<AgentBackendResult<AgentHeartbeatResponse>> SendHeartbeatAsync(AgentLocalIdentity identity, AgentHeartbeatRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _client.SendAsync<AgentHeartbeatResponse>(identity, HttpMethod.Post, HeartbeatPath, JsonContent.Create(request, options: JsonSerializerOptions.Web), cancellationToken);
    }

    public Task<AgentBackendResult<AgentPollResponse>> PollAsync(AgentLocalIdentity identity, string? cursor, CancellationToken cancellationToken)
    {
        var path = string.IsNullOrEmpty(cursor) ? PollPath : $"{PollPath}?cursor={Uri.EscapeDataString(cursor)}";
        return _client.SendAsync<AgentPollResponse>(identity, HttpMethod.Get, path, null, cancellationToken);
    }
}
