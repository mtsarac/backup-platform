using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using static BackupPlatform.UnitTests.BackupRunTestSupport;

namespace BackupPlatform.UnitTests.Scheduling;

/// <summary>A scheduled run: what it records about its occurrence, when it may still start, and the occurrence record that explains a decision.</summary>
public sealed class ScheduledBackupRunTests
{
    private static readonly DateTimeOffset Occurrence = new(2026, 10, 5, 10, 0, 0, TimeSpan.Zero);

    private static BackupJob ScheduledJob(TimeSpan? grace = null, BackupOverlapPolicy overlap = BackupOverlapPolicy.Queue) => new(Guid.NewGuid(), TenantId.New(), "job", AgentId.New(),
        Configuration(), Now, Policy(), overlap, BackupSchedule.Create(true, "0 * * * *", "UTC", grace ?? TimeSpan.FromMinutes(30), Now));

    private static BackupRun Scheduled(BackupJob? job = null, DateTimeOffset? created = null)
    {
        var run = BackupRun.ForScheduledOccurrence(Guid.NewGuid(), job ?? ScheduledJob(), Occurrence, created ?? Occurrence.AddSeconds(3));
        run.QueueFirstAttempt(Guid.NewGuid(), run.CreatedAt);
        return run;
    }

    [Fact]
    public void AScheduledRunRecordsTheOccurrenceSeparatelyFromWhenItWasActuallyCreated()
    {
        var run = Scheduled(created: Occurrence.AddSeconds(42));
        Assert.Equal(BackupTriggerTypes.Scheduled, run.TriggerType);
        Assert.Equal(Occurrence, run.ScheduledOccurrence);
        Assert.Equal(Occurrence.AddSeconds(42), run.CreatedAt);
        Assert.Equal(TimeSpan.FromSeconds(42), run.CreatedAt - run.ScheduledOccurrence);
    }

    [Fact]
    public void AScheduledRunSnapshotsTheJobPoliciesAtCreationSoLaterEditsCannotChangeHistory()
    {
        var job = ScheduledJob(overlap: BackupOverlapPolicy.Skip);
        var run = Scheduled(job);
        job.ChangeSchedule(null, BackupOverlapPolicy.Queue);
        Assert.Equal(BackupOverlapPolicy.Skip, run.OverlapPolicy);
        Assert.Equal(job.RetryPolicy, run.RetryPolicy);
        Assert.Equal(BackupTriggerTypes.Scheduled, run.TriggerType);
        Assert.Equal(Occurrence, run.ScheduledOccurrence);
    }

    [Fact]
    public void TheIdempotencyKeyIsDerivedFromTheInstantSoTheSameOccurrenceAlwaysCollides()
    {
        var job = ScheduledJob();
        var first = BackupRun.ForScheduledOccurrence(Guid.NewGuid(), job, Occurrence, Occurrence);
        var second = BackupRun.ForScheduledOccurrence(Guid.NewGuid(), job, Occurrence, Occurrence.AddMinutes(5));
        var other = BackupRun.ForScheduledOccurrence(Guid.NewGuid(), job, Occurrence.AddHours(1), Occurrence.AddHours(1));
        Assert.Equal(first.IdempotencyKey, second.IdempotencyKey);
        Assert.NotEqual(first.IdempotencyKey, other.IdempotencyKey);
        Assert.Equal("scheduled-20261005T100000000Z", first.IdempotencyKey);
    }

    [Fact]
    public void TheClaimDeadlineIsTheOccurrencePlusTheGraceNotTheCreationTime()
    {
        var run = Scheduled(ScheduledJob(TimeSpan.FromMinutes(45)), created: Occurrence.AddMinutes(10));
        Assert.Equal(Occurrence.AddMinutes(45), run.ClaimDeadline);
    }

    [Fact]
    public void AManualRunHasNoOccurrenceAndNoClaimDeadline()
    {
        var run = NewRun();
        Assert.Equal(BackupTriggerTypes.Manual, run.TriggerType);
        Assert.Null(run.ScheduledOccurrence);
        Assert.Null(run.ClaimDeadline);
    }

    [Fact]
    public void ATriggerTypeAndItsOccurrenceMustAgree()
    {
        BackupRun Build(string trigger, DateTimeOffset? occurrence, DateTimeOffset? deadline) => new(Guid.NewGuid(), TenantId.New(), Guid.NewGuid(), AgentId.New(), Configuration(),
            "k", Now, Policy(), BackupOverlapPolicy.Skip, trigger, occurrence, deadline);
        Assert.Throws<ArgumentException>(() => Build(BackupTriggerTypes.Scheduled, null, null));
        Assert.Throws<ArgumentException>(() => Build(BackupTriggerTypes.Manual, Occurrence, Occurrence.AddMinutes(30)));
        Assert.Throws<ArgumentException>(() => Build(BackupTriggerTypes.Scheduled, Occurrence, Occurrence.AddMinutes(-1)));
        Assert.Throws<ArgumentException>(() => Build(BackupTriggerTypes.Retry, null, null));
        Assert.Throws<ArgumentException>(() => Build("Whatever", null, null));
    }

    // ---- unclaimed scheduled work expires with its grace ------------------------------------------------------------------------

