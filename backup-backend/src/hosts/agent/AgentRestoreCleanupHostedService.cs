using BackupPlatform.Agent.Core.Tasks;
using Microsoft.Extensions.Hosting;

namespace BackupPlatform.Agent.Host;

internal sealed class AgentRestoreCleanupHostedService(RestoreCleanupWorker worker) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => worker.RunAsync(stoppingToken);
}
