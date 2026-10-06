namespace BackupPlatform.Domain.Backups;

/// <summary>Everything the decision needs, read by the caller under the locks that make it consistent. The evaluator itself touches no clock and no store.</summary>
/// <param name="LastRecordedOccurrence">The latest instant already decided for the job; everything up to it is settled.</param>
/// <param name="AgentAvailable">The job's agent is active and recently seen (persisted last-seen, server clock).</param>
/// <param name="ActiveRunId">A queued, retrying or executing run of the same job, if any.</param>
public sealed record BackupScheduleFacts(
    BackupSchedule Schedule, bool JobEnabled, BackupOverlapPolicy OverlapPolicy, DateTimeOffset? LastRecordedOccurrence,
    bool AgentAvailable, Guid? ActiveRunId, DateTimeOffset Now);

public sealed record OccurrenceResolution(DateTimeOffset ScheduledAt, BackupOccurrenceOutcome Outcome, string Reason, Guid? RunId = null);

/// <param name="Resolutions">Occurrences to record now.</param>
/// <param name="CreateRunFor">The single occurrence a run must be created for (recorded as RunCreated together with that run).</param>
/// <param name="Deferred">An eligible occurrence that cannot start yet (agent unavailable). It is NOT recorded: it stays due and is re-evaluated until it starts or its grace runs out.</param>
public sealed record BackupSchedulePlan(IReadOnlyList<OccurrenceResolution> Resolutions, DateTimeOffset? CreateRunFor, DateTimeOffset? Deferred)
{
    public static BackupSchedulePlan Nothing { get; } = new([], null, null);
}

/// <summary>
/// The scheduling rules, as one pure function. Given the instants that became due since the last decision it acts on the LATEST one
/// only, and only while it is within the grace period:
/// <list type="bullet">
/// <item>older due instants are never run: <see cref="BackupOccurrenceOutcome.MissedSuperseded"/>, or <see cref="BackupOccurrenceOutcome.MissedGraceExpired"/> when they are also too old;</item>
/// <item>the latest older than the grace period is <see cref="BackupOccurrenceOutcome.MissedGraceExpired"/> — no catch-up storm, whatever the overlap policy;</item>
/// <item>a disabled job or schedule records <see cref="BackupOccurrenceOutcome.JobDisabled"/> for the latest instant and runs nothing;</item>
/// <item>overlap Skip with an active run records <see cref="BackupOccurrenceOutcome.SkippedOverlap"/>; Queue creates the run behind it;</item>
/// <item>an unavailable agent defers the occurrence instead of creating a run that nobody can claim.</item>
/// </list>
/// "Within the grace period" is inclusive: lateness equal to the grace still runs.
/// </summary>
public static class BackupScheduleEvaluator
{
    /// <summary>Instants older than this are never looked at; it equals the largest grace period, so nothing that could still run is ignored.</summary>
    public static readonly TimeSpan Lookback = BackupSchedule.MaximumGracePeriod;
    /// <summary>At most this many due instants are recorded per evaluation (the most recent ones); a longer outage does not write thousands of rows.</summary>
    public const int MaximumResolutionsPerEvaluation = 25;

    public static BackupSchedulePlan Plan(BackupScheduleFacts facts)
    {
        var (schedule, jobEnabled, overlap, last, agentAvailable, activeRunId, now) = facts;
        var floor = now - Lookback;
        var after = last is { } recorded && recorded > floor ? recorded : floor;
        var due = new Queue<DateTimeOffset>(MaximumResolutionsPerEvaluation + 1);
        foreach (var occurrence in schedule.OccurrencesBetween(after, now))
        {
            if (due.Count == MaximumResolutionsPerEvaluation + 1) due.Dequeue();
            due.Enqueue(occurrence);
        }
        if (due.Count == 0) return BackupSchedulePlan.Nothing;

        var instants = due.ToArray();
        var latest = instants[^1];
        var earlier = instants[..^1];
        var grace = schedule.MissedRunGracePeriod;
        var resolutions = new List<OccurrenceResolution>(instants.Length);

        if (!jobEnabled || !schedule.Enabled)
        {
            resolutions.Add(new OccurrenceResolution(latest, BackupOccurrenceOutcome.JobDisabled, jobEnabled ? BackupOccurrenceReasons.ScheduleDisabled : BackupOccurrenceReasons.JobDisabled));
            return new BackupSchedulePlan(resolutions, null, null);
        }

        if (now - latest > grace)
        {
            var reason = agentAvailable ? BackupOccurrenceReasons.GraceElapsed : BackupOccurrenceReasons.AgentUnavailable;
            resolutions.AddRange(instants.Select(instant => new OccurrenceResolution(instant, BackupOccurrenceOutcome.MissedGraceExpired, reason)));
            return new BackupSchedulePlan(resolutions, null, null);
        }

        resolutions.AddRange(earlier.Select(instant => now - instant > grace
            ? new OccurrenceResolution(instant, BackupOccurrenceOutcome.MissedGraceExpired, agentAvailable ? BackupOccurrenceReasons.GraceElapsed : BackupOccurrenceReasons.AgentUnavailable)
            : new OccurrenceResolution(instant, BackupOccurrenceOutcome.MissedSuperseded, BackupOccurrenceReasons.SupersededByLater)));

        if (overlap == BackupOverlapPolicy.Skip && activeRunId is { } blocking)
        {
            resolutions.Add(new OccurrenceResolution(latest, BackupOccurrenceOutcome.SkippedOverlap, BackupOccurrenceReasons.ActiveRun, blocking));
            return new BackupSchedulePlan(resolutions, null, null);
        }
        return agentAvailable
            ? new BackupSchedulePlan(resolutions, latest, null)
            : new BackupSchedulePlan(resolutions, null, latest);
    }

    /// <summary>
    /// Whether <see cref="Plan"/> would do anything at <paramref name="now"/>: cheap enough to run for every schedulable job on each sweep,
    /// and exactly consistent with <see cref="Plan"/> because it asks the same question.
    /// </summary>
    public static bool HasDueOccurrence(BackupSchedule schedule, DateTimeOffset? lastRecordedOccurrence, DateTimeOffset now)
    {
        var floor = now - Lookback;
        var after = lastRecordedOccurrence is { } recorded && recorded > floor ? recorded : floor;
        return schedule.OccurrencesBetween(after, now).Any();
    }
}
