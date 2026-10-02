using BackupPlatform.Api.Security;
using BackupPlatform.Contracts.Agents;
using Microsoft.AspNetCore.Http.Connections;

namespace BackupPlatform.Api.Push;

public static class AgentHubEndpoints
{
    /// <summary>
    /// Maps the agent push channel. WebSockets only: long polling would re-authenticate against the database on every
    /// request and duplicates the dedicated polling fallback. Browser sessions are not accepted (agent policy only).
    /// </summary>
    public static IEndpointRouteBuilder MapAgentHub(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHub<AgentHub>(AgentPushChannelContract.HubPath, options => options.Transports = HttpTransportType.WebSockets)
            .RequireAuthorization(AuthorizationPolicies.Agent)
            .RequireRateLimiting(AgentRateLimitOptions.Policies.Connect)
            .WithMetadata(new RequiresAgentProtocol());
        return endpoints;
    }
}
