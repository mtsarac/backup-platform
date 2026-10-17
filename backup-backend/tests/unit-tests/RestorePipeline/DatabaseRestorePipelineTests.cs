using BackupPlatform.Agent.Core.Backup;
using BackupPlatform.Agent.Core.Restore;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.StorageProviders.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackupPlatform.UnitTests.RestorePipeline;

public sealed class DatabaseRestorePipelineTests : IDisposable
{
    private readonly RestorePipelineHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static RestoreExecutionStage[] Stages(DatabaseRestorePipelineResult result) => result.Stages.Select(record => record.Stage).ToArray();

    /// <summary>Nothing of the target was asked for and nothing is left in staging: the invariants of every failure before the engine's restore.</summary>
    private void AssertUntouched(DatabaseRestorePipelineResult result)
    {
        Assert.Equal(DatabaseRestoreTargetState.NotModified, result.TargetState);
        Assert.Empty(_h.Provider.Executions);
        Assert.Empty(_h.StagedFiles);
    }

    // ---- success ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ASuccessfulRestoreRunsEveryStageInOrderAndLeavesNothingInStaging()
    {
        var result = await _h.RunAsync();

        Assert.Equal(DatabaseRestoreStatus.Succeeded, result.Status);
        Assert.Equal(RestoreExecutionStage.Completed, result.FinalStage);
        Assert.Null(result.Failure);
        Assert.Equal(DatabaseRestoreTargetState.Online, result.TargetState);
        Assert.Equal("ONLINE", result.DatabaseState);
        Assert.Equal(
            [
                RestoreExecutionStage.Preparing, RestoreExecutionStage.DownloadingArtifact, RestoreExecutionStage.VerifyingChecksum,
                RestoreExecutionStage.InspectingBackup, RestoreExecutionStage.PreparingTarget, RestoreExecutionStage.Restoring,
                RestoreExecutionStage.Finalizing, RestoreExecutionStage.Completed
            ],
            Stages(result));
        Assert.All(result.Stages, record => Assert.NotNull(record.FinishedAt));
        Assert.Equal(Stages(result), _h.Progress.Select(progress => progress.Stage).Distinct());
        Assert.True(result.ChecksumVerified);
        Assert.True(result.BackupVerified);
        Assert.Equal(StagingCleanupStatus.Deleted, result.StagingCleanup.Status);
        Assert.Empty(_h.StagedFiles);
        Assert.Empty(result.Warnings);
        Assert.Equal(2, result.FileMappings.Count);
        Assert.NotNull(result.Inspection);
        Assert.NotNull(result.Plan);
        Assert.NotNull(result.RestoreStartedAt);
        Assert.NotNull(result.RestoreFinishedAt);
        Assert.True(result.StartedAt <= result.FinishedAt);
    }

    [Fact]
    public async Task TheStagedCopyIsTheReplicaByteForByteUnderAManagedNameAndTheEngineIsPointedAtItsServerSidePath()
    {
        var result = await _h.RunAsync();

        var staged = Assert.Single(_h.Provider.StagedAtInspect);
        Assert.True(ManagedBackupLayout.IsManagedArtifactName(staged.Name));
        Assert.StartsWith("restore_", staged.Name, StringComparison.Ordinal);
        Assert.Equal(RestorePipelineHarness.BackupBytes, staged.Bytes);
        var inspection = Assert.Single(_h.Provider.Inspections);
        Assert.Equal(Path.Combine(_h.StagingDirectory, staged.Name), inspection.ServerFile.Value);
        Assert.Equal(inspection.ServerFile, Assert.Single(_h.Provider.Executions).ServerFile);
        Assert.Equal(DatabaseRestoreStatus.Succeeded, result.Status);
    }

    [Fact]
    public async Task TheStagedCopyIsHeldAgainstTheStagingSweeperForTheWholeRunAndReleasedAfterwards()
    {
        var result = await _h.RunAsync();

        Assert.True(Assert.Single(_h.Provider.StagedAtInspect).Held);
        Assert.Equal(DatabaseRestoreStatus.Succeeded, result.Status);
        Assert.False(_h.Usage.IsHeld(Assert.Single(_h.Provider.StagedAtInspect).Name));
    }

    [Fact]
    public async Task TheHoldIsReleasedAfterAFailureAndAfterACancellationToo()
    {
        _h.Provider.Inspect = (_, _) => Task.FromResult(new RestoreInspectionResult(null, new RestoreFailure(RestoreFailureKind.BackupInvalid, RestoreExecutionStage.InspectingBackup, "media-invalid", "The file is not a valid backup.")));
        var failed = await _h.RunAsync();
        var name = Assert.Single(_h.Provider.StagedAtInspect).Name;
        Assert.Equal(DatabaseRestoreStatus.Failed, failed.Status);
        Assert.False(_h.Usage.IsHeld(name));

        using var cancelled = new CancellationTokenSource();
        _h.Provider.Inspect = async (_, token) =>
        {
            await cancelled.CancelAsync();
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException();
        };
        var result = await _h.RunAsync(cancellationToken: cancelled.Token);
        Assert.Equal(DatabaseRestoreStatus.Cancelled, result.Status);
        Assert.False(_h.Usage.IsHeld(_h.Provider.StagedAtInspect[0].Name));
    }

    [Fact]
    public async Task TheRunWorksWithoutAStagingRegistry()
    {
        using var harness = new RestorePipelineHarness();
        var pipeline = new DatabaseRestorePipeline([harness.Provider], [harness.Storage], NullLogger<DatabaseRestorePipeline>.Instance);

        var result = await pipeline.RunRestoreAsync(harness.Request(), null, default);

        Assert.Equal(DatabaseRestoreStatus.Succeeded, result.Status);
    }

    [Fact]
    public async Task TheRequestReachesTheProviderWithTheModeTheOptionsAndTheBackupSize()
    {
        var result = await _h.RunAsync(_h.Request(DatabaseRestoreMode.ReplaceExistingDatabase, terminateActiveConnections: true, verifyBackup: false));

        var preCheck = Assert.Single(_h.Provider.PreChecks);
        Assert.Equal(RestorePipelineHarness.Database, preCheck.DatabaseName);
        Assert.Equal(DatabaseRestoreMode.ReplaceExistingDatabase, preCheck.Mode);
        Assert.True(preCheck.TerminateActiveConnections);
        Assert.Equal(RestorePipelineHarness.BackupBytes.Length, preCheck.BackupSizeBytes);
        Assert.False(Assert.Single(_h.Provider.Inspections).RunVerifyOnly);
        var plan = Assert.Single(_h.Provider.Plans);
        Assert.Equal(DatabaseRestoreMode.ReplaceExistingDatabase, plan.Mode);
        Assert.True(plan.TerminateActiveConnections);
        var execute = Assert.Single(_h.Provider.Executions);
        Assert.Equal(DatabaseRestoreMode.ReplaceExistingDatabase, execute.Mode);
        Assert.True(execute.TerminateActiveConnections);
        Assert.Equal(RestorePipelineHarness.Database, execute.DatabaseName);
        Assert.Same(_h.Provider.DefaultPlan, execute.Plan);
        Assert.False(result.BackupVerified);
        Assert.Equal(DatabaseRestoreStatus.Succeeded, result.Status);
    }

