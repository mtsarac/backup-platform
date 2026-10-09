using BackupPlatform.BackupProviders.Abstractions.Database;

namespace BackupPlatform.BackupProviders.MSSQL.Capabilities;

/// <summary>The SQL Server capability identifiers this provider evaluates (plan §5: SupportsFullBackup ... SupportsTDE).</summary>
public static class SqlServerCapabilityIds
{
    public static DatabaseCapabilityId FullBackup { get; } = new("mssql.backup.full");
    public static DatabaseCapabilityId DifferentialBackup { get; } = new("mssql.backup.differential");
    public static DatabaseCapabilityId LogBackup { get; } = new("mssql.backup.log");
    public static DatabaseCapabilityId BackupCompression { get; } = new("mssql.backup.compression");
    public static DatabaseCapabilityId BackupChecksum { get; } = new("mssql.backup.checksum");
    public static DatabaseCapabilityId CopyOnly { get; } = new("mssql.backup.copy-only");
    public static DatabaseCapabilityId BackupEncryption { get; } = new("mssql.backup.encryption");
    public static DatabaseCapabilityId TransparentDataEncryption { get; } = new("mssql.tde");
    public static DatabaseCapabilityId RestoreVerifyOnly { get; } = new("mssql.restore.verify-only");

    /// <summary>
    /// The agent can restore a full backup into a database (HEADERONLY, FILELISTONLY, WITH MOVE, replace). Advertised by the agent product
    /// like the backup capabilities, not evaluated per instance: whether an instance allows it is the restore pre-check's job.
    /// </summary>
    public static DatabaseCapabilityId RestoreDatabase { get; } = new("mssql.restore.database");

    /// <summary>Identifiers that describe how a database is backed up and are therefore subject to per-database restrictions.</summary>
    internal static bool IsBackupCapability(DatabaseCapabilityId id) => id.Value.StartsWith("mssql.backup.", StringComparison.Ordinal);
}
