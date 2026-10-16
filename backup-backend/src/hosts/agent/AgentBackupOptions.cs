using BackupPlatform.BackupProviders.Abstractions.Database;

namespace BackupPlatform.Agent.Host;

/// <summary>
/// The optional <c>Agent:Backup</c> section: how this agent stages database backups and which storages it can reach. Every part is
/// optional; a part that is absent is simply not available (no storage provider is registered for it, no staging area exists).
/// Holds no secrets: storage credentials are never persisted on the agent.
/// </summary>
public sealed class AgentBackupOptions
{
    public const string SectionName = "Backup";

    public AgentBackupStagingOptions? Staging { get; set; }

    /// <summary>The agent host's own disk (or a volume mounted there).</summary>
    public AgentLocalStorageOptions? LocalStorage { get; set; }

    /// <summary>The platform's central storage root, reachable by the agent as a directory (a mounted or shared volume).</summary>
    public AgentCentralStorageOptions? CentralStorage { get; set; }

    /// <summary>Optional directory containing pg_dump and pg_restore; absent uses the process PATH.</summary>
    public AgentPostgreSqlOptions? PostgreSql { get; set; }
}

public sealed class AgentPostgreSqlOptions
{
    public string? ClientToolsDirectory { get; set; }
}

/// <summary>
/// The staging directory from both sides. The relation is declared, never guessed: <see cref="StagingMappingKind.SameHost"/> says
/// both sides spell the directory the same way (only <see cref="AgentDirectory"/> is needed), <see cref="StagingMappingKind.Mapped"/>
/// says they differ (a container volume, a mount, a share) and both must be given.
/// </summary>
public sealed class AgentBackupStagingOptions
{
    public StagingMappingKind? Mapping { get; set; }

    /// <summary>The directory as the database server's own file system names it. Required for <c>Mapped</c>.</summary>
    public string? ServerDirectory { get; set; }

    /// <summary>The directory as this agent process names it.</summary>
    public string? AgentDirectory { get; set; }

    /// <summary>How the agent removes leftovers of crashed backups from this directory. Optional: defaults apply.</summary>
    public AgentStagingCleanupOptions? Cleanup { get; set; }
}

/// <summary>
/// Sweeping the agent's own staging directory for staged backup files a crashed process left behind. Every value is optional; an
/// absent one takes the default (enabled, files older than 12 hours, scanned every hour). Only files the platform itself named are
/// ever removed, never an operator's own files in that directory.
/// </summary>
public sealed class AgentStagingCleanupOptions
{
    public bool? Enabled { get; set; }

    /// <summary>Files younger than this are never removed; keep it longer than the longest backup this agent runs.</summary>
    public TimeSpan? MinimumAge { get; set; }

    public TimeSpan? ScanInterval { get; set; }
}

public sealed class AgentLocalStorageOptions
{
    public string? RootDirectory { get; set; }

    /// <summary>Additional disks (an external SSD/HDD, a mount) by name to absolute directory. The UI selects a name; a path never travels in a task, so only what is listed here can be written.</summary>
    public Dictionary<string, string>? Volumes { get; set; }
}

public sealed class AgentCentralStorageOptions
{
    public string? RootDirectory { get; set; }

    /// <summary>The one tenant this agent stores for, lowercase; the provider cannot address any other tenant's objects.</summary>
    public string? TenantScope { get; set; }
}
