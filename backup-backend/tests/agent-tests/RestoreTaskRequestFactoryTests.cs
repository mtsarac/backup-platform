using BackupPlatform.Agent.Core.Restore;
using BackupPlatform.Agent.Core.Tasks;
using BackupPlatform.AgentTests.Support;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Contracts.Restores;
using BackupPlatform.StorageProviders.Abstractions;

namespace BackupPlatform.AgentTests;

public sealed class RestoreTaskRequestFactoryTests
{
    private readonly TaskCredentials _credentials = new();
    private readonly StagingArea _staging = StagingArea.SameHost(Path.GetTempPath());

    private sealed class FakeTaskStorageProviderFactory(IStorageProvider restoreProvider) : ITaskStorageProviderFactory
    {
        public int BackupCalls { get; private set; }
        public int RestoreCalls { get; private set; }
        public BackupTaskSnapshot? LastBackupTask { get; private set; }
        public RestoreTaskSnapshot? LastRestoreTask { get; private set; }

        public IStorageProvider CreateBackup(BackupTaskSnapshot task, BackupDestinationContract destination)
        {
            BackupCalls++;
            LastBackupTask = task;
            return restoreProvider;
        }

        public IStorageProvider CreateRestore(RestoreTaskSnapshot task)
        {
            RestoreCalls++;
            LastRestoreTask = task;
            return restoreProvider;
        }
    }

    private sealed class StubStorageProvider : IStorageProvider
    {
        public StorageProviderId Id => new("sftp");
        public StorageTopology Topology => StorageTopology.BackendRelayed;
        public StorageCapabilities Capabilities => new([StorageCapabilityIds.Read, StorageCapabilityIds.Delete]);
        public Task<StorageObjectMetadata> PutAsync(StorageObjectKey key, Stream content, StoragePutOptions options, IProgress<long>? progress, CancellationToken cancellationToken) =>
            Task.FromResult(new StorageObjectMetadata(new StorageLocation(Id, key), 0, null, DateTimeOffset.UtcNow));
        public Task<bool> ExistsAsync(StorageObjectKey key, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<StorageObjectMetadata?> GetMetadataAsync(StorageObjectKey key, CancellationToken cancellationToken) => Task.FromResult<StorageObjectMetadata?>(null);
        public Task<StorageVerificationResult> VerifyAsync(StorageObjectKey key, StorageVerificationExpectation expected, CancellationToken cancellationToken) =>
            Task.FromResult(new StorageVerificationResult(new StorageLocation(Id, key), StorageVerificationStatus.Verified, StorageVerificationMethod.ReadBack, expected.SizeBytes, expected.Checksum));
        public Task<Stream> OpenReadAsync(StorageObjectKey key, CancellationToken cancellationToken) => Task.FromResult<Stream>(new MemoryStream());
        public Task<bool> DeleteAsync(StorageObjectKey key, CancellationToken cancellationToken) => Task.FromResult(true);
        public IAsyncEnumerable<StorageEntry> EnumerateAsync(StorageObjectKey? prefix, DateTimeOffset modifiedBefore, CancellationToken cancellationToken) =>
            AsyncEnumerable.Empty<StorageEntry>();
    }

    private static RestoreTaskSnapshot CreateSnapshot(string access, string provider, string location, string target)
    {
        var tenant = Guid.NewGuid();
        return new RestoreTaskSnapshot
        {
            OperationId = Guid.NewGuid(),
            AttemptId = Guid.NewGuid(),
            AttemptNumber = 1,
            TenantId = tenant,
            LeaseToken = Guid.NewGuid(),
            LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2),
            ArtifactId = Guid.NewGuid(),
            ReplicaId = Guid.NewGuid(),
            Source = new RestoreTaskSource
            {
                Access = access,
                StorageProvider = provider,
                StorageExecutionLocation = location,
                StorageTarget = target,
                StorageKey = "tenant/sales.bak",
                SizeBytes = 2048,
                Checksum = new string('b', 64),
                ChecksumAlgorithm = "sha256"
            },
            Target = new RestoreTaskTarget
            {
                Host = "sql-host",
                Port = 1433,
                IntegratedSecurity = true
            },
            DatabaseName = "SalesRestored",
            Mode = "NewDatabase",
            Purpose = "Execution"
        };
    }

    [Fact]
    public async Task BackendRelayedSftpWithStorageFactoryProducesRequestWithStorageOverride()
    {
        var connectionId = Guid.NewGuid();
        var stubProvider = new StubStorageProvider();
        var factory = new FakeTaskStorageProviderFactory(stubProvider);
        var requestFactory = new RestoreTaskRequestFactory(_credentials, [_staging], factory);
        var task = CreateSnapshot("BackendRelayed", "sftp", "Backend", connectionId.ToString("D"));

        var request = await requestFactory.CreateAsync(task, CancellationToken.None);

        Assert.NotNull(request.StorageOverride);
        Assert.Same(stubProvider, request.StorageOverride);
        Assert.Equal(1, factory.RestoreCalls);
        Assert.Same(task, factory.LastRestoreTask);
    }

    [Fact]
    public async Task BackendRelayedSftpWithoutStorageFactoryThrowsInvalidConfiguration()
    {
        var connectionId = Guid.NewGuid();
        var requestFactory = new RestoreTaskRequestFactory(_credentials, [_staging], storageFactory: null);
        var task = CreateSnapshot("BackendRelayed", "sftp", "Backend", connectionId.ToString("D"));

        var ex = await Assert.ThrowsAsync<RestoreTaskRequestException>(() =>
            requestFactory.CreateAsync(task, CancellationToken.None).AsTask());

        Assert.Equal(RestoreAgentFailureCodes.InvalidConfiguration, ex.FailureCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-guid")]
    public async Task BackendRelayedSftpWithInvalidTargetThrowsInvalidConfiguration(string target)
    {
        var stubProvider = new StubStorageProvider();
        var factory = new FakeTaskStorageProviderFactory(stubProvider);
        var requestFactory = new RestoreTaskRequestFactory(_credentials, [_staging], factory);
        var task = CreateSnapshot("BackendRelayed", "sftp", "Backend", target);

        var ex = await Assert.ThrowsAsync<RestoreTaskRequestException>(() =>
            requestFactory.CreateAsync(task, CancellationToken.None).AsTask());

        Assert.Equal(RestoreAgentFailureCodes.InvalidConfiguration, ex.FailureCode);
    }

    [Fact]
    public async Task LocalAndCentralSourcesPreserveNullStorageOverride()
    {
        var stubProvider = new StubStorageProvider();
        var factory = new FakeTaskStorageProviderFactory(stubProvider);
        var requestFactory = new RestoreTaskRequestFactory(_credentials, [_staging], factory);

        var localTask = CreateSnapshot("AgentLocal", "local", "Agent", "local");
        var localRequest = await requestFactory.CreateAsync(localTask, CancellationToken.None);
        Assert.Null(localRequest.StorageOverride);

        var centralTask = CreateSnapshot("CentralStorage", "central", "Backend", "default");
        var centralRequest = await requestFactory.CreateAsync(centralTask, CancellationToken.None);
        Assert.Null(centralRequest.StorageOverride);

        Assert.Equal(0, factory.RestoreCalls);
    }
}
