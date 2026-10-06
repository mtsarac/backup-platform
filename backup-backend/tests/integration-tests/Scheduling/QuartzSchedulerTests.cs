using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Backups;
using static BackupPlatform.IntegrationTests.Scheduling.ScheduleKit;

namespace BackupPlatform.IntegrationTests.Scheduling;

/// <summary>
/// The real Quartz scheduler on its PostgreSQL store, kept in line with the jobs table. Every test makes the database and Quartz disagree in
/// one of the ways that can really happen (a commit that never reached Quartz, a lost or tampered trigger, a stale trigger of a job that
/// is gone) and expects reconciliation to repair it, once, without touching what is already right.
/// </summary>
public sealed class QuartzSchedulerTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private readonly ScheduleKit _kit = new(fixture);

    private static async Task<BackupScheduleReconciliation> ReconcileAsync(QuartzNode node) => await node.WithServiceAsync(service => service.ReconcileAsync(default));

    private static DateTimeOffset Truncate(DateTimeOffset value) => DateTimeOffset.FromUnixTimeMilliseconds(value.ToUnixTimeMilliseconds());

    [Fact]
    public async Task AMissingWakeUpIsCreatedAtTheDatabasesNextOccurrenceAndHoldsNoCronOrZone()
    {
        await using var node = QuartzNode.Create(fixture, new MutableClock(H(1, 20)));
        var agent = await _kit.Runs.AddAgentAsync();
        var job = await _kit.AddJobAsync(agent, cron: "0 6 * * *", zone: "Europe/Istanbul");

        var report = await ReconcileAsync(node);

        Assert.True(report.Created >= 1);
        var wakeUp = Assert.Single(await QuartzNode.WakeUpsAsync(fixture, job.Id));
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 3, 0, 0, TimeSpan.Zero), Truncate(wakeUp.StartTime)); // 06:00 in Istanbul
        Assert.Equal("SIMPLE", wakeUp.Type); // a one-shot instant, never a cron trigger: Quartz holds no schedule configuration
        Assert.Equal(0, await QuartzNode.ScalarAsync(fixture, "SELECT count(*) FROM quartz.qrtz_cron_triggers"));
        Assert.Equal([job.Id.ToString("N")], await QuartzNode.JobDetailsAsync(fixture, job.Id));
    }

    [Fact]
    public async Task ReconciliationIsIdempotentAndNeverRewritesAWakeUpThatIsAlreadyRight()
    {
        await using var node = QuartzNode.Create(fixture, new MutableClock(H(1, 20)));
        var agent = await _kit.Runs.AddAgentAsync();
        var job = await _kit.AddJobAsync(agent);
        await ReconcileAsync(node);
        var before = await QuartzNode.WakeUpsAsync(fixture, job.Id);

        var second = await ReconcileAsync(node);
        var third = await ReconcileAsync(node);

        Assert.False(second.Changed);
        Assert.False(third.Changed);
        Assert.True(second.Unchanged >= 1);
        Assert.Equal(before, await QuartzNode.WakeUpsAsync(fixture, job.Id)); // same trigger row, same name, not recreated
    }

    [Fact]
    public async Task AStaleCronIsReplacedByTheCurrentOneAndOnlyOneWakeUpRemains()
    {
        await using var node = QuartzNode.Create(fixture, new MutableClock(H(1, 20)));
        var agent = await _kit.Runs.AddAgentAsync();
        var job = await _kit.AddJobAsync(agent, cron: "0 * * * *");
        await ReconcileAsync(node);
        Assert.Equal(H(2), Truncate(Assert.Single(await QuartzNode.WakeUpsAsync(fixture, job.Id)).StartTime));

        // The edit committed to the database but the scheduler update never happened (crash between the two).
        await _kit.Store().ModifyJobAsync(job.TenantId, job.Id, target =>
            target.ChangeSchedule(target.Schedule!.Change(true, "45 * * * *", "UTC", null, H(1, 20))), default);
        var report = await ReconcileAsync(node);

        Assert.True(report.Rewritten >= 1);
        var wakeUp = Assert.Single(await QuartzNode.WakeUpsAsync(fixture, job.Id));
        Assert.Equal(H(1, 45), Truncate(wakeUp.StartTime));
        Assert.False((await ReconcileAsync(node)).Changed);
    }

    [Fact]
    public async Task AStaleTimeZoneIsReplacedByTheCurrentOne()
    {
        await using var node = QuartzNode.Create(fixture, new MutableClock(H(1, 20)));
        var agent = await _kit.Runs.AddAgentAsync();
        var job = await _kit.AddJobAsync(agent, cron: "0 6 * * *", zone: "UTC");
        await ReconcileAsync(node);
        Assert.Equal(H(6), Truncate(Assert.Single(await QuartzNode.WakeUpsAsync(fixture, job.Id)).StartTime));

        await _kit.Store().ModifyJobAsync(job.TenantId, job.Id, target =>
            target.ChangeSchedule(target.Schedule!.Change(true, "0 6 * * *", "America/New_York", null, H(1, 20))), default);
        var report = await ReconcileAsync(node);

        Assert.True(report.Rewritten >= 1);
        Assert.Equal(H(10), Truncate(Assert.Single(await QuartzNode.WakeUpsAsync(fixture, job.Id)).StartTime)); // 06:00 EDT is 10:00 UTC
    }

    [Fact]
    public async Task ChangingOnlyTheGraceOrOverlapPolicyNeverTouchesTheWakeUp()
    {
        await using var node = QuartzNode.Create(fixture, new MutableClock(H(1, 20)));
        var agent = await _kit.Runs.AddAgentAsync();
        var job = await _kit.AddJobAsync(agent);
        await ReconcileAsync(node);
        var before = await QuartzNode.WakeUpsAsync(fixture, job.Id);

        await _kit.Store().ModifyJobAsync(job.TenantId, job.Id, target =>
            target.ChangeSchedule(target.Schedule!.Change(true, "0 * * * *", "UTC", TimeSpan.FromHours(4), H(1, 20)), BackupOverlapPolicy.Queue), default);

        Assert.False((await ReconcileAsync(node)).Changed);
        Assert.Equal(before, await QuartzNode.WakeUpsAsync(fixture, job.Id));
    }

    [Fact]
    public async Task ALostWakeUpIsRecreated()
    {
        await using var node = QuartzNode.Create(fixture, new MutableClock(H(1, 20)));
        var agent = await _kit.Runs.AddAgentAsync();
        var job = await _kit.AddJobAsync(agent);
        await ReconcileAsync(node);
        await node.Coordinator.SynchronizeAsync(job.Id, null, default); // Quartz forgot it

        Assert.Empty(await QuartzNode.WakeUpsAsync(fixture, job.Id));
        var report = await ReconcileAsync(node);

        Assert.True(report.Created >= 1);
        Assert.Equal(H(2), Truncate(Assert.Single(await QuartzNode.WakeUpsAsync(fixture, job.Id)).StartTime));
    }

    [Fact]
    public async Task ATamperedTriggerIsNotConfigurationAndIsRewrittenFromTheDatabase()
    {
        await using var node = QuartzNode.Create(fixture, new MutableClock(H(1, 20)));
        var agent = await _kit.Runs.AddAgentAsync();
        var job = await _kit.AddJobAsync(agent);
        await ReconcileAsync(node);
        var original = Assert.Single(await QuartzNode.WakeUpsAsync(fixture, job.Id));
        var wrongTicks = H(9).UtcTicks;
        await QuartzNode.ExecuteAsync(fixture, $"UPDATE quartz.qrtz_triggers SET start_time = {wrongTicks}, next_fire_time = {wrongTicks} WHERE job_name = '{job.Id:N}'");

        var report = await ReconcileAsync(node);

        Assert.True(report.Rewritten >= 1);
        var repaired = Assert.Single(await QuartzNode.WakeUpsAsync(fixture, job.Id));
        Assert.Equal(Truncate(original.StartTime), Truncate(repaired.StartTime));
    }

    [Fact]
    public async Task ADisabledJobsWakeUpIsRemovedAndNeverRecreated()
    {
        await using var node = QuartzNode.Create(fixture, new MutableClock(H(1, 20)));
        var agent = await _kit.Runs.AddAgentAsync();
        var job = await _kit.AddJobAsync(agent);
        await ReconcileAsync(node);
        await _kit.DisableAsync(job); // committed; Quartz not told

        var removal = await ReconcileAsync(node);

        Assert.True(removal.Removed >= 1);
        Assert.Empty(await QuartzNode.WakeUpsAsync(fixture, job.Id));
        Assert.Empty(await QuartzNode.JobDetailsAsync(fixture, job.Id));
        Assert.False((await ReconcileAsync(node)).Changed);
        Assert.Empty(await QuartzNode.WakeUpsAsync(fixture, job.Id));
    }

    [Fact]
    public async Task AJobWhoseScheduleWasSwitchedOffOrRemovedLosesItsWakeUp()
    {
        await using var node = QuartzNode.Create(fixture, new MutableClock(H(1, 20)));
        var agent = await _kit.Runs.AddAgentAsync();
        var off = await _kit.AddJobAsync(agent);
        var removed = await _kit.AddJobAsync(agent);
        await ReconcileAsync(node);
        await _kit.Store().ModifyJobAsync(off.TenantId, off.Id, target => target.ChangeSchedule(target.Schedule!.Change(false, "0 * * * *", "UTC", null, H(1, 20))), default);
        await _kit.Store().ModifyJobAsync(removed.TenantId, removed.Id, target => target.ChangeSchedule(null), default);

        await ReconcileAsync(node);

        Assert.Empty(await QuartzNode.WakeUpsAsync(fixture, off.Id));
        Assert.Empty(await QuartzNode.WakeUpsAsync(fixture, removed.Id));
    }

    [Fact]
    public async Task ReEnablingARemovedJobCreatesAWakeUpAtAFutureOccurrenceNotThePast()
    {
        var clock = new MutableClock(H(1, 20));
        await using var node = QuartzNode.Create(fixture, clock);
        var agent = await _kit.Runs.AddAgentAsync();
        var job = await _kit.AddJobAsync(agent);
        await ReconcileAsync(node);
        await _kit.DisableAsync(job);
        await ReconcileAsync(node);
        clock.Advance(TimeSpan.FromHours(4)); // 05:20
        await _kit.EnableAsync(job, clock.GetUtcNow());

        var report = await ReconcileAsync(node);

        Assert.True(report.Created >= 1);
        Assert.Equal(H(6), Truncate(Assert.Single(await QuartzNode.WakeUpsAsync(fixture, job.Id)).StartTime));
    }

    [Fact]
    public async Task AnOrphanWakeUpOfAJobThatNoLongerExistsIsRemoved()
    {
        await using var node = QuartzNode.Create(fixture, new MutableClock(H(1, 20)));
        var agent = await _kit.Runs.AddAgentAsync();
        var ghost = Guid.NewGuid();
        await node.Coordinator.SynchronizeAsync(ghost, new BackupScheduleTarget(agent.TenantId, ghost, "stale", H(3)), default);
        Assert.Single(await QuartzNode.WakeUpsAsync(fixture, ghost));

        var report = await ReconcileAsync(node);

        Assert.True(report.Removed >= 1);
        Assert.Empty(await QuartzNode.WakeUpsAsync(fixture, ghost));
        Assert.Empty(await QuartzNode.JobDetailsAsync(fixture, ghost));
    }

    [Fact]
    public async Task ASchedulerStoreThatLostEverythingIsRebuiltInOnePass()
    {
        await using var node = QuartzNode.Create(fixture, new MutableClock(H(1, 20)));
        var agent = await _kit.Runs.AddAgentAsync();
        var jobs = new[] { await _kit.AddJobAsync(agent), await _kit.AddJobAsync(agent, cron: "15 * * * *"), await _kit.AddJobAsync(agent, cron: "0 4 * * *", zone: "Asia/Kolkata") };
        await ReconcileAsync(node);
        await QuartzNode.ExecuteAsync(fixture, "DELETE FROM quartz.qrtz_simple_triggers; DELETE FROM quartz.qrtz_triggers; DELETE FROM quartz.qrtz_job_details;");

        await ReconcileAsync(node);

        foreach (var job in jobs) Assert.Single(await QuartzNode.WakeUpsAsync(fixture, job.Id));
        Assert.False((await ReconcileAsync(node)).Changed);
    }

    // ---- persistence and clustering ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task WakeUpsSurviveABackendRestart()
    {
        var agent = await _kit.Runs.AddAgentAsync();
        var job = await _kit.AddJobAsync(agent);
        var clock = new MutableClock(H(1, 20));
        await using (var first = QuartzNode.Create(fixture, clock))
        {
            await first.StartAsync();
            await ReconcileAsync(first);
        }
        // The process is gone; a new one starts with empty memory.
        await using var second = QuartzNode.Create(fixture, clock);

        var wakeUp = Assert.Single(await QuartzNode.WakeUpsAsync(fixture, job.Id));
        Assert.Equal(H(2), Truncate(wakeUp.StartTime));
        var report = await ReconcileAsync(second);
        Assert.False(report.Changed);
        Assert.Equal(wakeUp, Assert.Single(await QuartzNode.WakeUpsAsync(fixture, job.Id)));
    }

    [Fact]
    public async Task TwoNodesReconcilingAtTheSameTimeLeaveExactlyOneWakeUpPerJob()
    {
        var agent = await _kit.Runs.AddAgentAsync();
        var jobs = new List<BackupJob>();
        for (var index = 0; index < 6; index++) jobs.Add(await _kit.AddJobAsync(agent, cron: $"{index * 5} * * * *"));
        var clock = new MutableClock(H(1, 20));
        await using var one = QuartzNode.Create(fixture, clock);
        await using var two = QuartzNode.Create(fixture, clock);

        for (var round = 0; round < 3; round++)
        {
            await Task.WhenAll(Task.Run(() => ReconcileAsync(one)), Task.Run(() => ReconcileAsync(two)), Task.Run(() => ReconcileAsync(one)), Task.Run(() => ReconcileAsync(two)));
        }

        foreach (var job in jobs)
        {
            Assert.Single(await QuartzNode.WakeUpsAsync(fixture, job.Id));
            Assert.Single(await QuartzNode.JobDetailsAsync(fixture, job.Id));
        }
    }

    [Fact]
    public async Task NodesSharingTheStoreFormOneClusterWithDistinctInstanceIds()
    {
        await using var one = QuartzNode.Create(fixture);
        await using var two = QuartzNode.Create(fixture);
        await one.StartAsync();
        await two.StartAsync();

        var deadline = DateTime.UtcNow.AddSeconds(30);
        long nodes = 0, distinct = 0;
        while (DateTime.UtcNow < deadline && distinct < 2)
        {
            nodes = await QuartzNode.ScalarAsync(fixture, "SELECT count(*) FROM quartz.qrtz_scheduler_state");
            distinct = await QuartzNode.ScalarAsync(fixture, "SELECT count(DISTINCT instance_name) FROM quartz.qrtz_scheduler_state");
            if (distinct < 2) await Task.Delay(250);
        }

        Assert.Equal((2L, 2L), (nodes, distinct));
    }

    [Fact]
    public async Task TwoClusteredNodesFiringEverySecondCreateEachOccurrencesRunOnlyOnce()
    {
        var agent = await _kit.Runs.AddAgentAsync();
        await _kit.SeenAsync(agent, DateTimeOffset.UtcNow);
        var job = await _kit.AddJobAsync(agent, cron: "* * * * * *", grace: TimeSpan.FromMinutes(5), overlap: BackupOverlapPolicy.Queue, from: DateTimeOffset.UtcNow, seconds: true);
        await using var one = QuartzNode.Create(fixture);
        await using var two = QuartzNode.Create(fixture);
        await one.WithServiceAsync(async service =>
        {
            await service.SynchronizeAsync(job.TenantId, job.Id, default);
            return 0;
        });
        await one.StartAsync();
        await two.StartAsync();

        var deadline = DateTime.UtcNow.AddSeconds(12);
        var samples = new List<int>();
        while (DateTime.UtcNow < deadline)
        {
            samples.Add((await QuartzNode.WakeUpsAsync(fixture, job.Id)).Count);
            await Task.Delay(100);
        }
        // A fired one-shot wake-up and the one already re-aimed at the next occurrence may coexist for a moment; there is never a pile-up.
        Assert.True(samples.Max() <= 2, $"wake-ups piled up: {string.Join(",", samples)}");
        Assert.Contains(1, samples);
        await one.DisposeAsync(); // both nodes stop before the history is read, so runs and occurrence records are one consistent snapshot
        await two.DisposeAsync();

        var runs = await _kit.RunsOfAsync(job);
        Assert.True(runs.Count >= 4, $"expected the wake-ups to keep firing; got {runs.Count} runs");
        var instants = runs.Select(run => run.ScheduledOccurrence!.Value).ToList();
        Assert.Equal(instants.Count, instants.Distinct().Count()); // one run per logical occurrence, whichever node fired
        var occurrences = await _kit.OccurrencesAsync(job);
        Assert.Equal(instants.Count, occurrences.Count(o => o.Outcome == BackupOccurrenceOutcome.RunCreated));
        Assert.All(occurrences, o => Assert.Equal(1, occurrences.Count(other => other.ScheduledAt == o.ScheduledAt)));
    }
}
