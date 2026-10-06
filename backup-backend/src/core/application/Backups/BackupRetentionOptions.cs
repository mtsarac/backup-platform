using BackupPlatform.Domain.Backups;

namespace BackupPlatform.Application.Backups;

/// <summary>
/// Backend-wide retention and cleanup policy (configuration section <c>Backups:Retention</c>). The backend is the only authority over what is
/// deleted; nothing here depends on process memory: what is due is always decided by the database and the storage, and every interval only bounds how
/// late something is noticed.
/// </summary>
public sealed class BackupRetentionOptions
{
    /// <summary>Master switch of the background workers (expiration, physical deletion, storage sweeps, occurrence pruning). The API stays available when off.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>How often the retention worker looks for expired backups and for deletions that are due.</summary>
    public TimeSpan ScanInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Upper bound of artifacts expired and of replicas claimed for deletion per batch; a cycle repeats batches while they come back full, up to <see cref="MaxBatchesPerCycle"/>.</summary>
    public int BatchSize { get; init; } = 100;

    public int MaxBatchesPerCycle { get; init; } = 20;

    // ---- system defaults for a job created without an explicit policy ----

    public bool DefaultRetentionEnabled { get; init; } = true;
    public TimeSpan DefaultKeepFor { get; init; } = TimeSpan.FromDays(30);
    public TimeSpan DefaultDeleteGracePeriod { get; init; } = TimeSpan.FromHours(24);

    // ---- operational minimums for a policy that enters through the API ----

    /// <summary>No job may keep backups for less than this: a typo must not turn a backup into a thing that expires within minutes.</summary>
    public TimeSpan MinimumKeepFor { get; init; } = TimeSpan.FromHours(1);

    /// <summary>No job may have a shorter window to undo a deletion.</summary>
    public TimeSpan MinimumDeleteGracePeriod { get; init; } = TimeSpan.FromHours(1);

    // ---- physical deletion ----

