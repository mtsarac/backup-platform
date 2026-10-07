using BackupPlatform.Application.Backups;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quartz;

namespace BackupPlatform.Infrastructure.Scheduling;

/// <summary>
/// Runs the wake-up scheduler and the sweep. The sweep is what makes the database authoritative: at startup and every
/// <see cref="BackupSchedulingOptions.ReconcileInterval"/> it starts whatever is due (whether or not a wake-up fired), drops unclaimed
/// scheduled runs that outlived their grace period, and reconciles Quartz with the jobs. It keeps working when Quartz is unreachable;
/// the wake-ups only make a backup start on time instead of up to one interval late.
/// </summary>
internal sealed class BackupSchedulerRuntime(ISchedulerFactory factory, IServiceScopeFactory scopes, BackupSchedulingOptions options, TimeProvider time, ILogger<BackupSchedulerRuntime> logger) : BackgroundService
{
    private IScheduler? _scheduler;
    private readonly object _stopLock = new();
    private Task? _stopTask;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return;
        using var timer = new PeriodicTimer(options.ReconcileInterval, time);
        try
        {
            do
            {
                await StartSchedulerAsync(stoppingToken);
                await SweepOnceAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            logger.LogDebug(new EventId(7101, "ReconciliationStopped"), "Backup scheduler reconciliation stopped; cancellation requested: {Cancelled}.", stoppingToken.IsCancellationRequested);
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        // app.Run and WebApplicationFactory can stop the same host concurrently. Quartz's second Shutdown
        // returns immediately, so every host stop must join our first stop before either can dispose DI.
        lock (_stopLock) return _stopTask ??= StopCoreAsync();
    }

    private async Task StopCoreAsync()
    {
        logger.LogDebug(new EventId(7100, "RuntimeStopping"), "Backup scheduler runtime stopping.");
        // The host's deadline must not leave reconciliation resolving services while DI is disposed.
        // As with the Quartz drain below, cancellation is requested but owned work is awaited to completion.
        await base.StopAsync(CancellationToken.None);
        if (_scheduler is { Status: not SchedulerStatus.Shutdown })
        {
            logger.LogDebug(new EventId(7102, "QuartzShutdownRequested"), "Backup scheduler Quartz shutdown requested.");
            await _scheduler.Shutdown(waitForJobsToComplete: true, CancellationToken.None);
            logger.LogDebug(new EventId(7103, "QuartzShutdownCompleted"), "Backup scheduler Quartz shutdown completed.");
        }
        logger.LogDebug(new EventId(7104, "RuntimeStopped"), "Backup scheduler runtime stopped.");
    }

    private async Task StartSchedulerAsync(CancellationToken cancellationToken)
    {
        if (_scheduler is { Status: SchedulerStatus.Running }) return;
        try
        {
            _scheduler = await factory.GetScheduler(cancellationToken);
            await _scheduler.Start(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _scheduler = null;
            logger.LogError(exception, "The backup wake-up scheduler could not start; sweeps keep running and will retry.");
        }
    }

    /// <summary>One sweep; never throws.</summary>
    internal async Task SweepOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var report = await scope.ServiceProvider.GetRequiredService<BackupScheduleService>().SweepAsync(cancellationToken);
            foreach (var error in report.Errors) logger.LogError(error, "A scheduling sweep step failed.");
            if (report.JobsEvaluated + report.RunsExpired != 0)
                logger.LogInformation("Scheduling sweep: {Evaluated} jobs evaluated, {Created} runs created, {Expired} unclaimed runs expired.", report.JobsEvaluated, report.RunsCreated, report.RunsExpired);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "The scheduling sweep failed.");
        }
    }
}
