namespace BackupPlatform.BackupProviders.Abstractions.Database;

public enum DatabaseAvailability
{
    Unknown = 0,
    Online,
    Unavailable
}

/// <summary>Engine-independent description of a database instance for display and reporting.</summary>
public sealed record DatabaseInstanceInfo(
    DatabaseEngineId Engine,
    string ServerName,
    string? InstanceName,
    string? Version,
    string? Edition,
    string? HostPlatform);

/// <summary>
/// A discovered database. System databases are marked, never dropped; whoever lists databases decides whether to filter them.
/// <see cref="Capabilities"/> is the database-level evaluation (engine restrictions such as non-backupable system databases applied).
/// </summary>
public sealed record DatabaseInfo(
    string Name,
    bool IsSystem,
    DatabaseAvailability Availability,
    DatabaseCapabilitySet Capabilities);

public sealed record DatabaseInstanceDiscovery(
    DatabaseInstanceInfo Instance,
    DatabaseCapabilitySet Capabilities,
    IReadOnlyList<DatabaseInfo> Databases);

/// <summary>
/// A database engine's discovery and backup pre-check surface. It is intentionally narrow: engine-specific detail (recovery
/// models, compatibility levels, edition rules) stays in the provider and surfaces through capabilities and pre-check
/// results. The runtime hosting the engine (native Windows, native Linux, container) is not part of this contract; a
/// provider reaches the engine over its normal network protocol.
/// </summary>
public interface IDatabaseBackupProvider
{
    DatabaseEngineId Engine { get; }

    /// <summary>
    /// Connects, reads instance and database metadata and evaluates capabilities. Throws <see cref="DatabaseProviderException"/>
    /// when the instance cannot be inspected and <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> fires.
    /// </summary>
    Task<DatabaseInstanceDiscovery> DiscoverAsync(
        DatabaseConnectionSettings connection,
        DatabaseCredential credential,
        CancellationToken cancellationToken);

    /// <summary>
    /// Verifies that a full backup of the requested database could start, without starting one. Expected problems
    /// (unreachable server, rejected login, missing permission, unsuitable database) are reported as failed items, not thrown.
    /// </summary>
    Task<BackupPreCheckReport> PreCheckFullBackupAsync(
        DatabaseConnectionSettings connection,
        DatabaseCredential credential,
        FullBackupPreCheckRequest request,
        CancellationToken cancellationToken);
}
