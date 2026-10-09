using BackupPlatform.Agent.Core.Backup;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.StorageProviders.Abstractions;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.Agent.Core.Restore;

/// <summary>
/// The agent-side restore pipeline: agent pre-checks and the engine's target pre-check, replica copy into staging, checksum verification,
/// backup inspection and planning by the engine, the restore itself and staging cleanup, in that order. Like the backup pipeline it is the
/// one place that knows the whole sequence and both sides of it: the engine provider and the storage provider know nothing of each other
/// and are resolved by identifier from whatever the host composed, so this type depends on neither SqlClient nor any storage implementation.
/// A host that registered no restore provider for an engine still composes: such a restore ends as a failed result with
/// <see cref="RestoreAgentFailureCodes.UnsupportedCapability"/> before anything is read or changed.
/// </summary>
public sealed partial class DatabaseRestorePipeline : IDatabaseRestorePipeline
{
    private readonly IReadOnlyDictionary<DatabaseEngineId, IDatabaseRestoreProvider> _providers;
    private readonly IReadOnlyDictionary<StorageProviderId, IStorageProvider> _storages;
    private readonly IReadOnlyDictionary<DatabaseEngineId, IDatabaseBackupProvider> _discoveryProviders;
    private readonly ILogger<DatabaseRestorePipeline> _logger;
    private readonly TimeProvider _time;
    private readonly StagingUsageRegistry? _staging;

    public DatabaseRestorePipeline(
        IEnumerable<IDatabaseRestoreProvider> providers,
        IEnumerable<IStorageProvider> storages,
        ILogger<DatabaseRestorePipeline> logger,
        TimeProvider? time = null,
        StagingUsageRegistry? staging = null,
        IEnumerable<IDatabaseBackupProvider>? discoveryProviders = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(storages);
        ArgumentNullException.ThrowIfNull(logger);

        _providers = providers.ToDictionary(provider => provider.Engine);
        _storages = storages.ToDictionary(storage => storage.Id);
        _discoveryProviders = (discoveryProviders ?? []).ToDictionary(provider => provider.Engine);
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _staging = staging;
    }

    public Task<DatabaseRestorePipelineResult> RunRestoreAsync(
        DatabaseRestorePipelineRequest request,
        IProgress<RestoreProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var tracker = new RestoreStageTracker(progress, _time);
        _providers.TryGetValue(request.Engine, out var provider);
        _storages.TryGetValue(request.Storage, out var storage);
        storage = request.StorageOverride ?? storage;
        return new DatabaseRestoreExecution(request, provider, storage, tracker, _logger, _time, _staging).RunAsync(cancellationToken);
    }
    public async Task<DatabaseRestorePipelineResult> RunDiscoveryAsync(
        RestoreDiscoveryRequest request,
        IProgress<RestoreProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var startedAt = _time.GetUtcNow();
        var tracker = new RestoreStageTracker(progress, _time);
        tracker.Enter(RestoreExecutionStage.Preparing);

        if (!_discoveryProviders.TryGetValue(request.Engine, out var provider))
        {
            return new DatabaseRestorePipelineResult
            {
                Status = DatabaseRestoreStatus.Failed,
                FinalStage = RestoreExecutionStage.Failed,
                Failure = new RestorePipelineFailure(RestoreAgentFailureCodes.UnsupportedCapability, RestoreExecutionStage.Preparing,
                    "unsupported-engine", "The agent does not support discovery for this database engine."),
                TargetState = DatabaseRestoreTargetState.NotModified,
                StartedAt = startedAt,
                FinishedAt = _time.GetUtcNow()
            };
        }

        try
        {
            var discovery = await provider.DiscoverAsync(request.Connection, request.Credential, cancellationToken).ConfigureAwait(false);
            tracker.Enter(RestoreExecutionStage.Completed);
            return new DatabaseRestorePipelineResult
            {
                Status = DatabaseRestoreStatus.Succeeded,
                FinalStage = RestoreExecutionStage.Completed,
                TargetState = DatabaseRestoreTargetState.NotModified,
                Discovery = discovery,
                StartedAt = startedAt,
                FinishedAt = _time.GetUtcNow()
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            tracker.Enter(RestoreExecutionStage.Cancelled);
            return new DatabaseRestorePipelineResult
            {
                Status = DatabaseRestoreStatus.Cancelled,
                FinalStage = RestoreExecutionStage.Cancelled,
                Failure = new RestorePipelineFailure(RestoreAgentFailureCodes.Cancelled, RestoreExecutionStage.Preparing,
                    "cancelled", "Discovery was cancelled."),
                TargetState = DatabaseRestoreTargetState.NotModified,
                StartedAt = startedAt,
                FinishedAt = _time.GetUtcNow()
            };
        }
        catch (Exception exception)
        {
            LogStageFailed(_logger, exception, RestoreExecutionStage.Preparing, RestoreAgentFailureCodes.DatabaseUnavailable, exception.GetType().Name);
            tracker.Enter(RestoreExecutionStage.Failed);
            return new DatabaseRestorePipelineResult
            {
                Status = DatabaseRestoreStatus.Failed,
                FinalStage = RestoreExecutionStage.Failed,
                Failure = new RestorePipelineFailure(RestoreAgentFailureCodes.DatabaseUnavailable, RestoreExecutionStage.Preparing,
                    "discovery-failed", "Could not discover instances on the database server."),
                TargetState = DatabaseRestoreTargetState.NotModified,
                StartedAt = startedAt,
                FinishedAt = _time.GetUtcNow()
            };
        }
    }

    [LoggerMessage(EventId = 200, Level = LogLevel.Warning,
        Message = "Restore stage failed: Stage={Stage} Code={Code} Reason={Reason}")]
    internal static partial void LogStageFailed(ILogger logger, Exception? exception, RestoreExecutionStage stage, string code, string reason);

    [LoggerMessage(EventId = 201, Level = LogLevel.Warning,
        Message = "Restore clean-up failed: Stage={Stage} Reason={Reason}")]
    internal static partial void LogCleanupFailed(ILogger logger, Exception? exception, RestoreExecutionStage stage, string reason);

    [LoggerMessage(EventId = 202, Level = LogLevel.Information,
        Message = "Restore finished: Status={Status} Stage={Stage} Code={Code}")]
    internal static partial void LogFinished(ILogger logger, DatabaseRestoreStatus status, RestoreExecutionStage stage, string? code);
}
