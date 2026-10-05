using BackupPlatform.BackupProviders.Abstractions.Database;

namespace BackupPlatform.Agent.Core.Backup;

/// <summary>
/// What an orchestrator (job runner, scheduler, manual trigger) talks to: one call takes one verified full backup all the way into
/// storage. It knows neither the engine nor the storage implementation; both are resolved by identifier from what the host registered.
/// </summary>
public interface IDatabaseBackupPipeline
{
    /// <summary>
    /// Runs pre-check, backup, engine verification, source checksum, artifact preparation, artifact checksum, upload, replica
    /// verification and staging cleanup, reporting <see cref="BackupExecutionStage"/> transitions through <paramref name="progress"/>.
    /// Expected failures and caller cancellation are returned as a result (classified by <see cref="BackupFailureKind"/>), never
    /// thrown; only argument errors throw. Progress is best-effort: a consumer that throws cannot change the outcome.
    /// </summary>
    Task<DatabaseBackupPipelineResult> RunFullBackupAsync(
        DatabaseBackupPipelineRequest request,
        IProgress<BackupProgress>? progress,
        CancellationToken cancellationToken);
}
