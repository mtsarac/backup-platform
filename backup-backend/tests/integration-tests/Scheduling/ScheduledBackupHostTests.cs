using System.Net;
using System.Text.Json;
using BackupPlatform.Application.Backups;
using BackupPlatform.Contracts.Backups;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Quartz;
using Xunit.Abstractions;

namespace BackupPlatform.IntegrationTests.Scheduling;

/// <summary>The whole scheduler runtime in a real API host: Quartz, the database and the sweep, against a real clock.</summary>
public sealed class ScheduledBackupHostTests(PostgreSqlFixture database, ITestOutputHelper output) : IClassFixture<PostgreSqlFixture>
{
    private static readonly Dictionary<string, string?> Quick = new()
    {
        ["Backups:Scheduling:Enabled"] = "true",
        ["Backups:Scheduling:AllowSecondPrecisionCron"] = "true",
        // The sweep is parked: a run that appears here was woken by Quartz alone.
        ["Backups:Scheduling:ReconcileInterval"] = "00:30:00"
    };

    [Fact]
    public async Task FactoryDisposalWaitsForReconciliationCleanupAndJoinsCancelledRepeatedStops()
    {
        for (var iteration = 0; iteration < 8; iteration++)
        {
            using var probe = new SchedulerLifecycleProbe();
            probe.ReleaseShutdown();
            probe.ReleaseFire();
            await using var original = new AuthApiFactory(database.ConnectionString, new MutableClock(live: true), configuration: Quick);
            await using var factory = original.WithWebHostBuilder(builder => builder.ConfigureServices(probe.ConfigureServices));
            try
            {
                var provider = factory.Services;
                _ = provider.GetRequiredService<SchedulerLifecycleProbe.ProviderDisposalProbe>();
                var runtime = SchedulerRuntime(provider);
                var scheduler = await provider.GetRequiredService<ISchedulerFactory>().GetScheduler();
                scheduler.ListenerManager.AddSchedulerListener(new SchedulerCallbackProbe(probe, provider, Guid.Empty));
                await probe.WaitAsync("reconciliation-active");

                provider.GetRequiredService<IHostApplicationLifetime>().StopApplication();
                var stop = runtime.StopAsync(new CancellationToken(canceled: true));
                await probe.WaitAsync("reconciliation-cancelled");
                var repeatedStop = runtime.StopAsync(new CancellationToken(canceled: true));
                Assert.False(stop.IsCompleted, probe.Trace);
                Assert.False(repeatedStop.IsCompleted, probe.Trace);
                var disposal = factory.DisposeAsync().AsTask();
                Assert.False(probe.Checkpoint("provider-disposed").IsCompleted, probe.Trace);

                probe.ReleaseReconciliation();
                await Task.WhenAll(stop, repeatedStop, disposal).WaitAsync(SchedulerLifecycleProbe.Timeout);
                Assert.True(runtime.ExecuteTask!.IsCompleted, probe.Trace);
                Assert.Equal(SchedulerStatus.Shutdown, scheduler.Status);
                probe.AssertOrder("host-stopping", "runtime-stopping", "reconciliation-cancelled",
                    "reconciliation-cleanup-completed", "reconciliation-stopped", "quartz-shutdown-requested",
                    "quartz-stopping-callback", "quartz-shutdown-callback", "quartz-shutdown-completed", "runtime-stopped", "provider-disposed");
            }
            finally
            {
                probe.ReleaseAll();
                output.WriteLine("Reconciliation lifecycle iteration {0}: {1}", iteration + 1, probe.Trace);
            }
        }
    }