    [Fact]
    public async Task TheEnginesVerificationIsRequestedAndRecordedWhenTheOptionIsOn()
    {
        var result = await _h.RunAsync();

        Assert.True(Assert.Single(_h.Provider.Inspections).RunVerifyOnly);
        Assert.True(result.BackupVerified);
    }

    [Fact]
    public async Task AVerificationThatWasRequestedButNotReportedIsAWarningAndNotARecordedPass()
    {
        _h.Provider.Inspect = (_, _) => Task.FromResult(new RestoreInspectionResult(RestorePipelineHarness.Inspection(verified: false), null));

        var result = await _h.RunAsync();

        Assert.Equal(DatabaseRestoreStatus.Succeeded, result.Status);
        Assert.False(result.BackupVerified);
        Assert.Contains(result.Warnings, warning => warning.Code == "backup-not-verified");
    }

    [Fact]
    public async Task TurningChecksumVerificationOffSkipsTheComparisonAndSaysSo()
    {
        var corrupted = (byte[])RestorePipelineHarness.BackupBytes.Clone();
        corrupted[100] ^= 0xFF;
        _h.Storage.Objects[_h.Key.Value] = corrupted;

        var result = await _h.RunAsync(_h.Request(verifyChecksum: false));

        Assert.Equal(DatabaseRestoreStatus.Succeeded, result.Status);
        Assert.False(result.ChecksumVerified);
        var warning = Assert.Single(result.Warnings, w => w.Code == "checksum-not-verified");
        Assert.Contains("turned off", warning.Message, StringComparison.Ordinal);
        Assert.Equal(corrupted, Assert.Single(_h.Provider.StagedAtInspect).Bytes);
    }

    [Fact]
    public async Task TheCopyReportsByteProgressThroughOneBoundedBufferAndTheChecksumStageReportsCompletion()
    {
        var result = await _h.RunAsync();

        var downloads = _h.Progress.Where(progress => progress.Stage == RestoreExecutionStage.DownloadingArtifact && progress.Bytes is not null).ToArray();
        Assert.True(downloads.Length >= 3, "a replica of 2 MiB and a bit is copied in several blocks");
        Assert.Equal(downloads.Select(progress => progress.Bytes), downloads.Select(progress => progress.Bytes).Order());
        Assert.Equal(RestorePipelineHarness.BackupBytes.Length, downloads[^1].Bytes);
        Assert.All(downloads, progress => Assert.Equal(RestorePipelineHarness.BackupBytes.Length, progress.TotalBytes));
        Assert.Equal(100, downloads[^1].Percent);
        var verified = Assert.Single(_h.Progress, progress => progress.Stage == RestoreExecutionStage.VerifyingChecksum && progress.Percent is not null);
        Assert.Equal(100, verified.Percent);
        Assert.Equal(DatabaseRestoreStatus.Succeeded, result.Status);
    }

    [Fact]
    public async Task TheProvidersOwnStagesAndPercentAreForwardedAndItsWarningsAreKept()
    {
        _h.Provider.Execute = (request, progress, _) =>
        {
            progress!.Report(new RestoreProgress(RestoreExecutionStage.PreparingTarget, null, null, null, DateTimeOffset.UtcNow));
            progress.Report(new RestoreProgress(RestoreExecutionStage.Restoring, 73, null, null, DateTimeOffset.UtcNow));
            progress.Report(new RestoreProgress(RestoreExecutionStage.Completed, 100, null, null, DateTimeOffset.UtcNow));
            return Task.FromResult(RestorePipelineHarness.Succeeded(request.Plan, [new BackupWarning("access-restored", "Multi-user access was restored.")]));
        };

        var result = await _h.RunAsync();

        Assert.Contains(_h.Progress, progress => progress is { Stage: RestoreExecutionStage.Restoring, Percent: 73 });
        Assert.Single(result.Warnings, warning => warning.Code == "access-restored");
        Assert.Equal(RestoreExecutionStage.Completed, result.FinalStage);
    }

    [Fact]
    public async Task AllItemsOfTheAgentAndTheProviderAreMergedIntoOneReportInTheOrderTheyWereProduced()
    {
        _h.Provider.PreCheck = RestorePipelineHarness.Report(
            new BackupPreCheckItem("server.reachable", BackupPreCheckStatus.Passed, "ok", "Reachable."),
            new BackupPreCheckItem("target.sessions", BackupPreCheckStatus.Warning, "sessions-unknown", "Sessions could not be counted."));

        var result = await _h.RunAsync();

        var ids = result.PreCheck.Select(item => item.CheckId).ToArray();
        Assert.Equal(["source.storage", "source.key", BackupPreCheckIds.StagingAgentAccess, BackupPreCheckIds.StagingDiskSpace, "source.replica", "server.reachable", "target.sessions", "plan.version"], ids);
        Assert.Equal(BackupPreCheckStatus.Warning, result.PreCheck.Single(item => item.CheckId == "target.sessions").Status);
        Assert.All(result.PreCheck, item => Assert.NotEqual(BackupPreCheckStatus.Failed, item.Status));
    }

    [Fact]
    public async Task APlansWarningsAndTheInspectionsWarningsAreRecorded()
    {
        _h.Provider.Inspect = (_, _) => Task.FromResult(new RestoreInspectionResult(RestorePipelineHarness.Inspection(), null, [new BackupWarning("inspect-note", "Inspection note.")]));
        _h.Provider.PlanAsync = (_, _) => Task.FromResult(new RestorePlanResult(
            RestorePipelineHarness.Report(new BackupPreCheckItem("plan.version", BackupPreCheckStatus.Warning, "newer-minor", "The backup is from a newer build.")),
            RestorePipelineHarness.Plan([new BackupWarning("plan-note", "Plan note.")])));

        var result = await _h.RunAsync();

        Assert.Equal(DatabaseRestoreStatus.Succeeded, result.Status);
        Assert.Equal(["inspect-note", "plan-note"], result.Warnings.Select(warning => warning.Code));
        Assert.Contains(result.PreCheck, item => item is { CheckId: "plan.version", Status: BackupPreCheckStatus.Warning });
    }

    [Fact]
    public async Task AProgressConsumerThatThrowsCannotChangeTheOutcome()
    {
        var result = await _h.Pipeline.RunRestoreAsync(_h.Request(), new ThrowingProgress(), default);

        Assert.Equal(DatabaseRestoreStatus.Succeeded, result.Status);
        Assert.Empty(_h.StagedFiles);
    }

    // ---- composition --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ARestoreForAnEngineWithoutARestoreProviderFailsAsUnsupportedBeforeAnythingIsRead()
    {
        using var harness = new RestorePipelineHarness(withProvider: false);

        var result = await harness.RunAsync();

        Assert.Equal(DatabaseRestoreStatus.Failed, result.Status);
        Assert.Equal(RestoreAgentFailureCodes.UnsupportedCapability, result.Failure!.Code);
        Assert.Equal(RestorePipelineReasons.EngineNotAvailable, result.Failure.ReasonCode);
        Assert.Equal(DatabaseRestoreTargetState.NotModified, result.TargetState);
        Assert.Equal(0, harness.Storage.OpenReads);
        Assert.Equal(0, harness.Storage.MetadataReads);
        Assert.Empty(harness.StagedFiles);
    }

