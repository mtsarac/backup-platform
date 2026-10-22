using System.Buffers;
using System.Security.Cryptography;
using BackupPlatform.Agent.Core.Backup;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Encryption;
using BackupPlatform.StorageProviders.Abstractions;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.Agent.Core.Restore;

/// <summary>
/// One run of the restore pipeline and everything it learns on the way. The stage methods either complete or throw
/// <see cref="RestoreStageFailedException"/> with an already classified, user-safe failure; <see cref="RunAsync"/> is the only place that
/// turns that (or cancellation, or anything unexpected) into a result, and the only place that cleans up. The one fact it must never lose is
/// what the target database was left as: the target is untouched until the provider's execute call starts, so everything before it is
/// <see cref="DatabaseRestoreTargetState.NotModified"/>, and an execute call that did not report its own state leaves it
/// <see cref="DatabaseRestoreTargetState.Unknown"/>.
/// </summary>
internal sealed class DatabaseRestoreExecution
{
    /// <summary>Bounded copy buffer: the replica is streamed through this block, never held in memory as a whole.</summary>
    private const int CopyBufferSize = 1024 * 1024;

    private readonly DatabaseRestorePipelineRequest _request;
    private readonly IDatabaseRestoreProvider? _provider;
    private readonly IStorageProvider? _storage;
    private readonly RestoreStageTracker _tracker;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly AgentStagingDirectory _staging;
    private readonly StagingUsageRegistry? _usage;
    private readonly List<IDisposable> _holds = [];
    private readonly List<BackupWarning> _warnings = [];
    private readonly List<BackupPreCheckItem> _preCheck = [];

    private DateTimeOffset _startedAt;
    private string? _stagedName;
    private readonly List<string> _stagedFiles = [];
    private long _copiedBytes;
    private bool _copyOverflow;
    private ContentChecksum? _copiedChecksum;
    private bool _checksumVerified;
    private bool _backupVerified;
    private RestoreBackupInspection? _inspection;
    private RestorePlan? _plan;
    private bool _executeStarted;
    private DatabaseRestoreResult? _executeResult;
    private StagingCleanup _stagingCleanup = StagingCleanup.NotNeeded;
    private RestoreVerificationReport? _verificationReport;
    private VerificationCleanup _verificationCleanup = VerificationCleanup.NotCreated;
    private string? _verificationCleanupReason;

    public DatabaseRestoreExecution(
        DatabaseRestorePipelineRequest request,
        IDatabaseRestoreProvider? provider,
        IStorageProvider? storage,
        RestoreStageTracker tracker,
        ILogger logger,
        TimeProvider time,
        StagingUsageRegistry? usage = null)
    {
        _request = request;
        _provider = provider;
        _storage = storage;
        _tracker = tracker;
        _logger = logger;
        _time = time;
        _usage = usage;
        _staging = new AgentStagingDirectory(request.Staging.AgentDirectory);
    }

    public async Task<DatabaseRestorePipelineResult> RunAsync(CancellationToken cancellationToken)
    {
        // The hold outlives every cleanup below: until the very end of the run the staging sweeper must see the staged copy as in use.
        try
        {
            return await RunCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            foreach (var hold in _holds)
            {
                hold.Dispose();
            }

            _holds.Clear();
        }
    }

