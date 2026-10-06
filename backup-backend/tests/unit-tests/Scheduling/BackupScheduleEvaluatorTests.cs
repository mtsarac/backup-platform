using BackupPlatform.Domain.Backups;

namespace BackupPlatform.UnitTests.Scheduling;

/// <summary>
/// The missed-run, grace, overlap and agent-availability rules as one pure function. Time is always an argument: nothing here reads a clock.
/// </summary>
public sealed class BackupScheduleEvaluatorTests
{
    private static DateTimeOffset At(int hour, int minute = 0, int day = 5) => new(2026, 10, day, hour, minute, 0, TimeSpan.Zero);

    private static BackupSchedule Hourly(TimeSpan grace, DateTimeOffset? from = null, string cron = "0 * * * *", string zone = "UTC") =>
        BackupSchedule.Create(true, cron, zone, grace, from ?? new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));

    private static BackupSchedulePlan Plan(BackupSchedule schedule, DateTimeOffset now, DateTimeOffset? last = null, bool jobEnabled = true,
        BackupOverlapPolicy overlap = BackupOverlapPolicy.Skip, bool agent = true, Guid? active = null) =>
        BackupScheduleEvaluator.Plan(new BackupScheduleFacts(schedule, jobEnabled, overlap, last, agent, active, now));

    private static OccurrenceResolution Only(BackupSchedulePlan plan, BackupOccurrenceOutcome outcome) => Assert.Single(plan.Resolutions, r => r.Outcome == outcome);

    // ---- within / outside grace -------------------------------------------------------------------------------------------------

    [Fact]
    public void BackendReturnsInsideGraceTheMissedOccurrenceRuns()
    {
        // scheduled 10:00, back at 10:20, grace 30m
        var plan = Plan(Hourly(TimeSpan.FromMinutes(30)), At(10, 20), last: At(9));
        Assert.Equal(At(10), plan.CreateRunFor);
        Assert.Empty(plan.Resolutions);
        Assert.Null(plan.Deferred);
    }

    [Fact]
    public void BackendReturnsOutsideGraceNothingRunsAndTheMissIsRecorded()
    {
        // scheduled 10:00, back at 10:45, grace 30m
        var plan = Plan(Hourly(TimeSpan.FromMinutes(30)), At(10, 45), last: At(9));
        Assert.Null(plan.CreateRunFor);
        var missed = Assert.Single(plan.Resolutions);
        Assert.Equal(At(10), missed.ScheduledAt);
        Assert.Equal(BackupOccurrenceOutcome.MissedGraceExpired, missed.Outcome);
        Assert.Equal(BackupOccurrenceReasons.GraceElapsed, missed.Reason);
    }

    [Fact]
    public void LatenessExactlyEqualToGraceStillRunsAndOneSecondMoreDoesNot()
    {
        var schedule = Hourly(TimeSpan.FromMinutes(30));
        Assert.Equal(At(10), Plan(schedule, At(10, 30), last: At(9)).CreateRunFor);
        Assert.Null(Plan(schedule, At(10, 30).AddSeconds(1), last: At(9)).CreateRunFor);
    }

    [Fact]
    public void AnOccurrenceThatJustBecameDueRunsWithNoDelay()
    {
        var plan = Plan(Hourly(TimeSpan.FromMinutes(30)), At(10), last: At(9));
        Assert.Equal(At(10), plan.CreateRunFor);
    }

    [Fact]
    public void NothingDueDecidesNothing()
    {
        var plan = Plan(Hourly(TimeSpan.FromMinutes(30)), At(10, 59), last: At(10));
        Assert.Same(BackupSchedulePlan.Nothing, plan);
    }

    // ---- several missed occurrences ---------------------------------------------------------------------------------------------

    [Fact]
    public void SeveralMissedOccurrencesOnlyTheLatestEligibleOneRuns()
    {
        // 08:00, 09:00, 10:00 missed; back at 10:20 with 90m grace.
        var plan = Plan(Hourly(TimeSpan.FromMinutes(90)), At(10, 20), last: At(7));
        Assert.Equal(At(10), plan.CreateRunFor);
        // 08:00 is 2h20m old (past grace); 09:00 is 1h20m old (within grace) but a later one supersedes it. Neither runs.
        var eight = Assert.Single(plan.Resolutions, r => r.ScheduledAt == At(8));
        Assert.Equal(BackupOccurrenceOutcome.MissedGraceExpired, eight.Outcome);
        var nine = Assert.Single(plan.Resolutions, r => r.ScheduledAt == At(9));
        Assert.Equal(BackupOccurrenceOutcome.MissedSuperseded, nine.Outcome);
        Assert.Equal(BackupOccurrenceReasons.SupersededByLater, nine.Reason);
        Assert.Equal(2, plan.Resolutions.Count);
    }

    [Fact]
    public void HourlyScheduleThreeHoursDownWithNinetyMinutesGraceRunsOneBackupNotThree()
    {
        var plan = Plan(Hourly(TimeSpan.FromMinutes(90)), At(12, 5), last: At(9));
        Assert.Equal(At(12), plan.CreateRunFor);
        Assert.Equal([At(10), At(11)], plan.Resolutions.Select(r => r.ScheduledAt));
        Assert.DoesNotContain(plan.Resolutions, r => r.Outcome == BackupOccurrenceOutcome.RunCreated);
    }

    [Fact]
    public void SeveralMissedAllPastGraceNoneRunsAndAllAreRecordedExpired()
    {
        var plan = Plan(Hourly(TimeSpan.FromMinutes(30)), At(13, 45), last: At(8));
        Assert.Null(plan.CreateRunFor);
        Assert.Equal([At(9), At(10), At(11), At(12), At(13)], plan.Resolutions.Select(r => r.ScheduledAt));
        Assert.All(plan.Resolutions.Take(4), r => Assert.Equal(BackupOccurrenceOutcome.MissedGraceExpired, r.Outcome));
        Assert.Equal(BackupOccurrenceOutcome.MissedGraceExpired, plan.Resolutions[^1].Outcome);
    }

    [Fact]
    public void AMinuteScheduleAfterALongOutageNeverStormsAndRecordsAtMostTheCap()
    {
        var schedule = Hourly(TimeSpan.FromMinutes(10), cron: "* * * * *");
        var now = At(12);
        var plan = Plan(schedule, now, last: now.AddDays(-5));
        Assert.Equal(now, plan.CreateRunFor);
        Assert.True(plan.Resolutions.Count <= BackupScheduleEvaluator.MaximumResolutionsPerEvaluation);
        Assert.DoesNotContain(plan.Resolutions, r => r.ScheduledAt == now);
        Assert.Equal(plan.Resolutions.OrderBy(r => r.ScheduledAt).Select(r => r.ScheduledAt), plan.Resolutions.Select(r => r.ScheduledAt));
    }

    [Fact]
    public void QueuePolicyAfterALongOutageStillCreatesOnlyTheLatestRun()
    {
        var schedule = Hourly(TimeSpan.FromHours(2), cron: "*/5 * * * *");
        var plan = Plan(schedule, At(12, 3), last: At(0), overlap: BackupOverlapPolicy.Queue, active: Guid.NewGuid());
        Assert.Equal(At(12), plan.CreateRunFor);
        Assert.DoesNotContain(plan.Resolutions, r => r.Outcome is BackupOccurrenceOutcome.RunCreated or BackupOccurrenceOutcome.SkippedOverlap);
    }

    [Fact]
    public void OccurrencesAlreadyDecidedAreNeverDecidedAgain()
    {
        var plan = Plan(Hourly(TimeSpan.FromMinutes(90)), At(10, 20), last: At(10));
        Assert.Same(BackupSchedulePlan.Nothing, plan);
    }

    [Fact]
    public void OccurrencesOlderThanTheLookbackAreNotEvenLookedAt()
    {
        var schedule = Hourly(TimeSpan.FromDays(7), cron: "0 0 * * *");
        var now = new DateTimeOffset(2026, 10, 30, 0, 30, 0, TimeSpan.Zero);
        var plan = Plan(schedule, now, last: null);
        Assert.All(plan.Resolutions, r => Assert.True(r.ScheduledAt > now - BackupScheduleEvaluator.Lookback));
    }

    // ---- never replay the past --------------------------------------------------------------------------------------------------

    [Fact]
    public void ANewScheduleDoesNotCatchUpOccurrencesBeforeItsAnchor()
    {
        // Created (or re-enabled) at 09:40; the 08:00 and 09:00 occurrences are history and 10:00 has not happened yet.
        var schedule = Hourly(TimeSpan.FromHours(3), from: At(9, 40));
        Assert.Same(BackupSchedulePlan.Nothing, Plan(schedule, At(9, 50), last: null));
        var plan = Plan(schedule, At(10, 5), last: null);
        Assert.Equal(At(10), plan.CreateRunFor);
        Assert.Empty(plan.Resolutions);
    }

    // ---- overlap ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void SkipPolicyWithAnActiveRunRecordsSkippedOverlapNamingTheBlockingRunAndCreatesNothing()
    {
        var blocking = Guid.NewGuid();
        var plan = Plan(Hourly(TimeSpan.FromMinutes(30)), At(10, 1), last: At(9), overlap: BackupOverlapPolicy.Skip, active: blocking);
        Assert.Null(plan.CreateRunFor);
        var skipped = Only(plan, BackupOccurrenceOutcome.SkippedOverlap);
        Assert.Equal(At(10), skipped.ScheduledAt);
        Assert.Equal(blocking, skipped.RunId);
        Assert.Equal(BackupOccurrenceReasons.ActiveRun, skipped.Reason);
    }

    [Fact]
    public void QueuePolicyWithAnActiveRunStillCreatesTheRunBehindIt()
    {
        var plan = Plan(Hourly(TimeSpan.FromMinutes(30)), At(10, 1), last: At(9), overlap: BackupOverlapPolicy.Queue, active: Guid.NewGuid());
        Assert.Equal(At(10), plan.CreateRunFor);
        Assert.Empty(plan.Resolutions);
    }

    [Fact]
    public void SkipPolicyWithoutAnActiveRunCreatesTheRun()
    {
        var plan = Plan(Hourly(TimeSpan.FromMinutes(30)), At(10, 1), last: At(9), overlap: BackupOverlapPolicy.Skip, active: null);
        Assert.Equal(At(10), plan.CreateRunFor);
    }

    [Fact]
    public void AnExpiredOccurrenceIsReportedExpiredEvenWhenOverlapWouldAlsoSkipIt()
    {
        var plan = Plan(Hourly(TimeSpan.FromMinutes(30)), At(10, 45), last: At(9), overlap: BackupOverlapPolicy.Skip, active: Guid.NewGuid());
        Assert.Equal(BackupOccurrenceOutcome.MissedGraceExpired, Assert.Single(plan.Resolutions).Outcome);
    }

    // ---- disabled ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ADisabledJobFiringAStaleWakeUpRunsNothingAndSaysWhy()
    {
        var plan = Plan(Hourly(TimeSpan.FromMinutes(30)), At(10, 1), last: At(9), jobEnabled: false);
        Assert.Null(plan.CreateRunFor);
        var decided = Assert.Single(plan.Resolutions);
        Assert.Equal(BackupOccurrenceOutcome.JobDisabled, decided.Outcome);
        Assert.Equal(BackupOccurrenceReasons.JobDisabled, decided.Reason);
        Assert.Equal(At(10), decided.ScheduledAt);
    }

    [Fact]
    public void ADisabledScheduleOnAnEnabledJobIsReportedAsAScheduleNotJobMatter()
    {
        var disabledSchedule = BackupSchedule.Create(false, "0 * * * *", "UTC", TimeSpan.FromMinutes(30), new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        var plan = Plan(disabledSchedule, At(10, 1), last: At(9));
        Assert.Null(plan.CreateRunFor);
        Assert.Equal(BackupOccurrenceReasons.ScheduleDisabled, Assert.Single(plan.Resolutions).Reason);
    }

    // ---- offline agent ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void OfflineAgentInsideGraceDefersTheOccurrenceWithoutRecordingOrCreatingAnything()
    {
        var plan = Plan(Hourly(TimeSpan.FromMinutes(30)), At(10, 5), last: At(9), agent: false);
        Assert.Null(plan.CreateRunFor);
        Assert.Equal(At(10), plan.Deferred);
        Assert.Empty(plan.Resolutions);
    }

    [Fact]
    public void TheAgentReturningInsideGraceRunsTheDeferredOccurrenceExactlyOnce()
    {
        var schedule = Hourly(TimeSpan.FromMinutes(30));
        var offline = Plan(schedule, At(10, 5), last: At(9), agent: false);
        Assert.Equal(At(10), offline.Deferred);
        // Nothing was recorded, so the next evaluation sees the same occurrence again, now with the agent back.
        var back = Plan(schedule, At(10, 25), last: At(9), agent: true);
        Assert.Equal(At(10), back.CreateRunFor);
        Assert.Null(back.Deferred);
        // And once that run is recorded, a further evaluation has nothing left to decide.
        Assert.Same(BackupSchedulePlan.Nothing, Plan(schedule, At(10, 26), last: At(10), agent: true));
    }

    [Fact]
    public void TheAgentReturningAfterGraceStartsNothingAndTheMissIsBlamedOnTheAgent()
    {
        var plan = Plan(Hourly(TimeSpan.FromMinutes(30)), At(10, 40), last: At(9), agent: false);
        Assert.Null(plan.CreateRunFor);
        Assert.Null(plan.Deferred);
        var missed = Assert.Single(plan.Resolutions);
        Assert.Equal(BackupOccurrenceOutcome.MissedGraceExpired, missed.Outcome);
        Assert.Equal(BackupOccurrenceReasons.AgentUnavailable, missed.Reason);
    }

    [Fact]
    public void AnAgentOfflineAcrossManyOccurrencesRunsOnlyTheLatestWhenItReturnsInsideGrace()
    {
        var schedule = Hourly(TimeSpan.FromMinutes(90));
        // 08:00, 09:00 and 10:00 all came due while the agent was away; it is back at 10:20.
        var plan = Plan(schedule, At(10, 20), last: At(7), agent: true);
        Assert.Equal(At(10), plan.CreateRunFor);
        Assert.DoesNotContain(plan.Resolutions, r => r.ScheduledAt == At(10));
    }

    [Fact]
    public void ASkipOverlapDecisionDoesNotWaitForAnOfflineAgent()
    {
        // The blocking run is real regardless of the agent's connectivity; the occurrence is skipped, not deferred.
        var plan = Plan(Hourly(TimeSpan.FromMinutes(30)), At(10, 5), last: At(9), agent: false, active: Guid.NewGuid());
        Assert.Null(plan.Deferred);
        Assert.Equal(BackupOccurrenceOutcome.SkippedOverlap, Assert.Single(plan.Resolutions).Outcome);
    }

    // ---- consistency with the cheap pre-filter ----------------------------------------------------------------------------------

    [Theory]
    [InlineData(10, 0)]
    [InlineData(10, 20)]
    [InlineData(10, 59)]
    [InlineData(11, 0)]
    public void HasDueOccurrenceAgreesWithWhetherThePlanDoesAnything(int hour, int minute)
    {
        var schedule = Hourly(TimeSpan.FromMinutes(30));
        var now = At(hour, minute);
        foreach (var last in new DateTimeOffset?[] { null, At(9), At(10) })
        {
            var plan = Plan(schedule, now, last: last, agent: false);
            var acts = plan.Resolutions.Count != 0 || plan.CreateRunFor is not null || plan.Deferred is not null;
            Assert.Equal(acts, BackupScheduleEvaluator.HasDueOccurrence(schedule, last, now));
        }
    }

    // ---- daylight saving --------------------------------------------------------------------------------------------------------

    [Fact]
    public void FallBackAFixedTimeThatHappensTwiceIsDecidedOnceSoItCanOnlyEverMakeOneRun()
    {
        // 01:30 New York on 2026-11-01 happens at 05:30Z and again at 06:30Z. The schedule produces only the first.
        var schedule = BackupSchedule.Create(true, "30 1 * * *", "America/New_York", TimeSpan.FromHours(3), new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        var firstPass = Plan(schedule, new DateTimeOffset(2026, 11, 1, 5, 31, 0, TimeSpan.Zero), last: new DateTimeOffset(2026, 10, 31, 5, 30, 0, TimeSpan.Zero));
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero), firstPass.CreateRunFor);
        var secondPass = Plan(schedule, new DateTimeOffset(2026, 11, 1, 6, 31, 0, TimeSpan.Zero), last: new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero));
        Assert.Same(BackupSchedulePlan.Nothing, secondPass);
    }

    [Fact]
    public void SpringForwardAFixedTimeInsideTheGapRunsOnceAtTheFirstValidInstant()
    {
        var schedule = BackupSchedule.Create(true, "30 2 * * *", "America/New_York", TimeSpan.FromHours(3), new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
        var plan = Plan(schedule, new DateTimeOffset(2026, 3, 8, 7, 5, 0, TimeSpan.Zero), last: new DateTimeOffset(2026, 3, 7, 7, 30, 0, TimeSpan.Zero));
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 7, 0, 0, TimeSpan.Zero), plan.CreateRunFor);
    }
}
