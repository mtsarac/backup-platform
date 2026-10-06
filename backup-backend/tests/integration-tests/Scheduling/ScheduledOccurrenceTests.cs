using BackupPlatform.Agents.Connectivity;
using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Backups;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using static BackupPlatform.IntegrationTests.BackupStoreHarness;
using static BackupPlatform.IntegrationTests.Scheduling.ScheduleKit;

namespace BackupPlatform.IntegrationTests.Scheduling;

/// <summary>
/// What the platform decides for each scheduled instant: grace and missed-run rules, an unavailable agent, overlap, disabled jobs and the
/// durable record of every decision. The scheduler library plays no part here: these are the database decisions it only wakes up.
/// </summary>
public sealed class ScheduledOccurrenceTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private readonly ScheduleKit _kit = new(fixture);

    private static async Task<BackupScheduleEvaluation> EvaluateAsync(ScheduleKit kit, BackupJob job, DateTimeOffset now) =>
        await kit.Store().EvaluateAsync(job.TenantId, job.Id, now, default);

    // ---- a due occurrence becomes one scheduled run ----------------------------------------------------------------------------

    [Fact]
    public async Task ADueOccurrenceCreatesOneScheduledRunThroughTheNormalPendingPipeline()
    {
        var agent = await _kit.Runs.AddAgentAsync();
        await _kit.SeenAsync(agent, H(1, 19));
        var job = await _kit.AddJobAsync(agent, grace: TimeSpan.FromMinutes(30));

        var evaluation = await EvaluateAsync(_kit, job, H(1, 20));

        Assert.NotNull(evaluation.CreatedRunId);
        Assert.Equal(agent.AgentId, evaluation.AgentToNotify);
        var run = await _kit.RunAsync(job, evaluation.CreatedRunId!.Value);
        Assert.Equal(BackupTriggerTypes.Scheduled, run.TriggerType);
        Assert.Equal(H(1), run.ScheduledOccurrence);
        Assert.Equal(H(1, 20), run.CreatedAt);
        Assert.Equal(H(1, 30), run.ClaimDeadline);
        Assert.Equal(BackupStatus.Pending, run.Status);
        var attempt = Assert.Single(run.Attempts);
        Assert.Equal((1, BackupStatus.Pending), (attempt.Number, attempt.Status));
        var occurrence = Assert.Single(await _kit.OccurrencesAsync(job));
        Assert.Equal((H(1), BackupOccurrenceOutcome.RunCreated, run.Id, H(1, 20)), (occurrence.ScheduledAt, occurrence.Outcome, occurrence.RunId, occurrence.DecidedAt));
        Assert.Equal(BackupOccurrenceReasons.RunCreated, occurrence.Reason);
        var view = BackupScheduleService.ToView(occurrence);
        Assert.Equal(20 * 60, view.DelaySeconds);
    }

    [Fact]
    public async Task NothingIsDecidedBeforeTheFirstOccurrence()
    {
        var agent = await _kit.Runs.AddAgentAsync();
        await _kit.SeenAsync(agent, H(0, 30));
        var job = await _kit.AddJobAsync(agent);

        var evaluation = await EvaluateAsync(_kit, job, H(0, 59));

        Assert.Null(evaluation.CreatedRunId);
        Assert.Empty(evaluation.Recorded);
        Assert.Empty(await _kit.OccurrencesAsync(job));
        Assert.Equal(0, await _kit.Runs.CountRunsAsync(job.Id));
    }

    [Fact]
    public async Task EvaluatingTheSameInstantAgainOrLaterNeverDecidesItTwice()
    {
        var agent = await _kit.Runs.AddAgentAsync();
        await _kit.SeenAsync(agent, H(1, 25));
        var job = await _kit.AddJobAsync(agent, overlap: BackupOverlapPolicy.Queue);
        var first = await EvaluateAsync(_kit, job, H(1, 5));

        var again = await EvaluateAsync(_kit, job, H(1, 5));
        var later = await EvaluateAsync(_kit, job, H(1, 25));

        Assert.NotNull(first.CreatedRunId);
        Assert.Null(again.CreatedRunId);
        Assert.Null(later.CreatedRunId);
        Assert.Equal(1, await _kit.Runs.CountRunsAsync(job.Id));
        Assert.Single(await _kit.OccurrencesAsync(job));
    }

    [Fact]
    public async Task ARefireAfterTheRunAlreadyFinishedStillCreatesNothingEvenUnderQueue()
    {
        var agent = await _kit.Runs.AddAgentAsync();
        await _kit.SeenAsync(agent, H(1, 20));
        var job = await _kit.AddJobAsync(agent, overlap: BackupOverlapPolicy.Queue);
        var store = _kit.Runs.Store(Limits(agent: 5));
        var created = await EvaluateAsync(_kit, job, H(1, 1));
        var claim = (await ClaimAsync(store, agent, H(1, 2)))!;
        await StartAttemptAsync(store, claim, H(1, 2, 5));
        await CompleteAsync(store, claim, Success(H(1, 2, 30)), H(1, 2, 30));
        Assert.Equal(BackupStatus.Succeeded, (await _kit.RunAsync(job, created.CreatedRunId!.Value)).Status);

        // The job is idle again, so overlap cannot be what stops a second run: only the recorded occurrence does.
        var refire = await EvaluateAsync(_kit, job, H(1, 10));

        Assert.Null(refire.CreatedRunId);
        Assert.Equal(1, await _kit.Runs.CountRunsAsync(job.Id));
    }

    [Fact]
    public async Task TheDatabaseRefusesASecondOccurrenceRecordAndASecondRunForTheSameInstantEvenIfEveryApplicationRuleIsBypassed()
    {
        var agent = await _kit.Runs.AddAgentAsync();
        await _kit.SeenAsync(agent, H(1, 5));
        var job = await _kit.AddJobAsync(agent, overlap: BackupOverlapPolicy.Queue);
        var created = await EvaluateAsync(_kit, job, H(1, 1));
        Assert.NotNull(created.CreatedRunId);

        await using (var context = fixture.CreatePlatformContext())
        {
            context.BackupScheduleOccurrences.Add(new BackupScheduleOccurrence(Guid.NewGuid(), job.TenantId, job.Id, H(1), BackupOccurrenceOutcome.MissedGraceExpired,
                BackupOccurrenceReasons.GraceElapsed, null, H(5)));
            var duplicate = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            Assert.Equal(PostgresErrorCodes.UniqueViolation, ((PostgresException)duplicate.InnerException!).SqlState);
        }
        await using (var context = fixture.CreatePlatformContext())
        {
            var rival = BackupRun.ForScheduledOccurrence(Guid.NewGuid(), await _kit.JobAsync(job), H(1), H(1, 2));
            rival.QueueFirstAttempt(Guid.NewGuid(), H(1, 2));
            context.BackupRuns.Add(rival);
            var duplicate = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            Assert.Equal(PostgresErrorCodes.UniqueViolation, ((PostgresException)duplicate.InnerException!).SqlState);
        }
        Assert.Equal(1, await _kit.Runs.CountRunsAsync(job.Id));
    }

    // ---- missed-run grace -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task BackendReturnsInsideGraceTheMissedOccurrenceRuns()
    {
        var agent = await _kit.Runs.AddAgentAsync();
        await _kit.SeenAsync(agent, H(1, 20));
        var job = await _kit.AddJobAsync(agent, grace: TimeSpan.FromMinutes(30));

        var evaluation = await EvaluateAsync(_kit, job, H(1, 20));

        var run = await _kit.RunAsync(job, evaluation.CreatedRunId!.Value);
        Assert.Equal(H(1), run.ScheduledOccurrence);
    }

    [Fact]
    public async Task BackendReturnsOutsideGraceNoRunIsCreatedNowOrLater()
    {
        var agent = await _kit.Runs.AddAgentAsync();
        await _kit.SeenAsync(agent, H(1, 44));
        var job = await _kit.AddJobAsync(agent, grace: TimeSpan.FromMinutes(30));

        var late = await EvaluateAsync(_kit, job, H(1, 45));
        var later = await EvaluateAsync(_kit, job, H(1, 46));

        Assert.Null(late.CreatedRunId);
        Assert.Null(later.CreatedRunId);
        var missed = Assert.Single(await _kit.OccurrencesAsync(job));
        Assert.Equal((H(1), BackupOccurrenceOutcome.MissedGraceExpired, BackupOccurrenceReasons.GraceElapsed), (missed.ScheduledAt, missed.Outcome, missed.Reason));
        Assert.Equal(0, await _kit.Runs.CountRunsAsync(job.Id));
        // The next occurrence is unaffected by the miss.
        await _kit.SeenAsync(agent, H(2, 5));
        Assert.NotNull((await EvaluateAsync(_kit, job, H(2, 5))).CreatedRunId);
    }

    [Fact]
    public async Task SeveralMissedOccurrencesCreateExactlyOneRunForTheLatestAndRecordTheOthersAsHistory()
    {
        var agent = await _kit.Runs.AddAgentAsync();
        await _kit.SeenAsync(agent, H(3, 19));
        var job = await _kit.AddJobAsync(agent, grace: TimeSpan.FromMinutes(90));

        var evaluation = await EvaluateAsync(_kit, job, H(3, 20));

        var run = await _kit.RunAsync(job, evaluation.CreatedRunId!.Value);
        Assert.Equal(H(3), run.ScheduledOccurrence);
        Assert.Equal(1, await _kit.Runs.CountRunsAsync(job.Id));
        var history = (await _kit.OccurrencesAsync(job)).Select(o => (o.ScheduledAt, o.Outcome)).ToArray();
        Assert.Equal([(H(1), BackupOccurrenceOutcome.MissedGraceExpired), (H(2), BackupOccurrenceOutcome.MissedSuperseded), (H(3), BackupOccurrenceOutcome.RunCreated)], history);
    }

    [Fact]
    public async Task AMinuteScheduleAfterALongOutageCreatesOneRunAndAtMostTheCapOfHistoryRows()
    {
        var agent = await _kit.Runs.AddAgentAsync();
        await _kit.SeenAsync(agent, H(24, 1));
        var job = await _kit.AddJobAsync(agent, cron: "* * * * *", grace: TimeSpan.FromMinutes(10), overlap: BackupOverlapPolicy.Queue);

        var evaluation = await EvaluateAsync(_kit, job, H(24, 1, 5));

        Assert.NotNull(evaluation.CreatedRunId);
        Assert.Equal(1, await _kit.Runs.CountRunsAsync(job.Id));
        Assert.True((await _kit.OccurrencesAsync(job)).Count <= BackupScheduleEvaluator.MaximumResolutionsPerEvaluation + 1);
        // Nothing older is revisited on the next evaluation either.
        Assert.Null((await EvaluateAsync(_kit, job, H(24, 1, 30))).CreatedRunId);
        Assert.Equal(1, await _kit.Runs.CountRunsAsync(job.Id));
    }

    [Fact]
    public async Task ABackendRestartInTheMiddleOfAnOutageCreatesExactlyOneExpectedRun()
    {
        var agent = await _kit.Runs.AddAgentAsync();
        var job = await _kit.AddJobAsync(agent, grace: TimeSpan.FromMinutes(90));
        var clock = new MutableClock(H(0, 30));
        // Backend A ran, then stopped; its scheduler state, wherever it was, is gone. 01:00 and 02:00 pass with nobody home.
        var firstBackend = _kit.Service(clock);
        Assert.Equal(0, (await firstBackend.SweepAsync(default)).RunsCreated);

        clock.Advance(TimeSpan.FromMinutes(100)); // 02:10: backend B starts, empty memory, fresh scheduler
        await _kit.SeenAsync(agent, clock.GetUtcNow());
        var secondBackend = _kit.Service(clock);
        var report = await secondBackend.SweepAsync(default);

        Assert.True(report.RunsCreated >= 1);
        Assert.Empty(report.Errors);
        var runs = await _kit.RunsOfAsync(job);
        var run = Assert.Single(runs);
        Assert.Equal(H(2), run.ScheduledOccurrence);
        Assert.Equal(BackupStatus.Pending, run.Status);
        // A later sweep, or a third backend, has nothing left to do for it.
        Assert.Equal(0, (await _kit.Service(clock).SweepAsync(default)).RunsCreated);
        Assert.Single(await _kit.RunsOfAsync(job));
    }

    // ---- disabled jobs ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ADisabledJobFiringAStaleWakeUpCreatesNoRunAndTheReasonIsRecorded()
    {
        var agent = await _kit.Runs.AddAgentAsync();
        await _kit.SeenAsync(agent, H(1, 5));
        var job = await _kit.AddJobAsync(agent);
        await _kit.DisableAsync(job);

        var evaluation = await EvaluateAsync(_kit, job, H(1, 5));

        Assert.Null(evaluation.CreatedRunId);
        var recorded = Assert.Single(await _kit.OccurrencesAsync(job));
        Assert.Equal((BackupOccurrenceOutcome.JobDisabled, BackupOccurrenceReasons.JobDisabled), (recorded.Outcome, recorded.Reason));
        Assert.Equal(0, await _kit.Runs.CountRunsAsync(job.Id));
        Assert.DoesNotContain(await _kit.Store().FindDueJobsAsync(H(2, 5), 1000, default), due => due.JobId == job.Id);
    }

    [Fact]
    public async Task ADisabledScheduleOnAnEnabledJobIsNotScheduledAndNeverRuns()
    {
        var agent = await _kit.Runs.AddAgentAsync();
        await _kit.SeenAsync(agent, H(1, 5));
        var job = await _kit.AddJobAsync(agent, scheduleEnabled: false);

        Assert.DoesNotContain(await _kit.Store().FindDueJobsAsync(H(1, 5), 1000, default), due => due.JobId == job.Id);
        Assert.DoesNotContain(await _kit.Store().ListScheduledJobsAsync(default), listed => listed.Id == job.Id);
        var evaluation = await EvaluateAsync(_kit, job, H(1, 5));

        Assert.Null(evaluation.CreatedRunId);
        Assert.Equal(BackupOccurrenceReasons.ScheduleDisabled, Assert.Single(await _kit.OccurrencesAsync(job)).Reason);
    }

    [Fact]
    public async Task ReEnablingNeverCatchesUpTheTimeTheJobWasOffAndResumesAtTheNextOccurrence()
    {
        var agent = await _kit.Runs.AddAgentAsync();
        var job = await _kit.AddJobAsync(agent, grace: TimeSpan.FromHours(6));
        await _kit.DisableAsync(job);

        await _kit.EnableAsync(job, H(5, 20)); // 01:00 .. 05:00 passed while off
        await _kit.SeenAsync(agent, H(5, 25));
        Assert.Empty((await EvaluateAsync(_kit, job, H(5, 25))).Recorded);
        Assert.Equal(0, await _kit.Runs.CountRunsAsync(job.Id));

        await _kit.SeenAsync(agent, H(6, 5));
        var evaluation = await EvaluateAsync(_kit, job, H(6, 5));
        var run = await _kit.RunAsync(job, evaluation.CreatedRunId!.Value);
        Assert.Equal(H(6), run.ScheduledOccurrence);
        Assert.Equal(1, await _kit.Runs.CountRunsAsync(job.Id));
        Assert.Equal(H(7), (await _kit.JobAsync(job)).Schedule!.NextOccurrenceAfter(H(6, 5)));
    }

    // ---- an unavailable agent ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnAgentThatHasNeverBeenSeenDefersTheOccurrenceInsteadOfCreatingAnUnclaimableRun()
    {
        var agent = await _kit.Runs.AddAgentAsync();
        var job = await _kit.AddJobAsync(agent, grace: TimeSpan.FromMinutes(30));

        var evaluation = await EvaluateAsync(_kit, job, H(1, 10));

        Assert.True(evaluation.Deferred);
        Assert.Null(evaluation.CreatedRunId);
        Assert.Empty(evaluation.Recorded);
        Assert.Equal(0, await _kit.Runs.CountRunsAsync(job.Id));
        Assert.Empty(await _kit.OccurrencesAsync(job));
        Assert.Contains(await _kit.Store().FindDueJobsAsync(H(1, 11), 1000, default), due => due.JobId == job.Id); // still due: it will be retried
    }

    [Fact]
    public async Task TheAgentReturningInsideGraceRunsTheDeferredBackupExactlyOnce()
    {
        var agent = await _kit.Runs.AddAgentAsync();
        var job = await _kit.AddJobAsync(agent, grace: TimeSpan.FromMinutes(30));
        Assert.True((await EvaluateAsync(_kit, job, H(1, 10))).Deferred);

        await _kit.SeenAsync(agent, H(1, 25));
        var back = await EvaluateAsync(_kit, job, H(1, 25));
        var again = await EvaluateAsync(_kit, job, H(1, 26));

        Assert.NotNull(back.CreatedRunId);
        Assert.Null(again.CreatedRunId);
        var run = Assert.Single(await _kit.RunsOfAsync(job));
        Assert.Equal((H(1), H(1, 25)), (run.ScheduledOccurrence!.Value, run.CreatedAt));
        Assert.Equal(H(1, 30), run.ClaimDeadline);
    }

    [Fact]
    public async Task TheAgentReturningAfterGraceStartsNothingAndTheOccurrenceIsRecordedAsLostToTheAgent()
    {
        var agent = await _kit.Runs.AddAgentAsync();
        var job = await _kit.AddJobAsync(agent, grace: TimeSpan.FromMinutes(30));
        Assert.True((await EvaluateAsync(_kit, job, H(1, 10))).Deferred);

        var expired = await EvaluateAsync(_kit, job, H(1, 40));
        await _kit.SeenAsync(agent, H(1, 41));
        var back = await EvaluateAsync(_kit, job, H(1, 41));

        Assert.False(expired.Deferred);
        Assert.Null(expired.CreatedRunId);
        Assert.Null(back.CreatedRunId);
        var missed = Assert.Single(await _kit.OccurrencesAsync(job));
        Assert.Equal((H(1), BackupOccurrenceOutcome.MissedGraceExpired, BackupOccurrenceReasons.AgentUnavailable), (missed.ScheduledAt, missed.Outcome, missed.Reason));
        Assert.Equal(0, await _kit.Runs.CountRunsAsync(job.Id));
    }

    [Fact]
    public async Task AnAgentGoneForSeveralOccurrencesRunsOnlyTheLatestWhenItReturnsInsideGrace()
    {
        var agent = await _kit.Runs.AddAgentAsync();
        var job = await _kit.AddJobAsync(agent, grace: TimeSpan.FromMinutes(90));
        Assert.True((await EvaluateAsync(_kit, job, H(1, 10))).Deferred);
        Assert.True((await EvaluateAsync(_kit, job, H(2, 10))).Deferred); // 01:00 is now superseded, 02:00 waits
        await _kit.SeenAsync(agent, H(3, 10));

        var back = await EvaluateAsync(_kit, job, H(3, 10));

        var run = await _kit.RunAsync(job, back.CreatedRunId!.Value);
        Assert.Equal(H(3), run.ScheduledOccurrence);
        Assert.Equal(1, await _kit.Runs.CountRunsAsync(job.Id));
    }

    [Fact]
    public async Task AHeartbeatThatIsMerelyLateDoesNotCostTheScheduleButASilentAgentOnlyDelaysIt()
    {
        var agent = await _kit.Runs.AddAgentAsync();
        var job = await _kit.AddJobAsync(agent, grace: TimeSpan.FromMinutes(30));
        var timeout = new AgentConnectivityOptions().OfflineTimeout;

        // Last seen just inside the offline timeout: still online, the run is created on time.
        await _kit.SeenAsync(agent, H(1, 1) - timeout + TimeSpan.FromSeconds(1));
        var onTime = await EvaluateAsync(_kit, job, H(1, 1));
        Assert.NotNull(onTime.CreatedRunId);

        // A second job whose agent last spoke a moment beyond the timeout: deferred, not lost, and it runs as soon as the agent speaks.
        var lateAgent = await _kit.Runs.AddAgentAsync();
        var lateJob = await _kit.AddJobAsync(lateAgent, grace: TimeSpan.FromMinutes(30));
        await _kit.SeenAsync(lateAgent, H(1, 1) - timeout - TimeSpan.FromSeconds(1));
        Assert.True((await EvaluateAsync(_kit, lateJob, H(1, 1))).Deferred);
        await _kit.SeenAsync(lateAgent, H(1, 3));
        Assert.NotNull((await EvaluateAsync(_kit, lateJob, H(1, 3))).CreatedRunId);
        Assert.Equal(H(1), Assert.Single(await _kit.RunsOfAsync(lateJob)).ScheduledOccurrence);
    }

    // ---- overlap ----------------------------------------------------------------------------------------------------------------

    private async Task<(StoreAgent Agent, BackupJob Job, BackupRun Active)> ActiveRunAsync(BackupOverlapPolicy overlap, string state, string cron = "0 * * * *", TimeSpan? grace = null)
    {
        var agent = await _kit.Runs.AddAgentAsync();
        var job = await _kit.AddJobAsync(agent, cron, grace: grace ?? TimeSpan.FromMinutes(30), overlap: overlap);
        var store = _kit.Runs.Store(Limits(agent: 5));
        var run = await _kit.Runs.StartRunAsync(store, job, "manual-" + state, T0);
        switch (state)
        {
            case "Pending":
                break;
            case "Running":
                await _kit.ClaimAndStartAsync(store, agent, T0.AddSeconds(1));
                break;
            case "RetryPending":
                var claim = (await _kit.ClaimAndStartAsync(store, agent, T0.AddSeconds(1)))!;
                await CompleteAsync(store, claim, Failed(BackupFailureCodes.OperationTimedOut), T0.AddSeconds(5));
                break;
            default: throw new ArgumentOutOfRangeException(nameof(state));
        }
        Assert.Equal(Enum.Parse<BackupStatus>(state), (await store.GetRunAsync(agent.TenantId, run.Id, default))!.Status);
        await _kit.SeenAsync(agent, H(1, 5));
        return (agent, job, run);
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("Running")]
    [InlineData("RetryPending")]
    public async Task SkipWithAnActiveRunCreatesNoSecondRunAndRecordsSkippedOverlapNamingTheBlockingRun(string state)
    {
        var (_, job, active) = await ActiveRunAsync(BackupOverlapPolicy.Skip, state);

        var evaluation = await EvaluateAsync(_kit, job, H(1, 5));
        var refire = await EvaluateAsync(_kit, job, H(1, 6));

        Assert.Null(evaluation.CreatedRunId);
        Assert.Null(refire.CreatedRunId);
        var skipped = Assert.Single(await _kit.OccurrencesAsync(job));
        Assert.Equal((H(1), BackupOccurrenceOutcome.SkippedOverlap, BackupOccurrenceReasons.ActiveRun, active.Id), (skipped.ScheduledAt, skipped.Outcome, skipped.Reason, skipped.RunId));
        var only = Assert.Single(await _kit.RunsOfAsync(job));
        Assert.Equal(active.Id, only.Id);
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("Running")]
    [InlineData("RetryPending")]
    public async Task QueueWithAnActiveRunCreatesAPendingScheduledRunBehindIt(string state)
    {
        var (_, job, active) = await ActiveRunAsync(BackupOverlapPolicy.Queue, state);

        var evaluation = await EvaluateAsync(_kit, job, H(1, 5));

        Assert.NotNull(evaluation.CreatedRunId);
        var runs = (await _kit.RunsOfAsync(job)).ToDictionary(run => run.Id);
        Assert.Equal(2, runs.Count);
        var queued = runs[evaluation.CreatedRunId!.Value];
        Assert.Equal((BackupStatus.Pending, BackupTriggerTypes.Scheduled, H(1)), (queued.Status, queued.TriggerType, queued.ScheduledOccurrence!.Value));
        Assert.Equal(Enum.Parse<BackupStatus>(state), runs[active.Id].Status);
        Assert.Equal(BackupOccurrenceOutcome.RunCreated, Assert.Single(await _kit.OccurrencesAsync(job)).Outcome);
    }

    [Fact]
    public async Task ARetryPendingScheduledRunBlocksTheNextOccurrenceUnderSkipBecauseTheLogicalRunIsStillActive()
    {
        var agent = await _kit.Runs.AddAgentAsync();
        var job = await _kit.AddJobAsync(agent, overlap: BackupOverlapPolicy.Skip);
        var store = _kit.Runs.Store(Limits(agent: 5));
        await _kit.SeenAsync(agent, H(1, 1));
        var nine = await EvaluateAsync(_kit, job, H(1, 1)); // the "09:00" run
        var claim = (await _kit.ClaimAndStartAsync(store, agent, H(1, 2)))!;
        await CompleteAsync(store, claim, Failed(BackupFailureCodes.DatabaseUnavailable), H(1, 3));
        Assert.Equal(BackupStatus.RetryPending, (await _kit.RunAsync(job, nine.CreatedRunId!.Value)).Status);

        await _kit.SeenAsync(agent, H(2, 1));
        var ten = await EvaluateAsync(_kit, job, H(2, 1)); // the "10:00" occurrence

        Assert.Null(ten.CreatedRunId);
        var skipped = Assert.Single(await _kit.OccurrencesAsync(job), o => o.ScheduledAt == H(2));
        Assert.Equal((BackupOccurrenceOutcome.SkippedOverlap, nine.CreatedRunId), (skipped.Outcome, skipped.RunId));
        Assert.Equal(1, await _kit.Runs.CountRunsAsync(job.Id));
        // The retry attempt itself is not an occurrence: it stays on the same run.
        Assert.Single((await _kit.RunAsync(job, nine.CreatedRunId.Value)).Attempts);
    }

    [Fact]
    public async Task ARetryPendingScheduledRunDoesNotStopQueueFromQueueingTheNextOccurrence()
    {
        var agent = await _kit.Runs.AddAgentAsync();
        var job = await _kit.AddJobAsync(agent, overlap: BackupOverlapPolicy.Queue);
        var store = _kit.Runs.Store(Limits(agent: 5));
        await _kit.SeenAsync(agent, H(1, 1));
        var nine = await EvaluateAsync(_kit, job, H(1, 1));
        var claim = (await _kit.ClaimAndStartAsync(store, agent, H(1, 2)))!;
        await CompleteAsync(store, claim, Failed(BackupFailureCodes.DatabaseUnavailable), H(1, 3));

        await _kit.SeenAsync(agent, H(2, 1));
        var ten = await EvaluateAsync(_kit, job, H(2, 1));

        Assert.NotNull(ten.CreatedRunId);
        Assert.NotEqual(nine.CreatedRunId, ten.CreatedRunId);
        Assert.Equal(2, await _kit.Runs.CountRunsAsync(job.Id));
    }

    [Fact]
    public async Task QueueAfterALongOutageWithAnActiveRunQueuesOnlyTheLatestOccurrence()
    {
        var (_, job, _) = await ActiveRunAsync(BackupOverlapPolicy.Queue, "Running", cron: "*/5 * * * *", grace: TimeSpan.FromHours(2));

        var agent = new StoreAgent(job.TenantId, job.AgentId);
        await _kit.SeenAsync(agent, H(10, 3));
        var evaluation = await EvaluateAsync(_kit, job, H(10, 3));

        Assert.NotNull(evaluation.CreatedRunId);
        var scheduled = (await _kit.RunsOfAsync(job)).Where(run => run.TriggerType == BackupTriggerTypes.Scheduled).ToArray();
        Assert.Equal(H(10), Assert.Single(scheduled).ScheduledOccurrence);
        Assert.DoesNotContain(await _kit.OccurrencesAsync(job), o => o.Outcome == BackupOccurrenceOutcome.SkippedOverlap);
    }

    // ---- concurrency limits and grace of unclaimed work -------------------------------------------------------------------------

    [Fact]
    public async Task ScheduledRunsObeyTheAgentConcurrencyLimitLikeAnyOther()
    {
        var agent = await _kit.Runs.AddAgentAsync();
        var one = await _kit.AddJobAsync(agent, grace: TimeSpan.FromHours(3));
        var two = await _kit.AddJobAsync(agent, grace: TimeSpan.FromHours(3));
        await _kit.SeenAsync(agent, H(1, 1));
        Assert.NotNull((await EvaluateAsync(_kit, one, H(1, 1))).CreatedRunId);
        Assert.NotNull((await EvaluateAsync(_kit, two, H(1, 1))).CreatedRunId);
        var store = _kit.Runs.Store(Limits(agent: 1));

        var first = await ClaimAsync(store, agent, H(1, 2));
        var second = await ClaimAsync(store, agent, H(1, 2));

        Assert.NotNull(first);
        Assert.Null(second); // two scheduled runs exist, the agent may run one
        await StartAttemptAsync(store, first!, H(1, 2, 5));
        await CompleteAsync(store, first!, Success(H(1, 2, 30)), H(1, 2, 30));
        Assert.NotNull(await ClaimAsync(store, agent, H(1, 5)));
    }

    [Fact]
    public async Task ScheduledRunsObeyTheTenantConcurrencyLimitAcrossAgents()
    {
        var tenant = await _kit.Runs.AddTenantAsync();
        var a = new StoreAgent(tenant, await _kit.Runs.AddAgentToAsync(tenant));
        var b = new StoreAgent(tenant, await _kit.Runs.AddAgentToAsync(tenant));
        var jobA = await _kit.AddJobAsync(a, grace: TimeSpan.FromHours(3));
        var jobB = await _kit.AddJobAsync(b, grace: TimeSpan.FromHours(3));
        await _kit.SeenAsync(a, H(1, 1));
        await _kit.SeenAsync(b, H(1, 1));
        await EvaluateAsync(_kit, jobA, H(1, 1));
        await EvaluateAsync(_kit, jobB, H(1, 1));
        var store = _kit.Runs.Store(Limits(agent: 1, tenant: 1));

        var first = await ClaimAsync(store, a, H(1, 2));
        var second = await ClaimAsync(store, b, H(1, 2));

        Assert.NotNull(first);
        Assert.Null(second);
    }

    [Fact]
    public async Task AScheduledRunNobodyClaimedBeforeItsDeadlineNeverStartsAndTheSweepEndsItAsExpired()
    {
        var agent = await _kit.Runs.AddAgentAsync();
        var job = await _kit.AddJobAsync(agent, grace: TimeSpan.FromMinutes(30));
        await _kit.SeenAsync(agent, H(1, 1));
        var created = await EvaluateAsync(_kit, job, H(1, 1));
        var store = _kit.Runs.Store(Limits(agent: 5));

        // Past the deadline the claim itself refuses the work, before any sweep has run.
        Assert.Null(await ClaimAsync(store, agent, H(1, 31)));
        Assert.Equal(BackupStatus.Pending, (await _kit.RunAsync(job, created.CreatedRunId!.Value)).Status);
        await _kit.Store().ExpireUnclaimedRunsAsync(H(1, 30), 100, default); // the deadline itself is still inside the grace
        Assert.Equal(BackupStatus.Pending, (await _kit.RunAsync(job, created.CreatedRunId.Value)).Status);
        Assert.True(await _kit.Store().ExpireUnclaimedRunsAsync(H(1, 31), 100, default) >= 1);

        var expired = await _kit.RunAsync(job, created.CreatedRunId.Value);
        Assert.Equal((BackupStatus.Cancelled, BackupFailureCodes.ScheduleGraceExpired), (expired.Status, expired.TerminalReason));
        await _kit.Store().ExpireUnclaimedRunsAsync(H(1, 32), 100, default); // already ended: a second sweep leaves it exactly as it was
        Assert.Equal(expired.CompletedAt, (await _kit.RunAsync(job, created.CreatedRunId.Value)).CompletedAt);
        Assert.Null(await ClaimAsync(store, agent, H(1, 33)));
    }

    [Fact]
    public async Task AScheduledRunTheAgentClaimedInTimeKeepsRunningPastItsDeadlineAndIsNeverSweptAway()
    {
        var agent = await _kit.Runs.AddAgentAsync();
        var job = await _kit.AddJobAsync(agent, grace: TimeSpan.FromMinutes(30));
        await _kit.SeenAsync(agent, H(1, 1));
        var created = await EvaluateAsync(_kit, job, H(1, 1));
        var store = _kit.Runs.Store(Limits(agent: 5));
        var claim = await _kit.ClaimAndStartAsync(store, agent, H(1, 29));
        Assert.NotNull(claim);

        await _kit.Store().ExpireUnclaimedRunsAsync(H(3), 100, default);

        Assert.Equal(BackupStatus.Running, (await _kit.RunAsync(job, created.CreatedRunId!.Value)).Status);
    }

    // ---- daylight saving, end to end through the store --------------------------------------------------------------------------

    [Fact]
    public async Task FallBackTheRepeatedHourProducesOneRunNotTwo()
    {
        var agent = await _kit.Runs.AddAgentAsync();
        var job = await _kit.AddJobAsync(agent, cron: "30 1 * * *", zone: "America/New_York", grace: TimeSpan.FromHours(3), from: new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero),
            overlap: BackupOverlapPolicy.Queue);
        var firstPass = new DateTimeOffset(2026, 11, 1, 5, 31, 0, TimeSpan.Zero);  // 01:31 EDT
        var secondPass = new DateTimeOffset(2026, 11, 1, 6, 31, 0, TimeSpan.Zero); // 01:31 EST: the same wall-clock time again
        await _kit.SeenAsync(agent, firstPass);
        var first = await EvaluateAsync(_kit, job, firstPass);
        await _kit.SeenAsync(agent, secondPass);
        var second = await EvaluateAsync(_kit, job, secondPass);

        Assert.NotNull(first.CreatedRunId);
        Assert.Null(second.CreatedRunId);
        var run = Assert.Single(await _kit.RunsOfAsync(job));
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero), run.ScheduledOccurrence);
    }

    [Fact]
    public async Task SpringForwardTheNonexistentLocalTimeProducesExactlyOneRunRightAfterTheGap()
    {
        var agent = await _kit.Runs.AddAgentAsync();
        var job = await _kit.AddJobAsync(agent, cron: "30 2 * * *", zone: "America/New_York", grace: TimeSpan.FromHours(3), from: new DateTimeOffset(2026, 3, 7, 12, 0, 0, TimeSpan.Zero),
            overlap: BackupOverlapPolicy.Queue);
        var after = new DateTimeOffset(2026, 3, 8, 7, 1, 0, TimeSpan.Zero);
        await _kit.SeenAsync(agent, after);
        var first = await EvaluateAsync(_kit, job, after);
        await _kit.SeenAsync(agent, after.AddHours(1));
        var second = await EvaluateAsync(_kit, job, after.AddHours(1));

        Assert.NotNull(first.CreatedRunId);
        Assert.Null(second.CreatedRunId);
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 7, 0, 0, TimeSpan.Zero), Assert.Single(await _kit.RunsOfAsync(job)).ScheduledOccurrence);
    }
}
