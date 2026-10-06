using BackupPlatform.Agent.Core.Backup;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.StorageProviders.Abstractions;

namespace BackupPlatform.UnitTests.Backup;

public sealed class DatabaseBackupPipelineTests : IDisposable
{
    private readonly PipelineHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Theory]
    [InlineData(StorageFailureKind.Io, BackupFailureCause.StorageUnavailable)]
    [InlineData(StorageFailureKind.Unexpected, BackupFailureCause.StorageUnavailable)]
    [InlineData(StorageFailureKind.NotFound, BackupFailureCause.StorageUnavailable)]
    [InlineData(StorageFailureKind.IntegrityMismatch, BackupFailureCause.StorageUnavailable)]
    [InlineData(StorageFailureKind.AccessDenied, BackupFailureCause.PermissionDenied)]
    [InlineData(StorageFailureKind.InvalidConfiguration, BackupFailureCause.InvalidConfiguration)]
    [InlineData(StorageFailureKind.NotSupported, BackupFailureCause.Unsupported)]
    [InlineData(StorageFailureKind.InsufficientSpace, BackupFailureCause.Unclassified)]
    [InlineData(StorageFailureKind.AlreadyExists, BackupFailureCause.Unclassified)]
    public async Task StorageFailuresCarryAnEngineIndependentCause(StorageFailureKind kind, BackupFailureCause cause)
    {
        _h.Storage.OnPut = (_, _, _, _) => throw new StorageException(kind, "The storage rejected the write.");

        var result = await _h.RunAsync();

        Assert.Equal(BackupFailureKind.StorageWriteFailed, result.Failure!.Kind);
        Assert.Equal(cause, result.Failure.Cause);
    }

    [Fact]
    public async Task AFailedPreCheckCarriesTheCauseOfItsFirstFailedItem()
    {
        _h.Provider.PreCheck = new BackupPreCheckReport(
        [
            new BackupPreCheckItem("server.reachable", BackupPreCheckStatus.Failed, "server-unreachable", "The server cannot be reached.", BackupFailureCause.DatabaseUnavailable),
            new BackupPreCheckItem("permission.backup", BackupPreCheckStatus.Failed, "backup-permission-denied", "No permission.", BackupFailureCause.PermissionDenied)
        ]);

        var result = await _h.RunAsync();

        Assert.Equal(BackupFailureKind.PreCheckFailed, result.Failure!.Kind);
        Assert.Equal(BackupFailureCause.DatabaseUnavailable, result.Failure.Cause);
    }

    [Fact]
    public async Task AProviderFailureKeepsItsCauseUntouched()
    {
        _h.Provider.Execute = (_, _, _) => Task.FromResult(PipelineHarness.FailedResult(
            FullBackupStatus.Failed,
            new BackupFailure(BackupFailureKind.SqlBackupFailed, BackupExecutionStage.BackingUp, "backup-timeout", "The backup timed out.", BackupFailureCause.Timeout)));

        var result = await _h.RunAsync();

        Assert.Equal(BackupFailureCause.Timeout, result.Failure!.Cause);
        Assert.Equal("backup-timeout", result.Failure.ReasonCode);
    }

    [Fact]
    public async Task HappyPathRunsEveryStageInOrderAndProducesSourceArtifactAndReplicaMetadata()
    {
        var result = await _h.RunAsync();

        Assert.Equal(DatabaseBackupStatus.Succeeded, result.Status);
        Assert.Equal(BackupExecutionStage.Completed, result.FinalStage);
        Assert.Null(result.Failure);
        Assert.Equal(
            [
                BackupExecutionStage.Preparing, BackupExecutionStage.BackingUp, BackupExecutionStage.Verifying,
                BackupExecutionStage.HashingSource, BackupExecutionStage.PreparingArtifact, BackupExecutionStage.HashingArtifact,
                BackupExecutionStage.Uploading, BackupExecutionStage.VerifyingReplica, BackupExecutionStage.Completed
            ],
            result.Stages.Select(stage => stage.Stage));
        Assert.All(result.Stages, stage => Assert.NotNull(stage.FinishedAt));

        var expected = InMemoryStorage.Checksum(PipelineHarness.BackupBytes);
        Assert.Equal(expected, result.SourceFile!.Checksum);
        Assert.Equal(PipelineHarness.BackupBytes.Length, result.SourceFile.SizeBytes);
        Assert.Equal(expected, result.Artifact!.Checksum);
        Assert.True(result.Artifact.IsSourceFile);
        Assert.Empty(result.Artifact.Transforms);
        Assert.Equal(result.SourceFile.FileName, result.Artifact.FileName);

        var replica = result.Replica!;
        Assert.Equal(StorageVerificationStatus.Verified, replica.VerificationStatus);
        Assert.Equal(expected, replica.Checksum);
        Assert.Equal(PipelineHarness.BackupBytes.Length, replica.SizeBytes);
        Assert.Equal(new StorageLocation(StorageProviderId.Local, new StorageObjectKey($"tenant-a/mssql/sales/{result.Artifact.FileName}")), replica.Location);
        Assert.Equal(PipelineHarness.BackupBytes, _h.Storage.Objects[replica.Location.Key.Value]);

        Assert.Equal(DatabaseBackupType.Full, result.BackupType);
        Assert.Equal(PipelineHarness.Database, result.Source!.DatabaseName);
        Assert.Equal(BackupVerificationStatus.Passed, result.Verification.Status);
        Assert.NotNull(result.BackupStartedAt);
        Assert.NotNull(result.BackupFinishedAt);
        Assert.NotNull(result.PreCheck);
        Assert.Empty(result.CleanupFailures);
    }

    [Fact]
    public async Task StagingIsReleasedOnlyAfterTheReplicaWasVerified()
    {
        var result = await _h.RunAsync();

        Assert.NotEmpty(_h.Storage.StagedFilesDuringVerify!);
        Assert.Empty(_h.StagedFiles);
        Assert.Equal(StagingCleanupStatus.Deleted, result.StagingCleanup.Status);
    }

    [Fact]
    public async Task ProgressReportsTheStagesAndPercentagesOfTheLongRunningSteps()
    {
        await _h.RunAsync();

        var stages = _h.Progress.Select(p => p.Stage).Distinct().ToArray();
        Assert.Equal(BackupExecutionStage.Preparing, stages.First());
        Assert.Equal(BackupExecutionStage.Completed, stages.Last());
        Assert.Equal(stages.Order(), stages);
        Assert.Contains(_h.Progress, p => p.Stage == BackupExecutionStage.HashingSource && p.Percent == 100 && p.Bytes == PipelineHarness.BackupBytes.Length);
        Assert.Contains(_h.Progress, p => p.Stage == BackupExecutionStage.Uploading && p.Percent == 100);
        Assert.Contains(_h.Progress, p => p.Stage == BackupExecutionStage.BackingUp && p.Percent == 50);
        Assert.All(_h.Progress, p => Assert.True(p.Percent is null or (>= 0 and <= 100)));
    }

    [Fact]
    public async Task AProgressConsumerThatThrowsCannotChangeTheOutcome()
    {
        var result = await _h.Pipeline.RunFullBackupAsync(_h.Request(), new ThrowingProgress(), CancellationToken.None);

        Assert.Equal(DatabaseBackupStatus.Succeeded, result.Status);
        Assert.NotNull(result.Replica);
    }

    [Fact]
    public async Task OptionsRequestedAndAppliedAreCarriedFromTheProviderAndWarningsAreKept()
    {
        var requested = new FullBackupOptions(BackupOptionMode.IfSupported, BackupOptionMode.Required, BackupOptionMode.IfSupported);
        _h.Provider.Execute = (request, progress, _) => Task.FromResult(PipelineHarness.SucceededResult(
            request,
            WriteStaged(request),
            new AppliedFullBackupOptions(false, true, false),
            [new BackupWarning("option-fell-back.compression", "Compression is not available on this edition; the backup is uncompressed.")]));

        var result = await _h.RunAsync(_h.Request(requested));

        Assert.Equal(requested, result.Options!.Requested);
        Assert.Equal(new AppliedFullBackupOptions(false, true, false), result.Options.Applied);
        Assert.Contains(result.Warnings, w => w.Code == "option-fell-back.compression");
        Assert.Equal(requested, _h.Provider.Requests.Single().Options);
    }

    [Fact]
    public async Task PreCheckWarningsAreCarriedAndAFailedPreCheckNeverStartsTheBackup()
    {
        _h.Provider.PreCheck = new BackupPreCheckReport(
        [
            new BackupPreCheckItem("staging.server-writable", BackupPreCheckStatus.Warning, "staging-server-write-unverifiable", "The write permission could not be probed."),
            new BackupPreCheckItem("database.state", BackupPreCheckStatus.Failed, "database-offline", "The database is offline and cannot be backed up.")
        ]);

        var result = await _h.RunAsync();

        Assert.Equal(DatabaseBackupStatus.Failed, result.Status);
        Assert.Equal(BackupFailureKind.PreCheckFailed, result.Failure!.Kind);
        Assert.Equal("database-offline", result.Failure.ReasonCode);
        Assert.Equal(BackupExecutionStage.Failed, result.FinalStage);
        Assert.Empty(_h.Provider.Requests);
        Assert.Contains(result.Warnings, w => w.Code == "precheck.staging.server-writable.staging-server-write-unverifiable");
        Assert.NotNull(result.PreCheck);
    }

    [Fact]
    public async Task PreCheckIsGivenTheStagingAreaAsBothSidesSeeIt()
    {
        await _h.RunAsync();

        Assert.Equal(_h.Staging, _h.Provider.PreCheckRequest!.Staging);
        Assert.Equal(PipelineHarness.Database, _h.Provider.PreCheckRequest.DatabaseName);
    }

    [Fact]
    public async Task UnknownEngineAndUnknownStorageFailBeforeAnythingRuns()
    {
        var request = _h.Request();
        var unknownEngine = new DatabaseBackupPipelineRequest(
            new DatabaseEngineId("postgresql"), request.Connection, request.Credential, request.DatabaseName, request.Staging, request.Storage, request.StoragePrefix);
        var engine = await _h.RunAsync(unknownEngine);
        var storage = await _h.RunAsync(_h.Request(storage: new StorageProviderId("s3")));

        Assert.Equal(BackupPipelineReasons.EngineNotAvailable, engine.Failure!.ReasonCode);
        Assert.Equal(BackupFailureKind.PreCheckFailed, engine.Failure.Kind);
        Assert.Equal(BackupPipelineReasons.StorageNotAvailable, storage.Failure!.ReasonCode);
        Assert.Empty(_h.Provider.Requests);
    }

    [Fact]
    public async Task ASqlBackupFailureIsClassifiedAndTheProvidersCleanupIsReported()
    {
        _h.Provider.Execute = (_, _, _) => Task.FromResult(PipelineHarness.FailedResult(
            FullBackupStatus.Failed,
            new BackupFailure(BackupFailureKind.SqlBackupFailed, BackupExecutionStage.BackingUp, "staging-not-writable", "SQL Server could not write the backup file."),
            new StagingCleanup(StagingCleanupStatus.Deleted)));

        var result = await _h.RunAsync();

        Assert.Equal(DatabaseBackupStatus.Failed, result.Status);
        Assert.Equal(BackupFailureKind.SqlBackupFailed, result.Failure!.Kind);
        Assert.Equal("staging-not-writable", result.Failure.ReasonCode);
        Assert.Equal(StagingCleanupStatus.Deleted, result.StagingCleanup.Status);
        Assert.Null(result.SourceFile);
        Assert.Null(result.Replica);
        Assert.Empty(_h.Storage.Objects);
    }

    [Fact]
    public async Task AVerificationFailureStopsThePipelineBeforeAnyHashingOrUpload()
    {
        _h.Provider.Execute = (_, _, _) => Task.FromResult(PipelineHarness.FailedResult(
            FullBackupStatus.Failed,
            new BackupFailure(BackupFailureKind.VerificationFailed, BackupExecutionStage.Verifying, "verify-media-invalid", "The backup file failed verification.")));

        var result = await _h.RunAsync();

        Assert.Equal(BackupFailureKind.VerificationFailed, result.Failure!.Kind);
        Assert.Equal(BackupExecutionStage.Verifying, result.Failure.Stage);
        Assert.DoesNotContain(result.Stages, s => s.Stage == BackupExecutionStage.HashingSource);
        Assert.Empty(_h.Storage.Objects);
    }

    [Fact]
    public async Task AProviderThatThrowsIsAnUnexpectedBackupFailureWithoutTheExceptionTextInTheResult()
    {
        _h.Provider.Execute = (_, _, _) => throw new InvalidOperationException("Server=sql.test;Password=SECRET-PASSWORD-123 at C:\\secret\\path");

        var result = await _h.RunAsync();

        Assert.Equal(BackupFailureKind.SqlBackupFailed, result.Failure!.Kind);
        Assert.Equal(BackupPipelineReasons.ProviderError, result.Failure.ReasonCode);
        AssertNoSensitiveText(result);
    }

    [Fact]
    public async Task ACancellationReportedByTheProviderIsACancelledResult()
    {
        _h.Provider.Execute = (_, _, _) => Task.FromResult(PipelineHarness.FailedResult(
            FullBackupStatus.Cancelled,
            new BackupFailure(BackupFailureKind.Cancelled, BackupExecutionStage.BackingUp, "cancelled", "The backup was cancelled."),
            new StagingCleanup(StagingCleanupStatus.Deleted)));

        var result = await _h.RunAsync();

        Assert.Equal(DatabaseBackupStatus.Cancelled, result.Status);
        Assert.Equal(BackupExecutionStage.Cancelled, result.FinalStage);
        Assert.Equal(BackupFailureKind.Cancelled, result.Failure!.Kind);
        Assert.Equal(StagingCleanupStatus.Deleted, result.StagingCleanup.Status);
    }

    [Fact]
    public async Task CancellationDuringUploadStopsTheUploadRemovesStagingEvenWhenFailedFilesAreKeptAndLeavesNoReplica()
    {
        using var cancellation = new CancellationTokenSource();
        _h.Storage.OnPut = async (_, _, _, token) =>
        {
            await cancellation.CancelAsync();
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("unreachable");
        };

        var result = await _h.RunAsync(_h.Request(onFailure: StagingFailurePolicy.Keep), cancellation.Token);

        Assert.Equal(DatabaseBackupStatus.Cancelled, result.Status);
        Assert.Equal(BackupFailureKind.Cancelled, result.Failure!.Kind);
        Assert.Equal(BackupExecutionStage.Uploading, result.Failure.Stage);
        Assert.Equal(BackupExecutionStage.Cancelled, result.FinalStage);
        Assert.Empty(_h.StagedFiles);
        Assert.Empty(_h.Storage.Objects);
        Assert.Equal(StagingCleanupStatus.Deleted, result.StagingCleanup.Status);
    }

    [Fact]
    public async Task CancellationBeforeTheRunReturnsACancelledResultWithoutTouchingTheProvider()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        _h.Provider.Execute = (_, _, token) =>
        {
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("unreachable");
        };

        var result = await _h.RunAsync(cancellationToken: cancellation.Token);

        Assert.Equal(DatabaseBackupStatus.Cancelled, result.Status);
        Assert.Empty(_h.StagedFiles);
    }

    [Fact]
    public async Task ACleanupFailureNeverHidesTheCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        _h.Storage.OnPut = async (_, _, _, token) =>
        {
            ObstructStagedFiles();
            await cancellation.CancelAsync();
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("unreachable");
        };

        var result = await _h.RunAsync(cancellationToken: cancellation.Token);

        Assert.Equal(DatabaseBackupStatus.Cancelled, result.Status);
        Assert.Equal(BackupFailureKind.Cancelled, result.Failure!.Kind);
        Assert.Equal(StagingCleanupStatus.Failed, result.StagingCleanup.Status);
        Assert.Contains(result.CleanupFailures, f => f.Kind == BackupFailureKind.CleanupFailed);
    }

    [Fact]
    public async Task ACleanupFailureAfterAVerifiedUploadKeepsTheBackupSuccessfulAndReportsTheFailureSeparately()
    {
        _h.Storage.OnVerify = (_, _) =>
        {
            ObstructStagedFiles();
            return null;
        };

        var result = await _h.RunAsync();

        Assert.Equal(DatabaseBackupStatus.Succeeded, result.Status);
        Assert.NotNull(result.Replica);
        Assert.Equal(StagingCleanupStatus.Failed, result.StagingCleanup.Status);
        var cleanup = Assert.Single(result.CleanupFailures);
        Assert.Equal(BackupFailureKind.CleanupFailed, cleanup.Kind);
        Assert.Contains(result.Warnings, w => w.Code == BackupPipelineReasons.StagingCleanupFailed);
    }

    [Fact]
    public async Task AMissingStagedFileAfterTheProviderSucceededIsASourceHashFailure()
    {
        _h.Provider.Execute = (request, _, _) => Task.FromResult(PipelineHarness.SucceededResult(request, 1234));

        var result = await _h.RunAsync();

        Assert.Equal(BackupFailureKind.SourceHashFailed, result.Failure!.Kind);
        Assert.Equal(BackupPipelineReasons.StagedFileMissing, result.Failure.ReasonCode);
        Assert.Empty(_h.Storage.Objects);
    }

    [Fact]
    public async Task AStagedFileThatDiffersInSizeFromWhatTheProviderReportedIsRejected()
    {
        _h.Provider.Execute = (request, _, _) => Task.FromResult(PipelineHarness.SucceededResult(request, WriteStaged(request) + 1));

        var result = await _h.RunAsync();

        Assert.Equal(BackupFailureKind.SourceHashFailed, result.Failure!.Kind);
        Assert.Equal(BackupPipelineReasons.StagedFileChanged, result.Failure.ReasonCode);
        Assert.Empty(_h.StagedFiles);
    }

    [Fact]
    public async Task AFailingPreparerIsAnArtifactPreparationFailureAndStagingIsCleanedByDefault()
    {
        using var harness = new PipelineHarness(new ThrowingPreparer());

        var result = await harness.RunAsync();

        Assert.Equal(BackupFailureKind.ArtifactPreparationFailed, result.Failure!.Kind);
        Assert.Equal(BackupExecutionStage.PreparingArtifact, result.Failure.Stage);
        Assert.Empty(harness.StagedFiles);
        Assert.Equal(StagingCleanupStatus.Deleted, result.StagingCleanup.Status);
        AssertNoSensitiveText(result);
    }

    [Fact]
    public async Task KeepPolicyLeavesTheStagedFileOfAFailedRunForDiagnosis()
    {
        using var harness = new PipelineHarness(new ThrowingPreparer());

        var result = await harness.RunAsync(harness.Request(onFailure: StagingFailurePolicy.Keep));

        Assert.Equal(BackupFailureKind.ArtifactPreparationFailed, result.Failure!.Kind);
        Assert.Single(harness.StagedFiles);
        Assert.Equal(StagingCleanupStatus.Kept, result.StagingCleanup.Status);
    }

    [Theory]
    [InlineData(StorageFailureKind.AlreadyExists, BackupPipelineReasons.StorageAlreadyExists)]
    [InlineData(StorageFailureKind.IntegrityMismatch, BackupPipelineReasons.StorageIntegrityMismatch)]
    [InlineData(StorageFailureKind.InsufficientSpace, BackupPipelineReasons.StorageInsufficientSpace)]
    [InlineData(StorageFailureKind.AccessDenied, BackupPipelineReasons.StorageAccessDenied)]
    [InlineData(StorageFailureKind.Io, BackupPipelineReasons.StorageIoError)]
    [InlineData(StorageFailureKind.Unexpected, BackupPipelineReasons.StorageUnexpected)]
    public async Task StorageFailuresAreClassifiedByKindAndTheStagingFileIsCleaned(StorageFailureKind kind, string reason)
    {
        _h.Storage.OnPut = (_, _, _, _) => throw new StorageException(kind, "The storage rejected the write.");

        var result = await _h.RunAsync();

        Assert.Equal(DatabaseBackupStatus.Failed, result.Status);
        Assert.Equal(BackupFailureKind.StorageWriteFailed, result.Failure!.Kind);
        Assert.Equal(reason, result.Failure.ReasonCode);
        Assert.Equal("The storage rejected the write.", result.Failure.Message);
        Assert.Empty(_h.StagedFiles);
        Assert.Null(result.Replica);
    }

    [Fact]
    public async Task AnExistingObjectIsNotOverwrittenByDefaultAndIsReplacedWhenTheCallerSaysSo()
    {
        var first = await _h.RunAsync();
        var key = first.Replica!.Location.Key;
        var original = _h.Storage.Objects[key.Value].ToArray();
        _h.Provider.Execute = (request, progress, _) =>
        {
            // The same artifact name is produced again: the second run must not silently replace the first.
            var renamed = new FullBackupRequest(request.DatabaseName, request.Staging, first.Artifact!.FileName, request.Options);
            return Task.FromResult(_h.Provider.WriteBackup(renamed, progress, [1, 2, 3, 4]));
        };

        var refused = await _h.RunAsync();
        Assert.Equal(BackupFailureKind.StorageWriteFailed, refused.Failure!.Kind);
        Assert.Equal(BackupPipelineReasons.StorageAlreadyExists, refused.Failure.ReasonCode);
        Assert.Equal(original, _h.Storage.Objects[key.Value]);

        var replaced = await _h.RunAsync(_h.Request(overwrite: StorageOverwritePolicy.Replace));
        Assert.Equal(DatabaseBackupStatus.Succeeded, replaced.Status);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, _h.Storage.Objects[key.Value]);
    }

    [Fact]
    public async Task AReplicaThatFailsVerificationIsReportedAndRemovedFromStorage()
    {
        _h.Storage.OnVerify = (key, _) => new StorageVerificationResult(
            new StorageLocation(StorageProviderId.Local, key), StorageVerificationStatus.ChecksumMismatch, StorageVerificationMethod.ReadBack, 1, null);

        var result = await _h.RunAsync();

        Assert.Equal(DatabaseBackupStatus.Failed, result.Status);
        Assert.Equal(BackupFailureKind.ReplicaVerificationFailed, result.Failure!.Kind);
        Assert.Equal(BackupPipelineReasons.ReplicaChecksumMismatch, result.Failure.ReasonCode);
        Assert.Null(result.Replica);
        Assert.Empty(_h.Storage.Objects);
        Assert.Empty(_h.StagedFiles);
    }

    [Theory]
    [InlineData(StorageVerificationStatus.Missing, BackupPipelineReasons.ReplicaMissing)]
    [InlineData(StorageVerificationStatus.SizeMismatch, BackupPipelineReasons.ReplicaSizeMismatch)]
    [InlineData(StorageVerificationStatus.Inconclusive, BackupPipelineReasons.ReplicaInconclusive)]
    public async Task EveryVerificationOutcomeThatIsNotVerifiedFailsTheRun(StorageVerificationStatus status, string reason)
    {
        _h.Storage.OnVerify = (key, _) => new StorageVerificationResult(new StorageLocation(StorageProviderId.Local, key), status, StorageVerificationMethod.SizeOnly, null, null);

        var result = await _h.RunAsync();

        Assert.Equal(BackupFailureKind.ReplicaVerificationFailed, result.Failure!.Kind);
        Assert.Equal(reason, result.Failure.ReasonCode);
    }

    [Fact]
    public async Task AFailedReplicaThatCannotBeDeletedIsReportedAsACleanupFailureWithoutChangingTheOutcome()
    {
        _h.Storage.Capabilities = StorageCapabilities.None;
        _h.Storage.OnVerify = (key, _) => new StorageVerificationResult(
            new StorageLocation(StorageProviderId.Local, key), StorageVerificationStatus.ChecksumMismatch, StorageVerificationMethod.ReadBack, 1, null);

        var result = await _h.RunAsync();

        Assert.Equal(BackupFailureKind.ReplicaVerificationFailed, result.Failure!.Kind);
        Assert.Contains(result.CleanupFailures, f => f.Kind == BackupFailureKind.CleanupFailed && f.ReasonCode == BackupPipelineReasons.ReplicaCleanupFailed);
    }

    [Fact]
    public async Task ASourceAndAnArtifactThatDifferKeepTheirOwnChecksumsAndBothStagedFilesAreRemoved()
    {
        using var harness = new PipelineHarness(new ReversingPreparer());

        var result = await harness.RunAsync();

        Assert.Equal(DatabaseBackupStatus.Succeeded, result.Status);
        Assert.NotEqual(result.SourceFile!.Checksum, result.Artifact!.Checksum);
        Assert.False(result.Artifact.IsSourceFile);
        Assert.Equal(new ArtifactTransform(ArtifactTransformKind.Compression, "reverse"), Assert.Single(result.Artifact.Transforms));
        var stored = harness.Storage.Objects[result.Replica!.Location.Key.Value];
        Assert.Equal(PipelineHarness.BackupBytes.Reverse().ToArray(), stored);
        Assert.Equal(InMemoryStorage.Checksum(stored), result.Artifact.Checksum);
        Assert.Equal(result.Artifact.Checksum, result.Replica.Checksum);
        Assert.Empty(harness.StagedFiles);
    }

    [Fact]
    public async Task TheStagedNameIsHeldBeforeTheProviderCreatesTheFileAndUntilTheRunEnds()
    {
        var usage = new StagingUsageRegistry();
        using var harness = new PipelineHarness(usage: usage);
        string? name = null;
        var heldWhenProviderStarted = false;
        var fileExistedWhenProviderStarted = true;
        var heldDuringVerify = false;
        harness.Provider.Execute = (request, progress, _) =>
        {
            name = request.StagedFileName;
            heldWhenProviderStarted = usage.IsHeld(request.StagedFileName);
            fileExistedWhenProviderStarted = File.Exists(Path.Combine(harness.StagingDirectory, request.StagedFileName));
            return Task.FromResult(harness.Provider.WriteBackup(request, progress));
        };
        harness.Storage.OnVerify = (_, _) =>
        {
            heldDuringVerify = usage.IsHeld(name!);
            return null;
        };

        var result = await harness.RunAsync();

        Assert.Equal(DatabaseBackupStatus.Succeeded, result.Status);
        Assert.True(heldWhenProviderStarted);
        Assert.False(fileExistedWhenProviderStarted);
        Assert.True(heldDuringVerify);
        Assert.NotNull(name);
        Assert.False(usage.IsHeld(name));
        Assert.Empty(harness.StagedFiles);
    }

    [Fact]
    public async Task ThePreparedArtifactNameIsHeldAsWellAndEverythingIsReleasedAfterCleanup()
    {
        var usage = new StagingUsageRegistry();
        using var harness = new PipelineHarness(new ReversingPreparer(), usage);
        var heldDuringVerify = new List<string>();
        harness.Storage.OnVerify = (_, _) =>
        {
            heldDuringVerify.AddRange(harness.StagedFiles.Where(usage.IsHeld));
            return null;
        };

        var result = await harness.RunAsync();

        Assert.Equal(DatabaseBackupStatus.Succeeded, result.Status);
        var source = result.SourceFile!.FileName;
        var artifact = result.Artifact!.FileName;
        Assert.NotEqual(source, artifact);
        Assert.Equal(new[] { artifact, source }.Order(StringComparer.Ordinal), heldDuringVerify.Order(StringComparer.Ordinal));
        Assert.False(usage.IsHeld(source));
        Assert.False(usage.IsHeld(artifact));
        Assert.Empty(harness.StagedFiles);
    }

    [Fact]
    public async Task TheStagedNameIsReleasedAfterAProviderFailure()
    {
        var usage = new StagingUsageRegistry();
        using var harness = new PipelineHarness(usage: usage);
        var heldWhenProviderStarted = false;
        harness.Provider.Execute = (request, _, _) =>
        {
            heldWhenProviderStarted = usage.IsHeld(request.StagedFileName);
            throw new InvalidOperationException("the provider crashed");
        };

        var result = await harness.RunAsync();

        Assert.Equal(DatabaseBackupStatus.Failed, result.Status);
        Assert.True(heldWhenProviderStarted);
        Assert.False(usage.IsHeld(Assert.Single(harness.Provider.Requests).StagedFileName));
    }

    [Fact]
    public async Task TheStagedNameIsReleasedAfterAnUploadFailureEvenWhenTheFileIsKept()
    {
        var usage = new StagingUsageRegistry();
        using var harness = new PipelineHarness(usage: usage);
        harness.Storage.OnPut = (_, _, _, _) => throw new StorageException(StorageFailureKind.Io, "The storage could not be written.");

        var result = await harness.RunAsync(harness.Request(onFailure: StagingFailurePolicy.Keep));

        Assert.Equal(DatabaseBackupStatus.Failed, result.Status);
        Assert.Equal(StagingCleanupStatus.Kept, result.StagingCleanup.Status);
        var name = Assert.Single(harness.Provider.Requests).StagedFileName;
        Assert.Equal([name], harness.StagedFiles);
        Assert.False(usage.IsHeld(name));
    }

    [Fact]
    public async Task TheStagedNameIsReleasedAfterCancellation()
    {
        var usage = new StagingUsageRegistry();
        using var harness = new PipelineHarness(usage: usage);
        using var cancellation = new CancellationTokenSource();
        harness.Provider.Execute = (_, _, token) =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("unreachable");
        };

        var result = await harness.RunAsync(cancellationToken: cancellation.Token);

        Assert.Equal(DatabaseBackupStatus.Cancelled, result.Status);
        Assert.False(usage.IsHeld(Assert.Single(harness.Provider.Requests).StagedFileName));
    }

    [Fact]
    public async Task HoldingNeverChangesTheResultOfARun()
    {
        using var plain = new PipelineHarness();
        using var held = new PipelineHarness(usage: new StagingUsageRegistry());

        var plainResult = await plain.RunAsync();
        var heldResult = await held.RunAsync();

        Assert.Equal(plainResult.Status, heldResult.Status);
        Assert.Equal(plainResult.Stages.Select(stage => stage.Stage), heldResult.Stages.Select(stage => stage.Stage));
        Assert.Equal(plainResult.StagingCleanup, heldResult.StagingCleanup);
        Assert.Equal(plainResult.SourceFile!.Checksum, heldResult.SourceFile!.Checksum);
    }

    [Fact]
    public async Task ResultsNeverContainCredentialsOrDirectoryPaths()
    {
        _h.Storage.OnPut = (_, _, _, _) => throw new StorageException(StorageFailureKind.Io, "The storage could not be written.", new IOException(_h.StagingDirectory + " SECRET-PASSWORD-123"));

        var result = await _h.RunAsync();

        AssertNoSensitiveText(result);
    }

    private long WriteStaged(FullBackupRequest request)
    {
        File.WriteAllBytes(Path.Combine(_h.StagingDirectory, request.StagedFileName), PipelineHarness.BackupBytes);
        return PipelineHarness.BackupBytes.Length;
    }

    /// <summary>
    /// Makes deleting the staged files fail for every user. Windows: a read handle without delete sharing stays open. Elsewhere a
    /// non-empty directory takes the place of each file (deleting it as a file is refused even for a privileged user).
    /// </summary>
    private void ObstructStagedFiles()
    {
        foreach (var path in Directory.GetFiles(_h.StagingDirectory))
        {
            if (OperatingSystem.IsWindows())
            {
                _h.Hold(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));
                continue;
            }

            File.Delete(path);
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "blocker"), "x");
        }
    }

    private void AssertNoSensitiveText(DatabaseBackupPipelineResult result)
    {
        var text = string.Join('\n',
            new[] { result.Failure?.Message, result.Failure?.ReasonCode }
                .Concat(result.Warnings.SelectMany(w => new[] { w.Code, w.Message }))
                .Concat(result.CleanupFailures.SelectMany(f => new[] { f.ReasonCode, f.Message })));
        Assert.DoesNotContain("SECRET", text, StringComparison.Ordinal);
        Assert.DoesNotContain(_h.StagingDirectory, text, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\secret", text, StringComparison.Ordinal);
    }

    private sealed class ThrowingProgress : IProgress<BackupProgress>
    {
        public void Report(BackupProgress value) => throw new InvalidOperationException("consumer failure");
    }

    private sealed class ThrowingPreparer : IArtifactPreparer
    {
        public Task<PreparedArtifact> PrepareAsync(ArtifactPreparationInput input, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Password=SECRET-PASSWORD-123 while writing the artifact");
    }

    /// <summary>A stand-in for a future compression or encryption step: writes a different file whose bytes differ from the source.</summary>
    private sealed class ReversingPreparer : IArtifactPreparer
    {
        public async Task<PreparedArtifact> PrepareAsync(ArtifactPreparationInput input, CancellationToken cancellationToken)
        {
            var bytes = await File.ReadAllBytesAsync(input.Staging.FilePath(input.SourceFileName), cancellationToken);
            Array.Reverse(bytes);
            var name = input.SourceFileName + ".rev";
            await File.WriteAllBytesAsync(input.Staging.FilePath(name), bytes, cancellationToken);
            return new PreparedArtifact(name, IsSourceFile: false, [new ArtifactTransform(ArtifactTransformKind.Compression, "reverse")]);
        }
    }
}
