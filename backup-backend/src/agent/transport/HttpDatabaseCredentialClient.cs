using System.Net.Http.Json;
using System.Text.Json;
using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Agent.Core.Tasks;
using BackupPlatform.Contracts.Agents;

namespace BackupPlatform.Agent.Transport;

/// <summary><see cref="IDatabaseCredentialClient"/> over HTTPS; the reference travels in the body, never in a URL or log.</summary>
public sealed class HttpDatabaseCredentialClient : IDatabaseCredentialClient
{
    private readonly AgentHttpClient _client;

    public HttpDatabaseCredentialClient(HttpClient http, AgentBackendEndpoint endpoint, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _client = new AgentHttpClient(http, endpoint, timeProvider);
    }

    public Task<AgentBackendResult<DatabaseCredentialResponse>> ResolveAsync(AgentLocalIdentity identity, string reference, CancellationToken cancellationToken) =>
        _client.SendAsync<DatabaseCredentialResponse>(identity, HttpMethod.Post, "api/agent/database-credentials",
            JsonContent.Create(new DatabaseCredentialRequest { Reference = reference }, options: JsonSerializerOptions.Web), cancellationToken);
}
