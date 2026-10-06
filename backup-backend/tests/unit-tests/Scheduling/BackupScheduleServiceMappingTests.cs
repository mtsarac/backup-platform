using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using static BackupPlatform.UnitTests.BackupRunTestSupport;

namespace BackupPlatform.UnitTests.Scheduling;

/// <summary>Request-to-domain mapping, the wake-up a job needs, and the read model, with no store and no scheduler involved.</summary>
public sealed class BackupScheduleServiceMappingTests
{
    private static readonly DateTimeOffset Clock = new(2026, 10, 5, 8, 30, 0, TimeSpan.Zero);
    private static readonly BackupSchedulingOptions Options = new();

    private static BackupJob Job(BackupSchedule? schedule, BackupOverlapPolicy overlap = BackupOverlapPolicy.Skip) =>
        new(Guid.NewGuid(), TenantId.New(), "job", AgentId.New(), Configuration(), Now, null, overlap, schedule);

    [Fact]
    public void OmittedMembersTakeTheConfiguredDefaults()
    {
        var options = new BackupSchedulingOptions { DefaultMissedRunGrace = TimeSpan.FromMinutes(45) };
        var schedule = BackupScheduleService.ToDomain(new BackupScheduleRequest(CronExpression: "0 3 * * *", TimeZone: "UTC"), null, Clock, options);
        Assert.True(schedule.Enabled);
        Assert.Equal(TimeSpan.FromMinutes(45), schedule.MissedRunGracePeriod);
        Assert.Equal(Clock, schedule.EffectiveFrom);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1.0)]
    [InlineData(59.0)]
    [InlineData(1e20)]
    public void UnusableGraceNumbersAreRejectedAsInvalidGraceNeverAsACrash(double seconds)
    {
        var rejected = Assert.Throws<InvalidBackupScheduleException>(() =>
            BackupScheduleService.ToDomain(new BackupScheduleRequest(true, "0 3 * * *", "UTC", seconds), null, Clock, Options));
        Assert.Equal("invalid_grace", rejected.Code);
    }

    [Fact]
    public void TheSecondsSwitchComesFromTheOptionsNotTheRequest()
    {
        var request = new BackupScheduleRequest(true, "*/10 * * * * *", "UTC", 60);
        Assert.Throws<InvalidBackupScheduleException>(() => BackupScheduleService.ToDomain(request, null, Clock, Options));
        Assert.NotNull(BackupScheduleService.ToDomain(request, null, Clock, new BackupSchedulingOptions { AllowSecondPrecisionCron = true }));
    }

    [Fact]
    public void AStoredSecondsScheduleStillWorksAfterTheSwitchIsTurnedOff()
    {
        var stored = BackupSchedule.Create(true, "*/10 * * * * *", "UTC", TimeSpan.FromMinutes(1), Clock, allowSeconds: true);
        Assert.Equal(Clock.AddSeconds(10), stored.NextOccurrenceAfter(Clock));
    }

    [Fact]
    public void EditingAnExistingScheduleKeepsItsAnchorUnlessTheOccurrencesChange()
    {
        var existing = BackupSchedule.Create(true, "0 3 * * *", "UTC", TimeSpan.FromMinutes(30), Now);
        var sameCron = BackupScheduleService.ToDomain(new BackupScheduleRequest(true, "0 3 * * *", "UTC", 7200), existing, Clock, Options);
        var newCron = BackupScheduleService.ToDomain(new BackupScheduleRequest(true, "0 4 * * *", "UTC", 7200), existing, Clock, Options);
        Assert.Equal(Now, sameCron.EffectiveFrom);
        Assert.Equal(Clock, newCron.EffectiveFrom);
    }

    [Fact]
    public void AJobNeedsAWakeUpOnlyWhileItIsScheduledAndItIsAimedAtItsNextOccurrence()
    {
        var schedule = BackupSchedule.Create(true, "0 * * * *", "UTC", TimeSpan.FromMinutes(30), Now);
        var scheduled = Job(schedule);
        var target = BackupScheduleService.TargetFor(scheduled, Clock);
        Assert.NotNull(target);
        Assert.Equal((scheduled.Id, scheduled.TenantId, new DateTimeOffset(2026, 10, 5, 9, 0, 0, TimeSpan.Zero), schedule.Fingerprint), (target.JobId, target.TenantId, target.FireAt, target.Fingerprint));

        Assert.Null(BackupScheduleService.TargetFor(Job(null), Clock));
        var disabled = Job(schedule);
        disabled.Disable();
        Assert.Null(BackupScheduleService.TargetFor(disabled, Clock));
        Assert.Null(BackupScheduleService.TargetFor(Job(BackupSchedule.Create(false, "0 * * * *", "UTC", null, Now)), Clock));
    }

    [Fact]
    public void TheViewShowsTheNextOccurrenceOnlyForAScheduledJobAndNeverTheWakeUp()
    {
        var schedule = BackupSchedule.Create(true, "30 3 * * *", "Europe/Istanbul", TimeSpan.FromMinutes(90), Now);
        var view = BackupOrchestrator.ToView(Job(schedule, BackupOverlapPolicy.Queue), Clock);
        var shown = view.Schedule!;
        Assert.Equal((true, "30 3 * * *", "Europe/Istanbul", 5400d, "Queue"), (shown.Enabled, shown.CronExpression, shown.TimeZone, shown.MissedRunGraceSeconds, shown.OverlapPolicy));
        Assert.Equal(new DateTimeOffset(2026, 10, 6, 0, 30, 0, TimeSpan.Zero), shown.NextOccurrenceAt);

        var job = Job(schedule);
        job.Disable();
        Assert.Null(BackupOrchestrator.ToView(job, Clock).Schedule!.NextOccurrenceAt);
        Assert.Null(BackupOrchestrator.ToView(Job(null), Clock).Schedule);
        Assert.DoesNotContain("Fingerprint", string.Join(",", typeof(BackupScheduleView).GetProperties().Select(p => p.Name)));
    }

    [Fact]
    public void ARunViewReportsTheOccurrenceTheDelayAndEachAttemptsTrigger()
    {
        var job = Job(BackupSchedule.Create(true, "0 * * * *", "UTC", TimeSpan.FromMinutes(30), Now));
        var run = BackupRun.ForScheduledOccurrence(Guid.NewGuid(), job, Now.AddHours(1), Now.AddHours(1).AddSeconds(12));
        run.QueueFirstAttempt(Guid.NewGuid(), run.CreatedAt);
        Fail(run, BackupFailureCodes.DatabaseUnavailable, Now.AddHours(1).AddMinutes(1));
        Assert.True(run.StartScheduledRetry(Guid.NewGuid(), Now.AddHours(2)));

        var view = BackupOrchestrator.ToView(run);

        Assert.Equal(("Scheduled", Now.AddHours(1), 12d), (view.TriggerType, view.ScheduledFor, view.ScheduleDelaySeconds));
        Assert.Equal(["Scheduled", "Retry"], view.Attempts.OrderBy(a => a.Number).Select(a => a.Trigger));
        var manual = BackupOrchestrator.ToView(NewRun());
        Assert.Equal(("Manual", null, null), (manual.TriggerType, manual.ScheduledFor, manual.ScheduleDelaySeconds));
        Assert.Equal("Manual", Assert.Single(manual.Attempts).Trigger);
    }

    [Fact]
    public void ARunThatExpiredUnclaimedHasAUserSafeReason()
    {
        var job = Job(BackupSchedule.Create(true, "0 * * * *", "UTC", TimeSpan.FromMinutes(30), Now));
        var run = BackupRun.ForScheduledOccurrence(Guid.NewGuid(), job, Now.AddHours(1), Now.AddHours(1));
        run.QueueFirstAttempt(Guid.NewGuid(), run.CreatedAt);
        Assert.True(run.ExpireUnclaimed(Now.AddHours(3)));

        var view = BackupOrchestrator.ToView(run);

        Assert.Equal((BackupFailureCodes.ScheduleGraceExpired, "Cancelled"), (view.ReasonCode, view.Status));
        Assert.Contains("grace period", view.Reason);
    }

    [Theory]
    [InlineData("00:00:00.5")]
    [InlineData("00:00:00")]
    public void ASweepIntervalBelowOneSecondIsAConfigurationError(string interval)
    {
        var options = new BackupSchedulingOptions { ReconcileInterval = TimeSpan.Parse(interval, System.Globalization.CultureInfo.InvariantCulture) };
        Assert.Contains(options.Validate(), problem => problem.Contains("ReconcileInterval"));
        Assert.Throws<InvalidOperationException>(options.ThrowIfInvalid);
    }

    [Fact]
    public void DefaultSchedulingOptionsAreValid()
    {
        Assert.Empty(new BackupSchedulingOptions().Validate());
        Assert.Contains(new BackupSchedulingOptions { DefaultMissedRunGrace = TimeSpan.FromSeconds(10) }.Validate(), p => p.Contains("DefaultMissedRunGrace"));
        Assert.Contains(new BackupSchedulingOptions { SweepBatchSize = 0 }.Validate(), p => p.Contains("SweepBatchSize"));
    }
}
