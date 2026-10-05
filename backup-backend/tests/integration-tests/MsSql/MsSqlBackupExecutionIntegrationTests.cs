using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.MSSQL;
using BackupPlatform.BackupProviders.MSSQL.Backup;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace BackupPlatform.IntegrationTests.MsSql;

/// <summary>
/// Full backups against a real SQL Server through the real staging mapping: what is written, what msdb says about it, what a
/// failure leaves behind, and that cancellation really stops the statement on the server. Every staged file a test creates is
/// removed again; the fixture sweeps anything a failed test leaves.
/// </summary>
[Collection(MsSqlCollection.Name)]
public sealed class MsSqlBackupExecutionIntegrationTests(MsSqlFixture fixture, ITestOutputHelper output) : IDisposable
{
    private static readonly TimeSpan FastProgress = TimeSpan.FromMilliseconds(100);

    // Random data does not compress: compression keeps CPU busy, so the big database takes seconds to back up and to verify instead of the
    // fraction of a second a plain copy to a RAM-backed staging directory needs.
    private static readonly FullBackupOptions SlowOptions = new(checksum: BackupOptionMode.Required, compression: BackupOptionMode.Required);

    private readonly List<string> _files = [];
    private readonly SqlServerBackupProvider _provider = CreateProvider(TimeSpan.FromSeconds(30));

    public void Dispose()
    {
        foreach (var file in _files)
        {
            new AgentStagingDirectory(fixture.Staging.AgentDirectory).TryDeleteFile(file);
        }
    }

    private static SqlServerBackupProvider CreateProvider(TimeSpan visibility) =>
        new(NullLogger<SqlServerBackupProvider>.Instance, null, null, new SqlServerExecutionSettings(visibility, FastProgress));

    private FullBackupRequest NewRequest(
        string database,
        string tag,
        FullBackupOptions? options = null,
        StagingArea? staging = null,
        TimeSpan? backupTimeout = null,
        TimeSpan? verifyTimeout = null,
        StagingFailurePolicy onFailure = StagingFailurePolicy.Delete,
        string? fileName = null)
    {
        var name = fileName ?? fixture.StagedFileName(tag);
        _files.Add(name);
        return new FullBackupRequest(database, staging ?? fixture.Staging, name, options, backupTimeout, verifyTimeout, onFailure);
    }

    private Task<FullBackupResult> RunAsync(
        FullBackupRequest request,
        DatabaseCredential? credential = null,
        IProgress<BackupProgress>? progress = null,
        SqlServerBackupProvider? provider = null,
        CancellationToken cancellationToken = default) =>
        (provider ?? _provider).ExecuteFullBackupAsync(fixture.Settings, credential ?? fixture.Admin, request, progress, cancellationToken);

    private bool StagedFileExists(FullBackupRequest request) => new AgentStagingDirectory(request.Staging.AgentDirectory).FileExists(request.StagedFileName);

    private static string Describe(FullBackupResult result) =>
        $"{result.Status} {result.Failure?.Kind}/{result.Failure?.Stage}/{result.Failure?.ReasonCode}: {result.Failure?.Message}";