    [Fact]
    public async Task FactoryDisposalDrainsRealBackupFiringsAndQuartzCallbacksBeforeProviderDisposal()
    {
        var kit = new ScheduleKit(database);
        for (var iteration = 0; iteration < 8; iteration++)
        {
            var clock = new MutableClock();
            var agent = await kit.Runs.AddAgentAsync();
            await ManagedService.ManagedServiceTestSupport.GrantLegacyUnlimitedAsync(database, agent.TenantId);
            var job = await kit.AddJobAsync(agent, from: clock.GetUtcNow());
            var next = job.Schedule!.NextOccurrenceAfter(clock.GetUtcNow())!.Value;
            using var probe = new SchedulerLifecycleProbe();
            await using var original = new AuthApiFactory(database.ConnectionString, clock, configuration: Quick);
            await using var factory = original.WithWebHostBuilder(builder => builder.ConfigureServices(probe.ConfigureServices));
            try
            {
                var provider = factory.Services;
                _ = provider.GetRequiredService<SchedulerLifecycleProbe.ProviderDisposalProbe>();
                var runtime = SchedulerRuntime(provider);
                var scheduler = await provider.GetRequiredService<ISchedulerFactory>().GetScheduler();
                var callbacks = new SchedulerCallbackProbe(probe, provider, job.Id);
                scheduler.ListenerManager.AddSchedulerListener(callbacks);
                scheduler.ListenerManager.AddJobListener(callbacks);
                await probe.WaitAsync("reconciliation-active");
                Assert.Single(await QuartzNode.WakeUpsAsync(database, job.Id));

                clock.Advance(next - clock.GetUtcNow());
                await kit.SeenAsync(agent, clock.GetUtcNow());
                await scheduler.TriggerJob(new JobKey(job.Id.ToString("N"), "backup-schedule"));
                await probe.WaitAsync("fire-active");
                var disposal = factory.DisposeAsync().AsTask();
                await probe.WaitAsync("reconciliation-cancelled");
                Assert.False(disposal.IsCompleted, probe.Trace);
                Assert.False(probe.Checkpoint("provider-disposed").IsCompleted, probe.Trace);
                probe.ReleaseReconciliation();
                await probe.WaitAsync("quartz-stopping-callback");

                // This second stop must join the held first shutdown, not return through Quartz's atomic no-op path.
                var repeatedStop = runtime.StopAsync(CancellationToken.None);
                Assert.False(repeatedStop.IsCompleted, probe.Trace);
                Assert.False(disposal.IsCompleted, probe.Trace);
                Assert.False(probe.Checkpoint("provider-disposed").IsCompleted, probe.Trace);
                probe.ReleaseShutdown();
                await probe.WaitAsync("quartz-stopping-callback-completed");
                Assert.False(disposal.IsCompleted, probe.Trace);
                probe.ReleaseFire();
                await Task.WhenAll(disposal, repeatedStop).WaitAsync(SchedulerLifecycleProbe.Timeout);

                Assert.Equal(SchedulerStatus.Shutdown, scheduler.Status);
                Assert.True(runtime.ExecuteTask!.IsCompleted, probe.Trace);
                var run = Assert.Single(await kit.RunsOfAsync(job));
                Assert.Equal("Scheduled", run.TriggerType.ToString());
                Assert.Equal("Pending", run.Status.ToString());
                Assert.Equal(next, run.ScheduledOccurrence);
                probe.AssertOrder("reconciliation-active", "fire-active", "host-stopping", "runtime-stopping",
                    "reconciliation-cancelled", "reconciliation-stopped", "quartz-shutdown-requested",
                    "quartz-stopping-callback", "quartz-stopping-callback-completed", "fire-completed",
                    "quartz-shutdown-callback", "quartz-shutdown-completed", "runtime-stopped", "provider-disposed");
            }
            finally
            {
                probe.ReleaseAll();
                await kit.DisableAsync(job);
                output.WriteLine("Firing lifecycle iteration {0}: {1}", iteration + 1, probe.Trace);
            }
        }
    }

    private static BackgroundService SchedulerRuntime(IServiceProvider provider) => provider.GetServices<IHostedService>()
        .OfType<BackgroundService>().Single(service => service.GetType().Name == "BackupSchedulerRuntime");

    [Fact]
    public async Task QuartzWakesUpAndCreatesExactlyOneScheduledRun()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(database, Quick, new MutableClock(live: true));
        using (var poll = await scenario.World.PollAsync(scenario.Agent)) Assert.Equal(HttpStatusCode.OK, poll.StatusCode);
        var body = new
        {
            name = "Scheduled",
            agentId = scenario.Agent.AgentId,
            configuration = new BackupExecutionConfiguration { Database = "master", Host = "sql-server", IntegratedSecurity = true, Destinations = BackupTestDestinations.LocalAgent },
            schedule = new { cronExpression = "*/2 * * * * *", timeZone = "UTC", missedRunGraceSeconds = 60 }
        };
        using var created = await scenario.CreateJobRawAsync(body);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var job = AgentWorld.ReadJson(created);
        var jobId = job.GetProperty("id").GetGuid();
        Assert.Equal("UTC", job.GetProperty("schedule").GetProperty("timeZone").GetString());

        var runs = await WaitAsync(async () =>
        {
            using var response = await scenario.Admin.GetAsync(scenario.Root + $"/backup-jobs/{jobId}/runs");
            var list = AgentWorld.ReadJson(response).GetProperty("items").EnumerateArray().ToList();
            return list.Count == 0 ? null : list;
        });
        var run = Assert.Single(runs);
        Assert.Equal("Scheduled", run.GetProperty("triggerType").GetString());
        Assert.Equal("Pending", run.GetProperty("status").GetString());

