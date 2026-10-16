using BackupPlatform.BackupProviders.Abstractions.Database;

namespace BackupPlatform.BackupProviders.PostgreSql;

/// <summary>The PostgreSQL capability identifiers (Full Backup and Full Restore only: no physical, WAL, PITR or incremental support exists).</summary>
public static class PostgreSqlCapabilityIds
{
    public static DatabaseCapabilityId FullBackup { get; } = new("postgres.backup.full");
    public static DatabaseCapabilityId BackupCompression { get; } = new("postgres.backup.compression");

    /// <summary>Data-checksum protection: a logical dump reads every page through the server, which verifies page checksums when the cluster has them enabled.</summary>
    public static DatabaseCapabilityId BackupChecksum { get; } = new("postgres.backup.checksum");

    /// <summary>A logical dump never touches a backup chain, so every PostgreSQL full backup is copy-only by nature.</summary>
    public static DatabaseCapabilityId CopyOnly { get; } = new("postgres.backup.copy-only");

    public static DatabaseCapabilityId DifferentialBackup { get; } = new("postgres.backup.differential");
    public static DatabaseCapabilityId IncrementalBackup { get; } = new("postgres.backup.incremental");
    public static DatabaseCapabilityId WalArchiving { get; } = new("postgres.backup.wal");

    /// <summary>The agent can restore a custom-format archive with pg_restore. Advertised by the agent product; whether a server allows it is the restore pre-check's job.</summary>
    public static DatabaseCapabilityId RestoreFull { get; } = new("postgres.restore.full");
    public static DatabaseCapabilityId RestorePrecheck { get; } = new("postgres.restore.precheck");
    public static DatabaseCapabilityId RestoreDiscovery { get; } = new("postgres.restore.discovery");
    public static DatabaseCapabilityId RestoreVerify { get; } = new("postgres.restore.verify");

    internal static bool IsBackupCapability(DatabaseCapabilityId id) => id.Value.StartsWith("postgres.backup.", StringComparison.Ordinal);
}
