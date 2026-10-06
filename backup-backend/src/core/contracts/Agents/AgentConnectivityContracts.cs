using System.Text;

namespace BackupPlatform.Contracts.Agents;

/// <summary>
/// Header every authenticated connectivity call (heartbeat, poll, push channel) must carry. It declares the protocol
/// revision the agent speaks as <c>major.minor</c>; it is not the software release version and not a credential.
/// </summary>
public static class AgentProtocolHeader
{
    public const string Name = "X-Backup-Agent-Protocol";
}

/// <summary>Machine-readable <see cref="AgentApiError.Error"/> values used by the connectivity surface.</summary>
public static class AgentErrorCodes
{
    public const string ProtocolIncompatible = "protocol_incompatible";
    public const string ProtocolMissing = "protocol_missing";
    public const string RateLimited = "rate_limited";
    public const string InvalidRequest = "invalid_request";
}

/// <summary>What the agent should do about an error, so upgrade handling needs no message parsing.</summary>
public static class AgentErrorActions
{
    /// <summary>The agent speaks an older protocol than the backend accepts.</summary>
    public const string UpgradeAgent = "upgrade_agent";
    /// <summary>The agent speaks a newer protocol than the backend knows; the backend must be upgraded first.</summary>
    public const string WaitForServerUpgrade = "wait_for_server_upgrade";
    public const string RetryLater = "retry_later";
}

/// <summary>Optional lightweight runtime facts. Not telemetry: no resource usage, and the backend never relies on them.</summary>
public sealed record AgentRuntimeInfo
{
    public string? OsDescription { get; init; }
    public string? RuntimeVersion { get; init; }
}

/// <summary>
/// Periodic liveness report. The authenticated principal, not this body, identifies the agent, and the server clock,
/// not any agent clock, decides when it was seen. Nothing here states "online": the backend derives that itself.
/// Wire JSON uses camelCase.
/// </summary>
public sealed record AgentHeartbeatRequest
{
    /// <summary>Agent software release version, for example <c>1.4.2</c>.</summary>
    public string AgentVersion { get; init; } = string.Empty;
    /// <summary>One of <see cref="AgentPlatformNames"/>.</summary>
    public string Platform { get; init; } = string.Empty;
    /// <summary>
    /// Opaque revision (at most 64 characters) of the capability set the agent currently advertises. Extension point:
    /// the backend stores it so a later explicit capability sync can be requested when it changes; a heartbeat never
    /// carries the capability list itself.
    /// </summary>
    public string? CapabilityRevision { get; init; }
    public AgentRuntimeInfo? Runtime { get; init; }
}

public sealed record AgentHeartbeatResponse
{
    /// <summary>Authoritative server time at which the heartbeat was recorded.</summary>
    public DateTimeOffset ServerTime { get; init; }
    public int HeartbeatIntervalSeconds { get; init; }
    public int OfflineTimeoutSeconds { get; init; }
    public AgentServerProtocol Protocol { get; init; } = new();
    /// <summary>Always false today; reserved for an explicit capability sync request.</summary>
    public bool CapabilitySyncRequested { get; init; }
}

/// <summary>Generic claimed-work envelope. Push messages only hint that an authoritative poll may find work.</summary>
public sealed record AgentTaskEnvelope
{
    public string Id { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public BackupPlatform.Contracts.Backups.BackupTaskSnapshot? Backup { get; init; }
    public BackupPlatform.Contracts.Backups.DeleteReplicaTaskSnapshot? DeleteReplica { get; init; }
}

public sealed record AgentPollResponse
{
    /// <summary>Opaque server position; the agent echoes it as <c>?cursor=</c> on its next poll.</summary>
    public string Cursor { get; init; } = string.Empty;
    public IReadOnlyList<AgentTaskEnvelope> Tasks { get; init; } = [];
    public int NextPollAfterSeconds { get; init; }
}

/// <summary>Names of the push channel (SignalR) route and of the server-to-agent messages.</summary>
public static class AgentPushChannelContract
{
    public const string HubPath = "/hubs/agent";
    public const string ServerHelloMethod = "ServerHello";
    public const string ProtocolNoticeMethod = "ProtocolNotice";
    public const string PollHintMethod = "PollHint";

    public const string NoticeUpgradeRequired = "upgrade_required";
    public const string NoticeServerUpgradeRequired = "server_upgrade_required";
    /// <summary>Sent after connecting when the agent's protocol is supported but older than the backend's current one.</summary>
    public const string NoticeUpgradeAvailable = "upgrade_available";
}

/// <summary>First message after connecting. <see cref="AgentId"/> is the identity the server authenticated, so the agent can verify it.</summary>
public sealed record AgentServerHello
{
    public Guid AgentId { get; init; }
    public DateTimeOffset ServerTime { get; init; }
    public AgentServerProtocol Protocol { get; init; } = new();
    public int HeartbeatIntervalSeconds { get; init; }
}

/// <summary>Protocol or compatibility notification; <see cref="Action"/> is one of <see cref="AgentErrorActions"/>.</summary>
public sealed record AgentProtocolNotice
{
    public string Kind { get; init; } = string.Empty;
    public string Action { get; init; } = string.Empty;
    public AgentServerProtocol Protocol { get; init; } = new();
}

/// <summary>Tells the agent that polling now would find new work. It carries no work itself.</summary>
public sealed record AgentPollHint
{
    public string Reason { get; init; } = string.Empty;
}
