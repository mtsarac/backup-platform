using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using static BackupPlatform.UnitTests.BackupRunTestSupport;

namespace BackupPlatform.UnitTests.Scheduling;

/// <summary>Cron, time zone and grace validation, and which UTC instants a schedule produces (including around daylight-saving changes).</summary>
public sealed class BackupScheduleTests
{
    private static readonly DateTimeOffset Anchor = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static BackupSchedule Schedule(string cron, string zone = "UTC", DateTimeOffset? from = null, bool seconds = false, TimeSpan? grace = null) =>
        BackupSchedule.Create(true, cron, zone, grace ?? TimeSpan.FromMinutes(30), from ?? Anchor, seconds);

    private static DateTimeOffset Utc(int year, int month, int day, int hour = 0, int minute = 0, int second = 0) => new(year, month, day, hour, minute, second, TimeSpan.Zero);

    private static List<DateTimeOffset> Between(BackupSchedule schedule, DateTimeOffset after, DateTimeOffset upTo) => schedule.OccurrencesBetween(after, upTo).ToList();

    // ---- validation -------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("0 3 * * *")]
    [InlineData("*/15 * * * *")]
    [InlineData("0 0 1 * *")]
    [InlineData("30 22 * * 1-5")]
    [InlineData("0 */6 * * MON,FRI")]
    public void ValidFiveFieldCronIsAccepted(string cron)
    {
        var schedule = Schedule(cron);
        Assert.Equal(cron, schedule.CronExpression);
        Assert.NotNull(schedule.NextOccurrenceAfter(Anchor));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("* * * *")]
    [InlineData("* * * * * * *")]
    [InlineData("61 * * * *")]
    [InlineData("* 24 * * *")]
    [InlineData("* * 32 * *")]
    [InlineData("* * * 13 *")]
    [InlineData("a b c d e")]
    [InlineData("@daily")]
    [InlineData("0\t3 * * *")]
    public void InvalidCronIsRejectedWithAStableCode(string? cron)
    {
        var rejected = Assert.Throws<InvalidBackupScheduleException>(() => Schedule(cron!));
        Assert.Equal("cronExpression", rejected.Field);
        Assert.Equal("invalid_cron", rejected.Code);
    }

