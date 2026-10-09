using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.MSSQL.Discovery;

namespace BackupPlatform.BackupProviders.MSSQL.Restore;

/// <summary>Machine-readable codes of the warnings a restore can raise.</summary>
public static class SqlServerRestoreWarningCodes
{
    /// <summary>Other sessions were disconnected (SINGLE_USER ... ROLLBACK IMMEDIATE) so the restore could take the database.</summary>
    public const string SessionsDisconnected = "target-sessions-disconnected";

    /// <summary>The database is in RESTORING state: not usable until it is restored again or dropped.</summary>
    public const string DatabaseLeftRestoring = "database-left-restoring";

    /// <summary>The replaced database no longer exists after a restore that did not complete: its old contents are gone.</summary>
    public const string DatabaseMissingAfterRestore = "database-missing-after-restore";

    /// <summary>This provider switched the database to SINGLE_USER and could not switch it back.</summary>
    public const string DatabaseLeftSingleUser = "database-left-single-user";

    /// <summary>The database was RESTRICTED_USER or SINGLE_USER before the restore and is MULTI_USER now (the undo of the temporary SINGLE_USER mode).</summary>
    public const string UserAccessChanged = "user-access-changed";

    /// <summary>The restored database came back in a restricted access mode (the backup was taken from one) and was switched to MULTI_USER.</summary>
    public const string UserAccessNormalized = "user-access-normalized";

    /// <summary>The condition of the target database could not be read after the attempt.</summary>
    public const string TargetStateUnreadable = "target-state-unreadable";

    /// <summary>The target was left in a state this version cannot call healthy and cannot describe more precisely.</summary>
    public const string TargetStateUnexpected = "target-state-unexpected";

    /// <summary>The backup file has no page checksums, so integrity checks (VERIFYONLY, RESTORE) cannot detect damaged pages.</summary>
    public const string BackupWithoutChecksums = "backup-without-checksums";

    /// <summary>Existing files of the replaced database are overwritten in place.</summary>
    public const string ExistingFilesReused = "existing-files-reused";
}

/// <summary>What was observed about the target after a restore attempt, and what was done to it.</summary>
internal sealed record SqlServerRestoreOutcomeInput(
    DatabaseRestoreMode Mode,
    bool RestoreIssued,
    bool RestoreSucceeded,
    bool AccessChanged,
    SqlServerObservedDatabase? Before,
    bool Observable,
    SqlServerObservedDatabase? After);

internal sealed record SqlServerRestoreOutcome(DatabaseRestoreTargetState TargetState, string? DatabaseState, IReadOnlyList<BackupWarning> Warnings);

/// <summary>
/// Decides what a restore attempt left behind, from what was observed instead of from what was intended: a statement that returned does not
/// prove a usable database and a cancelled or failed one does not prove a damaged one. Pure.
/// <list type="bullet">
/// <item>Success is <see cref="DatabaseRestoreTargetState.Online"/> only for a database that is ONLINE and MULTI_USER.</item>
/// <item>Nothing issued: <see cref="DatabaseRestoreTargetState.NotModified"/>. The database is only "modified" by the temporary SINGLE_USER mode
/// when that was not undone.</item>
/// <item>RESTORE issued and not completed: a new database that does not exist was never created (NotModified); one that exists is a partial
/// restore. A replaced database that is gone, or no longer ONLINE, or no longer the database it was (id and creation date), is partly restored;
/// one that is still the same ONLINE database was rejected before the engine touched it (for example an unreadable backup file or an invalid destination),
/// which is what a failed RESTORE of an intact database looks like.</item>
/// <item>The final read failed: <see cref="DatabaseRestoreTargetState.Unknown"/> unless nothing was issued.</item>
/// </list>
/// </summary>
internal static class SqlServerRestoreOutcomeRules
{
    public static string? StateText(SqlServerDatabaseState state) => state switch
    {
        SqlServerDatabaseState.Online => "ONLINE",
        SqlServerDatabaseState.Restoring => "RESTORING",
        SqlServerDatabaseState.Recovering => "RECOVERING",
        SqlServerDatabaseState.RecoveryPending => "RECOVERY_PENDING",
        SqlServerDatabaseState.Suspect => "SUSPECT",
        SqlServerDatabaseState.Emergency => "EMERGENCY",
        SqlServerDatabaseState.Offline => "OFFLINE",
        _ => null
    };

