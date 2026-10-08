using BackupPlatform.Application.Downloads;

namespace BackupPlatform.Api.Downloads;

public static class AgentDownloadRegistration
{
    public static IServiceCollection AddAgentDownloads(this IServiceCollection services)
    {
        services.AddSingleton<AgentDownloadSpool>();
        services.AddScoped<AgentDownloadSource>();
        services.AddScoped<IAgentDownloadSource>(provider => provider.GetRequiredService<AgentDownloadSource>());
        services.AddHostedService<AgentDownloadSpoolCleanup>();
        return services;
    }
}

internal sealed class AgentDownloadSpoolCleanup(AgentDownloadSpool spool, TimeProvider time) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), time, stoppingToken);
                spool.SweepExpired();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
