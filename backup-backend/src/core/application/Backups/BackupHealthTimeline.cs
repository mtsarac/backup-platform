using BackupPlatform.Domain.Backups;

namespace BackupPlatform.Application.Backups;

/// <summary>What a day of the home page strip shows. "Backup" is a backup run ("Yedekleme denemesi"); <c>NoBackup</c> and <c>NoJob</c> are different cells.</summary>
public static class HealthTimelineDayStatuses
{
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
    public const string Running = "Running";
    public const string NoBackup = "NoBackup";
    public const string NoJob = "NoJob";
}

public sealed record HealthTimelineDay(DateOnly Date, string Status, int AttemptCount);
public sealed record HealthTimelineJob(Guid JobId, string JobName, IReadOnlyList<HealthTimelineDay> Days);
public sealed record BackupHealthTimelineView(string TimeZone, IReadOnlyList<HealthTimelineJob> Jobs);

public sealed record HealthTimelineJobRow(Guid Id, string Name, DateTimeOffset CreatedAt);
public sealed record HealthTimelineRunRow(Guid JobId, DateTimeOffset CreatedAt, BackupStatus Status);

/// <summary>Days are local calendar dates of the caller's zone, so a 23 or 25 hour day is one day and the window is always 14 of them, today included.</summary>
public static class BackupHealthTimeline
{
    public const int DayCount = 14;

    /// <summary>Runs older than this can never fall in the window whatever the zone's offsets and DST shifts.</summary>
    public static DateTimeOffset RunsSince(DateTimeOffset now) => now.AddDays(-DayCount - 2);

    public static IReadOnlyList<HealthTimelineJob> Build(TimeZoneInfo zone, DateTimeOffset now, IReadOnlyList<HealthTimelineJobRow> jobs, IReadOnlyList<HealthTimelineRunRow> runs)
    {
        var today = LocalDate(zone, now);
        var window = Enumerable.Range(1 - DayCount, DayCount).Select(offset => today.AddDays(offset)).ToArray();
        var byJob = runs.ToLookup(run => run.JobId);
        return jobs.Select(job =>
        {
            var created = LocalDate(zone, job.CreatedAt);
            var perDay = byJob[job.Id].GroupBy(run => LocalDate(zone, run.CreatedAt)).ToDictionary(group => group.Key, group => group.Select(run => run.Status).ToArray());
            var days = window.Select(date =>
            {
                if (date < created) return new HealthTimelineDay(date, HealthTimelineDayStatuses.NoJob, 0);
                if (!perDay.TryGetValue(date, out var statuses)) return new HealthTimelineDay(date, HealthTimelineDayStatuses.NoBackup, 0);
                // One good backup makes the day, even when a second one failed; "running" only describes today.
                var status = statuses.Contains(BackupStatus.Succeeded) ? HealthTimelineDayStatuses.Succeeded
                    : date == today && statuses.Any(IsActive) ? HealthTimelineDayStatuses.Running
                    : HealthTimelineDayStatuses.Failed;
                return new HealthTimelineDay(date, status, statuses.Length);
            }).ToArray();
            return new HealthTimelineJob(job.Id, job.Name, days);
        }).ToArray();
    }

    private static DateOnly LocalDate(TimeZoneInfo zone, DateTimeOffset instant) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);

    private static bool IsActive(BackupStatus status) => status is BackupStatus.Pending or BackupStatus.Leased or BackupStatus.Running or BackupStatus.RetryPending or BackupStatus.Paused;
}
