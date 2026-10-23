using System.Security.Cryptography;
using BackupPlatform.Agent.Core.Backup;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Encryption;
using BackupPlatform.StorageProviders.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackupPlatform.UnitTests.Backup;

public sealed class DurableTransferPipelineTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PausePinsExactEncryptedArtifactAcrossANewPipelineAndResumesAtVerifiedOffset(bool timeRule)
    {
        using var key = RSA.Create(4096);
        using var harness = new PipelineHarness(new EncryptingArtifactPreparer());
        using var pause = new CancellationTokenSource();
        using var received = new MemoryStream();
        var checkpoints = new List<TransferCheckpointContract>();
        byte[]? originalArtifact = null;
        const int offset = 777777;
        harness.Storage.OnPut = async (_, content, options, token) =>
        {
            using var snapshot = new MemoryStream();
            await content.CopyToAsync(snapshot, token);
            originalArtifact = snapshot.ToArray();
            content.Position = 0;
            var prefix = new byte[offset];
            await content.ReadExactlyAsync(prefix, token);
            received.Write(prefix);
            await options.RecordCheckpointAsync!(new(offset, "mock-session"), token);
            pause.Cancel();
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("The pause signal must cancel the open transfer.");
        };
        var request = harness.Request() with
        {
            AttemptId = Guid.NewGuid(), PauseToken = timeRule ? default : pause.Token, TimeRulePauseToken = timeRule ? pause.Token : default,
            Encryption = new ArtifactEncryption(true, [new BpeRecipient(BpeRecipientType.TenantWrap, 1, key)]),
            Destinations = [new(StorageProviderId.Local, harness.Storage, Guid.NewGuid(), true)],
            RecordCheckpointAsync = (checkpoint, _) => { checkpoints.Add(checkpoint); return Task.CompletedTask; }
        };
        var paused = await harness.RunAsync(request);
        Assert.Equal(DatabaseBackupStatus.Paused, paused.Status);
        Assert.Equal(timeRule ? "TimeRule" : "Operator", paused.PauseReason);
        Assert.Single(harness.Provider.Requests);
        var artifact = Assert.IsType<BackupArtifactInfo>(paused.Artifact);
        var artifactPath = Path.Combine(harness.StagingDirectory, artifact.FileName);
        Assert.Equal(originalArtifact, await File.ReadAllBytesAsync(artifactPath));
        foreach (var file in Directory.GetFiles(harness.StagingDirectory)) File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-3));
        var directory = new BackupPlatform.BackupProviders.Abstractions.Database.AgentStagingDirectory(request.Staging.AgentDirectory);
        var cleaner = new StagingCleaner(directory, new StagingUsageRegistry(), TimeProvider.System, NullLogger<StagingCleaner>.Instance);
        await cleaner.RunAsync(new StagingCleanupOptions { MinimumAge = TimeSpan.FromHours(12) }, CancellationToken.None);
        Assert.True(File.Exists(artifactPath));
        harness.Storage.OnPut = async (storageKey, content, options, token) =>
        {
            Assert.Equal(offset, options.Checkpoint!.VerifiedByteOffset);
            Assert.Equal("mock-session", options.Checkpoint.ProviderState);
            using var snapshot = new MemoryStream();
            await content.CopyToAsync(snapshot, token);
            Assert.Equal(originalArtifact, snapshot.ToArray());
            content.Position = options.Checkpoint.VerifiedByteOffset;
            await content.CopyToAsync(received, token);
            return new(new(StorageProviderId.Local, storageKey), received.Length, options.ExpectedChecksum, DateTimeOffset.UtcNow);
        };
        harness.Storage.OnVerify = (storageKey, expected) => new(new(StorageProviderId.Local, storageKey), StorageVerificationStatus.Verified,
            StorageVerificationMethod.ReadBack, received.Length, ContentChecksum.Sha256(SHA256.HashData(received.ToArray())));
        var restarted = new DatabaseBackupPipeline([harness.Provider], [harness.Storage], new EncryptingArtifactPreparer(), NullLogger<DatabaseBackupPipeline>.Instance);
        var resumed = await restarted.RunFullBackupAsync(request with { ResumeTransfer = true, PauseToken = default, TimeRulePauseToken = default, Checkpoints = [checkpoints[^1]] }, null, CancellationToken.None);
        Assert.Equal(DatabaseBackupStatus.Succeeded, resumed.Status);
        Assert.Single(harness.Provider.Requests);
        Assert.Equal(originalArtifact, received.ToArray());
        using var plain = new MemoryStream();
        await BpeDecryptor.DecryptAsync(new MemoryStream(received.ToArray()), plain, key);
        Assert.Equal(PipelineHarness.BackupBytes, plain.ToArray());
        Assert.Empty(harness.StagedFiles);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancelled_checkpointed_upload_is_aborted_and_a_failed_abort_is_reported(bool abortFails)
    {
        using var harness = new PipelineHarness();
        using var cancel = new CancellationTokenSource();
        harness.Storage.OnPut = async (_, _, options, token) =>
        {
            await options.RecordCheckpointAsync!(new(10, "state-1"), token);
            await options.RecordCheckpointAsync!(new(20, "state-2"), token);
            cancel.Cancel();
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Cancellation must stop the upload.");
        };
        if (abortFails) harness.Storage.AbortFailure = () => new StorageException(StorageFailureKind.Io, "boom");
        var result = await harness.RunAsync(harness.Request() with { AttemptId = Guid.NewGuid(),
            Destinations = [new(StorageProviderId.Local, harness.Storage, Guid.NewGuid(), true)], RecordCheckpointAsync = (_, _) => Task.CompletedTask }, cancel.Token);
        Assert.Equal(DatabaseBackupStatus.Cancelled, result.Status);
        Assert.Equal("state-2", Assert.Single(harness.Storage.Aborted).State);
        Assert.Equal(abortFails, result.CleanupFailures.Any(x => x.ReasonCode == BackupPipelineReasons.ReplicaCleanupFailed));
    }

    [Fact]
    public async Task Paused_upload_is_not_aborted_because_it_will_resume()
    {
        using var harness = new PipelineHarness();
        using var pause = new CancellationTokenSource();
        harness.Storage.OnPut = async (_, _, options, token) =>
        {
            await options.RecordCheckpointAsync!(new(10, "state-1"), token);
            pause.Cancel();
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("The transfer must pause.");
        };
        var result = await harness.RunAsync(harness.Request() with { AttemptId = Guid.NewGuid(), PauseToken = pause.Token,
            Destinations = [new(StorageProviderId.Local, harness.Storage, Guid.NewGuid(), true)], RecordCheckpointAsync = (_, _) => Task.CompletedTask });
        Assert.Equal(DatabaseBackupStatus.Paused, result.Status);
        Assert.Empty(harness.Storage.Aborted);
    }

    [Fact]
    public async Task Terminal_attempt_confirmation_releases_paused_files_but_never_active_or_resumable_files()
    {
        using var harness = new PipelineHarness();
        using var pause = new CancellationTokenSource();
        harness.Storage.OnPut = (_, _, _, token) =>
        {
            pause.Cancel();
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("The transfer must pause.");
        };
        var request = harness.Request() with
        {
            AttemptId = Guid.NewGuid(), PauseToken = pause.Token,
            Destinations = [new(StorageProviderId.Local, harness.Storage, Guid.NewGuid(), true)]
        };
        var result = await harness.RunAsync(request);
        Assert.Equal(DatabaseBackupStatus.Paused, result.Status);
        var artifact = Assert.IsType<BackupArtifactInfo>(result.Artifact);
        var path = Path.Combine(harness.StagingDirectory, artifact.FileName);
        foreach (var file in Directory.GetFiles(harness.StagingDirectory)) File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-3));
        var registry = new StagingUsageRegistry();
        var terminal = false;
        var cleaner = new StagingCleaner(new(request.Staging.AgentDirectory), registry, TimeProvider.System,
            NullLogger<StagingCleaner>.Instance, (attemptId, _) =>
            {
                Assert.Equal(request.AttemptId, attemptId);
                return Task.FromResult(terminal);
            });
        var options = new StagingCleanupOptions { MinimumAge = TimeSpan.FromHours(12) };
        await cleaner.RunAsync(options, default);
        Assert.True(File.Exists(path));
        terminal = true;
        using (registry.Hold(artifact.FileName))
        {
            await cleaner.RunAsync(options, default);
            Assert.True(File.Exists(path));
        }
        await cleaner.RunAsync(options, default);
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".resume-pin"));
        Assert.False(File.Exists(Path.Combine(harness.StagingDirectory, $"transfer-{request.AttemptId:N}.json")));
    }

    [Theory]
    [InlineData(1, DatabaseBackupStatus.Succeeded)]
    [InlineData(3, DatabaseBackupStatus.Succeeded)]
    [InlineData(4, DatabaseBackupStatus.Paused)]
    public async Task ThreeExponentialRetriesKeepVerifiedPrefixAndExhaustionPausesRatherThanFails(int drops, DatabaseBackupStatus expectedStatus)
    {
        using var harness = new PipelineHarness();
        using var received = new MemoryStream();
        var calls = 0;
        harness.Storage.OnPut = async (storageKey, content, options, token) =>
        {
            calls++;
            Assert.Equal(received.Length, options.Checkpoint?.VerifiedByteOffset ?? 0);
            content.Position = received.Length;
            if (calls <= drops)
            {
                var prefix = new byte[1024];
                await content.ReadExactlyAsync(prefix, token);
                received.Write(prefix);
                await options.RecordCheckpointAsync!(new(received.Length, "mock-network-session"), token);
                throw new StorageException(StorageFailureKind.NetworkUnavailable, "The mocked connection dropped.");
            }
            await content.CopyToAsync(received, token);
            return new(new(StorageProviderId.Local, storageKey), received.Length, options.ExpectedChecksum, DateTimeOffset.UtcNow);
        };
        harness.Storage.OnVerify = (storageKey, expected) => new(new(StorageProviderId.Local, storageKey), StorageVerificationStatus.Verified,
            StorageVerificationMethod.ReadBack, received.Length, ContentChecksum.Sha256(SHA256.HashData(received.ToArray())));
        var result = await harness.RunAsync(harness.Request() with { AttemptId = Guid.NewGuid(),
            Destinations = [new(StorageProviderId.Local, harness.Storage, Guid.NewGuid(), true)], RecordCheckpointAsync = (_, _) => Task.CompletedTask });
        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(Math.Min(drops + 1, 4), calls);
        Assert.Single(harness.Provider.Requests);
        if (expectedStatus == DatabaseBackupStatus.Paused)
        {
            Assert.Equal("PausedByNetworkError", result.PauseReason);
            Assert.True(File.Exists(Path.Combine(harness.StagingDirectory, result.Artifact!.FileName)));
        }
        else Assert.Equal(PipelineHarness.BackupBytes, received.ToArray());
    }
}