    private async Task<DatabaseRestorePipelineResult> RunCoreAsync(CancellationToken cancellationToken)
    {
        _startedAt = _time.GetUtcNow();
        RestorePipelineFailure? failure = null;
        try
        {
            await RunStagesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (RestoreStageFailedException exception)
        {
            failure = exception.Failure;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            failure = new RestorePipelineFailure(
                RestoreAgentFailureCodes.Cancelled,
                _tracker.Current ?? RestoreExecutionStage.Preparing,
                RestorePipelineReasons.Cancelled,
                "The restore was cancelled.");
        }
        catch (Exception exception)
        {
            failure = Classify(
                _tracker.Current ?? RestoreExecutionStage.Preparing,
                RestoreAgentFailureCodes.InternalError,
                RestorePipelineReasons.Unexpected,
                "An unexpected error stopped the restore. Details are in the agent log.",
                exception);
        }

        // Whatever happened, a verification's disposable database is dropped before anything else is reported: it is a full copy of a customer's data.
        await CleanUpVerificationDatabaseAsync().ConfigureAwait(false);

        // Always, whatever happened: the staged copy is a full copy of a customer's database backup.
        CleanUpStagedCopy();

        if (failure is null)
        {
            _tracker.Enter(RestoreExecutionStage.Completed);
            return Build(DatabaseRestoreStatus.Succeeded, RestoreExecutionStage.Completed, null);
        }

        var cancelled = failure.Code == RestoreAgentFailureCodes.Cancelled;
        var final = cancelled ? RestoreExecutionStage.Cancelled : RestoreExecutionStage.Failed;
        _tracker.Enter(final);
        return Build(cancelled ? DatabaseRestoreStatus.Cancelled : DatabaseRestoreStatus.Failed, final, failure);
    }

    private async Task RunStagesAsync(CancellationToken cancellationToken)
    {
        _tracker.Enter(RestoreExecutionStage.Preparing);
        var (provider, storage) = ResolveProviders();

        // A stage boundary is a cancellation point whatever the providers do with the token: nothing starts after a cancellation was requested.
        cancellationToken.ThrowIfCancellationRequested();
        await PrepareAsync(provider, storage, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await DownloadAsync(storage, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        VerifyChecksum();
        cancellationToken.ThrowIfCancellationRequested();
        await DecryptAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await InspectAsync(provider, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await PlanAsync(provider, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (_request.Purpose == RestorePipelinePurpose.Precheck)
        {
            return;
        }
        await ExecuteAsync(provider, cancellationToken).ConfigureAwait(false);
        if (_request.Purpose == RestorePipelinePurpose.Verification)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await VerifyAsync(provider, cancellationToken).ConfigureAwait(false);
        }
    }

    private (IDatabaseRestoreProvider Provider, IStorageProvider Storage) ResolveProviders()
    {
        if (_provider is null)
        {
            throw Fail(RestoreExecutionStage.Preparing, RestoreAgentFailureCodes.UnsupportedCapability, RestorePipelineReasons.EngineNotAvailable,
                "This agent cannot restore databases of the requested engine.");
        }

        if (_storage is null)
        {
            throw FailCheck(RestoreExecutionStage.Preparing, RestoreAgentFailureCodes.ReplicaUnavailable, RestorePipelineReasons.Checks.SourceStorage,
                RestorePipelineReasons.StorageNotAvailable, "The storage that holds the replica is not available on this agent.");
        }

        if (!_storage.Capabilities.Supports(StorageCapabilityIds.Read))
        {
            throw FailCheck(RestoreExecutionStage.Preparing, RestoreAgentFailureCodes.UnsupportedCapability, RestorePipelineReasons.Checks.SourceStorage,
                RestorePipelineReasons.StorageNotReadable, "The storage that holds the replica cannot be read by this agent.");
        }

        Pass(RestorePipelineReasons.Checks.SourceStorage, "storage-readable", "The storage that holds the replica is available and readable.");
        return (_provider, _storage);
    }

    // ---- Preparing ------------------------------------------------------------------------------------------------------------

    private async Task PrepareAsync(IDatabaseRestoreProvider provider, IStorageProvider storage, CancellationToken cancellationToken)
    {
        const RestoreExecutionStage stage = RestoreExecutionStage.Preparing;
        ValidateKey(stage);
        InspectStagingDirectory(stage);
        await CheckReplicaAsync(storage, stage, cancellationToken).ConfigureAwait(false);

        BackupPreCheckReport report;
        try
        {
            report = await provider.PreCheckRestoreAsync(
                _request.Connection,
                _request.Credential,
                new RestorePreCheckRequest(_request.DatabaseName, _request.Mode, _request.TerminateActiveConnections, _request.ExpectedSizeBytes),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not RestoreStageFailedException)
        {
            throw Fail(stage, RestoreAgentFailureCodes.InternalError, RestorePipelineReasons.PreCheckError,
                "The restore pre-check could not be completed. Details are in the agent log.", exception);
        }

        _preCheck.AddRange(report.Items);
        FailOnFailedItem(report, stage);
    }

    /// <summary>The replica key must be one the platform wrote for THIS tenant: a key of another tenant is never read, however it got here.</summary>
    private void ValidateKey(RestoreExecutionStage stage)
    {
        if (!ManagedBackupLayout.TryParseStorageKey(_request.StorageKey.Value, out var parsed) || parsed is null || parsed.IsPartial)
        {
            throw FailCheck(stage, RestoreAgentFailureCodes.InvalidConfiguration, RestorePipelineReasons.Checks.SourceKey,
                RestorePipelineReasons.InvalidReplicaKey, "The replica's storage key is not a key of the platform's own layout.");
        }

        if (parsed.TenantId != _request.TenantId)
        {
            throw FailCheck(stage, RestoreAgentFailureCodes.InvalidConfiguration, RestorePipelineReasons.Checks.SourceKey,
                RestorePipelineReasons.ReplicaTenantMismatch, "The replica does not belong to the tenant of this restore.");
        }

        Pass(RestorePipelineReasons.Checks.SourceKey, "replica-key-valid", "The replica's storage key belongs to this tenant.");
    }

    /// <summary>What only the agent can know: its own side of the staging directory exists, is writable and has room for the whole replica.</summary>
    private void InspectStagingDirectory(RestoreExecutionStage stage)
    {
        var access = _staging.Inspect();
        if (!access.DirectoryExists)
        {
            throw FailCheck(stage, RestoreAgentFailureCodes.StagingInvalid, BackupPreCheckIds.StagingAgentAccess,
                RestorePipelineReasons.StagingMissing, "The staging directory does not exist on the agent.", BackupFailureCause.StagingInvalid);
        }

        if (!access.CanWrite)
        {
            throw FailCheck(stage, RestoreAgentFailureCodes.StagingInvalid, BackupPreCheckIds.StagingAgentAccess,
                RestorePipelineReasons.StagingNotWritable, "The agent cannot write to the staging directory.", BackupFailureCause.StagingInvalid);
        }

        Pass(BackupPreCheckIds.StagingAgentAccess, "staging-writable", "The agent can write to the staging directory.");

        if (access.FreeBytes is not { } free)
        {
            Warn(BackupPreCheckIds.StagingDiskSpace, RestorePipelineReasons.StagingSpaceUnknown,
                "The free space of the staging volume could not be determined.");
        }
        else if (free < _request.ExpectedSizeBytes * (_request.Decryption is null ? 1 : 2))
        {
            throw FailCheck(stage, RestoreAgentFailureCodes.InsufficientDiskSpace, BackupPreCheckIds.StagingDiskSpace,
                RestorePipelineReasons.StagingInsufficientSpace, "The staging volume does not have enough free space for the backup file.",
                BackupFailureCause.StagingInvalid);
        }
        else
        {
            Pass(BackupPreCheckIds.StagingDiskSpace, "staging-space-sufficient", "The staging volume has enough free space for the backup file.");
        }
    }

    /// <summary>The replica must still exist, with the size it was published with; a different size means the bytes changed since.</summary>
    private async Task CheckReplicaAsync(IStorageProvider storage, RestoreExecutionStage stage, CancellationToken cancellationToken)
    {
        StorageObjectMetadata? metadata;
        try
        {
            metadata = await storage.GetMetadataAsync(_request.StorageKey, cancellationToken).ConfigureAwait(false);
        }
        catch (StorageException exception)
        {
            throw StorageFailure(stage, exception, RestorePipelineReasons.Checks.SourceReplica);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not RestoreStageFailedException)
        {
            throw FailCheck(stage, RestoreAgentFailureCodes.StorageUnavailable, RestorePipelineReasons.Checks.SourceReplica,
                RestorePipelineReasons.ReplicaReadFailed, "The storage failed unexpectedly. Details are in the agent log.",
                BackupFailureCause.StorageUnavailable, exception);
        }

        if (metadata is null)
        {
            throw FailCheck(stage, RestoreAgentFailureCodes.ReplicaUnavailable, RestorePipelineReasons.Checks.SourceReplica,
                RestorePipelineReasons.ReplicaMissing, "The replica no longer exists in storage.");
        }

        if (metadata.SizeBytes != _request.ExpectedSizeBytes)
        {
            throw FailCheck(stage, RestoreAgentFailureCodes.ChecksumMismatch, RestorePipelineReasons.Checks.SourceReplica,
                RestorePipelineReasons.ReplicaSizeMismatch, "The stored replica has a different size than when it was published, so it cannot be trusted.");
        }

        Pass(RestorePipelineReasons.Checks.SourceReplica, "replica-present", "The replica exists and has the recorded size.");
    }

    // ---- DownloadingArtifact --------------------------------------------------------------------------------------------------

    private async Task DownloadAsync(IStorageProvider storage, CancellationToken cancellationToken)
    {
        const RestoreExecutionStage stage = RestoreExecutionStage.DownloadingArtifact;
        _tracker.Enter(stage);

        // Held before the file exists: the name must be protected from the staging sweeper from the first byte on.
        var name = ManagedBackupLayout.StagedFileName("restore", _startedAt, Guid.NewGuid());
        if (_usage is not null)
        {
            _holds.Add(_usage.Hold(name));
        }

        _stagedName = name;
        _stagedFiles.Add(name);

        Stream source;
        try
        {
            source = await storage.OpenReadAsync(_request.StorageKey, cancellationToken).ConfigureAwait(false);
        }
        catch (StorageException exception)
        {
            throw StorageFailure(stage, exception);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not RestoreStageFailedException)
        {
            throw Fail(stage, RestoreAgentFailureCodes.StorageUnavailable, RestorePipelineReasons.ReplicaReadFailed,
                "The storage failed unexpectedly. Details are in the agent log.", exception);
        }

        await using (source.ConfigureAwait(false))
        {
            FileStream target;
            try
            {
                // CreateNew: a staged file is never appended to or silently replaced.
                target = new FileStream(_staging.FilePath(name), FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 1,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw StagingWriteFailure(stage, exception);
            }

            await using (target.ConfigureAwait(false))
            {
                await CopyAsync(source, target, stage, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Streams the replica through one bounded buffer into the staged file, hashing the very bytes that are written when the checksum is to be verified.</summary>
    private async Task CopyAsync(Stream source, FileStream target, RestoreExecutionStage stage, CancellationToken cancellationToken)
    {
        var expected = _request.ExpectedSizeBytes;
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        using var hash = _request.VerifyChecksum ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
        try
        {
            while (true)
            {
                int read;
                try
                {
                    read = await source.ReadAsync(buffer.AsMemory(0, CopyBufferSize), cancellationToken).ConfigureAwait(false);
                }
                catch (StorageException exception)
                {
                    throw StorageFailure(stage, exception);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw Fail(stage, RestoreAgentFailureCodes.ArtifactTransferFailed, RestorePipelineReasons.ReplicaReadFailed,
                        "The replica could not be read completely from storage.", exception);
                }

                if (read == 0)
                {
                    break;
                }

                if (_copiedBytes + read > expected)
                {
                    // More bytes than the replica was published with: stop here instead of filling the staging volume with them. The verification reports it.
                    _copyOverflow = true;
                    break;
                }

                hash?.AppendData(buffer, 0, read);
                try
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw StagingWriteFailure(stage, exception);
                }

                _copiedBytes += read;
                _tracker.Report(stage, _copiedBytes * 100.0 / expected, _copiedBytes, expected);
            }

            try
            {
                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw StagingWriteFailure(stage, exception);
            }

            if (hash is not null)
            {
                _copiedChecksum = ContentChecksum.Sha256(hash.GetHashAndReset());
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // ---- VerifyingChecksum ----------------------------------------------------------------------------------------------------

    /// <summary>Nothing of the target has been touched yet, so a copy that is not exactly the published replica ends the restore right here.</summary>
    private void VerifyChecksum()
    {
        const RestoreExecutionStage stage = RestoreExecutionStage.VerifyingChecksum;
        _tracker.Enter(stage);
        var expected = _request.ExpectedSizeBytes;

        var staged = _staging.FileLength(_stagedName!);
        if (staged is null)
        {
            throw Fail(stage, RestoreAgentFailureCodes.StagingInvalid, RestorePipelineReasons.StagedFileMissing,
                "The staged copy of the backup disappeared before it could be verified.");
        }

        if (_copyOverflow || _copiedBytes != expected || staged != expected)
        {
            throw Fail(stage, RestoreAgentFailureCodes.ChecksumMismatch, RestorePipelineReasons.ReplicaSizeMismatch,
                "The replica read from storage has a different size than when it was published, so it cannot be trusted.");
        }

        if (!_request.VerifyChecksum)
        {
            _warnings.Add(new BackupWarning(
                RestorePipelineReasons.ChecksumNotVerified,
                "Checksum verification was turned off for this restore: the replica's bytes were not compared with the checksum recorded when it was published."));
            _tracker.Report(stage, 100, expected, expected);
            return;
        }

        if (_copiedChecksum != _request.ExpectedChecksum)
        {
            throw Fail(stage, RestoreAgentFailureCodes.ChecksumMismatch, RestorePipelineReasons.ReplicaChecksumMismatch,
                "The replica read from storage has a different checksum than when it was published, so it cannot be trusted.");
        }

        _checksumVerified = true;
        _tracker.Report(stage, 100, expected, expected);
    }

    // ---- Decryption (part of VerifyingChecksum) -------------------------------------------------------------------------------

    /// <summary>
    /// An encrypted replica is decrypted into a SECOND staged file and only that file ever reaches the engine. The plaintext must hash to the
    /// recorded source checksum first, so neither a tampered ciphertext nor a wrong key can put unauthenticated bytes into the target.
    /// </summary>
    private async Task DecryptAsync(CancellationToken cancellationToken)
    {
        if (_request.Decryption is not { } decryption)
        {
            return;
        }

        const RestoreExecutionStage stage = RestoreExecutionStage.VerifyingChecksum;
        var ciphertextName = _stagedName!;
        var plaintextName = ManagedBackupLayout.StagedFileName("restore", _startedAt, Guid.NewGuid());
        if (_usage is not null)
        {
            _holds.Add(_usage.Hold(plaintextName));
        }

        _stagedFiles.Add(plaintextName);
        try
        {
            await using var ciphertext = new FileStream(_staging.FilePath(ciphertextName), FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var plaintext = new FileStream(_staging.FilePath(plaintextName), FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 1,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await BpeDecryptor.DecryptAsync(ciphertext, plaintext, async (recipients, token) =>
            {
                // The backend unwraps the key it stored for this artifact; a header without a tenant-wrap recipient cannot be that artifact.
                if (!recipients.Any(recipient => recipient.Type == BpeRecipientType.TenantWrap))
                {
                    throw new BpeFormatException("The artifact has no tenant recipient.");
                }

                return await decryption.UnwrapDek(token).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
            await plaintext.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ArtifactKeyUnavailableException exception)
        {
            throw Fail(stage, RestoreAgentFailureCodes.ArtifactTransferFailed, RestorePipelineReasons.ArtifactKeyUnavailable,
                "The key of the encrypted backup could not be obtained, so the backup was not decrypted.", exception);
        }
        catch (BpeFormatException exception)
        {
            throw Fail(stage, RestoreAgentFailureCodes.ChecksumMismatch, RestorePipelineReasons.ArtifactAuthenticationFailed,
                "The encrypted backup failed authentication (corrupt, tampered with, or the wrong key), so it was not restored.", exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw StagingWriteFailure(stage, exception);
        }

        ContentChecksum actual;
        try
        {
            await using var decrypted = new FileStream(_staging.FilePath(plaintextName), FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            actual = await ContentHasher.ComputeSha256Async(decrypted, null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw StagingWriteFailure(stage, exception);
        }

        if (actual != decryption.SourceChecksum)
        {
            throw Fail(stage, RestoreAgentFailureCodes.ChecksumMismatch, RestorePipelineReasons.SourceChecksumMismatch,
                "The decrypted backup does not match the checksum recorded when it was taken, so it cannot be trusted.");
        }

        _stagedName = plaintextName;
        // Best effort: the ciphertext is no longer needed, and the final clean-up retries whatever stays behind.
        _staging.TryDeleteFile(ciphertextName);
    }

    // ---- InspectingBackup -----------------------------------------------------------------------------------------------------

    private async Task InspectAsync(IDatabaseRestoreProvider provider, CancellationToken cancellationToken)
    {
        const RestoreExecutionStage stage = RestoreExecutionStage.InspectingBackup;
        _tracker.Enter(stage);

        RestoreInspectionResult result;
        try
        {
            result = await provider.InspectBackupAsync(
                _request.Connection,
                _request.Credential,
                new RestoreInspectionRequest(_request.Staging.ServerDirectory.Combine(_stagedName!), _request.VerifyBackup, _request.VerifyTimeout,
                    new AgentPath(_staging.FilePath(_stagedName!))),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not RestoreStageFailedException)
        {
            throw Fail(stage, RestoreAgentFailureCodes.InternalError, RestorePipelineReasons.InspectionError,
                "The backup could not be inspected. Details are in the agent log.", exception);
        }

        _warnings.AddRange(result.Warnings);
        if (result.Failure is { } failure)
        {
            throw ProviderFailure(failure);
        }

        _inspection = result.Inspection!;
        _backupVerified = _request.VerifyBackup && _inspection.VerifyOnlyPassed;
        if (_request.VerifyBackup && !_backupVerified)
        {
            _warnings.Add(new BackupWarning("backup-not-verified", "The engine's own verification of the backup did not report a result."));
        }
    }

    private async Task PlanAsync(IDatabaseRestoreProvider provider, CancellationToken cancellationToken)
    {
        const RestoreExecutionStage stage = RestoreExecutionStage.InspectingBackup;
        RestorePlanResult result;
        try
        {
            result = await provider.PlanRestoreAsync(
                _request.Connection,
                _request.Credential,
                new RestorePlanRequest(_request.DatabaseName, _request.Mode, _request.TerminateActiveConnections, _inspection!),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not RestoreStageFailedException)
        {
            throw Fail(stage, RestoreAgentFailureCodes.InternalError, RestorePipelineReasons.PlanError,
                "The restore could not be planned. Details are in the agent log.", exception);
        }

        _preCheck.AddRange(result.Report.Items);
        FailOnFailedItem(result.Report, stage);
        _plan = result.Plan!;
        _warnings.AddRange(_plan.Warnings);
    }

    // ---- PreparingTarget / Restoring / Finalizing -----------------------------------------------------------------------------

    private async Task ExecuteAsync(IDatabaseRestoreProvider provider, CancellationToken cancellationToken)
    {
        // Last point at which a cancellation provably leaves the target alone.
        cancellationToken.ThrowIfCancellationRequested();
        _tracker.Enter(RestoreExecutionStage.PreparingTarget);

        DatabaseRestoreRequest request;
        try
        {
            request = new DatabaseRestoreRequest(
                _request.DatabaseName,
                _request.Staging.ServerDirectory.Combine(_stagedName!),
                _request.Mode,
                _request.TerminateActiveConnections,
                _inspection!,
                _plan!,
                _request.RestoreTimeout,
                new AgentPath(_staging.FilePath(_stagedName!))) { Files = _request.Files };
        }
        catch (ArgumentException exception)
        {
            throw Fail(RestoreExecutionStage.PreparingTarget, RestoreAgentFailureCodes.InvalidConfiguration, RestorePipelineReasons.ProviderError,
                "The restore request is not valid.", exception);
        }

        // From the moment the provider is called the target may change; an answer that does not state the target's condition leaves it unknown.
        _executeStarted = true;
        try
        {
            _executeResult = await provider.ExecuteRestoreAsync(
                _request.Connection,
                _request.Credential,
                request,
                new CallbackProgress<RestoreProgress>(_tracker.Forward),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not RestoreStageFailedException)
        {
            throw Fail(_tracker.Current ?? RestoreExecutionStage.Restoring, RestoreAgentFailureCodes.InternalError, RestorePipelineReasons.ProviderError,
                "The restore provider failed unexpectedly. Details are in the agent log.", exception);
        }

        _warnings.AddRange(_executeResult.Warnings);
        if (_executeResult.Failure is { } failure)
        {
            throw ProviderFailure(failure);
        }

        _tracker.Enter(RestoreExecutionStage.Finalizing);
    }

    // ---- Verifying / CleaningUp -----------------------------------------------------------------------------------------------

    private async Task VerifyAsync(IDatabaseRestoreProvider provider, CancellationToken cancellationToken)
    {
        const RestoreExecutionStage stage = RestoreExecutionStage.Verifying;
        _tracker.Enter(stage);
        if (provider is not IRestoreVerificationProvider verifier)
        {
            throw Fail(stage, RestoreAgentFailureCodes.UnsupportedCapability, RestorePipelineReasons.VerificationUnsupported,
                "This agent cannot verify restored databases of the requested engine.");
        }

        RestoreVerificationReport report;
        try
        {
            report = await verifier.VerifyDatabaseAsync(_request.Connection, _request.Credential,
                new RestoreVerificationRequest(_request.DatabaseName, _request.VerifyTimeout), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not RestoreStageFailedException)
        {
            throw Fail(stage, RestoreAgentFailureCodes.InternalError, RestorePipelineReasons.VerificationError,
                "The restored database could not be verified. Details are in the agent log.", exception);
        }

        _verificationReport = report;
        if (report.Passed)
        {
            return;
        }

        var failed = report.Checks.FirstOrDefault(check => !check.Passed);
        var reason = failed?.ReasonCode ?? RestorePipelineReasons.VerificationFailed;
        throw Fail(stage, reason == RestorePipelineReasons.Timeout ? RestoreAgentFailureCodes.OperationTimedOut : RestoreAgentFailureCodes.VerificationFailed,
            reason, failed?.Message ?? "The restored database did not pass its verification checks.");
    }

    /// <summary>
    /// Removes the disposable database once the verification ended, however it ended. Runs on its own bounded token, never the run's: a cancelled or timed-out restore is exactly
    /// when a half-restored copy of customer data is left behind. Never throws; a failed removal is reported so the backend keeps it owed and retries.
    /// </summary>
    private async Task CleanUpVerificationDatabaseAsync()
    {
        if (_request.Purpose != RestorePipelinePurpose.Verification || !_executeStarted)
        {
            return;
        }

        try
        {
            if (_provider is not IRestoreVerificationProvider verifier)
            {
                _verificationCleanup = VerificationCleanup.Failed;
                _verificationCleanupReason = RestorePipelineReasons.VerificationUnsupported;
                return;
            }

            _tracker.Enter(RestoreExecutionStage.CleaningUp);
            using var bound = new CancellationTokenSource(DatabaseDropRequest.DefaultTimeout);
            var result = await verifier.DropDatabaseAsync(_request.Connection, _request.Credential,
                new DatabaseDropRequest(_request.DatabaseName, DatabaseDropKind.VerificationDatabase), bound.Token).ConfigureAwait(false);
            _verificationCleanup = result.Outcome switch
            {
                DatabaseDropOutcome.Dropped => VerificationCleanup.Dropped,
                DatabaseDropOutcome.NotFound => VerificationCleanup.NotCreated,
                _ => VerificationCleanup.Failed
            };
            _verificationCleanupReason = _verificationCleanup == VerificationCleanup.Failed ? result.ReasonCode ?? RestorePipelineReasons.CleanupFailed : null;
        }
        catch (Exception exception)
        {
            DatabaseRestorePipeline.LogCleanupFailed(_logger, exception, _tracker.Current ?? RestoreExecutionStage.CleaningUp, RestorePipelineReasons.CleanupFailed);
            _verificationCleanup = VerificationCleanup.Failed;
            _verificationCleanupReason = exception is OperationCanceledException ? RestorePipelineReasons.Timeout : RestorePipelineReasons.CleanupFailed;
        }

        if (_verificationCleanup == VerificationCleanup.Failed)
        {
            _warnings.Add(new BackupWarning(RestorePipelineReasons.CleanupFailed,
                "The disposable verification database could not be removed. The platform keeps the removal owed and retries it."));
        }
    }

    // ---- results --------------------------------------------------------------------------------------------------------------

    private void CleanUpStagedCopy()
    {
        if (_stagedName is null)
        {
            return;
        }

        // Every file the run staged, not just the last one: a failed decryption leaves ciphertext and a partial plaintext behind.
        if (_stagedFiles.Select(_staging.TryDeleteFile).ToArray().All(deleted => deleted))
        {
            _stagingCleanup = new StagingCleanup(StagingCleanupStatus.Deleted);
            return;
        }

        _stagingCleanup = new StagingCleanup(StagingCleanupStatus.Failed, RestorePipelineReasons.StagingCleanupFailed);
        _warnings.Add(new BackupWarning(
            RestorePipelineReasons.StagingCleanupFailed,
            "The staged copy of the backup could not be removed and may still be present on the agent."));
        DatabaseRestorePipeline.LogCleanupFailed(_logger, null, _tracker.Current ?? RestoreExecutionStage.Preparing, RestorePipelineReasons.StagingCleanupFailed);
    }

    private DatabaseRestorePipelineResult Build(DatabaseRestoreStatus status, RestoreExecutionStage finalStage, RestorePipelineFailure? failure)
    {
        DatabaseRestorePipeline.LogFinished(_logger, status, failure?.Stage ?? finalStage, failure?.Code);
        var executed = _executeResult;
        return new DatabaseRestorePipelineResult
        {
            Status = status,
            FinalStage = finalStage,
            Failure = failure,
            // A verification only ever creates its own disposable database, whose fate is Verification.Cleanup; no database the user owns is touched.
            TargetState = _request.Purpose == RestorePipelinePurpose.Verification
                ? DatabaseRestoreTargetState.NotModified
                : executed?.TargetState ?? (_executeStarted ? DatabaseRestoreTargetState.Unknown : DatabaseRestoreTargetState.NotModified),
            DatabaseState = executed?.DatabaseState,
            StartedAt = _startedAt,
            FinishedAt = _time.GetUtcNow(),
            RestoreStartedAt = executed?.RestoreStartedAt,
            RestoreFinishedAt = executed?.RestoreFinishedAt,
            PreCheck = _preCheck.ToArray(),
            Inspection = _inspection,
            Plan = _plan,
            FileMappings = executed is { FileMappings.Count: > 0 } ? executed.FileMappings : _plan?.Mappings ?? [],
            Warnings = _warnings.ToArray(),
            ChecksumVerified = _checksumVerified,
            BackupVerified = _backupVerified,
            StagingCleanup = _stagingCleanup,
            Stages = _tracker.Records.ToArray(),
            Verification = _request.Purpose == RestorePipelinePurpose.Verification
                ? new RestoreVerificationOutcome(_verificationReport, _verificationCleanup, _verificationCleanupReason)
                : null,
            RetainedDatabaseName = executed?.RetainedDatabaseName
        };
    }

    // ---- failure helpers ------------------------------------------------------------------------------------------------------

    /// <summary>The first failed item decides: its reason, then its cause, then "pre-check failed".</summary>
    private void FailOnFailedItem(BackupPreCheckReport report, RestoreExecutionStage stage)
    {
        if (report.CanProceed)
        {
            return;
        }

        var first = report.Items.First(item => item.Status == BackupPreCheckStatus.Failed);
        throw Fail(stage, RestoreFailureMapping.Map(first.ReasonCode, first.Cause, RestoreFailureKind.PreCheckFailed), first.ReasonCode, first.Message);
    }

    private RestoreStageFailedException ProviderFailure(RestoreFailure failure)
    {
        if (failure.Kind == RestoreFailureKind.Cancelled)
        {
            return new RestoreStageFailedException(new RestorePipelineFailure(
                RestoreAgentFailureCodes.Cancelled, failure.Stage, failure.ReasonCode, failure.Message));
        }

        return Fail(failure.Stage, RestoreFailureMapping.Map(failure.ReasonCode, failure.Cause, failure.Kind), failure.ReasonCode, failure.Message);
    }

    private RestoreStageFailedException StorageFailure(RestoreExecutionStage stage, StorageException exception, string? checkId = null)
    {
        var code = RestoreFailureMapping.FromStorage(exception.Kind);
        var reason = exception.Kind == StorageFailureKind.NotFound ? RestorePipelineReasons.ReplicaMissing : RestorePipelineReasons.ReplicaReadFailed;
        return checkId is null
            ? Fail(stage, code, reason, exception.Message, exception)
            : FailCheck(stage, code, checkId, reason, exception.Message, exception: exception);
    }

    /// <summary>A failure to create or write the staged file: a full volume is a space problem, anything else is a staging problem.</summary>
    private RestoreStageFailedException StagingWriteFailure(RestoreExecutionStage stage, Exception exception) =>
        IsDiskFull(exception)
            ? Fail(stage, RestoreAgentFailureCodes.InsufficientDiskSpace, RestorePipelineReasons.StagingInsufficientSpace,
                "The staging volume ran out of space while the backup file was being copied.", exception)
            : Fail(stage, RestoreAgentFailureCodes.StagingInvalid, RestorePipelineReasons.StagingWriteFailed,
                "The backup file could not be written to the staging directory. Check the agent's access to it.", exception);

    // Windows: ERROR_DISK_FULL and ERROR_HANDLE_DISK_FULL as HRESULTs. Unix: ENOSPC and Linux EDQUOT, which .NET exposes as the IOException's HResult.
    internal static bool IsDiskFull(Exception exception) =>
        exception is IOException { HResult: unchecked((int)0x80070070) or unchecked((int)0x80070027) or 28 or 122 };

    private void Pass(string checkId, string reason, string message) =>
        _preCheck.Add(new BackupPreCheckItem(checkId, BackupPreCheckStatus.Passed, reason, message));

    private void Warn(string checkId, string reason, string message) =>
        _preCheck.Add(new BackupPreCheckItem(checkId, BackupPreCheckStatus.Warning, reason, message));

    /// <summary>A failure that is also a pre-check item, so the user sees which check stopped the restore.</summary>
    private RestoreStageFailedException FailCheck(
        RestoreExecutionStage stage,
        string code,
        string checkId,
        string reason,
        string message,
        BackupFailureCause cause = BackupFailureCause.Unclassified,
        Exception? exception = null)
    {
        _preCheck.Add(new BackupPreCheckItem(checkId, BackupPreCheckStatus.Failed, reason, message, cause));
        return Fail(stage, code, reason, message, exception);
    }

    private RestoreStageFailedException Fail(
        RestoreExecutionStage stage,
        string code,
        string reason,
        string message,
        Exception? exception = null) =>
        new(Classify(stage, code, reason, message, exception));

    private RestorePipelineFailure Classify(RestoreExecutionStage stage, string code, string reason, string message, Exception? exception)
    {
        DatabaseRestorePipeline.LogStageFailed(_logger, exception, stage, code, reason);
        return new RestorePipelineFailure(code, stage, reason, message);
    }
}

/// <summary>A stage ended in a classified failure. Carries the failure only; the exception that caused it was logged where it was classified.</summary>
internal sealed class RestoreStageFailedException(RestorePipelineFailure failure) : Exception(failure.Message)
{
    public RestorePipelineFailure Failure { get; } = failure;
}
