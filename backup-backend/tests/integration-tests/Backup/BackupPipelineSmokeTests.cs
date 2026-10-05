extern alias AgentCore;

using System.Security.Cryptography;
using AgentCore::BackupPlatform.Agent.Core.Backup;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.MSSQL;
using BackupPlatform.IntegrationTests.MsSql;
using BackupPlatform.StorageProviders.Abstractions;
using BackupPlatform.StorageProviders.Central;
using BackupPlatform.StorageProviders.Local;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackupPlatform.IntegrationTests.Backup;

/// <summary>
/// The whole pipeline against a real SQL Server and real storage providers on the real file system: pre-check, BACKUP, VERIFYONLY,
/// both checksums, upload, replica verification and staging cleanup. The checksums are re-computed here independently from the
/// stored bytes, so the assertions do not rely on the pipeline's own arithmetic.
/// </summary>
[Collection(MsSqlCollection.Name)]
public sealed class BackupPipelineSmokeTests(MsSqlFixture fixture) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bp_test_smoke_" + Guid.NewGuid().ToString("N")[..10]);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private (DatabaseBackupPipeline Pipeline, IStorageProvider Storage) Compose(bool central)
    {
        Directory.CreateDirectory(_root);
        IStorageProvider storage = central
            ? new CentralStorageProvider(new CentralStorageOptions(_root, "tenant-smoke"))
            : new LocalStorageProvider(new LocalStorageOptions(_root));
        var pipeline = new DatabaseBackupPipeline(
            [new SqlServerBackupProvider(NullLogger<SqlServerBackupProvider>.Instance)],
            [storage],
            new PassThroughArtifactPreparer(),
            NullLogger<DatabaseBackupPipeline>.Instance);
        return (pipeline, storage);
    }

    private DatabaseBackupPipelineRequest Request(string database, IStorageProvider storage, FullBackupOptions? options = null) =>
        new(
            DatabaseEngineId.MsSql,
            fixture.Settings,
            fixture.Admin,
            database,
            fixture.Staging,
            storage.Id,
            new StorageObjectKey("tenant-smoke/mssql/" + database),
            options);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFullBackupTravelsFromSqlServerThroughStagingIntoStorageAndStagingIsReleased(bool central)
    {
        var (pipeline, storage) = Compose(central);
        var progress = new List<BackupProgress>();
        var options = new FullBackupOptions(BackupOptionMode.Required, BackupOptionMode.Required, BackupOptionMode.IfSupported);

        var result = await pipeline.RunFullBackupAsync(Request(fixture.Source, storage, options), new Collector(progress), CancellationToken.None);

        Assert.True(result.Status == DatabaseBackupStatus.Succeeded, $"{result.Failure?.Kind}/{result.Failure?.ReasonCode}: {result.Failure?.Message}");
        Assert.Equal(BackupExecutionStage.Completed, result.FinalStage);
        Assert.Equal(fixture.Source, result.Source!.DatabaseName);
        Assert.Equal(options, result.Options!.Requested);
        Assert.True(result.Options.Applied.CopyOnly);
        Assert.True(result.Options.Applied.Checksum);
        Assert.Equal(BackupVerificationStatus.Passed, result.Verification.Status);

        // Independent proof: hash the bytes that really sit in storage.
        var metadata = await storage.GetMetadataAsync(result.Replica!.Location.Key, CancellationToken.None);
        Assert.NotNull(metadata);
        var physical = Directory.GetFiles(_root, "*.bak", SearchOption.AllDirectories).Single();
        Assert.Equal(central, physical.Contains(Path.Combine("tenants", "tenant-smoke"), StringComparison.Ordinal));
        var expected = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(physical)));
        Assert.Equal(expected, result.SourceFile!.Checksum.Hex);
        Assert.Equal(expected, result.Artifact!.Checksum.Hex);
        Assert.Equal(expected, result.Replica.Checksum.Hex);
        Assert.Equal(new FileInfo(physical).Length, result.SourceFile.SizeBytes);
        Assert.Equal(result.SourceFile.SizeBytes, result.Replica.SizeBytes);
        Assert.Equal(StorageVerificationStatus.Verified, result.Replica.VerificationStatus);
        Assert.Equal(storage.Id, result.Replica.Location.Provider);

        // The staged file is gone, and it was gone only because the replica verified.
        Assert.Empty(fixture.StagingEntries("bp_"));
        Assert.Equal(StagingCleanupStatus.Deleted, result.StagingCleanup.Status);
        Assert.Empty(result.CleanupFailures);

        Assert.Equal(
            [
                BackupExecutionStage.Preparing, BackupExecutionStage.BackingUp, BackupExecutionStage.Verifying,
                BackupExecutionStage.HashingSource, BackupExecutionStage.PreparingArtifact, BackupExecutionStage.HashingArtifact,
                BackupExecutionStage.Uploading, BackupExecutionStage.VerifyingReplica, BackupExecutionStage.Completed
            ],
            result.Stages.Select(s => s.Stage));
        Assert.Equal(result.Stages.Select(s => s.Stage), progress.Select(p => p.Stage).Distinct());
    }

    [Fact]
    public async Task AMissingDatabaseFailsThePreCheckBeforeAnythingIsWritten()
    {
        var (pipeline, storage) = Compose(central: false);

        var result = await pipeline.RunFullBackupAsync(Request("bp_it_does_not_exist_" + fixture.Suffix, storage), null, CancellationToken.None);

        Assert.Equal(DatabaseBackupStatus.Failed, result.Status);
        Assert.Equal(BackupFailureKind.PreCheckFailed, result.Failure!.Kind);
        Assert.Empty(fixture.StagingEntries("bp_"));
        Assert.Empty(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CancellingARunningBackupLeavesNoStagedFileNoReplicaAndNoRunningStatement()
    {
        var (pipeline, storage) = Compose(central: false);
        using var cancellation = new CancellationTokenSource();
        var progress = new Collector(new List<BackupProgress>(), onReport: p =>
        {
            if (p.Stage == BackupExecutionStage.BackingUp)
            {
                cancellation.Cancel();
            }
        });

        var result = await pipeline.RunFullBackupAsync(Request(fixture.Big, storage), progress, cancellation.Token);

        Assert.Equal(DatabaseBackupStatus.Cancelled, result.Status);
        Assert.Equal(BackupFailureKind.Cancelled, result.Failure!.Kind);
        Assert.Equal(BackupExecutionStage.Cancelled, result.FinalStage);
        Assert.Null(result.Replica);
        Assert.Empty(fixture.StagingEntries("bp_"));
        Assert.Empty(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
        Assert.Equal(0, await fixture.CountRunningBackupRequestsAsync(fixture.Big));
    }

    private sealed class Collector(List<BackupProgress> sink, Action<BackupProgress>? onReport = null) : IProgress<BackupProgress>
    {
        public void Report(BackupProgress value)
        {
            lock (sink)
            {
                sink.Add(value);
            }

            onReport?.Invoke(value);
        }
    }
}
