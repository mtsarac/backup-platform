using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.StorageProviders.Abstractions;
using BackupPlatform.StorageProviders.Abstractions.Transfer;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.Agent.Core.Backup;

/// <summary>
/// One run of the pipeline and everything it learns on the way. The stage methods either complete or throw
/// <see cref="StageFailedException"/> with an already classified, user-safe failure; <see cref="RunAsync"/> is the only place that
/// turns that (or cancellation, or anything unexpected) into a result, and the only place that cleans up.
/// </summary>
internal sealed class DatabaseBackupExecution
{
    private readonly DatabaseBackupPipelineRequest _request;
    private readonly IDatabaseBackupProvider? _provider;
    private readonly IReadOnlyDictionary<StorageProviderId, IStorageProvider> _storages;
    private readonly IArtifactPreparer _preparer;
    private readonly BackupStageTracker _tracker;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly AgentStagingDirectory _staging;
    private readonly List<BackupWarning> _warnings = [];
    private readonly List<BackupFailure> _cleanupFailures = [];
    private readonly StagingUsageRegistry? _usage;
    private readonly List<IDisposable> _holds = [];

    private DateTimeOffset _startedAt;
    private BackupPreCheckReport? _preCheck;
    private FullBackupResult? _providerResult;
    private StagedBackupFile? _stagedFile;
    private BackupSourceFile? _sourceFile;
    private PreparedArtifact? _prepared;
    private BackupArtifactInfo? _artifact;
    private readonly List<BackupDestinationResult> _destinations = [];
    private bool _transferCancelled;
    private StagingCleanup _stagingCleanup = StagingCleanup.NotNeeded;
    private RetainedTransfer? _retained;

    public DatabaseBackupExecution(
        DatabaseBackupPipelineRequest request,
        IDatabaseBackupProvider? provider,
        IReadOnlyDictionary<StorageProviderId, IStorageProvider> storages,
        IArtifactPreparer preparer,
        BackupStageTracker tracker,
        ILogger logger,
        TimeProvider time,
        StagingUsageRegistry? usage = null)
    {
        _request = request;
        _provider = provider;
        _storages = storages;
        _preparer = preparer;
        _tracker = tracker;
        _logger = logger;
        _time = time;
        _usage = usage;
        _staging = new AgentStagingDirectory(request.Staging.AgentDirectory);
    }

    public async Task<DatabaseBackupPipelineResult> RunAsync(CancellationToken cancellationToken)
    {
        // Holds outlive every cleanup below: until the very end of the run the staging sweeper must see these files as in use.
        try
        {
            return await RunCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ReleaseHolds();
        }
    }