    [Fact]
    public void ARunNobodyClaimedBeforeItsDeadlineIsCancelledAsExpiredAndNeverStarts()
    {
        var run = Scheduled(ScheduledJob(TimeSpan.FromMinutes(30)));
        var late = Occurrence.AddMinutes(30).AddSeconds(1);
        Assert.True(run.ExpireUnclaimed(late));
        Assert.Equal(BackupStatus.Cancelled, run.Status);
        Assert.Equal(BackupFailureCodes.ScheduleGraceExpired, run.TerminalReason);
        Assert.Equal(BackupStatus.Cancelled, run.CurrentAttempt.Status);
        Assert.Equal(late, run.CompletedAt);
        Assert.Contains(run.Facts, fact => fact.Kind == "BackupScheduledRunExpired");
    }

    [Fact]
    public void ExpiryIsExactlyAtTheDeadlineNotBefore()
    {
        var run = Scheduled(ScheduledJob(TimeSpan.FromMinutes(30)));
        Assert.False(run.ExpireUnclaimed(Occurrence.AddMinutes(30)));
        Assert.Equal(BackupStatus.Pending, run.Status);
        Assert.True(run.ExpireUnclaimed(Occurrence.AddMinutes(30).AddTicks(10)));
    }

    [Fact]
    public void ExpiringTwiceIsANoOpSoSeveralWorkersCannotDoubleCancel()
    {
        var run = Scheduled();
        var late = Occurrence.AddHours(2);
        Assert.True(run.ExpireUnclaimed(late));
        var facts = run.Facts.Count;
        Assert.False(run.ExpireUnclaimed(late.AddMinutes(1)));
        Assert.Equal(facts, run.Facts.Count);
        Assert.Equal(late, run.CompletedAt);
    }

    [Fact]
    public void ARunTheAgentAlreadyClaimedIsNeverExpiredEvenPastItsDeadline()
    {
        var run = Scheduled();
        Begin(run, Occurrence.AddMinutes(5));
        Assert.False(run.ExpireUnclaimed(Occurrence.AddHours(5)));
        Assert.Equal(BackupStatus.Running, run.Status);
    }

    [Fact]
    public void ARetryAttemptIsNotBoundByTheOriginalOccurrencesGrace()
    {
        var run = Scheduled(ScheduledJob(TimeSpan.FromMinutes(30)));
        Fail(run, BackupFailureCodes.DatabaseUnavailable, Occurrence.AddMinutes(5));
        Assert.Equal(BackupStatus.RetryPending, run.Status);
        Assert.True(run.StartScheduledRetry(Guid.NewGuid(), Occurrence.AddHours(2)));
        Assert.Equal(2, run.CurrentAttemptNumber);
        Assert.False(run.ExpireUnclaimed(Occurrence.AddHours(3)));
        Assert.Equal(BackupStatus.Pending, run.Status);
    }

    [Fact]
    public void AManualRunNeverExpires()
    {
        var run = NewRun();
        Assert.False(run.ExpireUnclaimed(Now.AddYears(1)));
        Assert.Equal(BackupStatus.Pending, run.Status);
    }

    [Fact]
    public void ACancelledRunCannotBeExpiredAgain()
    {
        var run = Scheduled();
        run.RequestCancellation(Occurrence.AddMinutes(1));
        Assert.False(run.ExpireUnclaimed(Occurrence.AddHours(9)));
        Assert.Equal(BackupFailureCodes.Cancelled, run.TerminalReason);
    }

    // ---- the occurrence record --------------------------------------------------------------------------------------------------

    private static BackupScheduleOccurrence Record(BackupOccurrenceOutcome outcome, Guid? runId) => new(Guid.NewGuid(), TenantId.New(), Guid.NewGuid(), Occurrence, outcome,
        BackupOccurrenceReasons.ActiveRun, runId, Occurrence.AddSeconds(7));

    [Theory]
    [InlineData(BackupOccurrenceOutcome.RunCreated)]
    [InlineData(BackupOccurrenceOutcome.SkippedOverlap)]
    public void OutcomesThatPointAtARunRequireOne(BackupOccurrenceOutcome outcome)
    {
        Assert.Throws<ArgumentException>(() => Record(outcome, null));
        Assert.Equal(outcome, Record(outcome, Guid.NewGuid()).Outcome);
    }

    [Theory]
    [InlineData(BackupOccurrenceOutcome.MissedGraceExpired)]
    [InlineData(BackupOccurrenceOutcome.MissedSuperseded)]
    [InlineData(BackupOccurrenceOutcome.JobDisabled)]
    public void OutcomesThatCreatedNothingMustNotNameARun(BackupOccurrenceOutcome outcome)
    {
        Assert.Throws<ArgumentException>(() => Record(outcome, Guid.NewGuid()));
        Assert.Null(Record(outcome, null).RunId);
    }

    [Theory]
    [InlineData(BackupAdmissionDenialCodes.QuotaSizeUnknown)]
    [InlineData(BackupAdmissionDenialCodes.QuotaBoundedUploadUnsupported)]
    public void UnsafeQuotaAdmissionCanBeRecordedWithoutClaimingTheQuotaIsFull(string reason)
    {
        var occurrence = new BackupScheduleOccurrence(Guid.NewGuid(), TenantId.New(), Guid.NewGuid(), Occurrence,
            BackupOccurrenceOutcome.AdmissionDenied, reason, null, Occurrence.AddSeconds(7));
        Assert.Equal(reason, occurrence.Reason);
        Assert.Null(occurrence.RunId);
        Assert.True(BackupAdmissionDenialCodes.IsKnown(occurrence.Reason));
    }

    [Fact]
    public void AnUnknownOutcomeIsRejected() =>
        Assert.Throws<ArgumentException>(() => Record((BackupOccurrenceOutcome)99, null));
}
