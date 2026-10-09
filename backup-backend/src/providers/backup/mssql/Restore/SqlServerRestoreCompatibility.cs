using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.MSSQL.Discovery;
using BackupPlatform.BackupProviders.MSSQL.PreChecks;

namespace BackupPlatform.BackupProviders.MSSQL.Restore;

/// <summary>
/// The backup-against-target rules of a restore, one pure function per rule. A rule that cannot reach a definite answer is a Warning:
/// compatibility is never reported as verified when it was only assumed.
/// </summary>
internal static class SqlServerRestoreCompatibility
{
    /// <summary>The oldest SQL Server major version (2008) whose backups current servers are documented to restore; older ones may fail.</summary>
    private const int OldestExpectedMajor = 10;

    /// <summary>Only a full database backup in one backup set and without backup encryption can be restored by this version.</summary>
    public static BackupPreCheckItem CheckBackupType(RestoreBackupHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);
        const string id = SqlServerRestoreCheckIds.BackupType;

        if (header.Kind != RestoreBackupKind.Database)
        {
            var kind = header.Kind switch
            {
                RestoreBackupKind.Differential => "a differential backup",
                RestoreBackupKind.TransactionLog => "a transaction log backup",
                _ => "a file, partial or other backup"
            };
            return SqlServerRestoreChecks.Failed(id, RestoreReasonCodes.BackupTypeUnsupported,
                $"The backup is {kind}; only a full database backup can be restored.");
        }

        return header.IsEncrypted
            ? SqlServerRestoreChecks.Failed(id, SqlServerRestoreReasons.BackupEncryptedUnsupported,
                "The backup is encrypted with BACKUP ... WITH ENCRYPTION; restoring encrypted backups is not supported.")
            : SqlServerRestoreChecks.Passed(id, SqlServerRestoreReasons.BackupTypeSupported, "The backup is a full database backup.");
    }

    /// <summary>
    /// Version direction. A backup from a newer MAJOR version cannot be restored (the database format is newer than the engine); the same major
    /// version with a newer minor/build is allowed but may use something the older build lacks; a backup from before SQL Server 2008 is a
    /// warning; an unparseable version on either side is unknown.
    /// </summary>
    public static BackupPreCheckItem CheckVersion(Version? source, SqlServerVersion? target)
    {
        const string id = SqlServerRestoreCheckIds.VersionCompatibility;

        if (source is null || target is not { } targetVersion)
        {
            return SqlServerRestoreChecks.Warning(id, SqlServerRestoreReasons.VersionUnknown,
                "The SQL Server version of the backup or of the target could not be read, so version compatibility cannot be verified.");
        }

        if (source.Major > targetVersion.Major)
        {
            return SqlServerRestoreChecks.Failed(id, RestoreReasonCodes.TargetVersionTooOld,
                $"The backup was taken by SQL Server {source.Major}.x, a newer version than the target's {targetVersion.Major}.x; a backup cannot be restored on an older version.");
        }

        if (source.Major == targetVersion.Major
            && (source.Minor, source.Build).CompareTo((targetVersion.Minor, targetVersion.Build)) > 0)
        {
            return SqlServerRestoreChecks.Warning(id, SqlServerRestoreReasons.VersionNewerBuild,
                $"The backup comes from a newer build ({source}) of SQL Server {source.Major}.x than the target runs ({targetVersion}); if the database uses something introduced by that update the restore can fail.");
        }

        if (source.Major < OldestExpectedMajor)
        {
            return SqlServerRestoreChecks.Warning(id, SqlServerRestoreReasons.VersionOld,
                $"The backup was taken by SQL Server {source.Major}.x, which is older than SQL Server 2008; the target may not be able to restore it.");
        }

        return SqlServerRestoreChecks.Passed(id, SqlServerRestoreReasons.VersionCompatible,
            $"The backup (SQL Server {source}) can be restored on the target (SQL Server {targetVersion}).");
    }

    /// <summary>
    /// A backup can carry features only some editions have (for example partitioning, compression, in-memory tables on older editions). The header does not list them, so
    /// compatibility is only certain when the target edition has every feature (Enterprise class: Enterprise, Developer, Evaluation); for any other edition it is unknown.
    /// </summary>
    public static BackupPreCheckItem CheckEdition(SqlServerEdition edition) =>
        edition.Engine == SqlServerEngineEdition.Enterprise
            ? SqlServerRestoreChecks.Passed(SqlServerRestoreCheckIds.EditionCompatibility, SqlServerRestoreReasons.EditionCompatible,
                "The target edition supports every feature a backup can need.")
            : SqlServerRestoreChecks.Warning(SqlServerRestoreCheckIds.EditionCompatibility, SqlServerRestoreReasons.EditionUnknown,
                "The target edition does not support every Enterprise feature a backup can use; SQL Server checks this during the restore, so it cannot be verified beforehand.");

    /// <summary>
    /// Replacing a database that has a different family GUID than the backup replaces an UNRELATED database. The user has confirmed the overwrite, so it
    /// is a warning, not a failure; an unreadable GUID is unknown.
    /// </summary>
    public static BackupPreCheckItem CheckRelation(Guid? targetFamily, Guid? backupFamily)
    {
        const string id = SqlServerRestoreCheckIds.TargetRelation;

        if (targetFamily is not { } target || backupFamily is not { } backup)
        {
            return SqlServerRestoreChecks.Warning(id, SqlServerRestoreReasons.TargetRelationUnknown,
                "Whether the existing database was created from this backup's database could not be determined.");
        }

        return target == backup
            ? SqlServerRestoreChecks.Passed(id, SqlServerRestoreReasons.TargetRelated, "The existing database belongs to the same lineage as the backup.")
            : SqlServerRestoreChecks.Warning(id, SqlServerRestoreReasons.TargetUnrelated,
                "The existing database is not related to the backed-up database (different family GUID); it will be replaced because the overwrite was confirmed.");
    }
}
