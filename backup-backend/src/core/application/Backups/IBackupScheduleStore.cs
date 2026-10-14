using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Application.Backups;

public sealed record ScheduledJobReference(TenantId TenantId, Guid JobId);

/// <summary>What one evaluation of a job's schedule did. Everything in it is already committed.</summary>
/// <param name="Recorded">Occurrences decided in this evaluation (including the RunCreated one).</param>
/// <param name="CreatedRunId">The scheduled run created, if any.</param>
/// <param name="AgentToNotify">The owner of the created run; the host may hint it to poll.</param>
/// <param name="Deferred">An eligible occurrence is waiting for its agent; it stays due and is re-evaluated on the next sweep.</param>
/// <param name="JobFound">False when the job no longer exists.</param>
public sealed record BackupScheduleEvaluation(
    IReadOnlyList<BackupScheduleOccurrence> Recorded, Guid? CreatedRunId, AgentId? AgentToNotify, bool Deferred, bool JobFound = true)
{
    public static BackupScheduleEvaluation Missing { get; } = new([], null, null, false, false);
}

/// <summary>
/// Durable scheduling decisions. Every method is atomic and serialised per agent with the same row lock that guards manual starts,
/// claims and retries, so a scheduled run can never race another way of creating or finishing a run of the same job.
/// </summary>
public interface IBackupScheduleStore
{
    /// <summary>
    /// Decides every occurrence of the job that became due and acts on the latest eligible one (see <see cref="BackupScheduleEvaluator"/>).
    /// Idempotent: the unique (job, instant) occurrence key and the unique run-per-occurrence key make a repeated, concurrent or
    /// multi-node call a no-op.
    /// </summary>
    Task<BackupScheduleEvaluation> EvaluateAsync(TenantId tenantId, Guid jobId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Enabled, scheduled jobs that have at least one undecided due occurrence at <paramref name="now"/>.</summary>
    Task<IReadOnlyList<ScheduledJobReference>> FindDueJobsAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken);

    /// <summary>Every job whose job and schedule are both enabled: the set the scheduler's triggers must mirror.</summary>
    Task<IReadOnlyList<BackupJob>> ListScheduledJobsAsync(CancellationToken cancellationToken);

    /// <summary>Ends scheduled runs whose first attempt was not claimed before their deadline. Returns the number ended.</summary>
    Task<int> ExpireUnclaimedRunsAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<BackupScheduleOccurrence>> ListOccurrencesAsync(TenantId tenantId, Guid jobId, int limit, CancellationToken cancellationToken);

    /// <summary>Applies <paramref name="change"/> to the job under the agent lock and saves it. Null when the job does not exist.</summary>
    Task<BackupJob?> ModifyJobAsync(TenantId tenantId, Guid jobId, Action<BackupJob> change, CancellationToken cancellationToken, UserId? actor = null, DateTimeOffset? now = null);
}

/// <summary>A one-shot wake-up the scheduler should hold for a job.</summary>
/// <param name="Fingerprint">Digest of the cron and time zone it was derived from; recognises a stale trigger. Not configuration: it is never read back as the schedule.</param>
public sealed record BackupScheduleTarget(TenantId TenantId, Guid JobId, string Fingerprint, DateTimeOffset FireAt);

public sealed record BackupScheduleReconciliation(int Created, int Rewritten, int Removed, int Unchanged)
{
    public bool Changed => Created + Rewritten + Removed != 0;
}

/// <summary>
/// The scheduler library's side of scheduling, and nothing more: hold at most one wake-up per job at the instant the database says. It
/// never decides whether a backup runs. Implementations live in infrastructure; nothing above it names the library.
/// </summary>
public interface IBackupScheduleCoordinator
{
    /// <summary>Makes the job's wake-up equal <paramref name="desired"/> (null removes it). Idempotent: an already matching wake-up is left untouched.</summary>
    Task SynchronizeAsync(Guid jobId, BackupScheduleTarget? desired, CancellationToken cancellationToken);

    /// <summary>Makes the whole scheduler store equal <paramref name="desired"/>: creates missing wake-ups, rewrites stale ones, removes disabled, deleted and orphaned ones.</summary>
    Task<BackupScheduleReconciliation> ReconcileAsync(IReadOnlyCollection<BackupScheduleTarget> desired, CancellationToken cancellationToken);
}

/// <summary>Best-effort hint that an agent has new work. Delivery is never relied on: agents also poll.</summary>
public interface IBackupWorkSignal
{
    Task WorkAvailableAsync(AgentId agentId, CancellationToken cancellationToken);
}