    [Fact]
    public async Task AReplicaOnAStorageThisAgentDoesNotHaveIsUnavailable()
    {
        using var harness = new RestorePipelineHarness(withStorage: false);

        var result = await harness.RunAsync();

        Assert.Equal(RestoreAgentFailureCodes.ReplicaUnavailable, result.Failure!.Code);
        Assert.Equal(RestorePipelineReasons.StorageNotAvailable, result.Failure.ReasonCode);
        Assert.Equal(DatabaseRestoreTargetState.NotModified, result.TargetState);
        Assert.Empty(harness.Provider.PreChecks);
    }

    [Fact]
    public async Task AStorageThatCannotBeReadIsUnsupportedAndNeverAsked()
    {
        _h.Storage.Capabilities = new([StorageCapabilityIds.Delete]);

        var result = await _h.RunAsync();

        Assert.Equal(RestoreAgentFailureCodes.UnsupportedCapability, result.Failure!.Code);
        Assert.Equal(RestorePipelineReasons.StorageNotReadable, result.Failure.ReasonCode);
        Assert.Equal(0, _h.Storage.OpenReads);
        Assert.Equal(0, _h.Storage.MetadataReads);
        AssertUntouched(result);
    }

    [Fact]
    public async Task ARequestForAnotherStorageProviderIdIsResolvedByThatId()
    {
        var result = await _h.RunAsync(_h.Request(storage: StorageProviderId.Central));

        Assert.Equal(RestorePipelineReasons.StorageNotAvailable, result.Failure!.ReasonCode);
    }

    // ---- replica identity -----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("not-a-managed-key.bak")]
    [InlineData("a/b/c/d/e.bak")]
    [InlineData("backups/00000000000000000000000000000001/00000000000000000000000000000002/00000000000000000000000000000003/00000000000000000000000000000004/Sales.bak")]
    public async Task AKeyOutsideThePlatformsLayoutIsRefusedAndNeverRead(string key)
    {
        var result = await _h.RunAsync(_h.Request(key: new StorageObjectKey(key)));

        Assert.Equal(RestoreAgentFailureCodes.InvalidConfiguration, result.Failure!.Code);
        Assert.Equal(RestorePipelineReasons.InvalidReplicaKey, result.Failure.ReasonCode);
        Assert.Equal(0, _h.Storage.OpenReads);
        Assert.Equal(0, _h.Storage.MetadataReads);
        AssertUntouched(result);
    }

    [Fact]
    public async Task AnUnpublishedUploadKeyIsNotAReplica()
    {
        var name = ManagedBackupLayout.StagedFileName("Sales", DateTimeOffset.UtcNow, Guid.NewGuid());
        var partial = new StorageObjectKey($"{_h.Tenant:N}/{Guid.NewGuid():N}/{Guid.NewGuid():N}/{Guid.NewGuid():N}/.{name}.{Guid.NewGuid():N}.partial");

        var result = await _h.RunAsync(_h.Request(key: partial));

        Assert.Equal(RestorePipelineReasons.InvalidReplicaKey, result.Failure!.ReasonCode);
        Assert.Equal(0, _h.Storage.OpenReads);
    }

    [Fact]
    public async Task AReplicaOfAnotherTenantIsNeverRead()
    {
        var result = await _h.RunAsync(_h.Request(tenant: Guid.NewGuid()));

        Assert.Equal(RestoreAgentFailureCodes.InvalidConfiguration, result.Failure!.Code);
        Assert.Equal(RestorePipelineReasons.ReplicaTenantMismatch, result.Failure.ReasonCode);
        Assert.Equal(0, _h.Storage.OpenReads);
        Assert.Equal(0, _h.Storage.MetadataReads);
        Assert.Empty(_h.Provider.PreChecks);
        AssertUntouched(result);
        Assert.Contains(result.PreCheck, item => item is { CheckId: "source.key", Status: BackupPreCheckStatus.Failed });
    }

    [Fact]
    public async Task AReplicaThatIsGoneFromStorageIsUnavailable()
    {
        _h.Storage.Objects.Clear();

        var result = await _h.RunAsync();

        Assert.Equal(RestoreAgentFailureCodes.ReplicaUnavailable, result.Failure!.Code);
        Assert.Equal(RestorePipelineReasons.ReplicaMissing, result.Failure.ReasonCode);
        Assert.Equal(RestoreExecutionStage.Preparing, result.Failure.Stage);
        Assert.Equal(DatabaseRestoreStatus.Failed, result.Status);
        Assert.Equal(RestoreExecutionStage.Failed, result.FinalStage);
        Assert.Equal(0, _h.Storage.OpenReads);
        Assert.Empty(_h.Provider.PreChecks);
        AssertUntouched(result);
    }

    [Fact]
    public async Task AReplicaThatVanishesBetweenTheCheckAndTheReadIsUnavailable()
    {
        _h.Storage.OnOpenRead = (_, _) => Task.FromException<Stream>(new StorageException(StorageFailureKind.NotFound, "The stored object does not exist."));

        var result = await _h.RunAsync();

        Assert.Equal(RestoreAgentFailureCodes.ReplicaUnavailable, result.Failure!.Code);
        Assert.Equal(RestoreExecutionStage.DownloadingArtifact, result.Failure.Stage);
        AssertUntouched(result);
    }

    [Fact]
    public async Task AReplicaWhoseStoredSizeChangedIsRefusedBeforeAnythingIsCopied()
    {
        _h.Storage.Objects[_h.Key.Value] = RestorePipelineHarness.BackupBytes[..^1];

        var result = await _h.RunAsync();

        Assert.Equal(RestoreAgentFailureCodes.ChecksumMismatch, result.Failure!.Code);
        Assert.Equal(RestorePipelineReasons.ReplicaSizeMismatch, result.Failure.ReasonCode);
        Assert.Equal(RestoreExecutionStage.Preparing, result.Failure.Stage);
        Assert.Equal(0, _h.Storage.OpenReads);
        AssertUntouched(result);
    }

