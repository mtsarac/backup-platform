using BackupPlatform.Application.Backups;

namespace BackupPlatform.Application.Restores;

/// <summary>
/// Backend-wide restore policy (configuration section <c>Restores</c>). Like the backup limits these are BACKEND policy: nothing an agent
/// reports about itself changes them, and every decision they feed is made against the database, so they hold across restarts and nodes.
/// </summary>
public sealed class RestoreExecutionOptions
{
    /// <summary>The restore agent lease is the backup lease: the agent renews every 10 seconds and loses the attempt when the backend sees no renewal for this long.</summary>
    public TimeSpan LeaseDuration { get; init; } = BackupOrchestrator.LeaseDuration;

    /// <summary>
    /// How long a restore may wait for its agent to pick it up. A restore is explicit user work on a specific target, so one nobody claimed for
    /// this long is withdrawn (Cancelled, <c>NotClaimed</c>, target untouched) instead of running unexpectedly hours later; its replica pin is released with it.
    /// </summary>
    public TimeSpan UnclaimedExpiry { get; init; } = TimeSpan.FromHours(1);

    /// <summary>How often the stale-restore sweep looks for expired leases and unclaimed restores. Only bounds how late either is noticed.</summary>
    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long a rollback copy (<c>bp_old_*</c>) a replace restore kept is guarded before an operator may ask for its removal. Nothing removes it on its own; this only
    /// stops a hurried click from discarding the copy before the restored database could have been checked.
    /// </summary>
    public TimeSpan RollbackRetention { get; init; } = TimeSpan.FromDays(7);

    /// <summary>How long an agent holds the lease on one database removal before the backend offers it again.</summary>
    public TimeSpan CleanupLeaseDuration { get; init; } = TimeSpan.FromMinutes(15);

    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (LeaseDuration < TimeSpan.FromSeconds(30) || LeaseDuration > TimeSpan.FromHours(1)) problems.Add("LeaseDuration must be between 30 seconds and one hour.");
        if (UnclaimedExpiry < TimeSpan.FromMinutes(1) || UnclaimedExpiry > TimeSpan.FromDays(7)) problems.Add("UnclaimedExpiry must be between one minute and seven days.");
        if (SweepInterval < TimeSpan.FromSeconds(1)) problems.Add("SweepInterval must be at least one second.");
        if (RollbackRetention < TimeSpan.Zero || RollbackRetention > TimeSpan.FromDays(365)) problems.Add("RollbackRetention must be between zero and 365 days.");
        if (CleanupLeaseDuration < TimeSpan.FromMinutes(1) || CleanupLeaseDuration > TimeSpan.FromHours(6)) problems.Add("CleanupLeaseDuration must be between one minute and six hours.");
        return problems;
    }

    public void ThrowIfInvalid()
    {
        var problems = Validate();
        if (problems.Count != 0) throw new InvalidOperationException("Invalid restore execution options: " + string.Join(" ", problems));
    }
}
