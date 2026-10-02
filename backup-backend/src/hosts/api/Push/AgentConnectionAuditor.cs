using BackupPlatform.Agents.Connectivity;
using BackupPlatform.Domain.Agents;

namespace BackupPlatform.Api.Push;

/// <summary>
/// Authentication happens when a connection opens, but a connection can outlive the agent's authorization (disabled,
/// revoked on another node or by direct database change). Periodically dropping connections of agents that are no
/// longer Active bounds that window; the agent's next connect is then refused by authentication.
/// </summary>
public sealed class AgentConnectionAuditor(
    AgentConnectionRegistry registry,
    IServiceScopeFactory scopes,
    AgentConnectivityOptions options,
    TimeProvider timeProvider,
    ILogger<AgentConnectionAuditor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.ConnectionAuditInterval, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await AuditAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    internal async Task AuditAsync(CancellationToken cancellationToken)
    {
        try
        {
            var connected = registry.ConnectedAgents;
            if (connected.Count == 0)
            {
                return;
            }
            await using var scope = scopes.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IAgentConnectivityStore>();
            var lifecycle = await store.GetLifecycleAsync(connected, cancellationToken);
            foreach (var agentId in connected)
            {
                if (!lifecycle.TryGetValue(agentId, out var status) || status != AgentStatus.Active)
                {
                    logger.LogInformation("Dropping the push connection of agent {AgentId}: lifecycle is {Status}.", agentId.Value, status);
                    registry.Abort(agentId);
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "The push connection audit failed; it will run again.");
        }
    }
}
