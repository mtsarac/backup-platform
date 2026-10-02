using BackupPlatform.Agent.Core.Connectivity;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.Agent.Host;

/// <summary>Runs the connectivity service for the lifetime of the host; stopping the host cancels it and waits for a clean stop.</summary>
public sealed class AgentConnectivityWorker(
    AgentConnectivityService connectivity,
    IHostApplicationLifetime lifetime,
    ILogger<AgentConnectivityWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await connectivity.RunAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (AgentIdentityUnavailableException exception)
        {
            logger.LogCritical("The agent cannot start: {Reason}", exception.Message);
            Environment.ExitCode = 1;
            lifetime.StopApplication();
        }
    }
}