    // ---- integrity ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ABitFlipWithTheRightSizeIsAChecksumMismatchBeforeAnyTargetChange()
    {
        var corrupted = (byte[])RestorePipelineHarness.BackupBytes.Clone();
        corrupted[corrupted.Length / 2] ^= 0x01;
        _h.Storage.Objects[_h.Key.Value] = corrupted;

        var result = await _h.RunAsync();

        Assert.Equal(DatabaseRestoreStatus.Failed, result.Status);
        Assert.Equal(RestoreAgentFailureCodes.ChecksumMismatch, result.Failure!.Code);
        Assert.Equal(RestorePipelineReasons.ReplicaChecksumMismatch, result.Failure.ReasonCode);
        Assert.Equal(RestoreExecutionStage.VerifyingChecksum, result.Failure.Stage);
        Assert.False(result.ChecksumVerified);
        Assert.Empty(_h.Provider.Inspections);
        Assert.Empty(_h.Provider.Plans);
        AssertUntouched(result);
        Assert.Equal(StagingCleanupStatus.Deleted, result.StagingCleanup.Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AReplicaThatIsShorterThanPublishedIsAChecksumMismatchEvenWhenTheChecksumComparisonIsOff(bool verifyChecksum)
    {
        _h.Storage.Objects[_h.Key.Value] = RestorePipelineHarness.BackupBytes[..^100];
        _h.Storage.OnGetMetadata = key => Task.FromResult<StorageObjectMetadata?>(new StorageObjectMetadata(new StorageLocation(StorageProviderId.Local, key), RestorePipelineHarness.BackupBytes.Length, null, DateTimeOffset.UtcNow));

        var result = await _h.RunAsync(_h.Request(verifyChecksum: verifyChecksum));

        Assert.Equal(RestoreAgentFailureCodes.ChecksumMismatch, result.Failure!.Code);
        Assert.Equal(RestorePipelineReasons.ReplicaSizeMismatch, result.Failure.ReasonCode);
        Assert.Equal(RestoreExecutionStage.VerifyingChecksum, result.Failure.Stage);
        AssertUntouched(result);
    }

    [Fact]
    public async Task AReplicaThatIsLongerThanPublishedStopsTheCopyEarlyInsteadOfFillingTheStagingVolume()
    {
        var longer = new byte[RestorePipelineHarness.BackupBytes.Length + 3 * 1024 * 1024];
        RestorePipelineHarness.BackupBytes.CopyTo(longer, 0);
        _h.Storage.OnGetMetadata = key => Task.FromResult<StorageObjectMetadata?>(new StorageObjectMetadata(new StorageLocation(StorageProviderId.Local, key), RestorePipelineHarness.BackupBytes.Length, null, DateTimeOffset.UtcNow));
        _h.Storage.OnOpenRead = (_, _) => Task.FromResult<Stream>(new MemoryStream(longer, writable: false));

        var result = await _h.RunAsync();

        Assert.Equal(RestoreAgentFailureCodes.ChecksumMismatch, result.Failure!.Code);
        Assert.Equal(RestorePipelineReasons.ReplicaSizeMismatch, result.Failure.ReasonCode);
        Assert.All(_h.Progress.Where(progress => progress.Bytes is not null), progress => Assert.True(progress.Bytes <= RestorePipelineHarness.BackupBytes.Length));
        AssertUntouched(result);
    }

    // ---- storage failures ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(StorageFailureKind.NotFound, RestoreAgentFailureCodes.ReplicaUnavailable)]
    [InlineData(StorageFailureKind.AccessDenied, RestoreAgentFailureCodes.StorageUnavailable)]
    [InlineData(StorageFailureKind.Io, RestoreAgentFailureCodes.StorageUnavailable)]
    [InlineData(StorageFailureKind.Unexpected, RestoreAgentFailureCodes.StorageUnavailable)]
    [InlineData(StorageFailureKind.AlreadyExists, RestoreAgentFailureCodes.StorageUnavailable)]
    [InlineData(StorageFailureKind.InsufficientSpace, RestoreAgentFailureCodes.InsufficientDiskSpace)]
    [InlineData(StorageFailureKind.NotSupported, RestoreAgentFailureCodes.UnsupportedCapability)]
    [InlineData(StorageFailureKind.InvalidConfiguration, RestoreAgentFailureCodes.InvalidConfiguration)]
    [InlineData(StorageFailureKind.IntegrityMismatch, RestoreAgentFailureCodes.ChecksumMismatch)]
    public async Task StorageFailuresWhileOpeningTheReplicaMapToThePlatformVocabulary(StorageFailureKind kind, string expected)
    {
        _h.Storage.OnOpenRead = (_, _) => Task.FromException<Stream>(new StorageException(kind, "A fixed storage sentence.", new IOException("C:\\secret\\path Password=SECRET-PASSWORD-123")));

        var result = await _h.RunAsync();

        Assert.Equal(expected, result.Failure!.Code);
        Assert.Equal("A fixed storage sentence.", result.Failure.Message);
        AssertUntouched(result);
    }

    [Theory]
    [InlineData(StorageFailureKind.NotFound, RestoreAgentFailureCodes.ReplicaUnavailable)]
    [InlineData(StorageFailureKind.AccessDenied, RestoreAgentFailureCodes.StorageUnavailable)]
    [InlineData(StorageFailureKind.Io, RestoreAgentFailureCodes.StorageUnavailable)]
    public async Task StorageFailuresWhileCheckingTheReplicaMapToThePlatformVocabulary(StorageFailureKind kind, string expected)
    {
        _h.Storage.OnGetMetadata = _ => Task.FromException<StorageObjectMetadata?>(new StorageException(kind, "A fixed storage sentence."));

        var result = await _h.RunAsync();

        Assert.Equal(expected, result.Failure!.Code);
        Assert.Equal(RestoreExecutionStage.Preparing, result.Failure.Stage);
        Assert.Contains(result.PreCheck, item => item is { CheckId: "source.replica", Status: BackupPreCheckStatus.Failed });
        AssertUntouched(result);
    }

    [Fact]
    public async Task AnUnexpectedStorageExceptionIsStorageUnavailableWithoutItsText()
    {
        _h.Storage.OnOpenRead = (_, _) => throw new InvalidOperationException("Password=SECRET-PASSWORD-123 at /srv/secret");

        var result = await _h.RunAsync();

        Assert.Equal(RestoreAgentFailureCodes.StorageUnavailable, result.Failure!.Code);
        Assert.DoesNotContain("SECRET", result.Failure.Message, StringComparison.Ordinal);
        AssertUntouched(result);
    }

    [Fact]
    public async Task AReadThatBreaksMidStreamIsAnArtifactTransferFailureAndTheStagedPartIsRemoved()
    {
        _h.Storage.OnOpenRead = (_, _) => Task.FromResult<Stream>(new BrokenStream(
            RestorePipelineHarness.BackupBytes, 1024 * 1024 + 5, new IOException("The network name is no longer available. C:\\share\\secret.bak")));

        var result = await _h.RunAsync();

        Assert.Equal(RestoreAgentFailureCodes.ArtifactTransferFailed, result.Failure!.Code);
        Assert.Equal(RestoreExecutionStage.DownloadingArtifact, result.Failure.Stage);
        Assert.DoesNotContain("secret", result.Failure.Message, StringComparison.OrdinalIgnoreCase);
        AssertUntouched(result);
    }

    // ---- staging ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AStagingDirectoryThatDoesNotExistIsStagingInvalid()
    {
        var missing = StagingArea.SameHost(Path.Combine(_h.StagingDirectory, "missing"));

        var result = await _h.RunAsync(_h.Request(staging: missing));

        Assert.Equal(RestoreAgentFailureCodes.StagingInvalid, result.Failure!.Code);
        Assert.Equal(RestorePipelineReasons.StagingMissing, result.Failure.ReasonCode);
        Assert.Equal(0, _h.Storage.OpenReads);
        Assert.Empty(_h.Provider.PreChecks);
        Assert.Equal(DatabaseRestoreTargetState.NotModified, result.TargetState);
    }

    [Fact]
    public async Task AStagingDirectoryTheAgentCannotWriteIsStagingInvalid()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            return;
        }

        File.SetUnixFileMode(_h.StagingDirectory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var result = await _h.RunAsync();

            Assert.Equal(RestoreAgentFailureCodes.StagingInvalid, result.Failure!.Code);
            Assert.Equal(RestorePipelineReasons.StagingNotWritable, result.Failure.ReasonCode);
            Assert.Equal(0, _h.Storage.OpenReads);
            Assert.Empty(_h.Provider.PreChecks);
        }
        finally
        {
            File.SetUnixFileMode(_h.StagingDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task AReplicaLargerThanTheFreeSpaceOfTheStagingVolumeIsInsufficientDiskSpaceBeforeAnyByteIsCopied()
    {
        var huge = long.MaxValue / 4;
        _h.Storage.OnGetMetadata = key => Task.FromResult<StorageObjectMetadata?>(new StorageObjectMetadata(new StorageLocation(StorageProviderId.Local, key), huge, null, DateTimeOffset.UtcNow));

        var result = await _h.RunAsync(_h.Request(size: huge));

        Assert.Equal(RestoreAgentFailureCodes.InsufficientDiskSpace, result.Failure!.Code);
        Assert.Equal(RestorePipelineReasons.StagingInsufficientSpace, result.Failure.ReasonCode);
        Assert.Equal(0, _h.Storage.OpenReads);
        Assert.Contains(result.PreCheck, item => item is { CheckId: BackupPreCheckIds.StagingDiskSpace, Status: BackupPreCheckStatus.Failed });
        AssertUntouched(result);
    }

    [Fact]
    public async Task AStagingDirectoryThatDisappearsWhileCopyingIsStagingInvalid()
    {
        _h.Storage.OnOpenRead = (key, _) =>
        {
            Directory.Delete(_h.StagingDirectory, recursive: true);
            return Task.FromResult<Stream>(new MemoryStream(RestorePipelineHarness.BackupBytes, writable: false));
        };

        var result = await _h.RunAsync();

        Assert.Equal(RestoreAgentFailureCodes.StagingInvalid, result.Failure!.Code);
        Assert.Equal(RestorePipelineReasons.StagingWriteFailed, result.Failure.ReasonCode);
        Assert.Equal(RestoreExecutionStage.DownloadingArtifact, result.Failure.Stage);
        Assert.Equal(DatabaseRestoreTargetState.NotModified, result.TargetState);
        Assert.Empty(_h.Provider.Executions);
    }

    [Theory]
    [InlineData(28, true)]
    [InlineData(122, true)]
    [InlineData(unchecked((int)0x80070070), true)]
    [InlineData(unchecked((int)0x80070027), true)]
    [InlineData(5, false)]
    [InlineData(0, false)]
    public void AFullDiskIsRecognisedFromTheErrorTheOperatingSystemGave(int hresult, bool expected) =>
        Assert.Equal(expected, DatabaseRestoreExecution.IsDiskFull(new IOException("write failed", hresult)));

    [Fact]
    public void OnlyAnIoExceptionCanBeAFullDisk() =>
        Assert.False(DatabaseRestoreExecution.IsDiskFull(new UnauthorizedAccessException()));

    // ---- pre-check ----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(RestoreReasonCodes.TargetDatabaseExists, RestoreAgentFailureCodes.TargetDatabaseExists)]
    [InlineData(RestoreReasonCodes.TargetDatabaseMissing, RestoreAgentFailureCodes.TargetDatabaseNotFound)]
    [InlineData(RestoreReasonCodes.TargetInUse, RestoreAgentFailureCodes.TargetInUse)]
    [InlineData(RestoreReasonCodes.TargetProtected, RestoreAgentFailureCodes.TargetProtected)]
    [InlineData(RestoreReasonCodes.BackupTypeUnsupported, RestoreAgentFailureCodes.BackupTypeUnsupported)]
    [InlineData(RestoreReasonCodes.TargetVersionTooOld, RestoreAgentFailureCodes.IncompatibleVersion)]
    [InlineData(RestoreReasonCodes.InsufficientDiskSpace, RestoreAgentFailureCodes.InsufficientDiskSpace)]
    [InlineData(RestoreReasonCodes.FileMappingFailed, RestoreAgentFailureCodes.FileMappingFailed)]
    public async Task AFailedPreCheckItemWithAnEngineIndependentReasonIsMappedByReason(string reason, string expected)
    {
        _h.Provider.PreCheck = RestorePipelineHarness.Report(RestorePipelineHarness.Failed(reason));

        var result = await _h.RunAsync();

        Assert.Equal(DatabaseRestoreStatus.Failed, result.Status);
        Assert.Equal(expected, result.Failure!.Code);
        Assert.Equal(reason, result.Failure.ReasonCode);
        Assert.Equal(RestoreExecutionStage.Preparing, result.Failure.Stage);
        Assert.Equal("A fixed sentence about " + reason + ".", result.Failure.Message);
        Assert.Equal(0, _h.Storage.OpenReads);
        Assert.Empty(_h.Provider.Inspections);
        AssertUntouched(result);
    }

    [Theory]
    [InlineData(BackupFailureCause.DatabaseUnavailable, RestoreAgentFailureCodes.DatabaseUnavailable)]
    [InlineData(BackupFailureCause.Timeout, RestoreAgentFailureCodes.OperationTimedOut)]
    [InlineData(BackupFailureCause.AuthenticationFailed, RestoreAgentFailureCodes.AuthenticationFailed)]
    [InlineData(BackupFailureCause.PermissionDenied, RestoreAgentFailureCodes.PermissionDenied)]
    [InlineData(BackupFailureCause.InvalidConfiguration, RestoreAgentFailureCodes.InvalidConfiguration)]
    [InlineData(BackupFailureCause.Unsupported, RestoreAgentFailureCodes.UnsupportedCapability)]
    [InlineData(BackupFailureCause.StagingInvalid, RestoreAgentFailureCodes.StagingInvalid)]
    [InlineData(BackupFailureCause.StorageUnavailable, RestoreAgentFailureCodes.StorageUnavailable)]
    [InlineData(BackupFailureCause.DatabaseNotFound, RestoreAgentFailureCodes.TargetDatabaseNotFound)]
    [InlineData(BackupFailureCause.Unclassified, RestoreAgentFailureCodes.PreCheckFailed)]
    public async Task AFailedPreCheckItemWithAProviderSpecificReasonIsMappedByItsCause(BackupFailureCause cause, string expected)
    {
        _h.Provider.PreCheck = RestorePipelineHarness.Report(RestorePipelineHarness.Failed("provider-specific", cause));

        var result = await _h.RunAsync();

        Assert.Equal(expected, result.Failure!.Code);
        Assert.Equal("provider-specific", result.Failure.ReasonCode);
        AssertUntouched(result);
    }

    [Fact]
    public async Task TheReasonCodeWinsOverTheCauseAndTheFirstFailedItemDecides()
    {
        _h.Provider.PreCheck = RestorePipelineHarness.Report(
            new BackupPreCheckItem("a", BackupPreCheckStatus.Warning, "just-a-warning", "Warning."),
            RestorePipelineHarness.Failed(RestoreReasonCodes.TargetInUse, BackupFailureCause.PermissionDenied, "target.sessions"),
            RestorePipelineHarness.Failed(RestoreReasonCodes.TargetProtected, BackupFailureCause.DatabaseUnavailable, "target.protected"));

        var result = await _h.RunAsync();

        Assert.Equal(RestoreAgentFailureCodes.TargetInUse, result.Failure!.Code);
        Assert.Equal(2, result.PreCheck.Count(item => item.Status == BackupPreCheckStatus.Failed));
    }

    [Fact]
    public async Task APreCheckThatThrowsIsAnInternalErrorWithoutTheExceptionText()
    {
        _h.Provider.PreCheckAsync = (_, _) => throw new InvalidOperationException("Server=db;Password=SECRET-PASSWORD-123");

        var result = await _h.RunAsync();

        Assert.Equal(RestoreAgentFailureCodes.InternalError, result.Failure!.Code);
        Assert.Equal(RestorePipelineReasons.PreCheckError, result.Failure.ReasonCode);
        Assert.DoesNotContain("SECRET", result.Failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, _h.Storage.OpenReads);
        AssertUntouched(result);
    }

    [Fact]
    public async Task TheProviderIsHandedTheCredentialAndTheConnectionOfTheRequest()
    {
        var request = _h.Request();

        await _h.RunAsync(request);

        Assert.Same(request.Credential, _h.Provider.LastCredential);
    }

    // ---- inspection and plan -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(RestoreFailureKind.BackupInvalid, "media-invalid", BackupFailureCause.Unclassified, RestoreAgentFailureCodes.BackupInvalid)]
    [InlineData(RestoreFailureKind.BackupInvalid, RestoreReasonCodes.BackupTypeUnsupported, BackupFailureCause.Unclassified, RestoreAgentFailureCodes.BackupTypeUnsupported)]
    [InlineData(RestoreFailureKind.BackupInvalid, "verify-timeout", BackupFailureCause.Timeout, RestoreAgentFailureCodes.OperationTimedOut)]
    [InlineData(RestoreFailureKind.BackupInvalid, "file-unreadable", BackupFailureCause.StagingInvalid, RestoreAgentFailureCodes.StagingInvalid)]
    [InlineData(RestoreFailureKind.BackupInvalid, "login-failed", BackupFailureCause.AuthenticationFailed, RestoreAgentFailureCodes.AuthenticationFailed)]
    [InlineData(RestoreFailureKind.PreCheckFailed, "anything", BackupFailureCause.Unclassified, RestoreAgentFailureCodes.PreCheckFailed)]
    [InlineData(RestoreFailureKind.SqlRestoreFailed, "anything", BackupFailureCause.Unclassified, RestoreAgentFailureCodes.SqlRestoreFailed)]
    public async Task AnInspectionFailureIsMappedByReasonThenCauseThenKindAndTheStagedCopyIsRemoved(
        RestoreFailureKind kind, string reason, BackupFailureCause cause, string expected)
    {
        _h.Provider.Inspect = (_, _) => Task.FromResult(new RestoreInspectionResult(
            null, new RestoreFailure(kind, RestoreExecutionStage.InspectingBackup, reason, "A fixed inspection sentence.", cause)));

        var result = await _h.RunAsync();

        Assert.Equal(DatabaseRestoreStatus.Failed, result.Status);
        Assert.Equal(expected, result.Failure!.Code);
        Assert.Equal(reason, result.Failure.ReasonCode);
        Assert.Equal(RestoreExecutionStage.InspectingBackup, result.Failure.Stage);
        Assert.Equal("A fixed inspection sentence.", result.Failure.Message);
        Assert.True(result.ChecksumVerified);
        Assert.Empty(_h.Provider.Plans);
        AssertUntouched(result);
    }

    [Fact]
    public async Task AnInspectionThatThrowsIsAnInternalErrorOnAnUntouchedTarget()
    {
        _h.Provider.Inspect = (_, _) => throw new InvalidOperationException("Password=SECRET-PASSWORD-123");

        var result = await _h.RunAsync();

        Assert.Equal(RestoreAgentFailureCodes.InternalError, result.Failure!.Code);
        Assert.Equal(RestorePipelineReasons.InspectionError, result.Failure.ReasonCode);
        Assert.DoesNotContain("SECRET", result.Failure.Message, StringComparison.Ordinal);
        AssertUntouched(result);
    }

    [Fact]
    public async Task APlanThatFailsItsChecksStopsTheRestoreAndItsItemsAreReported()
    {
        _h.Provider.PlanAsync = (_, _) => Task.FromResult(new RestorePlanResult(
            RestorePipelineHarness.Report(
                new BackupPreCheckItem("plan.database", BackupPreCheckStatus.Passed, "ok", "Fine."),
                RestorePipelineHarness.Failed(RestoreReasonCodes.TargetVersionTooOld, checkId: "plan.version")),
            null));

        var result = await _h.RunAsync();

        Assert.Equal(RestoreAgentFailureCodes.IncompatibleVersion, result.Failure!.Code);
        Assert.Equal(RestoreExecutionStage.InspectingBackup, result.Failure.Stage);
        Assert.Contains(result.PreCheck, item => item is { CheckId: "plan.version", Status: BackupPreCheckStatus.Failed });
        Assert.Null(result.Plan);
        Assert.NotNull(result.Inspection);
        AssertUntouched(result);
    }

    [Fact]
    public async Task APlanThatThrowsIsAnInternalErrorOnAnUntouchedTarget()
    {
        _h.Provider.PlanAsync = (_, _) => throw new InvalidOperationException("boom");

        var result = await _h.RunAsync();

        Assert.Equal(RestoreAgentFailureCodes.InternalError, result.Failure!.Code);
        Assert.Equal(RestorePipelineReasons.PlanError, result.Failure.ReasonCode);
        AssertUntouched(result);
    }

    // ---- execution ----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(RestoreFailureKind.SqlRestoreFailed, "restore-failed", BackupFailureCause.Unclassified, RestoreAgentFailureCodes.SqlRestoreFailed)]
    [InlineData(RestoreFailureKind.SqlRestoreFailed, RestoreReasonCodes.InsufficientDiskSpace, BackupFailureCause.Unclassified, RestoreAgentFailureCodes.InsufficientDiskSpace)]
    [InlineData(RestoreFailureKind.SqlRestoreFailed, RestoreReasonCodes.FileMappingFailed, BackupFailureCause.Unclassified, RestoreAgentFailureCodes.FileMappingFailed)]
    [InlineData(RestoreFailureKind.SqlRestoreFailed, "permission", BackupFailureCause.PermissionDenied, RestoreAgentFailureCodes.PermissionDenied)]
    [InlineData(RestoreFailureKind.SqlRestoreFailed, "timeout", BackupFailureCause.Timeout, RestoreAgentFailureCodes.OperationTimedOut)]
    [InlineData(RestoreFailureKind.SqlRestoreFailed, "connection-lost", BackupFailureCause.DatabaseUnavailable, RestoreAgentFailureCodes.DatabaseUnavailable)]
    [InlineData(RestoreFailureKind.BackupInvalid, "media-invalid", BackupFailureCause.Unclassified, RestoreAgentFailureCodes.BackupInvalid)]
    [InlineData(RestoreFailureKind.PreCheckFailed, RestoreReasonCodes.TargetInUse, BackupFailureCause.Unclassified, RestoreAgentFailureCodes.TargetInUse)]
    public async Task AFailedRestoreIsMappedAndKeepsTheTargetStateTheProviderEstablished(
        RestoreFailureKind kind, string reason, BackupFailureCause cause, string expected)
    {
        _h.Provider.Execute = (request, _, _) => Task.FromResult(RestorePipelineHarness.NotSucceeded(
            request.Plan,
            new RestoreFailure(kind, RestoreExecutionStage.Restoring, reason, "A fixed restore sentence.", cause),
            DatabaseRestoreTargetState.PartiallyRestored,
            "RESTORING",
            [new BackupWarning("single-user-left", "Could not restore multi-user access.")]));

        var result = await _h.RunAsync();

        Assert.Equal(DatabaseRestoreStatus.Failed, result.Status);
        Assert.Equal(RestoreExecutionStage.Failed, result.FinalStage);
        Assert.Equal(expected, result.Failure!.Code);
        Assert.Equal(reason, result.Failure.ReasonCode);
        Assert.Equal(RestoreExecutionStage.Restoring, result.Failure.Stage);
        Assert.Equal(DatabaseRestoreTargetState.PartiallyRestored, result.TargetState);
        Assert.Equal("RESTORING", result.DatabaseState);
        Assert.Single(result.Warnings, warning => warning.Code == "single-user-left");
        Assert.Equal(2, result.FileMappings.Count);
        Assert.Null(result.RestoreFinishedAt);
        Assert.NotNull(result.RestoreStartedAt);
        Assert.Empty(_h.StagedFiles);
        Assert.Equal(StagingCleanupStatus.Deleted, result.StagingCleanup.Status);
    }

    [Theory]
    [InlineData(DatabaseRestoreTargetState.NotModified)]
    [InlineData(DatabaseRestoreTargetState.PartiallyRestored)]
    [InlineData(DatabaseRestoreTargetState.Unknown)]
    public async Task EveryTargetStateTheProviderReportsOnFailureIsPassedThroughUnchanged(DatabaseRestoreTargetState state)
    {
        _h.Provider.Execute = (request, _, _) => Task.FromResult(RestorePipelineHarness.NotSucceeded(
            request.Plan, new RestoreFailure(RestoreFailureKind.SqlRestoreFailed, RestoreExecutionStage.Restoring, "x", "A fixed sentence."), state, null));

        var result = await _h.RunAsync();

        Assert.Equal(state, result.TargetState);
        Assert.Null(result.DatabaseState);
    }

    [Fact]
    public async Task AProviderThatThrowsDuringTheRestoreLeavesTheTargetUnknownAndNoExceptionText()
    {
        _h.Provider.Execute = (_, _, _) => throw new InvalidOperationException("Server=db;Password=SECRET-PASSWORD-123");

        var result = await _h.RunAsync();

        Assert.Equal(DatabaseRestoreStatus.Failed, result.Status);
        Assert.Equal(RestoreAgentFailureCodes.InternalError, result.Failure!.Code);
        Assert.Equal(RestorePipelineReasons.ProviderError, result.Failure.ReasonCode);
        Assert.DoesNotContain("SECRET", result.Failure.Message, StringComparison.Ordinal);
        Assert.Equal(DatabaseRestoreTargetState.Unknown, result.TargetState);
        Assert.Empty(_h.StagedFiles);
    }

    [Fact]
    public async Task ARestoreTheProviderCancelledReportsCancelledWithItsHonestTargetState()
    {
        _h.Provider.Execute = (request, _, _) => Task.FromResult(RestorePipelineHarness.NotSucceeded(
            request.Plan,
            new RestoreFailure(RestoreFailureKind.Cancelled, RestoreExecutionStage.Restoring, "cancelled", "The restore was cancelled."),
            DatabaseRestoreTargetState.PartiallyRestored));

        var result = await _h.RunAsync();

        Assert.Equal(DatabaseRestoreStatus.Cancelled, result.Status);
        Assert.Equal(RestoreExecutionStage.Cancelled, result.FinalStage);
        Assert.Equal(RestoreAgentFailureCodes.Cancelled, result.Failure!.Code);
        Assert.Equal(DatabaseRestoreTargetState.PartiallyRestored, result.TargetState);
        Assert.Equal("RESTORING", result.DatabaseState);
        Assert.Empty(_h.StagedFiles);
    }

    // ---- cancellation -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ACancellationBeforeTheRunTouchesNothing()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var result = await _h.RunAsync(cancellationToken: cancelled.Token);

        Assert.Equal(DatabaseRestoreStatus.Cancelled, result.Status);
        Assert.Equal(RestoreAgentFailureCodes.Cancelled, result.Failure!.Code);
        Assert.Equal(RestoreExecutionStage.Cancelled, result.FinalStage);
        Assert.Equal(0, _h.Storage.OpenReads);
        AssertUntouched(result);
    }

    [Fact]
    public async Task ACancellationDuringThePreCheckLeavesTheTargetAlone()
    {
        using var cts = new CancellationTokenSource();
        _h.Provider.PreCheckAsync = async (_, token) =>
        {
            await cts.CancelAsync();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException();
        };

        var result = await _h.RunAsync(cancellationToken: cts.Token);

        Assert.Equal(DatabaseRestoreStatus.Cancelled, result.Status);
        Assert.Equal(RestoreExecutionStage.Preparing, result.Failure!.Stage);
        Assert.Equal(0, _h.Storage.OpenReads);
        AssertUntouched(result);
    }

    [Fact]
    public async Task ACancellationDuringTheCopyStopsItRemovesTheStagedPartAndNeverReachesTheEngine()
    {
        using var cts = new CancellationTokenSource();
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _h.Storage.OnOpenRead = (_, _) => Task.FromResult<Stream>(new StallingStream(RestorePipelineHarness.BackupBytes, 1024 * 1024 + 7, stalled));

        var run = _h.RunAsync(cancellationToken: cts.Token);
        await stalled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(_h.StagedFiles);
        await cts.CancelAsync();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(DatabaseRestoreStatus.Cancelled, result.Status);
        Assert.Equal(RestoreExecutionStage.DownloadingArtifact, result.Failure!.Stage);
        Assert.Equal(StagingCleanupStatus.Deleted, result.StagingCleanup.Status);
        Assert.Empty(_h.Provider.Inspections);
        AssertUntouched(result);
    }

    [Fact]
    public async Task ACancellationDuringTheInspectionRemovesTheStagedCopy()
    {
        using var cts = new CancellationTokenSource();
        _h.Provider.Inspect = async (_, token) =>
        {
            await cts.CancelAsync();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException();
        };

        var result = await _h.RunAsync(cancellationToken: cts.Token);

        Assert.Equal(DatabaseRestoreStatus.Cancelled, result.Status);
        Assert.Equal(RestoreExecutionStage.InspectingBackup, result.Failure!.Stage);
        Assert.Empty(_h.Provider.Plans);
        AssertUntouched(result);
    }

    [Fact]
    public async Task ACancellationDuringThePlanRemovesTheStagedCopyAndLeavesTheTargetAlone()
    {
        using var cts = new CancellationTokenSource();
        _h.Provider.PlanAsync = async (_, token) =>
        {
            await cts.CancelAsync();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException();
        };

        var result = await _h.RunAsync(cancellationToken: cts.Token);

        Assert.Equal(DatabaseRestoreStatus.Cancelled, result.Status);
        AssertUntouched(result);
    }

    [Fact]
    public async Task ACancellationRequestedAfterThePlanButBeforeTheEngineRestoresIsHonouredWithoutCallingTheEngine()
    {
        using var cts = new CancellationTokenSource();
        _h.Provider.PlanAsync = async (_, _) =>
        {
            await cts.CancelAsync();
            return new RestorePlanResult(RestorePipelineHarness.Report(new BackupPreCheckItem("plan.ok", BackupPreCheckStatus.Passed, "ok", "Fine.")), _h.Provider.DefaultPlan);
        };

        var result = await _h.RunAsync(cancellationToken: cts.Token);

        Assert.Equal(DatabaseRestoreStatus.Cancelled, result.Status);
        Assert.Empty(_h.Provider.Executions);
        AssertUntouched(result);
    }

    [Fact]
    public async Task ACancellationDuringTheRestorePassesTheTokenToTheProviderAndReportsItsTargetState()
    {
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var providerSawCancellation = false;
        _h.Provider.Execute = async (request, progress, token) =>
        {
            progress!.Report(new RestoreProgress(RestoreExecutionStage.Restoring, 10, null, null, DateTimeOffset.UtcNow));
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            catch (OperationCanceledException)
            {
                providerSawCancellation = true;
            }

            return RestorePipelineHarness.NotSucceeded(
                request.Plan,
                new RestoreFailure(RestoreFailureKind.Cancelled, RestoreExecutionStage.Restoring, "cancelled", "The restore was cancelled."),
                DatabaseRestoreTargetState.PartiallyRestored);
        };

        var run = _h.RunAsync(cancellationToken: cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(_h.StagedFiles);
        await cts.CancelAsync();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(providerSawCancellation);
        Assert.Equal(DatabaseRestoreStatus.Cancelled, result.Status);
        Assert.Equal(DatabaseRestoreTargetState.PartiallyRestored, result.TargetState);
        Assert.Equal("RESTORING", result.DatabaseState);
        Assert.Empty(_h.StagedFiles);
        Assert.Equal(RestoreExecutionStage.Cancelled, result.FinalStage);
    }

    [Fact]
    public async Task AProviderThatThrowsCancellationDuringTheRestoreLeavesTheTargetUnknown()
    {
        using var cts = new CancellationTokenSource();
        _h.Provider.Execute = async (_, _, token) =>
        {
            await cts.CancelAsync();
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException();
        };

        var result = await _h.RunAsync(cancellationToken: cts.Token);

        Assert.Equal(DatabaseRestoreStatus.Cancelled, result.Status);
        Assert.Equal(DatabaseRestoreTargetState.Unknown, result.TargetState);
        Assert.Empty(_h.StagedFiles);
    }

    [Fact]
    public async Task ARestoreThatFinishedBeforeTheCancellationTookEffectStaysSucceeded()
    {
        using var cts = new CancellationTokenSource();
        _h.Provider.Execute = async (request, _, _) =>
        {
            await cts.CancelAsync();
            return RestorePipelineHarness.Succeeded(request.Plan);
        };

        var result = await _h.RunAsync(cancellationToken: cts.Token);

        Assert.Equal(DatabaseRestoreStatus.Succeeded, result.Status);
        Assert.Equal(DatabaseRestoreTargetState.Online, result.TargetState);
        Assert.Empty(_h.StagedFiles);
    }

    // ---- clean-up -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AStagedCopyThatCannotBeRemovedIsAWarningAndNeverFailsAFinishedRestore()
    {
        _h.Provider.Execute = (request, _, _) =>
        {
            // A directory under the staged name: File.Delete refuses it on every platform, standing in for a file the agent may not delete.
            var path = Directory.GetFiles(_h.StagingDirectory).Single();
            File.Delete(path);
            Directory.CreateDirectory(path);
            return Task.FromResult(RestorePipelineHarness.Succeeded(request.Plan));
        };

        var result = await _h.RunAsync();

        Assert.Equal(DatabaseRestoreStatus.Succeeded, result.Status);
        Assert.Equal(DatabaseRestoreTargetState.Online, result.TargetState);
        Assert.Equal(StagingCleanupStatus.Failed, result.StagingCleanup.Status);
        Assert.Equal(RestorePipelineReasons.StagingCleanupFailed, result.StagingCleanup.ReasonCode);
        var warning = Assert.Single(result.Warnings, w => w.Code == RestorePipelineReasons.StagingCleanupFailed);
        Assert.DoesNotContain(_h.StagingDirectory, warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStagedCopyThatCannotBeRemovedDoesNotChangeAFailureEither()
    {
        _h.Provider.Execute = (request, _, _) =>
        {
            var path = Directory.GetFiles(_h.StagingDirectory).Single();
            File.Delete(path);
            Directory.CreateDirectory(path);
            return Task.FromResult(RestorePipelineHarness.NotSucceeded(
                request.Plan, new RestoreFailure(RestoreFailureKind.SqlRestoreFailed, RestoreExecutionStage.Restoring, "x", "A fixed sentence."), DatabaseRestoreTargetState.PartiallyRestored));
        };

        var result = await _h.RunAsync();

        Assert.Equal(DatabaseRestoreStatus.Failed, result.Status);
        Assert.Equal(RestoreAgentFailureCodes.SqlRestoreFailed, result.Failure!.Code);
        Assert.Equal(DatabaseRestoreTargetState.PartiallyRestored, result.TargetState);
        Assert.Equal(StagingCleanupStatus.Failed, result.StagingCleanup.Status);
    }

    [Fact]
    public async Task AFileOfTheOperatorInTheStagingDirectoryIsNeverTouched()
    {
        var operatorFile = Path.Combine(_h.StagingDirectory, "operator-own.bak");
        await File.WriteAllBytesAsync(operatorFile, [1, 2, 3]);

        var result = await _h.RunAsync(_h.Request(checksum: RestorePipelineHarness.ChecksumOf([9])));

        Assert.Equal(RestoreAgentFailureCodes.ChecksumMismatch, result.Failure!.Code);
        Assert.Equal(["operator-own.bak"], _h.StagedFiles);
    }

    [Fact]
    public async Task EveryRunStagesUnderItsOwnNameSoTwoRunsNeverShareAFile()
    {
        await _h.RunAsync();
        var first = _h.Provider.StagedAtInspect[0].Name;
        await _h.RunAsync();
        var second = _h.Provider.StagedAtInspect[0].Name;

        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task ARequestWithNullArgumentsIsRefused()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => _h.Pipeline.RunRestoreAsync(null!, null, default));
    }

    private sealed class ThrowingProgress : IProgress<RestoreProgress>
    {
        public void Report(RestoreProgress value) => throw new InvalidOperationException("consumer failure");
    }
}
