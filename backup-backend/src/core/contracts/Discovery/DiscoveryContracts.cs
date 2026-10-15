namespace BackupPlatform.Contracts.Discovery;

/// <summary>Capability an agent advertises when it can run the passive local database discovery described by these contracts.</summary>
public static class DiscoveryProtocol
{
    public const string Capability = "discovery.database-inventory";

    public const int MaximumInstancesPerReport = 64;
    public const int MaximumDatabasesPerInstance = 5000;
    /// <summary>Keeps a full report well inside <see cref="MaximumReportBytes"/> (about 130 bytes per database).</summary>
    public const int MaximumDatabasesPerReport = 20_000;
    public const int MaximumReportBytes = 4 * 1024 * 1024;
}

/// <summary>Why the backend asks an agent to scan; carried by the heartbeat answer.</summary>
public static class DiscoveryScanReasons
{
    public const string Initial = "initial";
    public const string Periodic = "periodic";
    public const string Manual = "manual";
}

public static class DiscoveryEngines
{
    public const string MsSql = "mssql";
    public const string PostgreSql = "postgres";
}

/// <summary>Where an instance was found. The scan reports one status per source, so "docker is absent" is never confused with "no container found".</summary>
public static class DiscoverySources
{
    /// <summary>Operating-system service registration (Windows registry/services, Linux systemd, pg_lsclusters).</summary>
    public const string Service = "Service";
    /// <summary>A database protocol handshake answered on a well-known loopback port; no credentials involved.</summary>
    public const string Listener = "Listener";
    public const string Docker = "Docker";
}

public static class DiscoverySourceStates
{
    public const string Scanned = "Scanned";
    public const string Unavailable = "Unavailable";
    public const string Disabled = "Disabled";
    public const string Failed = "Failed";
}

public static class DiscoveryReachability
{
    public const string Reachable = "Reachable";
    public const string Unreachable = "Unreachable";
    public const string Unknown = "Unknown";
}

/// <summary>
/// What the agent could do with an instance. <see cref="CredentialsRequired"/> means no database was listed: finding a service is not
/// access to it.
/// </summary>
public static class DiscoveryAccess
{
    public const string CredentialsRequired = "CredentialsRequired";
    public const string Authenticated = "Authenticated";
    public const string AuthenticationFailed = "AuthenticationFailed";
    public const string Unreachable = "Unreachable";
    public const string Error = "Error";
    public const string Unknown = "Unknown";
}

public static class DiscoveryOutcomes
{
    public const string Completed = "Completed";
    /// <summary>At least one source failed or timed out; instances of those sources are never treated as missing.</summary>
    public const string Partial = "Partial";
    public const string Failed = "Failed";
}

public static class DiscoverySupportNames
{
    public const string Supported = "Supported";
    public const string Unsupported = "Unsupported";
    public const string Unknown = "Unknown";
}

public static class DiscoveryDatabaseStates
{
    public const string Online = "Online";
    public const string Offline = "Offline";
    public const string Unknown = "Unknown";
}

/// <summary>
/// Answer of <c>POST /api/agent/discovery/scans</c> (204 when nothing is due or a scan is already running): the backend atomically
/// claimed a scan for this agent. Contains only references to credentials, never secrets.
/// </summary>
public sealed record AgentDiscoveryPlan
{
    public Guid ScanId { get; init; }
    public string Reason { get; init; } = DiscoveryScanReasons.Periodic;
    public bool IncludeDocker { get; init; } = true;
    /// <summary>Total time budget of the scan; the agent stops and reports what it has when it expires.</summary>
    public int TimeoutSeconds { get; init; } = 120;
    /// <summary>When set, only this instance is enriched with a credential; the (cheap) passive detection still runs but nothing is marked missing.</summary>
    public string? OnlyInstanceKey { get; init; }
    public IReadOnlyList<AgentDiscoveryBinding> Bindings { get; init; } = [];
}

/// <summary>The agent-local credential reference a user bound to an instance; resolved by the agent per scan like a backup job's credential.</summary>
public sealed record AgentDiscoveryBinding
{
    public string InstanceKey { get; init; } = string.Empty;
    public string Engine { get; init; } = DiscoveryEngines.MsSql;
    public string? CredentialReference { get; init; }
    public bool IntegratedSecurity { get; init; }
    public bool Encrypt { get; init; } = true;
    public bool TrustServerCertificate { get; init; }
}

/// <summary><c>PUT /api/agent/discovery/scans/{scanId}</c>. The authenticated principal, never this body, identifies the agent and tenant.</summary>
public sealed record AgentDiscoveryReport
{
    public string Outcome { get; init; } = DiscoveryOutcomes.Completed;
    public string? FailureCode { get; init; }
    public IReadOnlyList<AgentDiscoverySourceStatus> Sources { get; init; } = [];
    public IReadOnlyList<AgentDiscoveredInstance> Instances { get; init; } = [];
}

public sealed record AgentDiscoverySourceStatus
{
    public string Source { get; init; } = string.Empty;
    public string Status { get; init; } = DiscoverySourceStates.Scanned;
    public string? ReasonCode { get; init; }
}

public sealed record AgentDiscoveredInstance
{
    /// <summary>Stable identity of the instance on this agent (for example <c>mssql|service|SQLEXPRESS</c>); survives restarts and port changes.</summary>
    public string InstanceKey { get; init; } = string.Empty;
    public string Engine { get; init; } = DiscoveryEngines.MsSql;
    public string Source { get; init; } = DiscoverySources.Service;
    public string DisplayName { get; init; } = string.Empty;
    public string? InstanceName { get; init; }
    public string Host { get; init; } = "localhost";
    public int? Port { get; init; }
    public string? Version { get; init; }
    public string? Edition { get; init; }
    public string? HostPlatform { get; init; }
    public string? ContainerName { get; init; }
    public string? ContainerImage { get; init; }
    public string Reachability { get; init; } = DiscoveryReachability.Unknown;
    public string Access { get; init; } = DiscoveryAccess.Unknown;
    public string? AccessReasonCode { get; init; }
    public string FullBackup { get; init; } = DiscoverySupportNames.Unknown;
    public string? FullBackupReasonCode { get; init; }
    /// <summary>Null when the databases were not enumerated (no credentials, unreachable, error): the backend then keeps its previous list and says when it was last checked.</summary>
    public IReadOnlyList<AgentDiscoveredDatabase>? Databases { get; init; }
}

public sealed record AgentDiscoveredDatabase
{
    public string Name { get; init; } = string.Empty;
    public bool IsSystem { get; init; }
    public string State { get; init; } = DiscoveryDatabaseStates.Unknown;
    public string FullBackup { get; init; } = DiscoverySupportNames.Unknown;
    public string? FullBackupReasonCode { get; init; }
}
