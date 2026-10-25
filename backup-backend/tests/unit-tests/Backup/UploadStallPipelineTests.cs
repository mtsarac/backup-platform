using System.Security.Cryptography;
using BackupPlatform.Agent.Core.Backup;
using BackupPlatform.StorageProviders.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace BackupPlatform.UnitTests.Backup;

public sealed class UploadStallPipelineTests
{
    private static readonly TimeSpan Stall = TimeSpan.FromMinutes(2);

    private static (PipelineHarness Harness, FakeTimeProvider Time, DatabaseBackupPipeline Pipeline) Create()
    {
        var harness = new PipelineHarness();
        var time = new FakeTimeProvider();
        var pipeline = new DatabaseBackupPipeline([harness.Provider], [harness.Storage], new PassThroughArtifactPreparer(), NullLogger<DatabaseBackupPipeline>.Instance, time);
        return (harness, time, pipeline);
    }

    [Fact]
    public async Task A_storage_that_accepts_the_connection_but_never_answers_pauses_the_transfer_as_a_network_error()
    {
        var (harness, time, pipeline) = Create();
        using var scope = harness;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Storage.OnPut = async (_, _, _, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("unreachable");
        };
        var run = pipeline.RunFullBackupAsync(harness.Request() with { UploadStallTimeout = Stall }, null, CancellationToken.None);
        await started.Task;
        time.Advance(Stall - TimeSpan.FromSeconds(1));
        Assert.False(run.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(1));

        var result = await run.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(DatabaseBackupStatus.Paused, result.Status);
        Assert.Equal("PausedByNetworkError", result.PauseReason);
    }

    [Fact]
    public async Task A_transfer_that_keeps_moving_bytes_is_never_a_stall_however_long_it_takes()
    {
        var (harness, time, pipeline) = Create();
        using var scope = harness;
        var started = time.GetUtcNow();
        harness.Storage.OnPut = async (key, content, options, _) =>
        {
            var buffer = new byte[PipelineHarness.BackupBytes.Length / 3 + 1];
            var total = 0;
            while (true)
            {
                var read = await content.ReadAsync(buffer);
                if (read == 0) break;
                total += read;
                // Each pause is shorter than the stall period; together they are longer than it.
                time.Advance(TimeSpan.FromSeconds(90));
            }
            return new(new(StorageProviderId.Local, key), total, options.ExpectedChecksum, DateTimeOffset.UtcNow);
        };
        harness.Storage.OnVerify = (key, _) => new(new(StorageProviderId.Local, key), StorageVerificationStatus.Verified,
            StorageVerificationMethod.ReadBack, PipelineHarness.BackupBytes.Length, ContentChecksum.Sha256(SHA256.HashData(PipelineHarness.BackupBytes)));

        var result = await pipeline.RunFullBackupAsync(harness.Request() with { UploadStallTimeout = Stall }, null, CancellationToken.None);

        Assert.Equal(DatabaseBackupStatus.Succeeded, result.Status);
        Assert.True(time.GetUtcNow() - started > Stall);
    }
}
