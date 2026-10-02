using BackupPlatform.Agents;
using BackupPlatform.Agents.Connectivity;
using BackupPlatform.Api.Security;
using BackupPlatform.Contracts.Agents;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace BackupPlatform.Api.Push;

/// <summary>Server-to-agent messages; method names are the wire names in <see cref="AgentPushChannelContract"/>.</summary>
public interface IAgentHubClient
{
    Task ServerHello(AgentServerHello message);

    Task ProtocolNotice(AgentProtocolNotice message);

    Task PollHint(AgentPollHint message);
}

/// <summary>
/// Push channel for agents. It accepts only the agent authentication scheme (a browser session never reaches it),
/// maps every connection to the authenticated agent principal, and exposes no client-to-server methods, so nothing a
/// client sends can name or impersonate another agent or tenant. It knows nothing about backups or providers.
/// </summary>
[Authorize(Policy = AuthorizationPolicies.Agent)]
public sealed class AgentHub(
    AgentConnectionRegistry registry,
    AgentConnectivityService connectivity,
    AgentSecurityOptions security,
    AgentConnectivityOptions options,
    TimeProvider timeProvider,
    ILogger<AgentHub> logger) : Hub<IAgentHubClient>
{
    public override async Task OnConnectedAsync()
    {
        var identity = AgentAuthentication.TryGetIdentity(Context.User);
        if (identity is null)
        {
            // Unreachable behind the agent policy; fail closed rather than serve an unmapped connection.
            Context.Abort();
            return;
        }
        registry.Register(identity.AgentId, Context);
        logger.LogInformation("Agent {AgentId} connected to the push channel.", identity.AgentId.Value);
        try
        {
            await connectivity.NoteConnectedAsync(identity, Context.ConnectionAborted);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Liveness is best effort here; the heartbeat remains authoritative.
            logger.LogWarning(exception, "Recording the push connection for agent {AgentId} failed.", identity.AgentId.Value);
        }
        await Clients.Caller.ServerHello(new AgentServerHello
        {
            AgentId = identity.AgentId.Value,
            ServerTime = timeProvider.GetUtcNow(),
            Protocol = AgentProtocolMiddleware.ToWire(security.Protocol),
            HeartbeatIntervalSeconds = (int)options.HeartbeatInterval.TotalSeconds
        });
        if (Context.GetHttpContext()?.Features.Get<AgentProtocolFeature>() is { } declared && declared.Version.CompareTo(security.Protocol.Version) < 0)
        {
            await Clients.Caller.ProtocolNotice(new AgentProtocolNotice
            {
                Kind = AgentPushChannelContract.NoticeUpgradeAvailable,
                Action = AgentErrorActions.UpgradeAgent,
                Protocol = AgentProtocolMiddleware.ToWire(security.Protocol)
            });
        }
        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (AgentAuthentication.TryGetIdentity(Context.User) is { } identity)
        {
            registry.Unregister(identity.AgentId, Context.ConnectionId);
            logger.LogInformation("Agent {AgentId} disconnected from the push channel.", identity.AgentId.Value);
        }
        return base.OnDisconnectedAsync(exception);
    }
}
