using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace BackupPlatform.Infrastructure.Scheduling;

/// <summary>
/// The wake-up. It carries only identifiers: what is due, whether the job still exists and is enabled, and what to do about it are all
/// decided from the database by <see cref="BackupScheduleService"/>. A stale, duplicated or refired wake-up therefore either finds nothing
/// due or finds the occurrence already decided, and does nothing.
/// </summary>
[DisallowConcurrentExecution]
internal sealed class ScheduledBackupJob(IServiceScopeFactory scopes, ILogger<ScheduledBackupJob> logger) : IJob
{
    public const string TenantKey = "tenant";
    public const string JobKey = "job";

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        var data = context.MergedJobDataMap;
        if (!Guid.TryParse(data.GetString(TenantKey), out var tenant) || !Guid.TryParse(data.GetString(JobKey), out var job)) return;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<BackupScheduleService>().EvaluateAsync(new TenantId(tenant), job, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Not rethrown: the sweep re-evaluates anything left due, and reconciliation re-aims the wake-up.
            logger.LogError(exception, "Evaluating the schedule of backup job {JobId} failed; the next sweep will retry.", job);
        }
    }
}
