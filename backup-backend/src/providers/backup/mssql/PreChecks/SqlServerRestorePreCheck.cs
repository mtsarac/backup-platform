using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.MSSQL.Discovery;
using BackupPlatform.BackupProviders.MSSQL.Restore;

namespace BackupPlatform.BackupProviders.MSSQL.PreChecks;

/// <summary>The target-only facts of a restore pre-check. <see cref="ActiveSessions"/> is null when the server did not say.</summary>
internal sealed record SqlServerRestorePreCheckFacts(
    SqlServerRestoreTargetFacts Target,
    SqlServerBackupPermission Permission,
    int? ActiveSessions);

/// <summary>
/// The decision logic of the SQL Server restore pre-check: given what the target server said, which checks pass, warn or fail. It needs no
/// backup file (that is <c>InspectBackupAsync</c>) and changes nothing. The order of the report is the order of the dependency: connection,
/// server metadata, the database name, then everything that depends on the database being found or not found.
/// </summary>
internal static class SqlServerRestorePreCheck
{
    public static IReadOnlyList<BackupPreCheckItem> Evaluate(SqlServerRestorePreCheckFacts facts, RestorePreCheckRequest request)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(request);

        var target = facts.Target;
        var replace = request.Mode == DatabaseRestoreMode.ReplaceExistingDatabase;

        if (SqlServerRestoreChecks.ConnectionFailureItems(target, AllChecks(request)) is { } connectionFailure)
        {
            return connectionFailure;
        }

        var items = new List<BackupPreCheckItem>(SqlServerRestoreChecks.ConnectedItems());

        if (target.MetadataFailure is not null || target.Instance is null)
        {
            items.Add(SqlServerRestoreChecks.MetadataUnavailable(target, SqlServerRestoreCheckIds.TargetServer));
            items.AddRange(AllChecks(request).Skip(1).Select(SqlServerRestoreChecks.Skipped));
            return items;
        }

        items.Add(SqlServerRestoreChecks.CheckServer(target.Instance));

        var name = SqlServerRestoreChecks.CheckDatabaseName(request.DatabaseName);
        items.Add(name);
        var nameUsable = name.Status != BackupPreCheckStatus.Failed;

        var database = target.Database;
        if (nameUsable)
        {
            items.Add(SqlServerRestoreChecks.CheckExists(request.Mode, database));
            if (SqlServerRestoreChecks.CheckProtected(request.DatabaseName, request.Mode, target) is { } protection)
            {
                items.Add(protection);
            }
        }
        else
        {
            items.Add(SqlServerRestoreChecks.Skipped(BackupPreCheckIds.DatabaseExists));
            items.Add(SqlServerRestoreChecks.Skipped(SqlServerRestoreCheckIds.TargetProtected));
        }

        var existingTarget = replace && database is not null;
        if (replace)
        {
            items.Add(nameUsable && existingTarget
                ? SqlServerRestoreChecks.CheckExistingState(database!)
                : SqlServerRestoreChecks.Skipped(BackupPreCheckIds.DatabaseState));
            items.Add(nameUsable && existingTarget
                ? SqlServerRestoreChecks.CheckSessions(facts.ActiveSessions, request.TerminateActiveConnections)
                : SqlServerRestoreChecks.Skipped(SqlServerRestoreCheckIds.ActiveSessions));
        }

        items.Add(nameUsable && (!replace || existingTarget)
            ? SqlServerRestoreChecks.CheckPermission(request.Mode, facts.Permission)
            : SqlServerRestoreChecks.Skipped(SqlServerRestoreCheckIds.RestorePermission));

        var (directoryItem, directories) = SqlServerRestoreChecks.CheckDirectories(target);
        items.Add(directoryItem);

        if (request.BackupSizeBytes is { } backupSize)
        {
            items.Add(CheckDiskSpace(target, directories, backupSize));
        }

        return items;
    }

    /// <summary>Every check of a pre-check, in report order, after the two connection checks; used for the Skipped items of a failed prerequisite.</summary>
    private static IReadOnlyList<string> AllChecks(RestorePreCheckRequest request)
    {
        var ids = new List<string>
        {
            SqlServerRestoreCheckIds.TargetServer,
            SqlServerRestoreCheckIds.DatabaseName,
            BackupPreCheckIds.DatabaseExists,
            SqlServerRestoreCheckIds.TargetProtected
        };

        if (request.Mode == DatabaseRestoreMode.ReplaceExistingDatabase)
        {
            ids.Add(BackupPreCheckIds.DatabaseState);
            ids.Add(SqlServerRestoreCheckIds.ActiveSessions);
        }

        ids.Add(SqlServerRestoreCheckIds.RestorePermission);
        ids.Add(SqlServerRestoreCheckIds.Directories);
        if (request.BackupSizeBytes is not null)
        {
            ids.Add(SqlServerRestoreCheckIds.DiskSpace);
        }

        return ids;
    }

    /// <summary>
    /// The only size known before the backup is read is the backup file's, which is a LOWER bound of what the restored files need (a compressed
    /// backup of a mostly empty database is small; the restored files are allocated at their full size). So it can prove a shortage but not
    /// sufficiency: free space below it fails, free space above it passes with the exact check left to the plan.
    /// </summary>
    private static BackupPreCheckItem CheckDiskSpace(SqlServerRestoreTargetFacts target, SqlServerTargetDirectories? directories, long backupSize)
    {
        const string id = SqlServerRestoreCheckIds.DiskSpace;
        if (directories is null)
        {
            return SqlServerRestoreChecks.Skipped(id);
        }

        if (target.Volumes is not { } volumes
            || SqlServerRestoreChecks.FindVolume(volumes, directories.Data.Value) is not { } volume)
        {
            return SqlServerRestoreChecks.Warning(id, SqlServerRestoreReasons.DiskSpaceUnknown,
                "SQL Server did not report the free space of the data volume (VIEW SERVER STATE is needed); the space check is repeated when the backup is planned.");
        }

        return volume.AvailableBytes < backupSize
            ? SqlServerRestoreChecks.Failed(id, RestoreReasonCodes.InsufficientDiskSpace,
                "The data volume has less free space than the backup file occupies, so the database cannot be restored there.")
            : SqlServerRestoreChecks.Passed(id, SqlServerRestoreReasons.DiskSpaceSufficient,
                "The data volume has at least as much free space as the backup file occupies; the exact need is checked when the backup is planned.");
    }
}