    [Fact]
    public async Task ASysadminBackupProducesAVerifiedStagedFileTheAgentCanSee()
    {
        var request = NewRequest(fixture.Source, "plain");

        var result = await RunAsync(request);

        Assert.True(result.Status == FullBackupStatus.Succeeded, Describe(result));
        var staged = result.StagedFile!;
        Assert.Equal(request.StagedFileName, staged.FileName);
        var onDisk = new FileInfo(staged.AgentPath.Value);
        Assert.True(onDisk.Exists);
        Assert.True(onDisk.Length > 0);
        Assert.Equal(onDisk.Length, staged.SizeBytes);
        Assert.EndsWith(request.StagedFileName, staged.ServerPath.Value, StringComparison.Ordinal);
        Assert.Equal(BackupVerificationStatus.Passed, result.Verification.Status);
        Assert.Equal("restore-verifyonly", result.Verification.Method);
        Assert.Equal(StagingCleanupStatus.NotNeeded, result.Cleanup.Status);
        Assert.Equal(new AppliedFullBackupOptions(false, false, false), result.Options!.Applied);
        Assert.Empty(result.Warnings);
        Assert.NotNull(result.BackupStartedAt);
        Assert.True(result.BackupFinishedAt >= result.BackupStartedAt);
        Assert.True(result.FinishedAt >= result.BackupFinishedAt);
        Assert.Equal(fixture.Source, result.Source!.DatabaseName);
        Assert.Equal("full", result.Source.Details["recovery-model"]);
        Assert.Equal(fixture.HostPlatform, result.Source.Details["host-platform"]);
        Assert.Equal("disabled", result.Source.Details["transparent-data-encryption"]);
        Assert.Equal("multi-user", result.Source.Details["user-access"]);
        Assert.Equal("130", result.Source.Details["compatibility-level"]);
        Assert.NotEmpty(result.Source.Details["edition"]);
        Assert.Equal(DatabaseEngineId.MsSql, result.Source.Instance.Engine);
    }

    [Fact]
    public async Task CopyOnlyAndChecksumAreAppliedExactlyAsMsdbRecordsThem()
    {
        var request = NewRequest(fixture.Source, "opts", new FullBackupOptions(BackupOptionMode.Required, BackupOptionMode.Required));

        var result = await RunAsync(request);

        Assert.True(result.Status == FullBackupStatus.Succeeded, Describe(result));
        Assert.Equal(new AppliedFullBackupOptions(true, true, false), result.Options!.Applied);
        var set = await fixture.FindBackupSetAsync(fixture.Source, request.StagedFileName);
        Assert.NotNull(set);
        Assert.True(set.IsCopyOnly);
        Assert.True(set.HasChecksums);
        Assert.Equal("BackupPlatform full backup", set.Name);
    }

    [Fact]
    public async Task WithoutRequestedOptionsTheBackupIsNotCopyOnlyAndHasNoChecksumsEvenIfTheServerDefaultsAreOn()
    {
        var request = NewRequest(fixture.Source, "defaults");
        await fixture.ExecuteAsync("EXEC sys.sp_configure N'backup checksum default', 1; RECONFIGURE;");
        try
        {
            var result = await RunAsync(request);

            Assert.True(result.Status == FullBackupStatus.Succeeded, Describe(result));
            var set = await fixture.FindBackupSetAsync(fixture.Source, request.StagedFileName);
            Assert.NotNull(set);
            Assert.False(set.IsCopyOnly);
            Assert.False(set.HasChecksums);
        }
        finally
        {
            await fixture.ExecuteAsync("EXEC sys.sp_configure N'backup checksum default', 0; RECONFIGURE;");
        }
    }

