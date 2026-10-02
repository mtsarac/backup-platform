namespace BackupPlatform.Agents.Connectivity;

/// <summary>Timing policy for liveness. Heartbeat cadence and the offline timeout are deliberately different values.</summary>
public sealed class AgentConnectivityOptions
{
    public static readonly TimeSpan MinimumHeartbeatInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MaximumHeartbeatInterval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MinimumPollInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MaximumPollInterval = TimeSpan.FromMinutes(10);
    /// <summary>The offline timeout must tolerate at least this many consecutive missed heartbeats plus the heartbeat that is merely late.</summary>
    public const int MinimumHeartbeatsPerTimeout = 3;

    /// <summary>Cadence the backend recommends to agents.</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>An agent not seen for this long is Offline. Default allows three missed 30 s heartbeats and one in flight.</summary>
    public TimeSpan OfflineTimeout { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>Recommended spacing between fallback polls.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Poll and push-connect "seen" touches skip the write when the stored last-seen is newer than this, which bounds
    /// write amplification from chatty fallback polling. Heartbeats always write (they carry reported metadata).
    /// </summary>
    public TimeSpan SeenWriteCoalescing { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How often live push connections are re-checked against agent lifecycle state.</summary>
    public TimeSpan ConnectionAuditInterval { get; init; } = TimeSpan.FromSeconds(30);

    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (HeartbeatInterval < MinimumHeartbeatInterval || HeartbeatInterval > MaximumHeartbeatInterval)
        {
            problems.Add($"HeartbeatInterval must be between {MinimumHeartbeatInterval} and {MaximumHeartbeatInterval}.");
        }
        if (OfflineTimeout < HeartbeatInterval * MinimumHeartbeatsPerTimeout)
        {
            problems.Add($"OfflineTimeout must be at least {MinimumHeartbeatsPerTimeout} times HeartbeatInterval so a few missed heartbeats do not flap the state.");
        }
        if (PollInterval < MinimumPollInterval || PollInterval > MaximumPollInterval)
        {
            problems.Add($"PollInterval must be between {MinimumPollInterval} and {MaximumPollInterval}.");
        }
        if (SeenWriteCoalescing < TimeSpan.Zero || SeenWriteCoalescing >= OfflineTimeout)
        {
            problems.Add("SeenWriteCoalescing must be non-negative and shorter than OfflineTimeout.");
        }
        if (ConnectionAuditInterval < TimeSpan.FromSeconds(1))
        {
            problems.Add("ConnectionAuditInterval must be at least one second.");
        }
        return problems;
    }

    public void ThrowIfInvalid()
    {
        var problems = Validate();
        if (problems.Count != 0)
        {
            throw new InvalidOperationException("Invalid agent connectivity configuration: " + string.Join(" ", problems));
        }
    }
}