    public static SqlServerRestoreOutcome Determine(SqlServerRestoreOutcomeInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var warnings = new List<BackupWarning>();
        var after = input.Observable ? input.After : null;
        var touched = input.RestoreIssued || input.AccessChanged;

        if (!input.Observable)
        {
            if (!touched)
            {
                return new SqlServerRestoreOutcome(DatabaseRestoreTargetState.NotModified, null, warnings);
            }

            warnings.Add(new BackupWarning(SqlServerRestoreWarningCodes.TargetStateUnreadable,
                "The condition of the target database could not be read after the attempt; check it before using it."));
            return new SqlServerRestoreOutcome(DatabaseRestoreTargetState.Unknown, null, warnings);
        }

        var databaseState = after?.StateDescription;

        if (input.RestoreSucceeded)
        {
            if (after is { State: SqlServerDatabaseState.Online, UserAccess: SqlServerUserAccess.MultiUser })
            {
                return new SqlServerRestoreOutcome(DatabaseRestoreTargetState.Online, databaseState, warnings);
            }

            warnings.Add(new BackupWarning(SqlServerRestoreWarningCodes.TargetStateUnexpected,
                "The restore finished but the database is not ONLINE and MULTI_USER; check it before using it."));
            return new SqlServerRestoreOutcome(DatabaseRestoreTargetState.PartiallyRestored, databaseState, warnings);
        }

        if (!touched)
        {
            return new SqlServerRestoreOutcome(DatabaseRestoreTargetState.NotModified, databaseState, warnings);
        }

        if (!input.RestoreIssued)
        {
            // Only the access mode was changed; the data was never touched.
            if (after is { UserAccess: not SqlServerUserAccess.MultiUser } && input.Mode == DatabaseRestoreMode.ReplaceExistingDatabase)
            {
                warnings.Add(new BackupWarning(SqlServerRestoreWarningCodes.DatabaseLeftSingleUser,
                    "The database was switched to SINGLE_USER mode for the restore and could not be switched back; run ALTER DATABASE ... SET MULTI_USER."));
                return new SqlServerRestoreOutcome(DatabaseRestoreTargetState.PartiallyRestored, databaseState, warnings);
            }

            return new SqlServerRestoreOutcome(DatabaseRestoreTargetState.NotModified, databaseState, warnings);
        }

        if (input.Mode == DatabaseRestoreMode.NewDatabase)
        {
            if (after is null)
            {
                return new SqlServerRestoreOutcome(DatabaseRestoreTargetState.NotModified, null, warnings);
            }

            AddStateWarning(warnings, after);
            return new SqlServerRestoreOutcome(DatabaseRestoreTargetState.PartiallyRestored, databaseState, warnings);
        }

        if (after is null)
        {
            warnings.Add(new BackupWarning(SqlServerRestoreWarningCodes.DatabaseMissingAfterRestore,
                "The database that was to be replaced no longer exists: the restore did not complete and its previous contents are gone."));
            return new SqlServerRestoreOutcome(DatabaseRestoreTargetState.PartiallyRestored, null, warnings);
        }

        var sameDatabase = input.Before is { } before
            && before.DatabaseId == after.DatabaseId
            && before.CreateDate == after.CreateDate
            && after.State == SqlServerDatabaseState.Online;
        if (sameDatabase && after.UserAccess == SqlServerUserAccess.MultiUser)
        {
            return new SqlServerRestoreOutcome(DatabaseRestoreTargetState.NotModified, databaseState, warnings);
        }

        if (sameDatabase)
        {
            warnings.Add(new BackupWarning(SqlServerRestoreWarningCodes.DatabaseLeftSingleUser,
                "The database was switched to SINGLE_USER mode for the restore and could not be switched back; run ALTER DATABASE ... SET MULTI_USER."));
            return new SqlServerRestoreOutcome(DatabaseRestoreTargetState.PartiallyRestored, databaseState, warnings);
        }

        AddStateWarning(warnings, after);
        return new SqlServerRestoreOutcome(DatabaseRestoreTargetState.PartiallyRestored, databaseState, warnings);
    }

    private static void AddStateWarning(List<BackupWarning> warnings, SqlServerObservedDatabase after)
    {
        if (after.State == SqlServerDatabaseState.Restoring)
        {
            warnings.Add(new BackupWarning(SqlServerRestoreWarningCodes.DatabaseLeftRestoring,
                "The database was left in RESTORING state and is not usable. Restore again over it (replace) or drop it."));
        }
    }
}
