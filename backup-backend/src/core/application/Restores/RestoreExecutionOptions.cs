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

    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (LeaseDuration < TimeSpan.FromSeconds(30) || LeaseDuration > TimeSpan.FromHours(1)) problems.Add("LeaseDuration must be between 30 seconds and one hour.");
        if (UnclaimedExpiry < TimeSpan.FromMinutes(1) || UnclaimedExpiry > TimeSpan.FromDays(7)) problems.Add("UnclaimedExpiry must be between one minute and seven days.");
        if (SweepInterval < TimeSpan.FromSeconds(1)) problems.Add("SweepInterval must be at least one second.");
        return problems;
    }

    public void ThrowIfInvalid()
    {
        var problems = Validate();
        if (problems.Count != 0) throw new InvalidOperationException("Invalid restore execution options: " + string.Join(" ", problems));
    }
}
