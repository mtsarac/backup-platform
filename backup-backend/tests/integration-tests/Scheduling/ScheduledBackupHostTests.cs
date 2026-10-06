using System.Net;
using System.Text.Json;
using BackupPlatform.Application.Backups;
using BackupPlatform.Contracts.Backups;
using Microsoft.Extensions.DependencyInjection;

namespace BackupPlatform.IntegrationTests.Scheduling;

/// <summary>The whole scheduler runtime in a real API host: Quartz, the database and the sweep, against a real clock.</summary>
public sealed class ScheduledBackupHostTests(PostgreSqlFixture database) : IClassFixture<PostgreSqlFixture>
{
    private static readonly Dictionary<string, string?> Quick = new()
    {
        ["Backups:Scheduling:Enabled"] = "true",
        ["Backups:Scheduling:AllowSecondPrecisionCron"] = "true",
        // The sweep is parked: a run that appears here was woken by Quartz alone.
        ["Backups:Scheduling:ReconcileInterval"] = "00:30:00"
    };

    [Fact]
    public async Task QuartzWakesUpAndCreatesExactlyOneScheduledRun()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(database, Quick, new MutableClock(live: true));
        using (var poll = await scenario.World.PollAsync(scenario.Agent)) Assert.Equal(HttpStatusCode.OK, poll.StatusCode);
        var body = new
        {
            name = "Scheduled",
            agentId = scenario.Agent.AgentId,
            configuration = new BackupExecutionConfiguration { Database = "master", Host = "sql-server", IntegratedSecurity = true },
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
            configuration = new BackupExecutionConfiguration { Database = "master", Host = "sql-server", IntegratedSecurity = true },
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
