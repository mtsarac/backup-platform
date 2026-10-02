using BackupPlatform.Domain.Agents;

namespace BackupPlatform.Agents.Connectivity;

/// <summary>Liveness derived from the last sign of life. Orthogonal to the agent lifecycle.</summary>
public enum AgentConnectivityState
{
    Unknown = 0,
    Online = 1,
    Offline = 2
}

/// <summary>Single status for display: lifecycle overrides compatibility, which overrides liveness.</summary>
public enum AgentOperationalStatus
{
    Unknown = 0,
    Online = 1,
    Offline = 2,
    Disabled = 3,
    Revoked = 4,
    VersionMismatch = 5
}

public static class AgentConnectivityEvaluator
{
    /// <summary>
    /// Online while the age of the last sign of life is below the timeout. A last-seen slightly in the future (server
    /// clock stepped back) counts as Online rather than corrupting the state. Only the server clock is ever compared.
    /// </summary>
    public static AgentConnectivityState Evaluate(DateTimeOffset? lastSeenAt, DateTimeOffset now, TimeSpan offlineTimeout)
    {
        if (lastSeenAt is null)
        {
            return AgentConnectivityState.Unknown;
        }
        return now - lastSeenAt.Value < offlineTimeout ? AgentConnectivityState.Online : AgentConnectivityState.Offline;
    }

    /// <param name="protocolSupported">Null when the agent never reported a protocol version.</param>
    public static AgentOperationalStatus EvaluateOperational(
        AgentStatus lifecycle, AgentConnectivityState connectivity, bool? protocolSupported) => lifecycle switch
    {
        AgentStatus.Revoked => AgentOperationalStatus.Revoked,
        AgentStatus.Disabled => AgentOperationalStatus.Disabled,
        AgentStatus.PendingEnrollment => AgentOperationalStatus.Unknown,
        _ when protocolSupported == false => AgentOperationalStatus.VersionMismatch,
        _ => connectivity switch
        {
            AgentConnectivityState.Online => AgentOperationalStatus.Online,
            AgentConnectivityState.Offline => AgentOperationalStatus.Offline,
            _ => AgentOperationalStatus.Unknown
        }
    };
}
