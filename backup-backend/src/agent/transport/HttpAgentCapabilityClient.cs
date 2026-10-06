using System.Net.Http.Json;
using System.Text.Json;
using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Contracts.Agents;

namespace BackupPlatform.Agent.Transport;

/// <summary><see cref="IAgentCapabilityClient"/> over HTTPS; authentication and failure mapping are shared with the other agent clients.</summary>
public sealed class HttpAgentCapabilityClient : IAgentCapabilityClient
{
    private const string CapabilitiesPath = "api/agent/capabilities";

    private readonly AgentHttpClient _client;

    public HttpAgentCapabilityClient(HttpClient http, AgentBackendEndpoint endpoint, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _client = new AgentHttpClient(http, endpoint, timeProvider);
    }

    public Task<AgentBackendResult<AgentCapabilitySyncResponse>> SyncAsync(
        AgentLocalIdentity identity, AgentCapabilitySyncRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _client.SendAsync<AgentCapabilitySyncResponse>(
            identity, HttpMethod.Put, CapabilitiesPath, JsonContent.Create(request, options: JsonSerializerOptions.Web), cancellationToken);
    }
}
