using BackupPlatform.Domain.Backups;

namespace BackupPlatform.Application.Backups;

/// <summary>
/// Backend-wide scheduling policy (configuration section <c>Backups:Scheduling</c>). Correctness never depends on the scheduler library:
/// the database decides what is due, and <see cref="ReconcileInterval"/> bounds how late anything is noticed if a wake-up is lost.
/// </summary>
public sealed class BackupSchedulingOptions
{
    /// <summary>Master switch for the scheduler runtime (the wake-up scheduler and the reconcile/sweep loop). Schedules can still be edited when off.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// How often the backend sweeps: starts every due occurrence it was not woken for, drops expired unclaimed scheduled runs, and
    /// reconciles the scheduler's triggers against the jobs. The first sweep runs at startup.
    /// </summary>
    public TimeSpan ReconcileInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Upper bound of due jobs evaluated, and of unclaimed runs expired, per sweep.</summary>
    public int SweepBatchSize { get; init; } = 200;

    /// <summary>Grace period of a schedule whose request does not state one.</summary>
    public TimeSpan DefaultMissedRunGrace { get; init; } = BackupSchedule.DefaultGracePeriod;

    /// <summary>Accept the six-field cron form with leading seconds. Meant for development and tests; production schedules are minute-granular.</summary>
    public bool AllowSecondPrecisionCron { get; init; }

    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (ReconcileInterval < TimeSpan.FromSeconds(1)) problems.Add("ReconcileInterval must be at least one second.");
        if (SweepBatchSize < 1) problems.Add("SweepBatchSize must be at least 1.");
        if (DefaultMissedRunGrace < BackupSchedule.MinimumGracePeriod || DefaultMissedRunGrace > BackupSchedule.MaximumGracePeriod)
            problems.Add("DefaultMissedRunGrace must be between 1 minute and 7 days.");
        return problems;
    }

    public void ThrowIfInvalid()
    {
        var problems = Validate();
        if (problems.Count != 0) throw new InvalidOperationException("Invalid backup scheduling options: " + string.Join(" ", problems));
    }
}
