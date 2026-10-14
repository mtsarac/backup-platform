namespace BackupPlatform.Contracts.Backups;

/// <summary>
/// The agent asks, before and while it uploads, how fast it may. <see cref="Release"/> ends the upload: its slice of the tenant limit is given
/// back at once instead of when it expires. Lease-fenced like every other task call.
/// </summary>
public sealed record UploadPolicyRequest
{
    public Guid LeaseToken { get; init; }
    public bool Release { get; init; }
}

/// <summary>
/// The ceilings that apply to this upload, in bytes per second (null = no limit at that scope). The agent obeys the lowest one it has.
/// <see cref="TenantShareBytesPerSecond"/> is this upload's slice of the tenant limit and is 0 while no slice is free (hold the transfer).
/// </summary>
public sealed record UploadPolicyResponse
{
    public long? JobBytesPerSecond { get; init; }
    public long? AgentBytesPerSecond { get; init; }
    public long? TenantShareBytesPerSecond { get; init; }

    /// <summary>The lowest rate the transfer can legitimately be held to while this policy exists, so deadlines can be widened by size / rate. Null when unlimited.</summary>
    public long? MinimumBytesPerSecond { get; init; }

    /// <summary>When to ask again.</summary>
    public int RefreshAfterSeconds { get; init; } = 15;

    /// <summary>How long <see cref="TenantShareBytesPerSecond"/> stays valid without a renewal; after that the backend gives the slice to others and the agent must stop sending.</summary>
    public int ValidForSeconds { get; init; } = 10;

    /// <summary>The rate the agent applies: the lowest ceiling that exists; null is unlimited.</summary>
    public long? EffectiveBytesPerSecond => MostRestrictive(JobBytesPerSecond, AgentBytesPerSecond, TenantShareBytesPerSecond);

    /// <summary>The longest a deadline is widened for throttling; a transfer that would need more is stopped by the provider's own limit.</summary>
    public static readonly TimeSpan MaximumDeadlineAllowance = TimeSpan.FromHours(24);

    /// <summary>
    /// Extra time a total-transfer deadline needs because the sender is held to <paramref name="minimumBytesPerSecond"/>: the size over that rate,
    /// capped at <see cref="MaximumDeadlineAllowance"/>. Zero when there is no limit. The one formula the agent and the backend relay share.
    /// </summary>
    public static TimeSpan DeadlineAllowance(long sizeBytes, long? minimumBytesPerSecond) =>
        minimumBytesPerSecond is > 0 && sizeBytes > 0
            ? TimeSpan.FromSeconds(Math.Min(sizeBytes / (double)minimumBytesPerSecond.Value, MaximumDeadlineAllowance.TotalSeconds))
            : TimeSpan.Zero;

    private static long? MostRestrictive(params long?[] limits)
    {
        long? result = null;
        foreach (var limit in limits)
            if (limit is { } value && (result is null || value < result)) result = value;
        return result;
    }
}
