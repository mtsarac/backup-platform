using BackupPlatform.Agent.Core.Backup;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.StorageProviders.Abstractions;
using BackupPlatform.StorageProviders.Abstractions.Transfer;

namespace BackupPlatform.UnitTests.Backup;

/// <summary>The upload bytes (and only they) pass through the pacing; the artifact that arrives, its checksum and its verification are unchanged.</summary>
public sealed class PacedUploadPipelineTests : IDisposable
{
    private readonly PipelineHarness _h = new();

    public void Dispose() => _h.Dispose();

    private DatabaseBackupPipelineRequest Request(IUploadBandwidth? bandwidth, InMemoryStorage storage, TimeSpan? timeout = null)
    {
        var single = _h.Request();
        return new DatabaseBackupPipelineRequest(single.Engine, single.Connection, single.Credential, single.DatabaseName, single.Staging,
            StorageProviderId.Local, single.StoragePrefix, single.Options, single.Overwrite, stagingOnFailure: single.StagingOnFailure)
        {
            StorageOverride = storage,
            Destinations = [new BackupPipelineDestination(StorageProviderId.Local, storage, Guid.NewGuid(), true)],
            DestinationTimeout = timeout,
            UploadBandwidth = bandwidth
        };
    }

    [Fact]
    public async Task A_paced_upload_stores_the_same_bytes_and_ends_its_pacing_session()
    {
        var storage = new InMemoryStorage(_h);
        var bandwidth = new FakeBandwidth(rate: 50_000_000);

        var result = await _h.RunAsync(Request(bandwidth, storage));

        Assert.Equal(DatabaseBackupStatus.Succeeded, result.Status);
        Assert.Equal(PipelineHarness.BackupBytes, Assert.Single(storage.Objects).Value);
        Assert.Equal(StorageVerificationStatus.Verified, result.Replica!.VerificationStatus);
        Assert.Equal(1, bandwidth.Begun);
        Assert.Equal(1, bandwidth.Disposed);
    }

    [Fact]
    public async Task Without_a_policy_the_upload_is_not_wrapped()
    {
        var storage = new InMemoryStorage(_h);
        Stream? seen = null;
        storage.OnPut = async (key, content, options, token) =>
        {
            seen = content;
            using var copy = new MemoryStream();
            await content.CopyToAsync(copy, token);
            return new StorageObjectMetadata(new StorageLocation(storage.Id, key), copy.Length, null, DateTimeOffset.UtcNow);
        };

        await _h.RunAsync(Request(null, storage));

        Assert.IsNotType<ThrottledStream>(seen);
    }

    [Fact]
    public async Task The_upload_fails_closed_when_the_policy_cannot_be_obtained_and_touches_no_storage()
    {
        var storage = new InMemoryStorage(_h);
        var putCalled = false;
        storage.OnPut = (_, _, _, _) => { putCalled = true; throw new InvalidOperationException("must not upload"); };
        var bandwidth = new FakeBandwidth { Unavailable = true };

        var result = await _h.RunAsync(Request(bandwidth, storage));

        Assert.Equal(DatabaseBackupStatus.Failed, result.Status);
        Assert.Equal(BackupPipelineReasons.UploadPolicyUnavailable, result.Failure!.ReasonCode);
        Assert.Equal(BackupFailureCause.StorageUnavailable, result.Failure.Cause);
        Assert.False(putCalled);
        Assert.Empty(_h.StagedFiles);
    }

    [Fact]
    public async Task The_destination_deadline_is_widened_by_the_pacing_allowance()
    {
        var storage = new InMemoryStorage(_h);
        storage.OnPut = async (key, content, options, token) =>
        {
            await Task.Delay(400, token);
            using var copy = new MemoryStream();
            await content.CopyToAsync(copy, token);
            return new StorageObjectMetadata(new StorageLocation(storage.Id, key), copy.Length, null, DateTimeOffset.UtcNow);
        };
        storage.OnVerify = (key, expected) => new StorageVerificationResult(new StorageLocation(storage.Id, key), StorageVerificationStatus.Verified,
            StorageVerificationMethod.ReadBack, expected.SizeBytes, expected.Checksum);

        var unpaced = await _h.RunAsync(Request(null, storage, TimeSpan.FromMilliseconds(150)));
        var paced = await _h.RunAsync(Request(new FakeBandwidth { Allowance = TimeSpan.FromSeconds(30) }, storage, TimeSpan.FromMilliseconds(150)));

        Assert.Equal(BackupPipelineReasons.DestinationTimeout, unpaced.Failure!.ReasonCode);
        Assert.Equal(DatabaseBackupStatus.Succeeded, paced.Status);
    }

    [Fact]
    public async Task A_failed_upload_still_ends_its_pacing_session()
    {
        var storage = new InMemoryStorage(_h) { OnPut = (_, _, _, _) => throw new StorageException(StorageFailureKind.Io, "disk") };
        var bandwidth = new FakeBandwidth();

        var result = await _h.RunAsync(Request(bandwidth, storage));

        Assert.Equal(DatabaseBackupStatus.Failed, result.Status);
        Assert.Equal(1, bandwidth.Disposed);
    }

    private sealed class FakeBandwidth(long? rate = null) : IUploadBandwidth
    {
        public bool Unavailable { get; init; }
        public TimeSpan Allowance { get; init; }
        public int Begun;
        public int Disposed;

        public Task<IUploadBandwidthSession> BeginAsync(CancellationToken cancellationToken)
        {
            if (Unavailable) throw new UploadBandwidthUnavailableException("down");
            Interlocked.Increment(ref Begun);
            return Task.FromResult<IUploadBandwidthSession>(new Session(this, new TransferRateLimiter(rate)));
        }

        private sealed class Session(FakeBandwidth owner, TransferRateLimiter limiter) : IUploadBandwidthSession
        {
            public Stream Wrap(Stream source) => new ThrottledStream(source, limiter);
            public TimeSpan DeadlineAllowance(long sizeBytes) => owner.Allowance;
            public ValueTask DisposeAsync()
            {
                Interlocked.Increment(ref owner.Disposed);
                return ValueTask.CompletedTask;
            }
        }
    }
}
