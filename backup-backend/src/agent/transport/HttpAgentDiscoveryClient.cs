using System.Net.Http.Json;
using System.Text.Json;
using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Discovery;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Contracts.Discovery;

namespace BackupPlatform.Agent.Transport;

/// <summary><see cref="IAgentDiscoveryClient"/> over HTTPS; authentication and failure mapping are shared with the other agent clients.</summary>
public sealed class HttpAgentDiscoveryClient : IAgentDiscoveryClient
{
    private const string ScansPath = "api/agent/discovery/scans";

    private readonly AgentHttpClient _client;

    public HttpAgentDiscoveryClient(HttpClient http, AgentBackendEndpoint endpoint, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _client = new AgentHttpClient(http, endpoint, timeProvider);
    }

    public Task<AgentBackendResult<AgentDiscoveryPlan?>> ClaimAsync(AgentLocalIdentity identity, CancellationToken cancellationToken) =>
        _client.SendOptionalAsync<AgentDiscoveryPlan>(identity, HttpMethod.Post, ScansPath, null, cancellationToken);

    public Task<AgentBackendResult<bool>> ReportAsync(AgentLocalIdentity identity, Guid scanId, AgentDiscoveryReport report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (scanId == Guid.Empty)
        {
            throw new ArgumentException("A scan identifier is required.", nameof(scanId));
        }
        return _client.SendEmptyAsync(
            identity, HttpMethod.Put, $"{ScansPath}/{scanId:D}", JsonContent.Create(report, options: JsonSerializerOptions.Web), cancellationToken);
    }
}