    private async Task<DatabaseBackupPipelineResult> RunCoreAsync(CancellationToken cancellationToken)
    {
        _startedAt = _time.GetUtcNow();
        BackupFailure? failure = null;
        try
        {
            await RunStagesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (TransferPausedException paused)
        {
            return Build(DatabaseBackupStatus.Paused, BackupExecutionStage.Uploading, null) with { PauseReason = paused.Reason };
        }
        catch (OperationCanceledException) when (_request.ShutdownToken.IsCancellationRequested && _artifact is not null)
        {
            return Build(DatabaseBackupStatus.Paused, BackupExecutionStage.Uploading, null) with { PauseReason = "PausedByNetworkError" };
        }
        catch (StageFailedException exception)
        {
            failure = exception.Failure;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            failure = new BackupFailure(
                BackupFailureKind.Cancelled,
                _tracker.Current ?? BackupExecutionStage.Preparing,
                BackupPipelineReasons.Cancelled,
                "The backup was cancelled.");
        }
        catch (Exception exception)
        {
            var stage = _tracker.Current ?? BackupExecutionStage.Preparing;
            failure = Classify(stage, BackupPipelineReasons.Unexpected, "An unexpected error stopped the backup. Details are in the agent log.", exception);
        }

        if (failure is null)
        {
            CleanupAfterSuccess();
            _tracker.Enter(BackupExecutionStage.Completed);
            return Build(DatabaseBackupStatus.Succeeded, BackupExecutionStage.Completed, null);
        }

        var cancelled = failure.Kind == BackupFailureKind.Cancelled;
        await CleanupAfterFailureAsync(cancelled).ConfigureAwait(false);
        var final = cancelled ? BackupExecutionStage.Cancelled : BackupExecutionStage.Failed;
        _tracker.Enter(final);
        return Build(cancelled ? DatabaseBackupStatus.Cancelled : DatabaseBackupStatus.Failed, final, failure);
    }

    private async Task RunStagesAsync(CancellationToken cancellationToken)
    {
        _tracker.Enter(BackupExecutionStage.Preparing);
        var (provider, targets) = ResolveProviders();

        if (_request.ResumeTransfer)
        {
            _retained = RetainedTransfer.Load(_staging, _request.AttemptId);
            _artifact = _retained.Artifact;
            _sourceFile = _retained.SourceFile;
            _startedAt = _retained.StartedAt;
            Hold(_artifact.FileName);
            Hold(_sourceFile.FileName);
            await using var retainedStream = OpenForRead(_artifact.FileName);
            if (retainedStream.Length != _artifact.SizeBytes ||
                await ContentHasher.ComputeSha256Async(retainedStream, null, cancellationToken) != _artifact.Checksum)
                throw Fail(BackupExecutionStage.HashingArtifact, BackupPipelineReasons.StagedFileChanged, "The retained artifact no longer matches its checkpoint.");
        }
        else
        {
            await PreCheckAsync(provider, cancellationToken).ConfigureAwait(false);
            await BackUpAsync(provider, cancellationToken).ConfigureAwait(false);
            await HashSourceAsync(cancellationToken).ConfigureAwait(false);
            await PrepareArtifactAsync(cancellationToken).ConfigureAwait(false);
            await HashArtifactAsync(cancellationToken).ConfigureAwait(false);
            _retained = new RetainedTransfer(_artifact!, _sourceFile!, _providerResult?.Source, _providerResult?.Options,
                _providerResult?.Verification ?? BackupVerification.NotRun, _startedAt, _providerResult?.BackupStartedAt, _providerResult?.BackupFinishedAt);
            _retained.Save(_staging, _request.AttemptId);
        }
        if (_request.MaxArtifactBytes is { } maximum && _artifact!.SizeBytes > maximum)
            throw Fail(BackupExecutionStage.HashingArtifact, BackupPipelineReasons.QuotaExceeded,
                "The backup artifact exceeds its hard-quota reservation; no destination was written.", kind: BackupFailureKind.PreCheckFailed);
        await TransferAsync(targets, cancellationToken).ConfigureAwait(false);
    }

    private (IDatabaseBackupProvider Provider, (BackupPipelineDestination Destination, IStorageProvider? Storage)[] Targets) ResolveProviders()
    {
        if (_provider is null)
        {
            throw Fail(BackupExecutionStage.Preparing, BackupPipelineReasons.EngineNotAvailable,
                "No backup provider is registered for the requested database engine.");
        }

        // Task-scoped providers win over host-registered ones. A destination this agent cannot reach is not skipped: it ends Failed below.
        var targets = _request.EffectiveDestinations
            .Select(destination => (destination, destination.StorageOverride ?? _storages.GetValueOrDefault(destination.Storage)))
            .ToArray();
        // Refused BEFORE the engine runs: a backup that can never reach the required number of copies must not be taken.
        if (targets.Count(target => target.Item2 is not null) < Math.Max(1, _request.RequiredVerifiedReplicas))
        {
            throw Fail(BackupExecutionStage.Preparing, BackupPipelineReasons.StorageNotAvailable, targets.Length == 1
                ? "The requested storage provider is not available on this agent."
                : "Too few of the requested storage destinations are available on this agent to reach the required number of copies.");
        }

        return (_provider, targets);
    }

    private async Task PreCheckAsync(IDatabaseBackupProvider provider, CancellationToken cancellationToken)
    {
        const BackupExecutionStage stage = BackupExecutionStage.Preparing;
        try
        {
            _preCheck = await provider.PreCheckFullBackupAsync(
                _request.Connection,
                _request.Credential,
                new FullBackupPreCheckRequest(_request.DatabaseName, staging: _request.Staging),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not StageFailedException)
        {
            throw Fail(stage, BackupPipelineReasons.PreCheckError, "The backup pre-check could not be completed. Details are in the agent log.", exception);
        }

        foreach (var item in _preCheck.Items.Where(item => item.Status == BackupPreCheckStatus.Warning))
        {
            _warnings.Add(new BackupWarning($"precheck.{item.CheckId}.{item.ReasonCode}", item.Message));
        }

        if (!_preCheck.CanProceed)
        {
            var first = _preCheck.Items.First(item => item.Status == BackupPreCheckStatus.Failed);
            throw Fail(stage, first.ReasonCode, first.Message, kind: BackupFailureKind.PreCheckFailed, cause: first.Cause);
        }
    }

    private async Task BackUpAsync(IDatabaseBackupProvider provider, CancellationToken cancellationToken)
    {
        FullBackupRequest backupRequest;
        FullBackupResult result;
        try
        {
            // Held before the provider is called: the name must be protected before SQL Server creates the file.
            var stagedFileName = ManagedBackupLayout.StagedFileName(_request.DatabaseName, _startedAt, Guid.NewGuid());
            Hold(stagedFileName);
            backupRequest = new FullBackupRequest(
                _request.DatabaseName,
                _request.Staging,
                stagedFileName,
                _request.Options,
                _request.BackupTimeout,
                _request.VerifyTimeout,
                _request.StagingOnFailure);
            result = await provider.ExecuteFullBackupAsync(
                _request.Connection,
                _request.Credential,
                backupRequest,
                new CallbackProgress<BackupProgress>(_tracker.Forward),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not StageFailedException)
        {
            throw Fail(BackupExecutionStage.BackingUp, BackupPipelineReasons.ProviderError, "The backup provider failed unexpectedly. Details are in the agent log.", exception);
        }

        _providerResult = result;
        _warnings.AddRange(result.Warnings);
        if (result.Status != FullBackupStatus.Succeeded)
        {
            // The provider owns the partial file it created and has already dealt with it.
            _stagingCleanup = result.Cleanup;
            throw new StageFailedException(result.Failure!);
        }

        _stagedFile = result.StagedFile;
    }

    private async Task HashSourceAsync(CancellationToken cancellationToken)
    {
        const BackupExecutionStage stage = BackupExecutionStage.HashingSource;
        _tracker.Enter(stage);
        var staged = _stagedFile!;
        var (checksum, size) = await HashFileAsync(
            staged.FileName, stage, BackupPipelineReasons.StagedFileMissing, BackupPipelineReasons.SourceHashFailed,
            BackupFailureKind.SourceHashFailed, cancellationToken).ConfigureAwait(false);
        if (size != staged.SizeBytes)
        {
            throw Fail(stage, BackupPipelineReasons.StagedFileChanged,
                "The staged backup file changed after it was verified, so it cannot be trusted.", kind: BackupFailureKind.SourceHashFailed);
        }

        _sourceFile = new BackupSourceFile(staged.FileName, size, checksum);
    }

    private async Task PrepareArtifactAsync(CancellationToken cancellationToken)
    {
        const BackupExecutionStage stage = BackupExecutionStage.PreparingArtifact;
        _tracker.Enter(stage);
        try
        {
            _prepared = await _preparer.PrepareAsync(
                new ArtifactPreparationInput(_staging, _sourceFile!.FileName, DatabaseBackupType.Full, _providerResult!.Options!.Applied, _request.Encryption),
                cancellationToken).ConfigureAwait(false);
            Hold(_prepared.FileName);
            if (!_staging.FileExists(_prepared.FileName))
            {
                throw Fail(stage, BackupPipelineReasons.ArtifactFileMissing,
                    "Artifact preparation did not leave a file in the staging directory.", kind: BackupFailureKind.ArtifactPreparationFailed);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not StageFailedException)
        {
            throw Fail(stage, BackupPipelineReasons.ArtifactPreparationFailed,
                "The backup artifact could not be prepared. Details are in the agent log.", exception, BackupFailureKind.ArtifactPreparationFailed);
        }
    }

    private async Task HashArtifactAsync(CancellationToken cancellationToken)
    {
        const BackupExecutionStage stage = BackupExecutionStage.HashingArtifact;
        _tracker.Enter(stage);
        var prepared = _prepared!;
        var (checksum, size) = await HashFileAsync(
            prepared.FileName, stage, BackupPipelineReasons.ArtifactFileMissing, BackupPipelineReasons.ArtifactHashFailed,
            BackupFailureKind.ArtifactPreparationFailed, cancellationToken).ConfigureAwait(false);
        _artifact = new BackupArtifactInfo(prepared.FileName, size, checksum, prepared.IsSourceFile, prepared.Transforms, prepared.Seal);
    }

    /// <summary>
    /// Copies the one artifact to every destination, one after the other. Each destination ends with its OWN result: a destination that fails (or
    /// times out) never touches another's copy, and a copy that was verified is never rolled back. The artifact file stays in staging until the last
    /// destination is done; every destination opens its own read stream, so no destination can consume another's.
    /// </summary>
    private async Task TransferAsync((BackupPipelineDestination Destination, IStorageProvider? Storage)[] targets, CancellationToken cancellationToken)
    {
        foreach (var (destination, storage) in targets)
        {
            if (_request.PauseToken.IsCancellationRequested) throw new TransferPausedException("Operator");
            if (!_request.ResumeTransfer && _request.RecordCheckpointAsync is { } record && destination.ConnectionId is { } destinationId)
                await record(new TransferCheckpointContract { StorageProvider = destination.Storage.Value, DestinationId = destinationId }, cancellationToken).ConfigureAwait(false);
            if (_request.TimeRulePauseToken.IsCancellationRequested) throw new TransferPausedException("TimeRule");
            if (_transferCancelled || cancellationToken.IsCancellationRequested)
            {
                _transferCancelled = true;
                var now = _time.GetUtcNow();
                _destinations.Add(new BackupDestinationResult(destination, BackupDestinationState.Cancelled, null,
                    new BackupFailure(BackupFailureKind.Cancelled, BackupExecutionStage.Uploading, BackupPipelineReasons.Cancelled, "The backup was cancelled."),
                    false, null, false, now, now));
                continue;
            }

            _destinations.Add(await TransferOneAsync(destination, storage, cancellationToken).ConfigureAwait(false));
        }

        if (_destinations.Any(result => result.State == BackupDestinationState.Verified))
        {
            if (_request.ReplicationPolicySatisfied is { } policy && !policy(_destinations))
                throw Fail(BackupExecutionStage.VerifyingReplica, BackupPipelineReasons.StorageNotAvailable,
                    "The verified replicas do not satisfy the admitted 3-2-1 policy.", kind: BackupFailureKind.StorageWriteFailed,
                    cause: BackupFailureCause.StorageUnavailable);
            // At least one verified copy exists: the artifact is real and is published with whatever else happened. Cancellation does not unmake it.
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new StageFailedException(_destinations.Select(result => result.Failure).First(failure => failure is not null)!);
    }

    private async Task<BackupDestinationResult> TransferOneAsync(BackupPipelineDestination destination, IStorageProvider? storage, CancellationToken cancellationToken)
    {
        var startedAt = _time.GetUtcNow();
        var transfer = new Transfer();
        BackupFailure? failure = null;
        BackupReplicaInfo? replica = null;
        var cancelled = false;
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _request.PauseToken, _request.TimeRulePauseToken);
        bounded.CancelAfter(_request.DestinationTimeout ?? DatabaseBackupPipelineRequest.DefaultDestinationTimeout);
        try
        {
            if (storage is null)
            {
                throw Fail(BackupExecutionStage.Uploading, BackupPipelineReasons.StorageNotAvailable,
                    "The requested storage provider is not available on this agent.", kind: BackupFailureKind.StorageWriteFailed, cause: BackupFailureCause.Unsupported);
            }

            await UploadAsync(destination, storage, transfer, bounded, bounded.Token).ConfigureAwait(false);
            replica = await VerifyReplicaAsync(storage, transfer, bounded.Token).ConfigureAwait(false);
        }
        catch (TransferPausedException) { throw; }
        catch (OperationCanceledException) when (_request.PauseToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TransferPausedException("Operator");
        }
        catch (OperationCanceledException) when (_request.TimeRulePauseToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TransferPausedException("TimeRule");
        }
        catch (StageFailedException exception)
        {
            failure = exception.Failure;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancelled = true;
            _transferCancelled = true;
            failure = new BackupFailure(BackupFailureKind.Cancelled, _tracker.Current ?? BackupExecutionStage.Uploading, BackupPipelineReasons.Cancelled, "The backup was cancelled.");
        }
        catch (OperationCanceledException exception)
        {
            failure = Classify(_tracker.Current ?? BackupExecutionStage.Uploading, BackupPipelineReasons.DestinationTimeout,
                "The storage destination did not finish within the allowed time.", exception, null, BackupFailureCause.Timeout);
        }
        catch (Exception exception)
        {
            failure = Classify(_tracker.Current ?? BackupExecutionStage.Uploading, BackupPipelineReasons.Unexpected,
                "An unexpected error stopped a storage destination. Details are in the agent log.", exception);
        }

        var objectMayRemain = false;
        if (replica is null && transfer.Stored && storage is not null && transfer.Key is not null)
        {
            objectMayRemain = !await RemoveUnverifiedReplicaAsync(storage, transfer.Key).ConfigureAwait(false);
        }
        // A verified or aborted upload no longer needs the journal; one whose abort failed stays journaled so a later sweep aborts it.
        if (replica is not null || storage is null || transfer.Key is null || transfer.ProviderState is not { } abandoned
            || await AbortAbandonedUploadAsync(storage, transfer.Key, abandoned).ConfigureAwait(false))
            ForgetUpload(destination);

        return new BackupDestinationResult(
            destination,
            replica is not null ? BackupDestinationState.Verified : cancelled ? BackupDestinationState.Cancelled : BackupDestinationState.Failed,
            replica, failure, transfer.Stored, transfer.Key, objectMayRemain, startedAt, _time.GetUtcNow());
    }

    /// <summary>A failed or cancelled destination never resumes, so the remote parts of its checkpointed upload are aborted. False when it could not be proven aborted; that is reported, not swallowed.</summary>
    private async Task<bool> AbortAbandonedUploadAsync(IStorageProvider storage, StorageObjectKey key, string providerState)
    {
        try
        {
            using var bounded = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await storage.AbortTransferAsync(key, providerState, bounded.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception)
        {
            RecordCleanupFailure(BackupExecutionStage.Uploading, BackupPipelineReasons.ReplicaCleanupFailed,
                "An abandoned upload could not be aborted and its uploaded parts may still be present in the storage.", exception);
            return false;
        }
    }

    /// <summary>Journals the newest checkpoint of a destination's upload BEFORE the server hears of it, so no remote upload id exists that the agent cannot abort later.</summary>
    private void JournalUpload(BackupPipelineDestination destination, StorageObjectKey? key, string providerState)
    {
        if (_retained is null || destination.ConnectionId is not { } destinationId || key is null) return;
        _retained = _retained.With(new RetainedUpload(destination.Storage.Value, destinationId, key.Value, providerState));
        _retained.Save(_staging, _request.AttemptId);
    }

    private void ForgetUpload(BackupPipelineDestination destination)
    {
        if (_retained is null || destination.ConnectionId is not { } destinationId) return;
        _retained = _retained.Without(destination.Storage.Value, destinationId);
        // A journal that cannot be rewritten keeps a stale entry; aborting a finished upload later is harmless.
        try { _retained.Save(_staging, _request.AttemptId); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private async Task UploadAsync(BackupPipelineDestination destination, IStorageProvider storage, Transfer transfer, CancellationTokenSource destinationDeadline, CancellationToken cancellationToken)
    {
        const BackupExecutionStage stage = BackupExecutionStage.Uploading;
        _tracker.Enter(stage);
        var artifact = _artifact!;

        try
        {
            transfer.Key = _request.StoragePrefix.Combine(artifact.FileName);
        }
        catch (ArgumentException exception)
        {
            throw Fail(stage, BackupPipelineReasons.StorageInvalidKey,
                "The artifact name cannot be used as a storage key.", exception, BackupFailureKind.StorageWriteFailed);
        }

        FileStream stream;
        try
        {
            stream = OpenForRead(artifact.FileName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw Fail(stage, BackupPipelineReasons.ArtifactFileUnreadable,
                "The artifact file could not be read for upload.", exception, BackupFailureKind.StorageWriteFailed);
        }

        await using (stream.ConfigureAwait(false))
        {
            // The same staged file feeds every destination; one that changed since it was hashed must not be sent to any of them.
            if (stream.Length != artifact.SizeBytes)
            {
                throw Fail(stage, BackupPipelineReasons.StagedFileChanged,
                    "The staged artifact changed after it was hashed, so it cannot be uploaded.", kind: BackupFailureKind.StorageWriteFailed);
            }

            IUploadBandwidthSession? pacing;
            try
            {
                pacing = _request.UploadBandwidth is { } bandwidth ? await bandwidth.BeginAsync(storage.Topology, cancellationToken).ConfigureAwait(false) : null;
            }
            catch (UploadPolicyException exception)
            {
                throw Fail(stage, BackupPipelineReasons.UploadPolicyUnavailable, "The upload limits could not be obtained, so the upload was not started.",
                    exception, BackupFailureKind.StorageWriteFailed, BackupFailureCause.StorageUnavailable);
            }

            StorageObjectMetadata? stored = null;
            await using var pacingScope = pacing;
            using var pauseRegistration = pacing?.TimeRulePauseToken.Register(() => destinationDeadline.Cancel());
            try
            {
                if (pacing is not null)
                {
                    // A paced upload is slow on purpose: it must not run into the time allowed for an unpaced one.
                    destinationDeadline.CancelAfter((_request.DestinationTimeout ?? DatabaseBackupPipelineRequest.DefaultDestinationTimeout) + pacing.DeadlineAllowance(artifact.SizeBytes));
                }
                var progress = new CallbackProgress<long>(bytes => _tracker.Report(
                    stage, artifact.SizeBytes > 0 ? bytes * 100.0 / artifact.SizeBytes : 100, bytes, artifact.SizeBytes));
                var prior = _request.Checkpoints.SingleOrDefault(x => x.StorageProvider == destination.Storage.Value && x.DestinationId == destination.ConnectionId);
                var checkpoint = prior is null ? null : new StorageTransferCheckpoint(prior.VerifiedByteOffset, prior.ProviderState);
                transfer.ProviderState = prior?.ProviderState;
                var options = new StoragePutOptions(_request.Overwrite, artifact.Checksum, artifact.SizeBytes)
                {
                    RecordCheckpointAsync = async (next, token) =>
                    {
                        transfer.ProviderState = next.ProviderState;
                        if (next.ProviderState is { } state) JournalUpload(destination, transfer.Key, state);
                        if (_request.RecordCheckpointAsync is { } record && destination.ConnectionId is { } destinationId)
                            await record(new TransferCheckpointContract { StorageProvider = destination.Storage.Value, DestinationId = destinationId,
                                VerifiedByteOffset = next.VerifiedByteOffset, ProviderState = next.ProviderState }, token).ConfigureAwait(false);
                        checkpoint = next;
                    }
                };
                for (var retry = 0; ; retry++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    stream.Position = 0;
                    try
                    {
                        stored = await storage.PutAsync(transfer.Key, pacing?.Wrap(stream) ?? stream, options with { Checkpoint = checkpoint }, progress, cancellationToken).ConfigureAwait(false);
                        break;
                    }
                    catch (StorageException error) when (error.Kind is StorageFailureKind.NetworkUnavailable or StorageFailureKind.Timeout)
                    {
                        if (retry == 3) throw new TransferPausedException("PausedByNetworkError");
                        await Task.Delay(TimeSpan.FromSeconds(1 << retry), _time, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (pacing?.TimeRulePauseToken.IsCancellationRequested == true
                && !_request.PauseToken.IsCancellationRequested && !_request.TimeRulePauseToken.IsCancellationRequested)
            {
                throw new TransferPausedException("TimeRule");
            }
            catch (StorageException exception) when (exception.Kind == StorageFailureKind.AlreadyExists && _request.ResumeTransfer)
            {
                // A destination that finished before the pause still holds this attempt's object under its deterministic key: it is verified, not re-uploaded.
            }
            catch (StorageException exception)
            {
                if (exception is { ObjectMayRemain: true, RemainingObjectKey: { } remaining })
                {
                    transfer.Stored = true;
                    transfer.Key = remaining;
                }
                throw Fail(stage, StorageReason(exception.Kind), exception.Message, exception, BackupFailureKind.StorageWriteFailed, StorageCause(exception.Kind));
            }
            catch (OperationCanceledException exception) when (exception.Data[nameof(StorageException)] is StorageException { ObjectMayRemain: true, RemainingObjectKey: { } })
            {
                transfer.Stored = true;
                transfer.Key = ((StorageException)exception.Data[nameof(StorageException)]!).RemainingObjectKey;
                throw;
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not StageFailedException and not TransferPausedException)
            {
                throw Fail(stage, BackupPipelineReasons.StorageUnexpected,
                    "The storage provider failed unexpectedly. Details are in the agent log.", exception, BackupFailureKind.StorageWriteFailed,
                    BackupFailureCause.StorageUnavailable);
            }

            // From here on an object exists under the final key; if anything below fails it must not be left behind unverified.
            transfer.Stored = true;
            if (stored is not null && (stored.SizeBytes != artifact.SizeBytes || (stored.Checksum is { } checksum && checksum != artifact.Checksum)))
            {
                throw Fail(stage, BackupPipelineReasons.StorageIntegrityMismatch,
                    "The storage reported a different size or checksum than the artifact that was sent.", kind: BackupFailureKind.StorageWriteFailed,
                    cause: BackupFailureCause.StorageUnavailable);
            }
        }
    }

    private async Task<BackupReplicaInfo> VerifyReplicaAsync(IStorageProvider storage, Transfer transfer, CancellationToken cancellationToken)
    {
        const BackupExecutionStage stage = BackupExecutionStage.VerifyingReplica;
        _tracker.Enter(stage);
        var artifact = _artifact!;
        StorageVerificationResult verification;
        try
        {
            for (var retry = 0; ; retry++)
            {
                try
                {
                    verification = await storage.VerifyAsync(transfer.Key!, new StorageVerificationExpectation(artifact.SizeBytes, artifact.Checksum), cancellationToken).ConfigureAwait(false);
                    break;
                }
                catch (StorageException error) when (error.Kind is StorageFailureKind.NetworkUnavailable or StorageFailureKind.Timeout)
                {
                    if (retry == 3) throw new TransferPausedException("PausedByNetworkError");
                    await Task.Delay(TimeSpan.FromSeconds(1 << retry), _time, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not StageFailedException and not TransferPausedException)
        {
            var message = exception is StorageException storageException
                ? storageException.Message
                : "The stored copy could not be verified. Details are in the agent log.";
            throw Fail(stage, BackupPipelineReasons.ReplicaVerificationError, message, exception, BackupFailureKind.ReplicaVerificationFailed);
        }

        var (reason, text) = verification.Status switch
        {
            StorageVerificationStatus.Verified => (null, null),
            StorageVerificationStatus.Missing => (BackupPipelineReasons.ReplicaMissing, "The stored copy is missing after the upload."),
            StorageVerificationStatus.SizeMismatch => (BackupPipelineReasons.ReplicaSizeMismatch, "The stored copy has a different size than the artifact."),
            StorageVerificationStatus.ChecksumMismatch => (BackupPipelineReasons.ReplicaChecksumMismatch, "The stored copy has a different checksum than the artifact."),
            _ => (BackupPipelineReasons.ReplicaInconclusive, "The storage could not confirm the size and checksum of the stored copy.")
        };

        if (reason is not null)
        {
            throw Fail(stage, reason, text!, kind: BackupFailureKind.ReplicaVerificationFailed);
        }

        return new BackupReplicaInfo(
            verification.Location,
            verification.ActualSizeBytes ?? artifact.SizeBytes,
            verification.ActualChecksum ?? artifact.Checksum,
            verification.Status,
            verification.Method,
            _time.GetUtcNow());
    }

    /// <summary>What one destination has done so far: the key it targets and whether an object was written under it.</summary>
    private sealed class Transfer
    {
        public StorageObjectKey? Key { get; set; }
        public bool Stored { get; set; }
        /// <summary>The newest resumable-upload state; remote parts exist for it until the upload is published or aborted.</summary>
        public string? ProviderState { get; set; }
    }

    private async Task<(ContentChecksum Checksum, long Size)> HashFileAsync(
        string fileName,
        BackupExecutionStage stage,
        string missingReason,
        string failedReason,
        BackupFailureKind kind,
        CancellationToken cancellationToken)
    {
        FileStream stream;
        try
        {
            stream = OpenForRead(fileName);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            throw Fail(stage, missingReason, "The staged file is missing.", exception, kind);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw Fail(stage, failedReason, "The staged file could not be read. Check the agent's access to the staging directory.", exception, kind);
        }

        await using (stream.ConfigureAwait(false))
        {
            var length = stream.Length;
            long read = 0;
            try
            {
                var checksum = await ContentHasher.ComputeSha256Async(
                    stream,
                    new CallbackProgress<long>(bytes =>
                    {
                        read = bytes;
                        _tracker.Report(stage, length > 0 ? bytes * 100.0 / length : 100, bytes, length);
                    }),
                    cancellationToken).ConfigureAwait(false);
                if (read != length)
                {
                    throw Fail(stage, BackupPipelineReasons.StagedFileChanged,
                        "The staged file changed while it was being checksummed.", kind: kind);
                }

                return (checksum, length);
            }
            catch (IOException exception)
            {
                throw Fail(stage, failedReason, "The staged file could not be read. Check the agent's access to the staging directory.", exception, kind);
            }
        }
    }

    private FileStream OpenForRead(string fileName) =>
        new(_staging.FilePath(fileName), FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1, FileOptions.Asynchronous | FileOptions.SequentialScan);

    private void Hold(string fileName)
    {
        if (_usage is not null)
        {
            _holds.Add(_usage.Hold(fileName));
        }
    }

    private void ReleaseHolds()
    {
        foreach (var hold in _holds)
        {
            hold.Dispose();
        }

        _holds.Clear();
    }

    /// <summary>The journal pins the staged files; it stays while a remote upload could not be aborted, so the cleaner can abort it once the attempt is terminal.</summary>
    private void ReleaseJournal()
    {
        if (_retained is { HasUploads: false }) _retained.Release(_staging, _request.AttemptId);
    }

    private void CleanupAfterSuccess()
    {
        // Reached only after the replica was verified: this is the earliest point at which staging may be released.
        ReleaseJournal();
        var failed = DeleteStaged(StagedNames());
        if (failed)
        {
            _stagingCleanup = new StagingCleanup(StagingCleanupStatus.Failed, BackupPipelineReasons.StagingCleanupFailed);
            _warnings.Add(new BackupWarning(
                BackupPipelineReasons.StagingCleanupFailed,
                "The backup is stored and verified, but its staging file could not be removed."));
            return;
        }

        _stagingCleanup = new StagingCleanup(StagingCleanupStatus.Deleted);
    }

    private async Task CleanupAfterFailureAsync(bool cancelled)
    {
        // Destinations clean up their own unverified objects as they fail (TransferOneAsync); only staging is left.
        ReleaseJournal();

        // A file handed over by the provider is ours to remove; a file the provider failed on was dealt with by the provider.
        if (_stagedFile is null)
        {
            return;
        }

        if (!cancelled && _request.StagingOnFailure == StagingFailurePolicy.Keep)
        {
            _stagingCleanup = new StagingCleanup(StagingCleanupStatus.Kept);
            return;
        }

        if (DeleteStaged(StagedNames()))
        {
            _stagingCleanup = new StagingCleanup(StagingCleanupStatus.Failed, BackupPipelineReasons.StagingCleanupFailed);
            _warnings.Add(new BackupWarning(BackupPipelineReasons.StagingCleanupFailed, "A staged backup file could not be removed and may still be present."));
            return;
        }

        _stagingCleanup = new StagingCleanup(StagingCleanupStatus.Deleted);
    }

    /// <summary>Removes an object that was written but never verified. False when it could not be proven removed: the object may still exist.</summary>
    private async Task<bool> RemoveUnverifiedReplicaAsync(IStorageProvider storage, StorageObjectKey key)
    {
        var stage = _tracker.Current ?? BackupExecutionStage.Uploading;
        if (!storage.Capabilities.Supports(StorageCapabilityIds.Delete))
        {
            RecordCleanupFailure(stage, BackupPipelineReasons.ReplicaCleanupFailed,
                "A stored copy that could not be verified remains because the storage cannot delete.", null);
            return false;
        }

        try
        {
            // Bounded, and independent of the (possibly already expired or cancelled) token of the transfer that failed.
            using var bounded = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await storage.DeleteAsync(key, bounded.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception)
        {
            RecordCleanupFailure(stage, BackupPipelineReasons.ReplicaCleanupFailed,
                "A stored copy that could not be verified could not be removed and may still be present.", exception);
            return false;
        }
    }

    /// <summary>Deletes the staged files; true when at least one could not be removed. Never throws.</summary>
    private bool DeleteStaged(IEnumerable<string> fileNames)
    {
        var stage = _tracker.Current ?? BackupExecutionStage.Preparing;
        var failed = false;
        foreach (var name in fileNames)
        {
            if (!_staging.TryDeleteFile(name))
            {
                failed = true;
                RecordCleanupFailure(stage, BackupPipelineReasons.StagingCleanupFailed, "A staged backup file could not be removed.", null);
            }
        }

        return failed;
    }

    private IEnumerable<string> StagedNames()
    {
        if (_request.ResumeTransfer && _retained is { } retained)
        {
            yield return retained.SourceFile.FileName;
            if (retained.Artifact.FileName != retained.SourceFile.FileName) yield return retained.Artifact.FileName;
            yield break;
        }
        if (_stagedFile is { } staged)
        {
            yield return staged.FileName;
        }

        if (_prepared is { IsSourceFile: false } prepared)
        {
            yield return prepared.FileName;
        }
    }

    private void RecordCleanupFailure(BackupExecutionStage stage, string reason, string message, Exception? exception)
    {
        DatabaseBackupPipeline.LogCleanupFailed(_logger, exception, stage, reason);
        _cleanupFailures.Add(new BackupFailure(BackupFailureKind.CleanupFailed, stage, reason, message));
    }

    private DatabaseBackupPipelineResult Build(DatabaseBackupStatus status, BackupExecutionStage finalStage, BackupFailure? failure)
    {
        DatabaseBackupPipeline.LogFinished(_logger, status, failure?.Stage ?? finalStage, failure?.ReasonCode);
        var provider = _providerResult;
        return new DatabaseBackupPipelineResult
        {
            Status = status,
            FinalStage = finalStage,
            Failure = failure,
            Source = provider?.Source ?? _retained?.Source,
            StartedAt = _startedAt,
            FinishedAt = _time.GetUtcNow(),
            BackupStartedAt = provider?.BackupStartedAt ?? _retained?.BackupStartedAt,
            BackupFinishedAt = provider?.BackupFinishedAt ?? _retained?.BackupFinishedAt,
            Options = provider?.Options ?? _retained?.Options,
            PreCheck = _preCheck,
            Verification = provider?.Verification ?? _retained?.Verification ?? BackupVerification.NotRun,
            SourceFile = _sourceFile,
            Artifact = _artifact,
            Destinations = _destinations.ToArray(),
            Warnings = _warnings.ToArray(),
            StagingCleanup = _stagingCleanup,
            CleanupFailures = _cleanupFailures.ToArray(),
            Stages = _tracker.Records.ToArray()
        };
    }

    private StageFailedException Fail(
        BackupExecutionStage stage,
        string reason,
        string message,
        Exception? exception = null,
        BackupFailureKind? kind = null,
        BackupFailureCause cause = BackupFailureCause.Unclassified) =>
        new(Classify(stage, reason, message, exception, kind, cause));

    private BackupFailure Classify(
        BackupExecutionStage stage,
        string reason,
        string message,
        Exception? exception,
        BackupFailureKind? kind = null,
        BackupFailureCause cause = BackupFailureCause.Unclassified)
    {
        var resolved = kind ?? KindOf(stage);
        DatabaseBackupPipeline.LogStageFailed(_logger, exception, stage, resolved, reason);
        return new BackupFailure(resolved, stage, reason, message, cause);
    }

    private static BackupFailureKind KindOf(BackupExecutionStage stage) => stage switch
    {
        BackupExecutionStage.Preparing => BackupFailureKind.PreCheckFailed,
        BackupExecutionStage.BackingUp => BackupFailureKind.SqlBackupFailed,
        BackupExecutionStage.Verifying => BackupFailureKind.VerificationFailed,
        BackupExecutionStage.HashingSource => BackupFailureKind.SourceHashFailed,
        BackupExecutionStage.PreparingArtifact or BackupExecutionStage.HashingArtifact => BackupFailureKind.ArtifactPreparationFailed,
        BackupExecutionStage.Uploading => BackupFailureKind.StorageWriteFailed,
        _ => BackupFailureKind.ReplicaVerificationFailed
    };

    private static BackupFailureCause StorageCause(StorageFailureKind kind) => kind switch
    {
        StorageFailureKind.AccessDenied or StorageFailureKind.AuthenticationFailed or StorageFailureKind.HostKeyMismatch => BackupFailureCause.PermissionDenied,
        StorageFailureKind.InvalidConfiguration => BackupFailureCause.InvalidConfiguration,
        StorageFailureKind.NotSupported => BackupFailureCause.Unsupported,
        StorageFailureKind.InsufficientSpace or StorageFailureKind.AlreadyExists => BackupFailureCause.Unclassified,
        _ => BackupFailureCause.StorageUnavailable
    };

    private static string StorageReason(StorageFailureKind kind) => kind switch
    {
        StorageFailureKind.AlreadyExists => BackupPipelineReasons.StorageAlreadyExists,
        StorageFailureKind.IntegrityMismatch => BackupPipelineReasons.StorageIntegrityMismatch,
        StorageFailureKind.InsufficientSpace => BackupPipelineReasons.StorageInsufficientSpace,
        StorageFailureKind.AccessDenied => BackupPipelineReasons.StorageAccessDenied,
        StorageFailureKind.AuthenticationFailed or StorageFailureKind.HostKeyMismatch => BackupPipelineReasons.StorageAuthenticationFailed,
        StorageFailureKind.NotSupported => BackupPipelineReasons.StorageNotSupported,
        StorageFailureKind.InvalidConfiguration => BackupPipelineReasons.StorageInvalidConfiguration,
        StorageFailureKind.Io => BackupPipelineReasons.StorageIoError,
        _ => BackupPipelineReasons.StorageUnexpected
    };
}

/// <summary>A stage ended in a classified failure. Carries the failure only; the exception that caused it was logged where it was classified.</summary>
internal sealed class StageFailedException(BackupFailure failure) : Exception(failure.Message)
{
    public BackupFailure Failure { get; } = failure;
}

/// <summary>Synchronous progress sink: unlike <see cref="Progress{T}"/> it does not post to the thread pool, so stage transitions keep their order.</summary>
internal sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
{
    public void Report(T value) => callback(value);
}
