using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;

namespace BackupPlatform.Domain.Backups;

/// <summary>What the platform decided for one scheduled instant. Every decision is final and recorded exactly once per (job, instant).</summary>
public enum BackupOccurrenceOutcome
{
    /// <summary>A scheduled <see cref="BackupRun"/> was created for it.</summary>
    RunCreated = 1,
    /// <summary>The job's overlap policy is Skip and another run of the job was still active; nothing was created.</summary>
    SkippedOverlap = 2,
    /// <summary>It was not acted on within the job's missed-run grace period and will never run.</summary>
    MissedGraceExpired = 3,
    /// <summary>A later occurrence became due before this one ran; only the most recent eligible occurrence is acted on, so it was dropped.</summary>
    MissedSuperseded = 4,
    /// <summary>The job or its schedule was disabled when the occurrence became due.</summary>
    JobDisabled = 5,
    /// <summary>The commercial layer refused a new run for it (subscription or quota). Nothing was created; the reason is the denial code.</summary>
    AdmissionDenied = 6
}

/// <summary>Stable codes that explain an occurrence outcome.</summary>
public static class BackupOccurrenceReasons
{
    public const string RunCreated = "run_created";
    public const string ActiveRun = "active_run";
    public const string AgentUnavailable = "agent_unavailable";
    public const string GraceElapsed = "grace_elapsed";
    public const string SupersededByLater = "superseded_by_later_occurrence";
    public const string JobDisabled = "job_disabled";
    public const string ScheduleDisabled = "schedule_disabled";
    // A denied occurrence's reason is the admission denial code itself (BackupAdmissionDenialCodes), so the explanation a user sees is the same everywhere.
}

/// <summary>Why a NEW backup run was refused. Stable, user-visible codes; a retry attempt of an already admitted run is never refused.</summary>
public static class BackupAdmissionDenialCodes
{
    public const string SubscriptionRequired = "subscription_required";
    public const string QuotaExceeded = "quota_exceeded";
    public const string QuotaSizeUnknown = "quota_size_unknown";
    public const string QuotaBoundedUploadUnsupported = "quota_bounded_upload_unsupported";
    /// <summary>The job's storage connection is disabled: new runs may not target it. Existing replicas on it are unaffected.</summary>
    public const string StorageConnectionDisabled = "storage_connection_disabled";
    /// <summary>The job names a storage connection that does not exist, is another tenant's, or serves another provider.</summary>
    public const string StorageConnectionUnavailable = "storage_connection_unavailable";
    /// <summary>
    /// Historic: occurrences recorded while multi-destination execution did not exist yet. Kept recognized (those rows are history, and are NEVER re-run
    /// automatically); nothing produces it any more.
    /// </summary>
    public const string MultiDestinationExecutionNotSupported = "multi_destination_execution_not_supported";
    /// <summary>The job has several destinations and its agent does not advertise multi-destination execution. Fail-closed for manual and scheduled runs alike.</summary>
    public const string AgentMultiDestinationUnsupported = "agent_multi_destination_unsupported";
    public const string AgentDirectStorageUnsupported = "agent_direct_storage_unsupported";
    /// <summary>The job's destinations cannot all be executed as configured: the same connection twice after resolution, or a destination type or the 3-2-1 check its agent does not support.</summary>
    public const string DestinationCombinationUnsupported = "destination_combination_unsupported";
    /// <summary>The job enforces 3-2-1, but its resolved destinations do not span two media classes with one verified off-site copy.</summary>
    public const string ThreeTwoOneUnsatisfied = "three_two_one_unsatisfied";
    public const string AgentEngineUnsupported = "agent_engine_unsupported";

    public static bool IsKnown(string? code) => code is SubscriptionRequired or QuotaExceeded or QuotaSizeUnknown or StorageConnectionDisabled or StorageConnectionUnavailable
        or MultiDestinationExecutionNotSupported or AgentMultiDestinationUnsupported or AgentEngineUnsupported or DestinationCombinationUnsupported or ThreeTwoOneUnsatisfied or QuotaBoundedUploadUnsupported;
}

public static class BackupTriggerTypes
{
    public const string Manual = "Manual";
    public const string Scheduled = "Scheduled";
    /// <summary>Not a run type: a retry is a further attempt of the run that failed. Reported as the trigger of attempts after the first.</summary>
    public const string Retry = "Retry";
}

/// <summary>
/// The durable, immutable record of one decided scheduled instant. It is the scheduler's idempotency memory and its explanation: why a
/// backup did, or did not, start for that time. The unique (job, instant) key makes a second decision for the same occurrence impossible
/// whichever node, trigger refire or reconciliation produced it.
/// </summary>
public sealed class BackupScheduleOccurrence : ITenantOwned
{
    private BackupScheduleOccurrence() { TenantId = null!; Outcome = default; Reason = null!; }
    public BackupScheduleOccurrence(Guid id, TenantId tenantId, Guid jobId, DateTimeOffset scheduledAt, BackupOccurrenceOutcome outcome, string reason, Guid? runId, DateTimeOffset decidedAt)
    {
        BackupGuard.Id(id);
        BackupGuard.Id(jobId);
        ArgumentNullException.ThrowIfNull(tenantId);
        if (!Enum.IsDefined(outcome)) throw new ArgumentException("Unknown occurrence outcome.");
        BackupGuard.Reference(reason);
        if ((outcome is BackupOccurrenceOutcome.RunCreated or BackupOccurrenceOutcome.SkippedOverlap) != runId.HasValue)
            throw new ArgumentException("RunCreated and SkippedOverlap name a run; no other outcome does.");
        Id = id;
        TenantId = tenantId;
        JobId = jobId;
        ScheduledAt = BackupGuard.Timestamp(scheduledAt);
        Outcome = outcome;
        Reason = reason;
        RunId = runId;
        DecidedAt = BackupGuard.Timestamp(decidedAt);
    }
    public Guid Id { get; private set; }
    public TenantId TenantId { get; private set; }
    public Guid JobId { get; private set; }
    /// <summary>The scheduled instant, UTC.</summary>
    public DateTimeOffset ScheduledAt { get; private set; }
    public BackupOccurrenceOutcome Outcome { get; private set; }
    public string Reason { get; private set; }
    /// <summary>The run created (RunCreated) or the active run that blocked this occurrence (SkippedOverlap).</summary>
    public Guid? RunId { get; private set; }
    /// <summary>When the decision was made; the difference to <see cref="ScheduledAt"/> is the scheduling delay.</summary>
    public DateTimeOffset DecidedAt { get; private set; }
}
