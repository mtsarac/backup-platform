using BackupPlatform.BackupProviders.Abstractions.Database;

namespace BackupPlatform.BackupProviders.MSSQL.Backup;

internal enum StagingCleanupAction
{
    /// <summary>Leave the file alone (nothing was created yet, or it is handed over).</summary>
    None = 0,
    Delete,
    Keep
}

/// <summary>How an execution ended, as far as the staged file is concerned.</summary>
internal enum ExecutionEnd
{
    Succeeded = 0,
    Cancelled,
    BackupFailed,
    VerificationFailed
}

/// <summary>
/// What happens to the staged file when an execution ends. The provider created the file, so it removes what a failed run left:
/// a cancelled run always (nobody wants a partial file), a failed BACKUP always (it is incomplete by definition), a failed
/// verification unless the caller asked to keep the file for diagnosis. A successful run hands the file over untouched. Pure.
/// </summary>
internal static class StagingCleanupPolicy
{
    public static StagingCleanupAction Decide(ExecutionEnd end, StagingFailurePolicy onFailure, bool backupWasIssued) =>
        !backupWasIssued ? StagingCleanupAction.None
        : end switch
        {
            ExecutionEnd.Succeeded => StagingCleanupAction.None,
            ExecutionEnd.VerificationFailed when onFailure == StagingFailurePolicy.Keep => StagingCleanupAction.Keep,
            _ => StagingCleanupAction.Delete
        };

    /// <summary>
    /// The cleanup record and, when the file should be gone but is not, the warning that says so. The primary outcome of the
    /// execution is never touched by a cleanup problem: it only ever adds a warning.
    /// </summary>
    public static (StagingCleanup Cleanup, BackupWarning? Warning) Describe(StagingCleanupAction action, bool fileExisted, bool deleted)
    {
        if (action == StagingCleanupAction.None || !fileExisted)
        {
            return (StagingCleanup.NotNeeded, null);
        }

        if (action == StagingCleanupAction.Keep)
        {
            return (new StagingCleanup(StagingCleanupStatus.Kept), null);
        }

        return deleted
            ? (new StagingCleanup(StagingCleanupStatus.Deleted), null)
            : (new StagingCleanup(StagingCleanupStatus.Failed, "staging-delete-failed"),
                new BackupWarning(
                    SqlServerBackupWarningCodes.StagingCleanupFailed,
                    "The partial staged file could not be removed and may remain in the staging directory."));
    }
}