    /// <summary>How long one node owns a delete before another may take it over (the crash-recovery bound).</summary>
    public TimeSpan DeleteLeaseDuration { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Wait after the first failed delete; it doubles per failure up to <see cref="DeleteRetryMaxDelay"/>. The wait is bounded, never zero: a failing delete cannot hot-loop.</summary>
    public TimeSpan DeleteRetryInitialDelay { get; init; } = TimeSpan.FromMinutes(1);

    public TimeSpan DeleteRetryMaxDelay { get; init; } = TimeSpan.FromHours(1);

    // ---- storage sweeps (orphans and unpublished uploads) ----

    public bool StorageSweepEnabled { get; init; } = true;
    public TimeSpan StorageSweepInterval { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// A stored backup file that no replica references is only an orphan once it is older than this: a file written moments ago may belong to a
    /// completion that has not reached the backend yet.
    /// </summary>
    public TimeSpan OrphanMinimumAge { get; init; } = TimeSpan.FromHours(6);

    /// <summary>An unpublished upload (<c>.partial</c>) is only cleaned up once it has not been written to for this long. A writer touches it continuously.</summary>
    public TimeSpan PartialUploadMinimumAge { get; init; } = TimeSpan.FromHours(6);

    /// <summary>Upper bound of files deleted by one sweep, so a misconfigured storage root cannot turn one sweep into an unbounded purge.</summary>
    public int MaxSweepDeletionsPerRun { get; init; } = 1000;

    // ---- schedule occurrence history ----

    /// <summary>How long decided schedule occurrences are kept. The newest occurrence of every job is always kept: it is the scheduler's watermark.</summary>
    public TimeSpan ScheduleOccurrenceHistoryRetention { get; init; } = TimeSpan.FromDays(90);

    public TimeSpan ScheduleOccurrencePruneInterval { get; init; } = TimeSpan.FromHours(1);

    public int ScheduleOccurrencePruneBatchSize { get; init; } = 1000;

    /// <summary>Identifies this backend node in delete leases. Defaults to host name plus a per-process random part.</summary>
    public string? NodeId { get; init; }

    /// <summary>The configured node id, or a fresh host/process/random one. Call once per process and keep the result: every call without a configured id returns a different value.</summary>
    public string ResolveNodeId()
    {
        if (!string.IsNullOrWhiteSpace(NodeId)) return NodeId.Trim();
        var generated = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
        return generated.Length <= 128 ? generated : generated[^128..];
    }

    /// <summary>The policy a job gets when its request states none (or only parts of one).</summary>
    public BackupRetentionPolicy DefaultPolicy() => new() { Enabled = DefaultRetentionEnabled, KeepFor = DefaultKeepFor, DeleteGracePeriod = DefaultDeleteGracePeriod };

    /// <summary>The delay before the next try of a delete that failed <paramref name="failedAttempts"/> times.</summary>
    public TimeSpan DeleteRetryDelay(int failedAttempts)
    {
        var steps = Math.Clamp(failedAttempts - 1, 0, 30);
        var seconds = Math.Min(DeleteRetryMaxDelay.TotalSeconds, DeleteRetryInitialDelay.TotalSeconds * Math.Pow(2, steps));
        return TimeSpan.FromSeconds(seconds);
    }

    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (ScanInterval < TimeSpan.FromSeconds(1)) problems.Add("ScanInterval must be at least one second.");
        if (BatchSize is < 1 or > 1000) problems.Add("BatchSize must be between 1 and 1000.");
        if (MaxBatchesPerCycle < 1) problems.Add("MaxBatchesPerCycle must be at least 1.");
        if (MinimumKeepFor < BackupRetentionPolicy.MinimumKeepForLimit) problems.Add("MinimumKeepFor must be at least one second.");
        if (MinimumDeleteGracePeriod < BackupRetentionPolicy.MinimumDeleteGraceLimit) problems.Add("MinimumDeleteGracePeriod must be at least one second.");
        try { DefaultPolicy().Validate(); }
        catch (ArgumentException) { problems.Add("The default retention policy (DefaultKeepFor, DefaultDeleteGracePeriod) is out of range."); }
        if (DefaultRetentionEnabled && DefaultKeepFor < MinimumKeepFor) problems.Add("DefaultKeepFor must not be below MinimumKeepFor.");
        if (DefaultDeleteGracePeriod < MinimumDeleteGracePeriod) problems.Add("DefaultDeleteGracePeriod must not be below MinimumDeleteGracePeriod.");
        if (DeleteLeaseDuration < TimeSpan.FromSeconds(5)) problems.Add("DeleteLeaseDuration must be at least five seconds.");
        if (DeleteRetryInitialDelay < TimeSpan.FromSeconds(1)) problems.Add("DeleteRetryInitialDelay must be at least one second.");
        if (DeleteRetryMaxDelay < DeleteRetryInitialDelay) problems.Add("DeleteRetryMaxDelay must not be below DeleteRetryInitialDelay.");
        if (StorageSweepInterval < TimeSpan.FromSeconds(1)) problems.Add("StorageSweepInterval must be at least one second.");
        if (OrphanMinimumAge < TimeSpan.FromSeconds(1)) problems.Add("OrphanMinimumAge must be at least one second.");
        if (PartialUploadMinimumAge < TimeSpan.FromSeconds(1)) problems.Add("PartialUploadMinimumAge must be at least one second.");
        if (MaxSweepDeletionsPerRun < 1) problems.Add("MaxSweepDeletionsPerRun must be at least 1.");
        if (ScheduleOccurrenceHistoryRetention < TimeSpan.FromSeconds(1)) problems.Add("ScheduleOccurrenceHistoryRetention must be at least one second.");
        if (ScheduleOccurrencePruneInterval < TimeSpan.FromSeconds(1)) problems.Add("ScheduleOccurrencePruneInterval must be at least one second.");
        if (ScheduleOccurrencePruneBatchSize is < 1 or > 10_000) problems.Add("ScheduleOccurrencePruneBatchSize must be between 1 and 10000.");
        return problems;
    }

    public void ThrowIfInvalid()
    {
        var problems = Validate();
        if (problems.Count != 0) throw new InvalidOperationException("Invalid backup retention options: " + string.Join(" ", problems));
    }
}