        Assert.Equal([jobId.ToString("N")], await QuartzNode.JobDetailsAsync(database, jobId));
    }

    private static async Task<JsonElement> CreateQuickJobAsync(BackupApiScenario scenario, string cron = "*/2 * * * * *", string overlap = "Queue")
    {
        using var poll = await scenario.World.PollAsync(scenario.Agent);
        Assert.Equal(HttpStatusCode.OK, poll.StatusCode);
        var body = new
        {
            name = "Scheduled",
            agentId = scenario.Agent.AgentId,
            configuration = new BackupExecutionConfiguration { Database = "master", Host = "sql-server", IntegratedSecurity = true, Destinations = BackupTestDestinations.LocalAgent },
            overlapPolicy = overlap,
            schedule = new { cronExpression = cron, timeZone = "UTC", missedRunGraceSeconds = 60 }
        };
        using var created = await scenario.CreateJobRawAsync(body);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return AgentWorld.ReadJson(created);
    }

    private static async Task<IReadOnlyList<BackupPlatform.Domain.Backups.BackupRun>> RunsAsync(BackupApiScenario scenario, Guid jobId)
    {
        await using var scope = scenario.World.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IBackupRunStore>().ListRunsAsync(scenario.Setup.Tenant.Id, jobId, 100, default);
    }

    private async Task<int> RunCountAsync(Guid jobId)
    {
        await using var context = database.CreatePlatformContext();
        return context.BackupRuns.Count(run => run.JobId == jobId);
    }

    [Fact]
    public async Task DisablingAJobRemovesItsTriggerAndNoRunIsCreatedAfterwardsEvenIfAStaleWakeUpFires()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(database, Quick, new MutableClock(live: true));
        var jobId = (await CreateQuickJobAsync(scenario)).GetProperty("id").GetGuid();
        await WaitAsync(async () => await RunCountAsync(jobId) >= 1 ? "ready" : null);

        using var disabled = await scenario.Admin.SendAsync(HttpMethod.Post, scenario.Root + $"/backup-jobs/{jobId}/disable");
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);

        Assert.Empty(await QuartzNode.WakeUpsAsync(database, jobId));
        Assert.Empty(await QuartzNode.JobDetailsAsync(database, jobId));
        var runsWhenDisabled = await RunCountAsync(jobId);
        await Task.Delay(TimeSpan.FromSeconds(6));
        Assert.Equal(runsWhenDisabled, await RunCountAsync(jobId));

        // A wake-up that was already in flight when the job was disabled: it is evaluated against the job as it is now.
        await using var scope = scenario.World.Factory.Services.CreateAsyncScope();
        var late = await scope.ServiceProvider.GetRequiredService<BackupScheduleService>().EvaluateAsync(scenario.Setup.Tenant.Id, jobId, default);
        Assert.Null(late.CreatedRunId);
        Assert.Equal(runsWhenDisabled, await RunCountAsync(jobId));
    }

    [Fact]
    public async Task ChangingTheCronReplacesTheTriggerAndKeepsTheRunsAlreadyCreated()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(database, Quick, new MutableClock(live: true));
        var jobId = (await CreateQuickJobAsync(scenario)).GetProperty("id").GetGuid();
        await WaitAsync(async () => await RunCountAsync(jobId) >= 1 ? "ready" : null);
        var first = (await RunsAsync(scenario, jobId)).OrderBy(run => run.CreatedAt).First();

        // Far in the future: hourly at minute 17. Nothing may fire from the old every-2-seconds trigger any more.
        using var changed = await scenario.Admin.SendAsync(HttpMethod.Put, scenario.Root + $"/backup-jobs/{jobId}/schedule",
            new { cronExpression = "17 * * * *", timeZone = "UTC", missedRunGraceSeconds = 60, overlapPolicy = "Queue" });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

        var wakeUp = Assert.Single(await QuartzNode.WakeUpsAsync(database, jobId));
        var next = AgentWorld.ReadJson(changed).GetProperty("schedule").GetProperty("nextOccurrenceAt").GetDateTimeOffset();
        Assert.Equal(17, next.Minute);
        Assert.Equal(next.ToUnixTimeMilliseconds(), wakeUp.StartTime.ToUnixTimeMilliseconds());
        var runs = await RunCountAsync(jobId);
        await Task.Delay(TimeSpan.FromSeconds(5));
        Assert.Equal(runs, await RunCountAsync(jobId));
        var again = (await RunsAsync(scenario, jobId)).Single(run => run.Id == first.Id);
        Assert.Equal((first.ScheduledOccurrence, first.CreatedAt, first.TriggerType), (again.ScheduledOccurrence, again.CreatedAt, again.TriggerType));
    }

    [Fact]
    public async Task CreatingAJobPutsAWakeUpInTheDatabaseBackedStoreImmediately()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(database, Quick, new MutableClock(live: true));
        var job = await CreateQuickJobAsync(scenario, cron: "5 4 * * *");
        var jobId = job.GetProperty("id").GetGuid();

        var wakeUp = Assert.Single(await QuartzNode.WakeUpsAsync(database, jobId));
        Assert.Equal(job.GetProperty("schedule").GetProperty("nextOccurrenceAt").GetDateTimeOffset().ToUnixTimeMilliseconds(), wakeUp.StartTime.ToUnixTimeMilliseconds());
        Assert.Equal(0, await RunCountAsync(jobId));
    }

    private static async Task<T> WaitAsync<T>(Func<Task<T?>> probe) where T : class
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (await probe() is { } value) return value;
            await Task.Delay(250);
        }
        throw new Xunit.Sdk.XunitException("Timed out waiting for the scheduled run.");
    }
}
