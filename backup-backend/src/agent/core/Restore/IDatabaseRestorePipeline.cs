using BackupPlatform.BackupProviders.Abstractions.Database;

namespace BackupPlatform.Agent.Core.Restore;

/// <summary>
/// What the restore worker talks to: one call takes one stored backup replica all the way into a database. It knows neither the engine nor the
/// storage implementation; both are resolved by identifier from what the host registered.
/// </summary>
public interface IDatabaseRestorePipeline
{
    /// <summary>
    /// Runs pre-check, replica copy into staging, checksum verification, backup inspection, restore planning, the restore itself and staging
    /// cleanup, reporting <see cref="RestoreExecutionStage"/> transitions through <paramref name="progress"/>. Expected failures and caller
    /// cancellation are returned as a result (never thrown) that states what the target database was left as; only argument errors throw.
    /// Progress is best-effort: a consumer that throws cannot change the outcome.
    /// </summary>
    Task<DatabaseRestorePipelineResult> RunRestoreAsync(
        DatabaseRestorePipelineRequest request,
        IProgress<RestoreProgress>? progress,
        CancellationToken cancellationToken);
}
