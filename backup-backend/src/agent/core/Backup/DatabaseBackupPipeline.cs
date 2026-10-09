using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.StorageProviders.Abstractions;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.Agent.Core.Backup;

/// <summary>
/// The agent-side backup pipeline: pre-check, engine backup into staging, engine verification, source checksum, artifact
/// preparation, artifact checksum, storage upload, replica verification and staging cleanup, in that order. It is the one place
/// that knows the whole sequence and the only place that knows both sides of it: the engine provider and the storage provider
/// know nothing of each other, and they are resolved by identifier from whatever the host composed, so this type depends on
/// neither SqlClient nor any storage implementation.
/// </summary>
public sealed partial class DatabaseBackupPipeline : IDatabaseBackupPipeline
{
    private readonly IReadOnlyDictionary<DatabaseEngineId, IDatabaseBackupProvider> _providers;
    private readonly IReadOnlyDictionary<StorageProviderId, IStorageProvider> _storages;
    private readonly IArtifactPreparer _preparer;
    private readonly ILogger<DatabaseBackupPipeline> _logger;
    private readonly TimeProvider _time;
    private readonly StagingUsageRegistry? _staging;

    public DatabaseBackupPipeline(
        IEnumerable<IDatabaseBackupProvider> providers,
        IEnumerable<IStorageProvider> storages,
        IArtifactPreparer preparer,
        ILogger<DatabaseBackupPipeline> logger,
        TimeProvider? time = null,
        StagingUsageRegistry? staging = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(storages);
        ArgumentNullException.ThrowIfNull(preparer);
        ArgumentNullException.ThrowIfNull(logger);

        _providers = providers.ToDictionary(provider => provider.Engine);
        _storages = storages.ToDictionary(storage => storage.Id);
        _preparer = preparer;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _staging = staging;
    }

    public Task<DatabaseBackupPipelineResult> RunFullBackupAsync(
        DatabaseBackupPipelineRequest request,
        IProgress<BackupProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var tracker = new BackupStageTracker(progress, _time);
        _providers.TryGetValue(request.Engine, out var provider);
        _storages.TryGetValue(request.Storage, out var storage);
        storage = request.StorageOverride ?? storage;
        return new DatabaseBackupExecution(request, provider, storage, _preparer, tracker, _logger, _time, _staging).RunAsync(cancellationToken);
    }

    [LoggerMessage(EventId = 100, Level = LogLevel.Warning,
        Message = "Backup stage failed: Stage={Stage} Kind={Kind} Reason={Reason}")]
    internal static partial void LogStageFailed(ILogger logger, Exception? exception, BackupExecutionStage stage, BackupFailureKind kind, string reason);

    [LoggerMessage(EventId = 101, Level = LogLevel.Warning,
        Message = "Backup clean-up failed: Stage={Stage} Reason={Reason}")]
    internal static partial void LogCleanupFailed(ILogger logger, Exception? exception, BackupExecutionStage stage, string reason);

    [LoggerMessage(EventId = 102, Level = LogLevel.Information,
        Message = "Backup finished: Status={Status} Stage={Stage} Reason={Reason}")]
    internal static partial void LogFinished(ILogger logger, DatabaseBackupStatus status, BackupExecutionStage stage, string? reason);
}
