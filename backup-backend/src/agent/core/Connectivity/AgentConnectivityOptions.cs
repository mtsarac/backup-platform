using System.Globalization;

namespace BackupPlatform.Agent.Core.Connectivity;

/// <summary>
/// Timing knobs of the agent connectivity layer. Plain data: the host binds and validates it, the core only reads it.
/// </summary>
public sealed class AgentConnectivityOptions
{
    public static readonly TimeSpan MinimumHeartbeatInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MaximumHeartbeatInterval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MinimumPollInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MaximumPollInterval = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MaximumPushFallbackDelay = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MaximumReconnectDelay = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan MinimumPollSpacingLowerBound = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MinimumPollSpacingUpperBound = TimeSpan.FromMinutes(1);

    /// <summary>Heartbeat period; the effective period never exceeds the interval the server advertises.</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Polling period for fallback and optional push-connected safety polling; the server's hint can only lengthen it.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Safety polling finds durable tasks even when a connected push channel loses a hint.</summary>
    public bool PollWhilePushConnected { get; set; }

    /// <summary>How long the push channel must stay unavailable before polling becomes active. Zero polls from the start.</summary>
    public TimeSpan PushFallbackDelay { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>First step of the exponential reconnect backoff.</summary>
    public TimeSpan ReconnectBaseDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Cap of the reconnect backoff, and the delay between retries while unauthorized or protocol-incompatible.</summary>
    public TimeSpan ReconnectMaxDelay { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Smallest gap between two polls, so a burst of poll hints or reconnects cannot flood the backend.</summary>
    public TimeSpan MinimumPollSpacing { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Returns one message per invalid value; empty when the options are usable. Messages start with the property name.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        CheckRange(problems, nameof(HeartbeatInterval), HeartbeatInterval, MinimumHeartbeatInterval, MaximumHeartbeatInterval);
        CheckRange(problems, nameof(PollInterval), PollInterval, MinimumPollInterval, MaximumPollInterval);
        CheckRange(problems, nameof(PushFallbackDelay), PushFallbackDelay, TimeSpan.Zero, MaximumPushFallbackDelay);
        CheckRange(problems, nameof(MinimumPollSpacing), MinimumPollSpacing, MinimumPollSpacingLowerBound, MinimumPollSpacingUpperBound);
        var baseValid = ReconnectBaseDelay > TimeSpan.Zero && ReconnectBaseDelay <= MaximumReconnectDelay;
        if (!baseValid)
        {
            problems.Add(string.Create(CultureInfo.InvariantCulture,
                $"{nameof(ReconnectBaseDelay)} must be greater than {TimeSpan.Zero} and at most {MaximumReconnectDelay} (was {ReconnectBaseDelay})."));
        }
        if (ReconnectMaxDelay > MaximumReconnectDelay)
        {
            problems.Add(string.Create(CultureInfo.InvariantCulture,
                $"{nameof(ReconnectMaxDelay)} must be at most {MaximumReconnectDelay} (was {ReconnectMaxDelay})."));
        }
        else if (ReconnectMaxDelay < ReconnectBaseDelay)
        {
            problems.Add(string.Create(CultureInfo.InvariantCulture,
                $"{nameof(ReconnectMaxDelay)} must not be smaller than {nameof(ReconnectBaseDelay)} (was {ReconnectMaxDelay}, base {ReconnectBaseDelay})."));
        }
        return problems;
    }

    private static void CheckRange(List<string> problems, string name, TimeSpan value, TimeSpan minimum, TimeSpan maximum)
    {
        if (value < minimum || value > maximum)
        {
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"{name} must be between {minimum} and {maximum} (was {value})."));
        }
    }
}
