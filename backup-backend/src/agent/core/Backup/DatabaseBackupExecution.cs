using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.StorageProviders.Abstractions;
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
    private readonly IStorageProvider? _storage;
    private readonly IArtifactPreparer _preparer;
    private readonly BackupStageTracker _tracker;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly AgentStagingDirectory _staging;
    private readonly List<BackupWarning> _warnings = [];
    private readonly List<BackupFailure> _cleanupFailures = [];

    private DateTimeOffset _startedAt;
    private BackupPreCheckReport? _preCheck;
    private FullBackupResult? _providerResult;
    private StagedBackupFile? _stagedFile;
    private BackupSourceFile? _sourceFile;
    private PreparedArtifact? _prepared;
    private BackupArtifactInfo? _artifact;
    private StorageObjectKey? _key;
    private bool _replicaStored;
    private BackupReplicaInfo? _replica;
    private StagingCleanup _stagingCleanup = StagingCleanup.NotNeeded;

    public DatabaseBackupExecution(
        DatabaseBackupPipelineRequest request,
        IDatabaseBackupProvider? provider,
        IStorageProvider? storage,
        IArtifactPreparer preparer,
        BackupStageTracker tracker,
        ILogger logger,
        TimeProvider time)
    {
        _request = request;
        _provider = provider;
        _storage = storage;
        _preparer = preparer;
        _tracker = tracker;
        _logger = logger;
        _time = time;
        _staging = new AgentStagingDirectory(request.Staging.AgentDirectory);
    }

    public async Task<DatabaseBackupPipelineResult> RunAsync(CancellationToken cancellationToken)
    {
        _startedAt = _time.GetUtcNow();
        BackupFailure? failure = null;
        try
        {
            await RunStagesAsync(cancellationToken).ConfigureAwait(false);
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
        var (provider, storage) = ResolveProviders();

        await PreCheckAsync(provider, cancellationToken).ConfigureAwait(false);
        await BackUpAsync(provider, cancellationToken).ConfigureAwait(false);
        await HashSourceAsync(cancellationToken).ConfigureAwait(false);
        await PrepareArtifactAsync(cancellationToken).ConfigureAwait(false);
        await HashArtifactAsync(cancellationToken).ConfigureAwait(false);
        await UploadAsync(storage, cancellationToken).ConfigureAwait(false);
        await VerifyReplicaAsync(storage, cancellationToken).ConfigureAwait(false);
    }

    private (IDatabaseBackupProvider Provider, IStorageProvider Storage) ResolveProviders()
    {
        if (_provider is null)
        {
            throw Fail(BackupExecutionStage.Preparing, BackupPipelineReasons.EngineNotAvailable,
                "No backup provider is registered for the requested database engine.");
        }

        if (_storage is null)
        {
            throw Fail(BackupExecutionStage.Preparing, BackupPipelineReasons.StorageNotAvailable,
                "The requested storage provider is not available on this agent.");
        }

        return (_provider, _storage);
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
            backupRequest = new FullBackupRequest(
                _request.DatabaseName,
                _request.Staging,
                BackupFileNaming.StagedFileName(_request.DatabaseName, _startedAt, Guid.NewGuid()),
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
                new ArtifactPreparationInput(_staging, _sourceFile!.FileName, DatabaseBackupType.Full, _providerResult!.Options!.Applied),
                cancellationToken).ConfigureAwait(false);
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
        _artifact = new BackupArtifactInfo(prepared.FileName, size, checksum, prepared.IsSourceFile, prepared.Transforms);
    }

    private async Task UploadAsync(IStorageProvider storage, CancellationToken cancellationToken)
    {
        const BackupExecutionStage stage = BackupExecutionStage.Uploading;
        _tracker.Enter(stage);
        var artifact = _artifact!;

        try
        {
            _key = _request.StoragePrefix.Combine(artifact.FileName);
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
            StorageObjectMetadata stored;
            try
            {
                var progress = new CallbackProgress<long>(bytes => _tracker.Report(
                    stage, artifact.SizeBytes > 0 ? bytes * 100.0 / artifact.SizeBytes : 100, bytes, artifact.SizeBytes));
                stored = await storage.PutAsync(
                    _key,
                    stream,
                    new StoragePutOptions(_request.Overwrite, artifact.Checksum, artifact.SizeBytes),
                    progress,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (StorageException exception)
            {
                throw Fail(stage, StorageReason(exception.Kind), exception.Message, exception, BackupFailureKind.StorageWriteFailed, StorageCause(exception.Kind));
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not StageFailedException)
            {
                throw Fail(stage, BackupPipelineReasons.StorageUnexpected,
                    "The storage provider failed unexpectedly. Details are in the agent log.", exception, BackupFailureKind.StorageWriteFailed,
                    BackupFailureCause.StorageUnavailable);
            }

            // From here on an object exists under the final key; if anything below fails it must not be left behind unverified.
            _replicaStored = true;
            if (stored.SizeBytes != artifact.SizeBytes || (stored.Checksum is { } checksum && checksum != artifact.Checksum))
            {
                throw Fail(stage, BackupPipelineReasons.StorageIntegrityMismatch,
                    "The storage reported a different size or checksum than the artifact that was sent.", kind: BackupFailureKind.StorageWriteFailed,
                    cause: BackupFailureCause.StorageUnavailable);
            }
        }
    }

    private async Task VerifyReplicaAsync(IStorageProvider storage, CancellationToken cancellationToken)
    {
        const BackupExecutionStage stage = BackupExecutionStage.VerifyingReplica;
        _tracker.Enter(stage);
        var artifact = _artifact!;
        StorageVerificationResult verification;
        try
        {
            verification = await storage.VerifyAsync(
                _key!, new StorageVerificationExpectation(artifact.SizeBytes, artifact.Checksum), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not StageFailedException)
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

        _replica = new BackupReplicaInfo(
            verification.Location,
            verification.ActualSizeBytes ?? artifact.SizeBytes,
            verification.ActualChecksum ?? artifact.Checksum,
            verification.Status,
            verification.Method,
            _time.GetUtcNow());
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

    private void CleanupAfterSuccess()
    {
        // Reached only after the replica was verified: this is the earliest point at which staging may be released.
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
        if (_replicaStored && _storage is not null && _key is not null)
        {
            await RemoveUnverifiedReplicaAsync(_storage, _key).ConfigureAwait(false);
        }

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

    private async Task RemoveUnverifiedReplicaAsync(IStorageProvider storage, StorageObjectKey key)
    {
        var stage = _tracker.Current ?? BackupExecutionStage.Uploading;
        if (!storage.Capabilities.Supports(StorageCapabilityIds.Delete))
        {
            RecordCleanupFailure(stage, BackupPipelineReasons.ReplicaCleanupFailed,
                "A stored copy that could not be verified remains because the storage cannot delete.", null);
            return;
        }

        try
        {
            await storage.DeleteAsync(key, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            RecordCleanupFailure(stage, BackupPipelineReasons.ReplicaCleanupFailed,
                "A stored copy that could not be verified could not be removed and may still be present.", exception);
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
            Source = provider?.Source,
            StartedAt = _startedAt,
            FinishedAt = _time.GetUtcNow(),
            BackupStartedAt = provider?.BackupStartedAt,
            BackupFinishedAt = provider?.BackupFinishedAt,
            Options = provider?.Options,
            PreCheck = _preCheck,
            Verification = provider?.Verification ?? BackupVerification.NotRun,
            SourceFile = _sourceFile,
            Artifact = _artifact,
            Replica = status == DatabaseBackupStatus.Succeeded ? _replica : null,
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
        StorageFailureKind.AccessDenied => BackupFailureCause.PermissionDenied,
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
