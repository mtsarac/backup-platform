using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BackupPlatform.Api.Endpoints;

internal sealed class AgentLogRetentionWorker(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<AgentLogRetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var days = configuration.GetValue("AgentLogs:RetentionDays", 30);
        if (days < 1) throw new InvalidOperationException("AgentLogs:RetentionDays must be positive.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                await db.Database.ExecuteSqlRawAsync("DELETE FROM agent_logs WHERE \"ReceivedAt\" < @cutoff",
                    [new NpgsqlParameter("cutoff", DateTime.UtcNow.AddDays(-days))], stoppingToken);
            }
            catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException)
            {
                logger.LogWarning("Agent log retention failed ({ExceptionType}).", exception.GetType().Name);
            }
            await Task.Delay(TimeSpan.FromDays(1), stoppingToken);
        }
    }
}