    [Fact]
    public async Task CompressionIsAppliedWhenSupportedAndOtherwiseFallsBackWithAWarning()
    {
        var request = NewRequest(fixture.Source, "compress", new FullBackupOptions(compression: BackupOptionMode.IfSupported));

        var result = await RunAsync(request);

        Assert.True(result.Status == FullBackupStatus.Succeeded, Describe(result));
        var set = await fixture.FindBackupSetAsync(fixture.Source, request.StagedFileName);
        Assert.NotNull(set);
        if (result.Options!.Applied.Compression)
        {
            Assert.True(set.CompressedBackupSize < set.BackupSize, $"compressed {set.CompressedBackupSize} vs {set.BackupSize}");
        }
        else
        {
            Assert.Equal(set.CompressedBackupSize, set.BackupSize);
            Assert.Contains(result.Warnings, warning => warning.Code.StartsWith("compression-", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task AServerThatCompressesByDefaultDoesNotCompressUnlessAsked()
    {
        var request = NewRequest(fixture.Source, "nocompress");
        await fixture.ExecuteAsync("EXEC sys.sp_configure N'backup compression default', 1; RECONFIGURE;");
        try
        {
            var result = await RunAsync(request);

            Assert.True(result.Status == FullBackupStatus.Succeeded, Describe(result));
            Assert.False(result.Options!.Applied.Compression);
            var set = await fixture.FindBackupSetAsync(fixture.Source, request.StagedFileName);
            Assert.Equal(set!.BackupSize, set.CompressedBackupSize);
        }
        finally
        {
            await fixture.ExecuteAsync("EXEC sys.sp_configure N'backup compression default', 0; RECONFIGURE;");
        }
    }

    [Fact]
    public async Task ABackupOperatorWithoutTheVerifyPermissionIsRefusedBeforeABackupIsStarted()
    {
        var request = NewRequest(fixture.Source, "bareoperator");

        var result = await RunAsync(request, fixture.Credential(fixture.Operator));

        Assert.Equal(FullBackupStatus.Failed, result.Status);
        Assert.Equal(BackupFailureKind.PreCheckFailed, result.Failure!.Kind);
        Assert.Equal(BackupExecutionStage.Preparing, result.Failure.Stage);
        Assert.Equal(SqlServerBackupReasons.VerifyPermissionDenied, result.Failure.ReasonCode);
        Assert.Null(result.BackupStartedAt);
        Assert.False(StagedFileExists(request));
    }

    [Fact]
    public async Task TheLeastPrivilegeBackupOperatorCanBackUpAndVerify()
    {
        var request = NewRequest(fixture.Source, "operator", new FullBackupOptions(checksum: BackupOptionMode.Required));

        var result = await RunAsync(request, fixture.Credential(fixture.OperatorWithVerify));

        Assert.True(result.Status == FullBackupStatus.Succeeded, Describe(result));
        Assert.Equal(BackupVerificationStatus.Passed, result.Verification.Status);
        Assert.True(StagedFileExists(request));
    }

    [Fact]
    public async Task StagesAreReportedInOrderPercentNeverDecreasesAndBytesFollowTheStagedFile()
    {
        var request = NewRequest(fixture.Big, "progress", SlowOptions);
        var progress = new RecordingProgress();

        var result = await RunAsync(request, progress: progress);

        Assert.True(result.Status == FullBackupStatus.Succeeded, Describe(result));
        var reports = progress.Reports;
        output.WriteLine("percent values: " + string.Join(",", reports.Where(r => r.Percent is not null && r.Bytes is null).Select(r => r.Percent)));
        output.WriteLine("byte reports: " + string.Join(",", reports.Where(r => r.Bytes is not null).Select(r => r.Bytes)));
        Assert.Equal(
            [BackupExecutionStage.Preparing, BackupExecutionStage.BackingUp, BackupExecutionStage.Verifying],
            reports.Select(r => r.Stage).Distinct());
        Assert.Equal(reports.Select(r => r.Stage).Order(), reports.Select(r => r.Stage));
        Assert.DoesNotContain(reports, r => r.Stage is BackupExecutionStage.Completed or BackupExecutionStage.Failed or BackupExecutionStage.Cancelled);
        var percents = reports.Where(r => r.Percent is not null).Select(r => r.Percent!.Value).ToArray();
        Assert.NotEmpty(percents);
        Assert.Equal(percents.Order(), percents);
        Assert.All(percents, p => Assert.InRange(p, 0, 100));
        Assert.Equal(100, percents[^1]);
        var bytes = reports.Where(r => r.Bytes is not null).Select(r => r.Bytes!.Value).ToArray();
        Assert.NotEmpty(bytes);
        Assert.Equal(bytes.Order(), bytes);
        Assert.Equal(result.StagedFile!.SizeBytes, bytes[^1]);
    }

    [Fact]
    public async Task AProgressConsumerThatThrowsDoesNotChangeTheResult()
    {
        var request = NewRequest(fixture.Source, "throwing");

        var result = await RunAsync(request, progress: new ThrowingProgress());

        Assert.True(result.Status == FullBackupStatus.Succeeded, Describe(result));
        Assert.Equal(BackupVerificationStatus.Passed, result.Verification.Status);
    }

    [Fact]
    public async Task ADamagedFileFailsTheVerificationAndTheFileIsDeletedByDefault()
    {
        var request = NewRequest(fixture.Source, "damaged");

        var result = await RunAsync(request, progress: new DamagingProgress(request));

        Assert.Equal(FullBackupStatus.Failed, result.Status);
        Assert.Equal(BackupFailureKind.VerificationFailed, result.Failure!.Kind);
        Assert.Equal(BackupExecutionStage.Verifying, result.Failure.Stage);
        Assert.Equal(SqlServerBackupReasons.BackupMediaInvalid, result.Failure.ReasonCode);
        Assert.Equal(BackupVerificationStatus.Failed, result.Verification.Status);
        Assert.Null(result.StagedFile);
        Assert.Equal(StagingCleanupStatus.Deleted, result.Cleanup.Status);
        Assert.False(StagedFileExists(request));
        Assert.DoesNotContain(fixture.Staging.AgentDirectory.Value, result.Failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADamagedFileIsKeptWhenThePolicyAsksForIt()
    {
        var request = NewRequest(fixture.Source, "kept", onFailure: StagingFailurePolicy.Keep);

        var result = await RunAsync(request, progress: new DamagingProgress(request));

        Assert.Equal(BackupFailureKind.VerificationFailed, result.Failure!.Kind);
        Assert.Equal(StagingCleanupStatus.Kept, result.Cleanup.Status);
        Assert.True(StagedFileExists(request));
    }

    [Fact]
    public async Task TheVerifyHelperTellsTruncatedFromGarbledFiles()
    {
        var request = NewRequest(fixture.Source, "verifyhelper", new FullBackupOptions(checksum: BackupOptionMode.Required));
        var good = await RunAsync(request);
        Assert.Equal(FullBackupStatus.Succeeded, good.Status);
        var path = good.StagedFile!.AgentPath.Value;
        var server = good.StagedFile.ServerPath;

        var passed = await _provider.VerifyBackupFileAsync(fixture.Settings, fixture.Admin, server, true, TimeSpan.FromSeconds(60), CancellationToken.None);
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            stream.SetLength(20_000);
        }

        var truncated = await _provider.VerifyBackupFileAsync(fixture.Settings, fixture.Admin, server, true, TimeSpan.FromSeconds(60), CancellationToken.None);

        Assert.True(passed.Passed);
        Assert.False(truncated.Passed);
        Assert.Equal(SqlServerBackupReasons.BackupFileUnreadable, truncated.ReasonCode);
        Assert.DoesNotContain("/", truncated.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AServerDirectoryTheServiceAccountCannotUseFailsFastWithASafeReasonAndLeavesNothing()
    {
        var staging = StagingArea.Mapped(new DatabaseServerPath($"/root/bp_it_nowrite_{fixture.Suffix}"), fixture.Staging.AgentDirectory);
        var request = NewRequest(fixture.Source, "nowrite", staging: staging);
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var result = await RunAsync(request);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"took {clock.Elapsed}");
        Assert.Equal(FullBackupStatus.Failed, result.Status);
        Assert.Equal(BackupFailureKind.SqlBackupFailed, result.Failure!.Kind);
        Assert.Equal(BackupExecutionStage.BackingUp, result.Failure.Stage);
        Assert.Equal(SqlServerBackupReasons.StagingWriteFailed, result.Failure.ReasonCode);
        Assert.DoesNotContain("/root", result.Failure.Message, StringComparison.Ordinal);
        Assert.False(StagedFileExists(request));
        Assert.Equal(StagingCleanupStatus.NotNeeded, result.Cleanup.Status);
    }

    [Theory]
    [InlineData("reader")]
    [InlineData("denied")]
    public async Task ALoginWithoutBackupPermissionFailsWithAPermissionReasonAndNoFile(string who)
    {
        var login = who == "reader" ? fixture.ReaderOnly : fixture.Denied;
        var request = NewRequest(fixture.OnlineFull, "perm");

        var result = await RunAsync(request, fixture.Credential(login));

        Assert.Equal(FullBackupStatus.Failed, result.Status);
        Assert.Equal(SqlServerBackupReasons.PermissionDenied, result.Failure!.ReasonCode);
        Assert.False(StagedFileExists(request));
    }

    [Fact]
    public async Task AnExistingFileWithTheStagedNameIsNeverOverwrittenOrDeleted()
    {
        var request = NewRequest(fixture.Source, "exists");
        new AgentStagingDirectory(request.Staging.AgentDirectory).CreateMarkerFile(request.StagedFileName);

        var result = await RunAsync(request);

        Assert.Equal(SqlServerBackupReasons.StagedFileExists, result.Failure!.ReasonCode);
        Assert.Equal(BackupFailureKind.SqlBackupFailed, result.Failure.Kind);
        Assert.Equal(StagingCleanupStatus.NotNeeded, result.Cleanup.Status);
        Assert.Equal(1, new AgentStagingDirectory(request.Staging.AgentDirectory).FileLength(request.StagedFileName));
    }

    [Fact]
    public async Task AMissingAgentDirectoryIsReportedBeforeAnythingRuns()
    {
        var staging = StagingArea.Mapped(fixture.Staging.ServerDirectory, new AgentPath(Path.Combine(Path.GetTempPath(), $"bp-it-missing-{fixture.Suffix}")));

        var result = await RunAsync(NewRequest(fixture.Source, "noagentdir", staging: staging));

        Assert.Equal(SqlServerBackupReasons.StagingAgentDirectoryMissing, result.Failure!.ReasonCode);
        Assert.Null(result.BackupStartedAt);
    }

    [Fact]
    public async Task AMissingOrOfflineDatabaseFailsThePreconditionsWithoutRunningABackup()
    {
        var missing = await RunAsync(NewRequest("bp_it_does_not_exist_" + fixture.Suffix, "missing"));
        var offline = await RunAsync(NewRequest(fixture.Offline, "offline"));

        Assert.Equal(BackupFailureKind.PreCheckFailed, missing.Failure!.Kind);
        Assert.Equal(SqlServerBackupReasons.DatabaseNotFound, missing.Failure.ReasonCode);
        Assert.Equal(BackupFailureKind.PreCheckFailed, offline.Failure!.Kind);
        Assert.Equal(SqlServerBackupReasons.DatabaseNotOnline, offline.Failure.ReasonCode);
        Assert.Equal(BackupExecutionStage.Preparing, offline.Failure.Stage);
        Assert.Null(offline.BackupStartedAt);
    }

    [Fact]
    public async Task ABackupOfADatabaseWhoseNameNeedsQuotingWorksAgainstTheLiveParser()
    {
        var name = $"bp_it_q]uote'd_{fixture.Suffix}";
        await fixture.CreateScratchDatabaseAsync(name);
        var asAdmin = NewRequest(name, "quoted-admin");
        var asOperator = NewRequest(name, "quoted-op");

        var admin = await RunAsync(asAdmin);
        var operatorResult = await RunAsync(asOperator, fixture.Credential(fixture.OperatorWithVerify));

        Assert.True(admin.Status == FullBackupStatus.Succeeded, Describe(admin));
        Assert.True(operatorResult.Status == FullBackupStatus.Succeeded, Describe(operatorResult));
        Assert.Equal(name, admin.Source!.DatabaseName);
    }

    [Fact]
    public async Task AStagedFileNameWithAQuoteWorks()
    {
        var request = NewRequest(fixture.Source, "quote", fileName: $"bp_it_it's_{fixture.Suffix}.bak");

        var result = await RunAsync(request);

        Assert.True(result.Status == FullBackupStatus.Succeeded, Describe(result));
        Assert.True(StagedFileExists(request));
    }

    [Fact]
    public async Task ACancelledCallStopsTheStatementOnTheServerAndRemovesThePartialFile()
    {
        var request = NewRequest(fixture.Big, "cancel", SlowOptions);
        using var cancellation = new CancellationTokenSource();
        var run = RunAsync(request, cancellationToken: cancellation.Token);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!StagedFileExists(request) && !run.IsCompleted && clock.Elapsed < TimeSpan.FromSeconds(60))
        {
            await Task.Delay(10);
        }

        // Cancel while data is flowing and progress messages are streaming: this is the case an asynchronous SqlClient call ignored.
        await Task.Delay(300);
        Assert.False(run.IsCompleted, "The big backup finished before it could be cancelled; make the fixture database bigger.");
        var cancelledAt = System.Diagnostics.Stopwatch.StartNew();
        await cancellation.CancelAsync();
        var result = await run;
        output.WriteLine($"cancelled run returned {cancelledAt.ElapsedMilliseconds} ms after the cancellation");

        Assert.True(cancelledAt.Elapsed < TimeSpan.FromSeconds(2), $"took {cancelledAt.Elapsed} to stop");
        Assert.Null(result.BackupFinishedAt);

        Assert.Equal(FullBackupStatus.Cancelled, result.Status);
        Assert.Equal(BackupFailureKind.Cancelled, result.Failure!.Kind);
        Assert.Equal(BackupExecutionStage.BackingUp, result.Failure.Stage);
        Assert.Null(result.StagedFile);
        Assert.False(StagedFileExists(request));
        Assert.Equal(0, await fixture.CountRunningBackupRequestsAsync(fixture.Big));
        Assert.True(result.Cleanup.Status is StagingCleanupStatus.Deleted or StagingCleanupStatus.NotNeeded, result.Cleanup.Status.ToString());
    }

    [Fact]
    public async Task ACallCancelledBeforeItStartsReturnsCancelledWithoutTouchingTheServer()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var result = await RunAsync(NewRequest(fixture.Source, "precancel"), cancellationToken: cancellation.Token);

        Assert.Equal(FullBackupStatus.Cancelled, result.Status);
        Assert.Null(result.BackupStartedAt);
        Assert.Equal(StagingCleanupStatus.NotNeeded, result.Cleanup.Status);
    }

    [Fact]
    public async Task ABackupThatOutlastsItsTimeoutFailsWithATimeoutReasonAndIsCleanedUp()
    {
        var request = NewRequest(fixture.Big, "timeout", SlowOptions, backupTimeout: TimeSpan.FromSeconds(1));

        var result = await RunAsync(request);

        Assert.Equal(FullBackupStatus.Failed, result.Status);
        Assert.Equal(SqlServerBackupReasons.BackupTimeout, result.Failure!.ReasonCode);
        Assert.Equal(BackupFailureKind.SqlBackupFailed, result.Failure.Kind);
        Assert.False(StagedFileExists(request));
        Assert.Equal(0, await fixture.CountRunningBackupRequestsAsync(fixture.Big));
    }

    [Fact]
    public async Task AVerificationThatOutlastsItsTimeoutFailsWithATimeoutReason()
    {
        var request = NewRequest(fixture.Big, "verifytimeout", SlowOptions);
        var made = await RunAsync(request);
        Assert.True(made.Status == FullBackupStatus.Succeeded, Describe(made));

        var outcome = await _provider.VerifyBackupFileAsync(fixture.Settings, fixture.Admin, made.StagedFile!.ServerPath, true, TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.False(outcome.Passed);
        Assert.Equal(SqlServerBackupReasons.VerifyTimeout, outcome.ReasonCode);
    }

    [Fact]
    public async Task AWrongMappingFailsAFinishedBackupWithAMismatchReasonAndTheServerSideFileIsReportedAsPossiblyRemaining()
    {
        using var otherAgentDirectory = new TemporaryDirectory();
        var staging = StagingArea.Mapped(fixture.Staging.ServerDirectory, otherAgentDirectory.Path);
        var request = NewRequest(fixture.Source, "mismatch", staging: staging);

        var result = await RunAsync(request, fixture.Credential(fixture.OperatorWithVerify));

        Assert.Equal(FullBackupStatus.Failed, result.Status);
        Assert.Equal(SqlServerBackupReasons.StagingMappingMismatch, result.Failure!.ReasonCode);
        Assert.Contains(result.Warnings, warning => warning.Code == SqlServerBackupWarningCodes.ServerFileMayRemain);
        Assert.Empty(Directory.GetFileSystemEntries(otherAgentDirectory.Path.Value));
        Assert.True(new AgentStagingDirectory(fixture.Staging.AgentDirectory).TryDeleteFile(request.StagedFileName));
    }

    [Fact]
    public async Task AWrongMappingIsCaughtByTheWatchdogWhileALongBackupRunsAndNothingIsLeftBehind()
    {
        using var otherAgentDirectory = new TemporaryDirectory();
        var staging = StagingArea.Mapped(fixture.Staging.ServerDirectory, otherAgentDirectory.Path);
        var request = NewRequest(fixture.Big, "watchdog", SlowOptions, staging: staging);
        var provider = CreateProvider(TimeSpan.FromSeconds(1));
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var result = await RunAsync(request, fixture.Credential(fixture.OperatorWithVerify), provider: provider);

        output.WriteLine($"watchdog result after {clock.Elapsed}: {Describe(result)}");
        Assert.Equal(FullBackupStatus.Failed, result.Status);
        Assert.Equal(SqlServerBackupReasons.StagingMappingMismatch, result.Failure!.ReasonCode);
        Assert.Equal(BackupFailureKind.SqlBackupFailed, result.Failure.Kind);
        Assert.Equal(BackupExecutionStage.BackingUp, result.Failure.Stage);
        Assert.Null(result.BackupFinishedAt);
        Assert.Empty(Directory.GetFileSystemEntries(otherAgentDirectory.Path.Value));
        Assert.False(new AgentStagingDirectory(fixture.Staging.AgentDirectory).FileExists(request.StagedFileName));
        Assert.Equal(0, await fixture.CountRunningBackupRequestsAsync(fixture.Big));
    }

    private sealed class RecordingProgress : IProgress<BackupProgress>
    {
        private readonly List<BackupProgress> _reports = [];

        public IReadOnlyList<BackupProgress> Reports
        {
            get
            {
                lock (_reports)
                {
                    return _reports.ToArray();
                }
            }
        }

        public void Report(BackupProgress value)
        {
            lock (_reports)
            {
                _reports.Add(value);
            }
        }
    }

    private sealed class ThrowingProgress : IProgress<BackupProgress>
    {
        public void Report(BackupProgress value) => throw new InvalidOperationException("consumer is broken");
    }

    /// <summary>Garbles the staged file the moment verification starts; the provider reports stages synchronously, so this lands between BACKUP and VERIFYONLY.</summary>
    private sealed class DamagingProgress(FullBackupRequest request) : IProgress<BackupProgress>
    {
        public void Report(BackupProgress value)
        {
            if (value.Stage != BackupExecutionStage.Verifying)
            {
                return;
            }

            var path = new AgentStagingDirectory(request.Staging.AgentDirectory).FilePath(request.StagedFileName);
            var garbage = new byte[64 * 1024];
            Random.Shared.NextBytes(garbage);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            stream.Write(garbage);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory() => Path = new AgentPath(Directory.CreateTempSubdirectory("bp-it-other-").FullName);

        public AgentPath Path { get; }

        public void Dispose() => Directory.Delete(Path.Value, recursive: true);
    }
}
