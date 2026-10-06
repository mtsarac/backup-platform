using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Backups;
using static BackupPlatform.IntegrationTests.Scheduling.ScheduleKit;

namespace BackupPlatform.IntegrationTests.Scheduling;

/// <summary>
/// The database is authoritative and the scheduler is kept in line with it: edits push the new wake-up, a scheduler failure never undoes a
/// committed edit, and the sweep repairs whatever was lost. Uses a recording scheduler so every instruction can be asserted exactly.
/// </summary>
public sealed class ScheduleSynchronizationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private readonly ScheduleKit _kit = new(fixture);

    private static BackupScheduleRequest Request(string cron = "0 * * * *", string zone = "UTC", bool enabled = true, double grace = 1800, string? overlap = null) =>
        new(enabled, cron, zone, grace, overlap);

    [Fact]
    public async Task ChangingTheScheduleAimsTheWakeUpAtTheNextOccurrenceInTheJobsZone()
    {
        var agent = await _kit.Runs.AddAgentAsync();
        var job = await _kit.AddJobAsync(agent, scheduleEnabled: false);
        var scheduler = new RecordingCoordinator();
        var service = _kit.Service(new MutableClock(H(1, 20)), scheduler);

        var view = await service.ChangeScheduleAsync(job.TenantId, job.Id, Request("0 6 * * *", "Europe/Istanbul"), default);

        var held = Assert.Single(scheduler.Held).Value;
        Assert.Equal((job.Id, job.TenantId), (held.JobId, held.TenantId));
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 3, 0, 0, TimeSpan.Zero), held.FireAt); // 06:00 Istanbul is 03:00 UTC
        Assert.Equal(held.FireAt, view!.Schedule!.NextOccurrenceAt);
        Assert.Equal(job.Id, view.Id);
    }

    [Fact]
    public async Task EditingTheCronOrZoneReplacesTheWakeUpAndChangingOnlyTheGraceDoesNot()
    {
        var agent = await _kit.Runs.AddAgentAsync();
        var job = await _kit.AddJobAsync(agent);
        var scheduler = new RecordingCoordinator();
        var service = _kit.Service(new MutableClock(H(1, 20)), scheduler);
        await service.SynchronizeAsync(job.TenantId, job.Id, default);
        var original = scheduler.Held[job.Id];

        await service.ChangeScheduleAsync(job.TenantId, job.Id, Request(grace: 7200), default);
        Assert.Equal(original, scheduler.Held[job.Id]); // same occurrences: the same wake-up

        await service.ChangeScheduleAsync(job.TenantId, job.Id, Request("30 * * * *"), default);
        Assert.NotEqual(original.Fingerprint, scheduler.Held[job.Id].Fingerprint);
        Assert.Equal(H(1, 30), scheduler.Held[job.Id].FireAt);

        var beforeZone = scheduler.Held[job.Id];
        await service.ChangeScheduleAsync(job.TenantId, job.Id, Request("30 * * * *", "Asia/Kolkata"), default);
        Assert.NotEqual(beforeZone.Fingerprint, scheduler.Held[job.Id].Fingerprint);
        Assert.Equal("Asia/Kolkata", (await _kit.JobAsync(job)).Schedule!.TimeZoneId);
    }

    [Fact]
    public async Task DisablingRemovesTheWakeUpAndEnablingAimsAtAFutureOccurrenceOnly()
    {
        var agent = await _kit.Runs.AddAgentAsync();
        var job = await _kit.AddJobAsync(agent);
        var scheduler = new RecordingCoordinator();
        var clock = new MutableClock(H(1, 20));
        var service = _kit.Service(clock, scheduler);
        await service.SynchronizeAsync(job.TenantId, job.Id, default);
        Assert.Contains(job.Id, scheduler.Held.Keys);

        var disabled = await service.SetEnabledAsync(job.TenantId, job.Id, false, default);
        Assert.False(disabled!.Enabled);
        Assert.DoesNotContain(job.Id, scheduler.Held.Keys);
        Assert.Null(disabled.Schedule!.NextOccurrenceAt);

        clock.Advance(TimeSpan.FromHours(4)); // 05:20
        var enabled = await service.SetEnabledAsync(job.TenantId, job.Id, true, default);
        Assert.True(enabled!.Enabled);
        Assert.Equal(H(6), scheduler.Held[job.Id].FireAt);
        Assert.Equal(H(6), enabled.Schedule!.NextOccurrenceAt);
    }

    [Fact]
    public async Task DisablingOrRemovingTheScheduleDropsTheWakeUpAndAScheduleOffSwitchDoesToo()
    {
        var agent = await _kit.Runs.AddAgentAsync();
        var job = await _kit.AddJobAsync(agent);
        var scheduler = new RecordingCoordinator();
        var service = _kit.Service(new MutableClock(H(1, 20)), scheduler);
        await service.SynchronizeAsync(job.TenantId, job.Id, default);

        await service.ChangeScheduleAsync(job.TenantId, job.Id, Request(enabled: false), default);
        Assert.Empty(scheduler.Held);

        await service.ChangeScheduleAsync(job.TenantId, job.Id, Request(enabled: true), default);
        Assert.Single(scheduler.Held);

        var removed = await service.ChangeScheduleAsync(job.TenantId, job.Id, null, default);
        Assert.Empty(scheduler.Held);
        Assert.Null(removed!.Schedule);
        Assert.Null((await _kit.JobAsync(job)).Schedule);
    }

    [Fact]
    public async Task APastRunHistoryIsUntouchedByAScheduleEditOrRemoval()
    {
        var agent = await _kit.Runs.AddAgentAsync();
        await _kit.SeenAsync(agent, H(1, 1));
        var job = await _kit.AddJobAsync(agent);
        var clock = new MutableClock(H(1, 1));
        var service = _kit.Service(clock, new RecordingCoordinator());
        var created = await service.EvaluateAsync(job.TenantId, job.Id, default);
        var before = await _kit.RunAsync(job, created.CreatedRunId!.Value);

        await service.ChangeScheduleAsync(job.TenantId, job.Id, Request("15 3 * * *", "America/New_York", grace: 600, overlap: "Queue"), default);
        await service.ChangeScheduleAsync(job.TenantId, job.Id, null, default);

        var after = await _kit.RunAsync(job, before.Id);
        Assert.Equal((before.TriggerType, before.ScheduledOccurrence, before.ClaimDeadline, before.OverlapPolicy, before.CreatedAt),
            (after.TriggerType, after.ScheduledOccurrence, after.ClaimDeadline, after.OverlapPolicy, after.CreatedAt));
        Assert.Equal(before.Configuration, after.Configuration);
        Assert.Equal(BackupOverlapPolicy.Skip, after.OverlapPolicy);
        Assert.Single(await _kit.OccurrencesAsync(job));
    }

    [Fact]
    public async Task AWakeUpFromTheOldScheduleFiringAfterAnEditFindsNothingDueAndTheNewScheduleRunsOnItsOwnTimes()
    {
        var agent = await _kit.Runs.AddAgentAsync();
        await _kit.SeenAsync(agent, H(1, 6));
        var job = await _kit.AddJobAsync(agent);
        var clock = new MutableClock(H(1, 5));
        var service = _kit.Service(clock, new RecordingCoordinator());
        await service.ChangeScheduleAsync(job.TenantId, job.Id, Request("30 * * * *"), default); // edit at 01:05; the old 01:00 wake-up is stale

        clock.Advance(TimeSpan.FromMinutes(1));
        var stale = await service.EvaluateAsync(job.TenantId, job.Id, default);
        Assert.Null(stale.CreatedRunId);
        Assert.Empty(await _kit.OccurrencesAsync(job));

        await _kit.SeenAsync(agent, H(1, 31));
        clock.Advance(TimeSpan.FromMinutes(25));
        var fresh = await service.EvaluateAsync(job.TenantId, job.Id, default);
        Assert.Equal(H(1, 30), (await _kit.RunAsync(job, fresh.CreatedRunId!.Value)).ScheduledOccurrence);
    }

    // ---- the database commit must not depend on the scheduler -------------------------------------------------------------------

    [Fact]
    public async Task ASchedulerThatIsDownNeverFailsOrUndoesACommittedEditAndReconciliationRepairsItLater()
    {
        var agent = await _kit.Runs.AddAgentAsync();
        var job = await _kit.AddJobAsync(agent);
        var scheduler = new RecordingCoordinator();
        var service = _kit.Service(new MutableClock(H(1, 20)), scheduler);
        await service.SynchronizeAsync(job.TenantId, job.Id, default);
        var stale = scheduler.Held[job.Id];

        scheduler.Failing = true;
        var view = await service.ChangeScheduleAsync(job.TenantId, job.Id, Request("45 * * * *"), default);

        Assert.NotNull(view);
        Assert.Equal("45 * * * *", (await _kit.JobAsync(job)).Schedule!.CronExpression); // committed
        Assert.Equal(stale, scheduler.Held[job.Id]); // the scheduler still has the old wake-up: that is the inconsistency reconciliation exists for
        Assert.False(await service.TrySynchronizeAsync(job.TenantId, job.Id, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SynchronizeAsync(job.TenantId, job.Id, default));

        scheduler.Failing = false;
        var repair = await service.ReconcileAsync(default);
        Assert.True(repair.Rewritten >= 1);
        Assert.Equal(H(1, 45), scheduler.Held[job.Id].FireAt);
        Assert.False((await service.ReconcileAsync(default)).Changed);
    }

    [Fact]
    public async Task AnUnreachableSchedulerNeverStopsTheSweepFromStartingDueBackups()
    {
        var agent = await _kit.Runs.AddAgentAsync();
        await _kit.SeenAsync(agent, H(1, 5));
        var job = await _kit.AddJobAsync(agent);
        var scheduler = new RecordingCoordinator { Failing = true };
        var service = _kit.Service(new MutableClock(H(1, 5)), scheduler);

        var report = await service.SweepAsync(default);

        Assert.NotEmpty(report.Errors); // reconciliation failed and said so
        Assert.True(report.RunsCreated >= 1);
        Assert.Equal(H(1), Assert.Single(await _kit.RunsOfAsync(job)).ScheduledOccurrence);
    }

    [Fact]
    public async Task ALostWakeUpDoesNotLoseTheBackupBecauseTheSweepEvaluatesEveryDueJobAndRestoresTheWakeUp()
    {
        var agent = await _kit.Runs.AddAgentAsync();
        await _kit.SeenAsync(agent, H(1, 5));
        var job = await _kit.AddJobAsync(agent);
        var scheduler = new RecordingCoordinator(); // empty: the scheduler lost everything
        var service = _kit.Service(new MutableClock(H(1, 5)), scheduler);

        var report = await service.SweepAsync(default);

        Assert.Empty(report.Errors);
        Assert.Equal(H(1), Assert.Single(await _kit.RunsOfAsync(job)).ScheduledOccurrence);
        Assert.Equal(H(2), scheduler.Held[job.Id].FireAt);
    }

    [Fact]
    public async Task ReconciliationTouchesNothingWhenSchedulerAndDatabaseAlreadyAgree()
    {
        var agent = await _kit.Runs.AddAgentAsync();
        var job = await _kit.AddJobAsync(agent);
        var scheduler = new RecordingCoordinator();
        var service = _kit.Service(new MutableClock(H(1, 20)), scheduler);
        await service.ReconcileAsync(default);

        var again = await service.ReconcileAsync(default);

        Assert.False(again.Changed);
        Assert.True(again.Unchanged >= 1);
        Assert.Contains(job.Id, scheduler.Held.Keys);
    }

    [Fact]
    public async Task EvaluatingAJobReAimsItsWakeUpAtTheFollowingOccurrence()
    {
        var agent = await _kit.Runs.AddAgentAsync();
        await _kit.SeenAsync(agent, H(1, 1));
        var job = await _kit.AddJobAsync(agent);
        var scheduler = new RecordingCoordinator();
        var signal = new RecordingSignal();
        var service = _kit.Service(new MutableClock(H(1, 1)), scheduler, signal);

        var result = await service.EvaluateAsync(job.TenantId, job.Id, default);

        Assert.NotNull(result.CreatedRunId);
        Assert.Equal(H(2), scheduler.Held[job.Id].FireAt);
        Assert.Equal(agent.AgentId, Assert.Single(signal.Hints));
    }

    [Fact]
    public async Task AWakeUpForAJobThatNoLongerExistsDoesNothingAndRemovesItsWakeUp()
    {
        var agent = await _kit.Runs.AddAgentAsync();
        var scheduler = new RecordingCoordinator();
        var service = _kit.Service(new MutableClock(H(1, 1)), scheduler);
        var ghost = Guid.NewGuid();
        scheduler.Held[ghost] = new BackupScheduleTarget(agent.TenantId, ghost, "x", H(1));

        var evaluation = await service.EvaluateAsync(agent.TenantId, ghost, default);

        Assert.False(evaluation.JobFound);
        Assert.Null(evaluation.CreatedRunId);
        Assert.DoesNotContain(ghost, scheduler.Held.Keys);
    }

    [Fact]
    public async Task AnotherTenantsJobIsInvisibleToScheduleChangesAndOccurrenceHistory()
    {
        var owner = await _kit.Runs.AddAgentAsync();
        var stranger = await _kit.Runs.AddAgentAsync();
        var job = await _kit.AddJobAsync(owner);
        var service = _kit.Service(new MutableClock(H(1, 1)));

        Assert.Null(await service.ChangeScheduleAsync(stranger.TenantId, job.Id, Request("5 * * * *"), default));
        Assert.Null(await service.SetEnabledAsync(stranger.TenantId, job.Id, false, default));
        Assert.Null(await service.ListOccurrencesAsync(stranger.TenantId, job.Id, 10, default));
        var untouched = await _kit.JobAsync(job);
        Assert.True(untouched.Enabled);
        Assert.Equal("0 * * * *", untouched.Schedule!.CronExpression);
    }

    // ---- request validation -----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("nonsense", "UTC", 1800, "cronExpression", "invalid_cron")]
    [InlineData("0 * * * *", "Nowhere/Land", 1800, "timeZone", "invalid_time_zone")]
    [InlineData("0 * * * *", "UTC", 5, "missedRunGraceSeconds", "invalid_grace")]
    [InlineData("0 * * * *", "UTC", double.NaN, "missedRunGraceSeconds", "invalid_grace")]
    [InlineData("0 * * * *", "UTC", double.PositiveInfinity, "missedRunGraceSeconds", "invalid_grace")]
    public async Task AnInvalidScheduleIsRejectedBeforeAnythingIsSavedAndLeavesTheJobAsItWas(string cron, string zone, double grace, string field, string code)
    {
        var agent = await _kit.Runs.AddAgentAsync();
        var job = await _kit.AddJobAsync(agent);
        var scheduler = new RecordingCoordinator();
        var service = _kit.Service(new MutableClock(H(1, 1)), scheduler);

        var rejected = await Assert.ThrowsAsync<InvalidBackupScheduleException>(() =>
            service.ChangeScheduleAsync(job.TenantId, job.Id, Request(cron, zone, grace: grace), default));

        Assert.Equal((field, code), (rejected.Field, rejected.Code));
        var after = await _kit.JobAsync(job);
        Assert.Equal("0 * * * *", after.Schedule!.CronExpression);
        Assert.Equal(0, scheduler.Calls);
    }

    [Fact]
    public async Task OmittedScheduleMembersTakeTheDocumentedDefaults()
    {
        var agent = await _kit.Runs.AddAgentAsync();
        var job = await _kit.AddJobAsync(agent, scheduleEnabled: false);
        var options = new BackupSchedulingOptions { DefaultMissedRunGrace = TimeSpan.FromMinutes(45) };
        var service = _kit.Service(new MutableClock(H(1, 1)), new RecordingCoordinator(), options: options);

        var view = await service.ChangeScheduleAsync(job.TenantId, job.Id, new BackupScheduleRequest(CronExpression: "0 2 * * *", TimeZone: "UTC"), default);

        Assert.True(view!.Schedule!.Enabled);
        Assert.Equal(45 * 60, view.Schedule.MissedRunGraceSeconds);
        Assert.Equal("Skip", view.Schedule.OverlapPolicy);
    }
}
