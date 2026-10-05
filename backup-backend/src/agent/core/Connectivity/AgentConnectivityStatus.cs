namespace BackupPlatform.Agent.Core.Connectivity;

public enum AgentChannelState
{
    /// <summary>Not running: before start or after shutdown.</summary>
    Stopped,
    /// <summary>The push channel is not connected; connecting or waiting to retry.</summary>
    Connecting,
    /// <summary>The push channel is connected; configured safety polling may remain active.</summary>
    Connected,
    /// <summary>The backend does not accept the credential; retries happen at the slow maximum delay with a freshly loaded identity.</summary>
    Unauthorized,
    /// <summary>Agent and backend protocol windows do not overlap; retries happen at the slow maximum delay.</summary>
    ProtocolIncompatible,
}

/// <summary>
/// Point-in-time view of the connectivity layer. Times are local observations only; the backend alone decides whether
/// the agent is online.
/// </summary>
public sealed record AgentConnectivitySnapshot
{
    public AgentChannelState State { get; init; } = AgentChannelState.Stopped;

    /// <summary>True while the polling fallback is active (push unavailable beyond the fallback delay).</summary>
    public bool PollingActive { get; init; }

    /// <summary>Failed push connect/reconnect attempts since the push channel was last connected.</summary>
    public int ConsecutivePushFailures { get; init; }

    public int ConsecutiveHeartbeatFailures { get; init; }

    public int ConsecutivePollFailures { get; init; }

    /// <summary>Local UTC time at which the last heartbeat was acknowledged by the backend.</summary>
    public DateTimeOffset? LastSuccessfulHeartbeatUtc { get; init; }

    public DateTimeOffset? LastSuccessfulPollUtc { get; init; }

    /// <summary>The heartbeat period currently in use, after applying the server-advertised interval.</summary>
    public TimeSpan EffectiveHeartbeatInterval { get; init; }
}

public interface IAgentConnectivityStatus
{
    AgentConnectivitySnapshot Current { get; }

    /// <summary>Raised after every change of <see cref="Current"/>, from arbitrary threads. Handlers must not block or throw.</summary>
    event Action<AgentConnectivitySnapshot>? Changed;
}
