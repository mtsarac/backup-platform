using BackupPlatform.Application.Backups;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quartz;

namespace BackupPlatform.IntegrationTests.Scheduling;

/// <summary>Bounded lifecycle trace and release gates around real reconciliation, firing and Quartz callbacks.</summary>
internal sealed class SchedulerLifecycleProbe : ILoggerProvider
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private const int TraceLimit = 64;
    private readonly object _lock = new();
    private readonly List<string> _stages = [];
    private readonly List<string> _errors = [];
    private readonly Dictionary<string, TaskCompletionSource> _checkpoints = [];
    private readonly TaskCompletionSource _reconcileRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _fireRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _shutdownRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddLogging(logging => logging
            .AddFilter("BackupPlatform.Infrastructure.Scheduling.BackupSchedulerRuntime", LogLevel.Debug)
            .AddProvider(this));
        var coordinator = services.Single(service => service.ServiceType == typeof(IBackupScheduleCoordinator));
        services.Remove(coordinator);
        services.AddSingleton<IBackupScheduleCoordinator>(provider => new CheckpointCoordinator(
            (IBackupScheduleCoordinator)(coordinator.ImplementationInstance
                ?? coordinator.ImplementationFactory?.Invoke(provider)
                ?? ActivatorUtilities.CreateInstance(provider, coordinator.ImplementationType!)), this));
        services.AddSingleton(provider => new ProviderDisposalProbe(this, provider.GetRequiredService<IHostApplicationLifetime>()));
    }

    public Task Checkpoint(string stage)
    {
        lock (_lock) return GetCheckpoint(stage).Task;
    }

    public Task WaitAsync(string stage) => Checkpoint(stage).WaitAsync(Timeout);
    public Task HoldReconciliationAsync() => _reconcileRelease.Task.WaitAsync(Timeout);
    public Task HoldFireAsync() => _fireRelease.Task.WaitAsync(Timeout);
    public Task HoldShutdownAsync() => _shutdownRelease.Task.WaitAsync(Timeout);
    public void ReleaseReconciliation() => _reconcileRelease.TrySetResult();
    public void ReleaseShutdown() => _shutdownRelease.TrySetResult();
    public void ReleaseFire() => _fireRelease.TrySetResult();

    public void ReleaseAll()
    {
        ReleaseReconciliation();
        ReleaseShutdown();
        ReleaseFire();
    }

    public void Record(string stage)
    {
        lock (_lock)
        {
            if (_stages.Count < TraceLimit) _stages.Add(stage);
            GetCheckpoint(stage).TrySetResult();
        }
    }

    private TaskCompletionSource GetCheckpoint(string stage)
    {
        if (!_checkpoints.TryGetValue(stage, out var checkpoint))
        {
            checkpoint = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _checkpoints.Add(stage, checkpoint);
        }
        return checkpoint;
    }

    public string Trace
    {
        get { lock (_lock) return string.Join(" -> ", _stages); }
    }

    public void AssertOrder(params string[] stages)
    {
        lock (_lock)
        {
            var previous = -1;
            foreach (var stage in stages)
            {
                var index = _stages.IndexOf(stage);
                Assert.True(index > previous, $"Missing or out-of-order {stage}: {Trace}");
                previous = index;
            }
            Assert.Empty(_errors);
        }
    }

    public ILogger CreateLogger(string categoryName) => new CaptureLogger(categoryName, this);
    public void Dispose() { }

    private sealed class CaptureLogger(string category, SchedulerLifecycleProbe probe) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (category == "BackupPlatform.Infrastructure.Scheduling.BackupSchedulerRuntime")
            {
                var stage = eventId.Id switch
                {
                    7100 => "runtime-stopping",
                    7101 => "reconciliation-stopped",
                    7102 => "quartz-shutdown-requested",
                    7103 => "quartz-shutdown-completed",
                    7104 => "runtime-stopped",
                    _ => null
                };
                if (stage is not null) probe.Record(stage);
            }
            if (logLevel >= LogLevel.Error || HasDisposedProvider(exception))
            {
                lock (probe._lock)
                {
                    if (probe._errors.Count < TraceLimit) probe._errors.Add($"{category}: {formatter(state, exception)} {exception}");
                }
            }
        }

        private static bool HasDisposedProvider(Exception? exception) => exception is ObjectDisposedException
            || exception is AggregateException aggregate && aggregate.InnerExceptions.Any(HasDisposedProvider)
            || exception?.InnerException is { } inner && HasDisposedProvider(inner);
    }

    private sealed class CheckpointCoordinator(IBackupScheduleCoordinator inner, SchedulerLifecycleProbe probe) : IBackupScheduleCoordinator
    {
        public Task SynchronizeAsync(Guid jobId, BackupScheduleTarget? desired, CancellationToken cancellationToken) =>
            inner.SynchronizeAsync(jobId, desired, cancellationToken);

        public async Task<BackupScheduleReconciliation> ReconcileAsync(IReadOnlyCollection<BackupScheduleTarget> desired, CancellationToken cancellationToken)
        {
            var report = await inner.ReconcileAsync(desired, cancellationToken);
            probe.Record("reconciliation-active");
            try
            {
                await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                probe.Record("reconciliation-cancelled");
                // Async cleanup keeps the real reconciliation scope alive after it acknowledges cancellation.
                await probe.HoldReconciliationAsync();
                probe.Record("reconciliation-cleanup-completed");
            }
            return report;
        }
    }

    internal sealed class ProviderDisposalProbe : IDisposable
    {
        private readonly SchedulerLifecycleProbe _probe;
        private readonly CancellationTokenRegistration _stopping;

        public ProviderDisposalProbe(SchedulerLifecycleProbe probe, IHostApplicationLifetime lifetime)
        {
            _probe = probe;
            _stopping = lifetime.ApplicationStopping.Register(() => probe.Record("host-stopping"));
        }

        public void Dispose()
        {
            _probe.Record("provider-disposed");
            _stopping.Dispose();
        }
    }
}

internal sealed class SchedulerCallbackProbe(SchedulerLifecycleProbe probe, IServiceProvider provider, Guid jobId) : ISchedulerListener, IJobListener
{
    public async ValueTask SchedulerShuttingDown(IScheduler scheduler, CancellationToken cancellationToken = default)
    {
        probe.Record("quartz-stopping-callback");
        await probe.HoldShutdownAsync();
        // Like Quartz's lazy signaler, a callback may still resolve from the root while shutting down.
        _ = provider.GetRequiredService<TimeProvider>();
        probe.Record("quartz-stopping-callback-completed");
    }

    public ValueTask SchedulerShutdown(IScheduler scheduler, CancellationToken cancellationToken = default)
    {
        _ = provider.GetRequiredService<TimeProvider>();
        probe.Record("quartz-shutdown-callback");
        return default;
    }

    public async ValueTask JobToBeExecuted(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        if (context.JobDetail.Key.Name != jobId.ToString("N")) return;
        probe.Record("fire-active");
        await probe.HoldFireAsync();
    }

    public ValueTask JobWasExecuted(IJobExecutionContext context, JobExecutionException? jobException, CancellationToken cancellationToken = default)
    {
        if (context.JobDetail.Key.Name != jobId.ToString("N")) return default;
        Assert.Null(jobException);
        _ = provider.GetRequiredService<TimeProvider>();
        probe.Record("fire-completed");
        return default;
    }
}