    [Fact]
    public void RejectedCronMessagesNeverEchoTheParsersOwnText()
    {
        var rejected = Assert.Throws<InvalidBackupScheduleException>(() => Schedule("99 * * * *"));
        Assert.DoesNotContain("99", rejected.Message);
        Assert.DoesNotContain("Cronos", rejected.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SixFieldCronNeedsTheExplicitSecondsSwitchAndIsNeverSilentlyAccepted()
    {
        Assert.Throws<InvalidBackupScheduleException>(() => Schedule("*/5 * * * * *"));
        var allowed = Schedule("*/5 * * * * *", seconds: true);
        Assert.Equal(Utc(2026, 1, 1, 0, 0, 5), allowed.NextOccurrenceAfter(Anchor));
    }

    [Fact]
    public void CronWhitespaceIsNormalised()
    {
        Assert.Equal("0 3 * * *", Schedule("  0   3 *  * *  ").CronExpression);
    }

    [Theory]
    [InlineData("UTC")]
    [InlineData("Europe/Istanbul")]
    [InlineData("America/New_York")]
    [InlineData("Asia/Kolkata")]
    public void IanaTimeZonesAreAccepted(string zone) => Assert.Equal(zone, Schedule("0 3 * * *", zone).TimeZoneId);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Mars/Olympus_Mons")]
    [InlineData("Turkey Standard Time")]
    [InlineData("../../etc/passwd")]
    [InlineData("Europe/Istanbul;DROP")]
    [InlineData("Local")]
    public void UnknownOrNonIanaTimeZonesAreRejected(string? zone)
    {
        var rejected = Assert.Throws<InvalidBackupScheduleException>(() => Schedule("0 3 * * *", zone!));
        Assert.Equal("timeZone", rejected.Field);
        Assert.Equal("invalid_time_zone", rejected.Code);
    }

    [Fact]
    public void AnExplicitZoneIsRequiredTheServersLocalZoneIsNeverAssumed()
    {
        var rejected = Assert.Throws<InvalidBackupScheduleException>(() => BackupSchedule.Create(true, "0 3 * * *", null, null, Anchor));
        Assert.Equal("invalid_time_zone", rejected.Code);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(0)]
    [InlineData(-60)]
    [InlineData(7 * 86400 + 1)]
    public void GraceOutsideOneMinuteToSevenDaysIsRejected(int seconds)
    {
        var rejected = Assert.Throws<InvalidBackupScheduleException>(() => Schedule("0 3 * * *", grace: TimeSpan.FromSeconds(seconds)));
        Assert.Equal("missedRunGraceSeconds", rejected.Field);
        Assert.Equal("invalid_grace", rejected.Code);
    }

    [Theory]
    [InlineData(60)]
    [InlineData(7 * 86400)]
    public void GraceBoundsAreInclusive(int seconds) => Assert.Equal(TimeSpan.FromSeconds(seconds), Schedule("0 3 * * *", grace: TimeSpan.FromSeconds(seconds)).MissedRunGracePeriod);

    [Fact]
    public void OmittedGraceTakesTheDefault() =>
        Assert.Equal(BackupSchedule.DefaultGracePeriod, BackupSchedule.Create(true, "0 3 * * *", "UTC", null, Anchor).MissedRunGracePeriod);

    // ---- occurrences ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void UtcScheduleProducesUtcInstantsAndNextOccurrenceIsStrictlyAfter()
    {
        var schedule = Schedule("0 3 * * *");
        Assert.Equal(Utc(2026, 3, 10, 3), schedule.NextOccurrenceAfter(Utc(2026, 3, 10, 2, 59, 59)));
        Assert.Equal(Utc(2026, 3, 11, 3), schedule.NextOccurrenceAfter(Utc(2026, 3, 10, 3)));
    }

    [Fact]
    public void NonUtcZoneIsEvaluatedInThatZoneNotInTheServersOrUtc()
    {
        // Istanbul is UTC+3 all year: 03:00 local is 00:00 UTC.
        Assert.Equal(Utc(2026, 7, 1), Schedule("0 3 * * *", "Europe/Istanbul").NextOccurrenceAfter(Utc(2026, 6, 30, 12)));
        // New York is UTC-4 in July (EDT) and UTC-5 in January (EST).
        Assert.Equal(Utc(2026, 7, 1, 7), Schedule("0 3 * * *", "America/New_York").NextOccurrenceAfter(Utc(2026, 6, 30, 12)));
        Assert.Equal(Utc(2026, 1, 15, 8), Schedule("0 3 * * *", "America/New_York").NextOccurrenceAfter(Utc(2026, 1, 15)));
    }

    [Fact]
    public void TheSameCronInTwoZonesIsTwoDifferentInstants()
    {
        var utc = Schedule("0 3 * * *", "UTC").NextOccurrenceAfter(Utc(2026, 6, 30, 12));
        var istanbul = Schedule("0 3 * * *", "Europe/Istanbul").NextOccurrenceAfter(Utc(2026, 6, 30, 12));
        Assert.Equal(TimeSpan.FromHours(3), utc - istanbul);
    }

    [Fact]
    public void OccurrencesBetweenIsExclusiveOfTheLowerAndInclusiveOfTheUpperBound()
    {
        var schedule = Schedule("0 * * * *");
        var occurrences = Between(schedule, Utc(2026, 3, 10, 8), Utc(2026, 3, 10, 10));
        Assert.Equal([Utc(2026, 3, 10, 9), Utc(2026, 3, 10, 10)], occurrences);
    }

    [Fact]
    public void OccurrencesBeforeTheEffectiveAnchorNeverCount()
    {
        var schedule = Schedule("0 * * * *", from: Utc(2026, 3, 10, 9, 30));
        Assert.Equal([Utc(2026, 3, 10, 10)], Between(schedule, Utc(2026, 3, 10, 6), Utc(2026, 3, 10, 10)));
        Assert.Equal(Utc(2026, 3, 10, 10), schedule.NextOccurrenceAfter(Utc(2026, 3, 1)));
    }

    [Fact]
    public void AnOccurrenceExactlyAtTheAnchorBelongsToThePastAndIsNotReplayed()
    {
        var schedule = Schedule("0 * * * *", from: Utc(2026, 3, 10, 9));
        Assert.Equal([Utc(2026, 3, 10, 10)], Between(schedule, Utc(2026, 3, 10, 8), Utc(2026, 3, 10, 10)));
    }

    // ---- daylight saving --------------------------------------------------------------------------------------------------------

    [Fact]
    public void SpringForwardAFixedTimeInsideTheGapFiresOnceRightAfterTheGap()
    {
        // 2026-03-08 in New York: 02:00 EST jumps to 03:00 EDT (07:00 UTC). 02:30 local does not exist.
        var schedule = Schedule("30 2 * * *", "America/New_York");
        var transitionDay = Between(schedule, Utc(2026, 3, 8, 0), Utc(2026, 3, 9, 0));
        Assert.Equal([Utc(2026, 3, 8, 7)], transitionDay);
        // The days around it are ordinary.
        Assert.Equal([Utc(2026, 3, 7, 7, 30)], Between(schedule, Utc(2026, 3, 7, 0), Utc(2026, 3, 8, 0)));
        Assert.Equal([Utc(2026, 3, 9, 6, 30)], Between(schedule, Utc(2026, 3, 9, 0), Utc(2026, 3, 10, 0)));
    }

    [Fact]
    public void FallBackAFixedTimeThatHappensTwiceFiresOnlyAtItsFirstOccurrence()
    {
        // 2026-11-01 in New York: 02:00 EDT falls back to 01:00 EST, so 01:30 local happens at 05:30 UTC and again at 06:30 UTC.
        var schedule = Schedule("30 1 * * *", "America/New_York");
        var transitionDay = Between(schedule, Utc(2026, 11, 1, 0), Utc(2026, 11, 2, 0));
        Assert.Equal([Utc(2026, 11, 1, 5, 30)], transitionDay);
    }

    [Fact]
    public void FallBackAFrequentScheduleKeepsRunningThroughTheRepeatedHourWithDistinctInstants()
    {
        var schedule = Schedule("*/30 * * * *", "America/New_York");
        var occurrences = Between(schedule, Utc(2026, 11, 1, 4, 30), Utc(2026, 11, 1, 8));
        Assert.Equal(occurrences.Distinct().Count(), occurrences.Count);
        Assert.Equal(7, occurrences.Count);
        Assert.All(occurrences.Zip(occurrences.Skip(1)), pair => Assert.Equal(TimeSpan.FromMinutes(30), pair.Second - pair.First));
    }

    [Fact]
    public void SpringForwardAFrequentScheduleSkipsTheNonexistentHourWithoutDuplicates()
    {
        var schedule = Schedule("0 * * * *", "America/New_York");
        var occurrences = Between(schedule, Utc(2026, 3, 8, 4), Utc(2026, 3, 8, 9));
        // 05:00Z = 00:00 EST, 06:00Z = 01:00 EST, 07:00Z = 03:00 EDT (02:00 never existed), 08:00Z = 04:00 EDT, 09:00Z = 05:00 EDT.
        Assert.Equal([Utc(2026, 3, 8, 5), Utc(2026, 3, 8, 6), Utc(2026, 3, 8, 7), Utc(2026, 3, 8, 8), Utc(2026, 3, 8, 9)], occurrences);
    }

    [Theory]
    [InlineData("America/New_York", "30 2 * * *")]
    [InlineData("America/New_York", "30 1 * * *")]
    [InlineData("Europe/Berlin", "30 2 * * *")]
    [InlineData("Europe/Berlin", "30 2,3 * * *")]
    [InlineData("Australia/Lord_Howe", "15 2 * * *")]
    public void AcrossAWholeYearNoInstantIsEverProducedTwiceAndTheSequenceIsStrictlyIncreasing(string zone, string cron)
    {
        var schedule = Schedule(cron, zone);
        var occurrences = Between(schedule, Utc(2026, 1, 1), Utc(2027, 1, 1));
        Assert.Equal(occurrences.Distinct().Count(), occurrences.Count);
        Assert.Equal(occurrences.OrderBy(instant => instant), occurrences);
        // A daily fixed-time schedule fires every day of the year exactly once, DST or not.
        Assert.Equal(365, occurrences.Count - (cron.Contains(',') ? 365 : 0));
    }

    [Fact]
    public void BerlinFallBackAndSpringForwardMatchTheirUtcOffsets()
    {
        var schedule = Schedule("30 2 * * *", "Europe/Berlin");
        // 2026-03-29 02:00 CET -> 03:00 CEST (01:00 UTC): 02:30 does not exist, so it fires right after the gap.
        Assert.Equal([Utc(2026, 3, 29, 1)], Between(schedule, Utc(2026, 3, 29, 0), Utc(2026, 3, 30, 0)));
        // 2026-10-25 03:00 CEST -> 02:00 CET (01:00 UTC): 02:30 happens twice, first at 00:30 UTC.
        Assert.Equal([Utc(2026, 10, 25, 0, 30)], Between(schedule, Utc(2026, 10, 25, 0), Utc(2026, 10, 26, 0)));
    }

    // ---- changes ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ChangingTheCronOrZoneMovesTheAnchorToNowSoTheNewScheduleNeverReplaysThePast()
    {
        var original = Schedule("0 3 * * *");
        var later = Utc(2026, 5, 5, 12);
        var recron = original.Change(true, "0 4 * * *", "UTC", null, later);
        var rezone = original.Change(true, "0 3 * * *", "Europe/Istanbul", null, later);
        Assert.Equal(later, recron.EffectiveFrom);
        Assert.Equal(later, rezone.EffectiveFrom);
        Assert.Equal(Utc(2026, 5, 6, 4), recron.NextOccurrenceAfter(Anchor));
    }

    [Fact]
    public void ChangingOnlyTheGraceKeepsTheAnchorAndTheOccurrences()
    {
        var original = Schedule("0 3 * * *");
        var changed = original.Change(true, "0 3 * * *", "UTC", TimeSpan.FromHours(2), Utc(2026, 5, 5, 12));
        Assert.Equal(original.EffectiveFrom, changed.EffectiveFrom);
        Assert.Equal(TimeSpan.FromHours(2), changed.MissedRunGracePeriod);
    }

    [Fact]
    public void EnablingADisabledScheduleReanchorsButDisablingDoesNot()
    {
        var enabled = Schedule("0 3 * * *");
        var later = Utc(2026, 5, 5, 12);
        var disabled = enabled.Change(false, "0 3 * * *", "UTC", null, later);
        Assert.False(disabled.Enabled);
        Assert.Equal(enabled.EffectiveFrom, disabled.EffectiveFrom);
        var evenLater = Utc(2026, 6, 1);
        var reenabled = disabled.Change(true, "0 3 * * *", "UTC", null, evenLater);
        Assert.Equal(evenLater, reenabled.EffectiveFrom);
    }

    [Fact]
    public void RejectedChangeLeavesTheOriginalScheduleUntouched()
    {
        var original = Schedule("0 3 * * *");
        Assert.Throws<InvalidBackupScheduleException>(() => original.Change(true, "nonsense", "UTC", null, Utc(2026, 5, 5)));
        Assert.Equal("0 3 * * *", original.CronExpression);
    }

    [Fact]
    public void FingerprintFollowsCronAndZoneOnly()
    {
        var baseline = Schedule("0 3 * * *");
        Assert.Equal(baseline.Fingerprint, Schedule("0 3 * * *", grace: TimeSpan.FromHours(3), from: Utc(2026, 9, 9)).Fingerprint);
        Assert.NotEqual(baseline.Fingerprint, Schedule("0 4 * * *").Fingerprint);
        Assert.NotEqual(baseline.Fingerprint, Schedule("0 3 * * *", "Europe/Istanbul").Fingerprint);
        Assert.Equal(16, baseline.Fingerprint.Length);
    }

    // ---- the job ----------------------------------------------------------------------------------------------------------------

    private static BackupJob Job(BackupSchedule? schedule) => new(Guid.NewGuid(), TenantId.New(), "job", AgentId.New(), Configuration(), Now, null, BackupOverlapPolicy.Skip, schedule);

    [Fact]
    public void AJobIsScheduledOnlyWhileBothTheJobAndItsScheduleAreEnabled()
    {
        Assert.False(Job(null).IsScheduled);
        Assert.True(Job(Schedule("0 3 * * *")).IsScheduled);
        var offSchedule = Job(BackupSchedule.Create(false, "0 3 * * *", "UTC", null, Anchor));
        Assert.False(offSchedule.IsScheduled);
        var disabled = Job(Schedule("0 3 * * *"));
        disabled.Disable();
        Assert.False(disabled.IsScheduled);
    }

    [Fact]
    public void ReEnablingAJobNeverCatchesUpTheTimeItWasOff()
    {
        var job = Job(Schedule("0 * * * *"));
        job.Disable();
        var back = Utc(2026, 6, 1, 12, 20);
        job.Enable(back);
        Assert.Equal(back, job.Schedule!.EffectiveFrom);
        Assert.Equal(Utc(2026, 6, 1, 13), job.Schedule.NextOccurrenceAfter(Utc(2026, 1, 1)));
    }

    [Fact]
    public void EnablingAnAlreadyEnabledJobKeepsItsAnchor()
    {
        var job = Job(Schedule("0 * * * *"));
        job.Enable(Utc(2026, 6, 1));
        Assert.Equal(Anchor, job.Schedule!.EffectiveFrom);
    }

    [Fact]
    public void RemovingTheScheduleLeavesAnOnDemandJob()
    {
        var job = Job(Schedule("0 * * * *"));
        job.ChangeSchedule(null);
        Assert.Null(job.Schedule);
        Assert.False(job.IsScheduled);
    }
}
