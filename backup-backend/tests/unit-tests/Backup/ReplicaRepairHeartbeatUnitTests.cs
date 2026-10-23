using System.Collections.Concurrent;
using System.Security.Cryptography;
using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.StorageProviders.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BackupPlatform.UnitTests.Backup;

public sealed class ReplicaRepairHeartbeatUnitTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 12, 10, 0, 0, TimeSpan.Zero);
    private static readonly TenantId Tenant = TenantId.New();
    private static readonly byte[] ArtifactData = [10, 20, 30, 40, 50, 60, 70, 80];
    private static readonly string Checksum = Convert.ToHexString(SHA256.HashData(ArtifactData)).ToLowerInvariant();
    private static readonly long Size = ArtifactData.Length;

    private static (BackupArtifact Artifact, BackupReplica SourceReplica, ReplicaRepairAdmissionResult Admission) CreateSetup(Guid targetConnId)
    {
        var artifact = new BackupArtifact(Guid.NewGuid(), Tenant, Guid.NewGuid(), Size, new('a', 64), Checksum, "sha256", T0, "{}", "{}");
        var sourceReplica = new BackupReplica(
            Guid.NewGuid(),
            Tenant,
            artifact.Id,
            "central",
            "central",
            $"{Tenant.Value:N}/job/run/attempt/source.bak",
            Size,
            Checksum,
            "sha256",
            "Verified",
            "ReadBack",
            T0,
            T0,
            BackupStorageExecutionLocation.Backend,
            owningAgentId: null,
            storageConnectionId: Guid.NewGuid());
        artifact.AddReplica(sourceReplica);

        var targetDest = new BackupDestination("sftp", targetConnId);
        var admission = new ReplicaRepairAdmissionResult(true, null, null, artifact, targetDest, sourceReplica, [sourceReplica]);
        return (artifact, sourceReplica, admission);
    }

    [Fact]
    public async Task Heartbeat_ContinuesDuringLongVerification_And_AllowsCompletionBeyondInitialLease()
    {
        var time = new FakeTimeProvider(T0);
        var targetConnId = Guid.NewGuid();
        var (artifact, sourceReplica, admission) = CreateSetup(targetConnId);
        var store = new FakeRepairStore();
        var provider = new FakeStorageProvider(ArtifactData, Checksum);

        provider.OnVerifyAsync = async ct =>
        {
            // Advance time in 20s steps past the 10-minute (600s) lease threshold
            for (var i = 0; i < 35; i++)
            {
                var tcs = new TaskCompletionSource<bool>();
                store.OnRenewLease = () => tcs.TrySetResult(true);
                // The heartbeat timer may not be registered yet; keep advancing until a renewal is observed.
                while (!tcs.Task.IsCompleted)
                {
                    time.Advance(TimeSpan.FromSeconds(20));
                    await Task.Delay(5);
                }
            }

            return new StorageVerificationResult(
                new StorageLocation(provider.Id, new StorageObjectKey("test")),
                StorageVerificationStatus.Verified,
                StorageVerificationMethod.ReadBack,
                Size,
                new ContentChecksum(ChecksumAlgorithm.Sha256, Checksum));
        };

        var resolver = new FakeStorageResolver(provider);
        var service = new ReplicaRepairService(store, resolver, time, NullLogger<ReplicaRepairService>.Instance);

        var operationId = Guid.NewGuid();
        var claimedOp = new ReplicaRepairOperation(operationId, Tenant, artifact.Id, targetConnId, "sftp", sourceReplica.Id, T0);
        claimedOp.Claim(Guid.NewGuid(), "worker-1", T0, TimeSpan.FromMinutes(10));

        var result = await service.ExecuteRepairAsync(
            Tenant,
            artifact.Id,
            operationId,
            admission,
            externalToken: CancellationToken.None,
            claimedOperation: claimedOp);

        Assert.NotNull(result);
        Assert.True(store.RenewLeaseCallCount >= 30, $"Expected >= 30 renewals during verify, got {store.RenewLeaseCallCount}");
        Assert.NotNull(store.CompletedReplica);
        Assert.Equal("Verified", store.CompletedReplica.VerificationState);
        Assert.Equal(0, store.CancelOperationCallCount);
        Assert.Empty(provider.DeletedKeys);
    }

    [Theory]
    [InlineData(true)]  // renewal reports the lease was lost
    [InlineData(false)] // renewal throws (e.g. DB connection failure)
    public async Task Heartbeat_WhenRenewalFailsDuringVerification_StopsSafely_WithoutPublishingDeletingOrCancelling(bool leaseLost)
    {
        var time = new FakeTimeProvider(T0);
        var targetConnId = Guid.NewGuid();
        var (artifact, sourceReplica, admission) = CreateSetup(targetConnId);
        var store = leaseLost
            ? new FakeRepairStore { ShouldRenewSucceed = false }
            : new FakeRepairStore { RenewLeaseThrows = new InvalidOperationException("Simulated DB connection failure") };
        var provider = new FakeStorageProvider(ArtifactData, Checksum);
        provider.OnVerifyAsync = async ct =>
        {
            var tcs = new TaskCompletionSource<bool>();
            store.OnRenewLease = () => tcs.TrySetResult(true);
            // The heartbeat timer may not be registered yet; keep advancing until a renewal is observed.
            while (!tcs.Task.IsCompleted)
            {
                time.Advance(TimeSpan.FromSeconds(20));
                await Task.Delay(5);
            }

            while (!ct.IsCancellationRequested)
            {
                await Task.Yield();
            }

            ct.ThrowIfCancellationRequested();
            return new StorageVerificationResult(
                new StorageLocation(provider.Id, new StorageObjectKey("test")),
                StorageVerificationStatus.Verified,
                StorageVerificationMethod.ReadBack,
                Size,
                new ContentChecksum(ChecksumAlgorithm.Sha256, Checksum));
        };

        var resolver = new FakeStorageResolver(provider);
        var service = new ReplicaRepairService(store, resolver, time, NullLogger<ReplicaRepairService>.Instance);

        var operationId = Guid.NewGuid();
        var claimedOp = new ReplicaRepairOperation(operationId, Tenant, artifact.Id, targetConnId, "sftp", sourceReplica.Id, T0);
        claimedOp.Claim(Guid.NewGuid(), "worker-1", T0, TimeSpan.FromMinutes(10));

        var result = await service.ExecuteRepairAsync(
            Tenant,
            artifact.Id,
            operationId,
            admission,
            externalToken: CancellationToken.None,
            claimedOperation: claimedOp);

        Assert.NotNull(result);
        Assert.Null(store.CompletedReplica);
        Assert.Empty(provider.DeletedKeys);
        Assert.Equal(0, store.CancelOperationCallCount);
    }

    [Fact]
    public async Task HostShutdown_CancelsTransfer_WithoutDeletingRemoteObjectOrMarkingCancelled()
    {
        var time = new FakeTimeProvider(T0);
        var targetConnId = Guid.NewGuid();
        var (artifact, sourceReplica, admission) = CreateSetup(targetConnId);
        var store = new FakeRepairStore();
        var provider = new FakeStorageProvider(ArtifactData, Checksum);
        using var hostCts = new CancellationTokenSource();

        provider.OnVerifyAsync = ct =>
        {
            hostCts.Cancel();
            throw new OperationCanceledException(hostCts.Token);
        };

        var resolver = new FakeStorageResolver(provider);
        var service = new ReplicaRepairService(store, resolver, time, NullLogger<ReplicaRepairService>.Instance);

        var operationId = Guid.NewGuid();
        var claimedOp = new ReplicaRepairOperation(operationId, Tenant, artifact.Id, targetConnId, "sftp", sourceReplica.Id, T0);
        claimedOp.Claim(Guid.NewGuid(), "worker-1", T0, TimeSpan.FromMinutes(10));

        var result = await service.ExecuteRepairAsync(
            Tenant,
            artifact.Id,
            operationId,
            admission,
            externalToken: hostCts.Token,
            claimedOperation: claimedOp);

        Assert.NotNull(result);
        Assert.Null(store.CompletedReplica);
        Assert.Empty(provider.DeletedKeys);
        Assert.Equal(0, store.CancelOperationCallCount);
    }

    [Fact]
    public async Task UserCancellation_CleansUpUnverifiedTarget_And_MarksOperationCancelled()
    {
        var time = new FakeTimeProvider(T0);
        var targetConnId = Guid.NewGuid();
        var (artifact, sourceReplica, admission) = CreateSetup(targetConnId);
        var store = new FakeRepairStore();
        var provider = new FakeStorageProvider(ArtifactData, Checksum);

        var resolver = new FakeStorageResolver(provider);
        var service = new ReplicaRepairService(store, resolver, time, NullLogger<ReplicaRepairService>.Instance);

        var operationId = Guid.NewGuid();
        var claimedOp = new ReplicaRepairOperation(operationId, Tenant, artifact.Id, targetConnId, "sftp", sourceReplica.Id, T0);
        claimedOp.Claim(Guid.NewGuid(), "worker-1", T0, TimeSpan.FromMinutes(10));

        provider.OnVerifyAsync = async ct =>
        {
            // Simulate user cancellation through service
            await service.CancelRepairAsync(Tenant, artifact.Id, operationId, CancellationToken.None);
            var delay = 0;
            while (!ct.IsCancellationRequested && delay++ < 50)
            {
                await Task.Yield();
            }

            ct.ThrowIfCancellationRequested();
            return new StorageVerificationResult(
                new StorageLocation(provider.Id, new StorageObjectKey("test")),
                StorageVerificationStatus.Verified,
                StorageVerificationMethod.ReadBack,
                Size,
                new ContentChecksum(ChecksumAlgorithm.Sha256, Checksum));
        };

        var result = await service.ExecuteRepairAsync(
            Tenant,
            artifact.Id,
            operationId,
            admission,
            externalToken: CancellationToken.None,
            claimedOperation: claimedOp);

        Assert.NotNull(result);
        Assert.Null(store.CompletedReplica);
        Assert.NotEmpty(provider.DeletedKeys);
        Assert.Equal(1, store.CancelOperationCallCount);
    }

    private sealed class FakeStorageResolver(IStorageProvider provider) : IBackupStorageResolver
    {
        public IReadOnlyList<BackupManagedStorage> ManagedStorages => [];
        public IStorageProvider? Resolve(TenantId tenantId, string storageProvider, string storageTarget) => provider;
    }

    private sealed class FakeStorageProvider(byte[] data, string checksum) : IStorageProvider
    {
        public StorageProviderId Id { get; } = new("fake");
        public StorageTopology Topology => StorageTopology.BackendRelayed;
        public StorageCapabilities Capabilities => new([StorageCapabilityIds.Read, StorageCapabilityIds.Delete]);
        public ConcurrentBag<string> DeletedKeys { get; } = new();

        public Func<CancellationToken, Task<StorageVerificationResult>>? OnVerifyAsync { get; set; }

        public Task<StorageObjectMetadata> PutAsync(StorageObjectKey key, Stream content, StoragePutOptions options, IProgress<long>? progress, CancellationToken cancellationToken)
        {
            using var ms = new MemoryStream();
            content.CopyTo(ms);
            progress?.Report(data.Length);
            return Task.FromResult(new StorageObjectMetadata(new StorageLocation(Id, key), data.Length, new ContentChecksum(ChecksumAlgorithm.Sha256, checksum), DateTimeOffset.UtcNow));
        }

        public Task<Stream> OpenReadAsync(StorageObjectKey key, CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new MemoryStream(data));

        public Task<StorageVerificationResult> VerifyAsync(StorageObjectKey key, StorageVerificationExpectation expected, CancellationToken cancellationToken)
        {
            if (OnVerifyAsync is not null)
                return OnVerifyAsync(cancellationToken);

            return Task.FromResult(new StorageVerificationResult(
                new StorageLocation(Id, key),
                StorageVerificationStatus.Verified,
                StorageVerificationMethod.ReadBack,
                data.Length,
                new ContentChecksum(ChecksumAlgorithm.Sha256, checksum)));
        }

        public Task<bool> DeleteAsync(StorageObjectKey key, CancellationToken cancellationToken)
        {
            DeletedKeys.Add(key.Value);
            return Task.FromResult(true);
        }

        public Task<bool> ExistsAsync(StorageObjectKey key, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<StorageObjectMetadata?> GetMetadataAsync(StorageObjectKey key, CancellationToken cancellationToken) => Task.FromResult<StorageObjectMetadata?>(null);
        public IAsyncEnumerable<StorageEntry> EnumerateAsync(StorageObjectKey? prefix, DateTimeOffset modifiedBefore, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeRepairStore : IReplicaRepairStore
    {
        public Action? OnRenewLease { get; set; }
        public int RenewLeaseCallCount;
        public bool ShouldRenewSucceed { get; set; } = true;
        public bool CancellationRequestedOnRenew { get; set; }
        public Exception? RenewLeaseThrows { get; set; }
        public BackupReplica? CompletedReplica;
        public int CancelOperationCallCount;
        public int FailOperationCallCount;

        public Task<ReplicaRepairLeaseRenewal> RenewLeaseAsync(TenantId tenantId, Guid operationId, Guid leaseToken, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref RenewLeaseCallCount);
            OnRenewLease?.Invoke();
            if (RenewLeaseThrows is not null)
                throw RenewLeaseThrows;
            return Task.FromResult<ReplicaRepairLeaseRenewal>(new(ShouldRenewSucceed, CancellationRequestedOnRenew));
        }
        public Task<BackupReplica> CompleteOperationAsync(TenantId tenantId, Guid operationId, Guid leaseToken, BackupReplica newReplica, DateTimeOffset now, CancellationToken cancellationToken)
        {
            CompletedReplica = newReplica;
            return Task.FromResult(newReplica);
        }

        public Task<bool> CancelOperationAsync(TenantId tenantId, Guid operationId, Guid leaseToken, DateTimeOffset now, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref CancelOperationCallCount);
            return Task.FromResult(true);
        }

        public Task<bool> FailOperationAsync(TenantId tenantId, Guid operationId, Guid leaseToken, string failureCode, string? reason, DateTimeOffset now, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref FailOperationCallCount);
            return Task.FromResult(true);
        }

        public Task<bool> FailTargetConflictAsync(TenantId tenantId, Guid operationId, Guid leaseToken, DateTimeOffset now, CancellationToken cancellationToken) =>
            FailOperationAsync(tenantId, operationId, leaseToken, ReplicaRepairFailureCodes.TargetConflict, null, now, cancellationToken);

        public Task<bool> ScheduleRetryAsync(TenantId tenantId, Guid operationId, Guid leaseToken, string failureCode, string? reason, DateTimeOffset nextRetryAt, DateTimeOffset now, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<bool> PinSourceAsync(TenantId tenantId, Guid operationId, Guid leaseToken, Guid sourceReplicaId, string targetKey, DateTimeOffset now, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<bool> CanCleanupTargetAsync(TenantId tenantId, Guid operationId, Guid leaseToken, string targetKey, DateTimeOffset now, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<bool> UpdateProgressAsync(TenantId tenantId, Guid operationId, Guid leaseToken, long transferredBytes, long totalBytes, DateTimeOffset now, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<bool> RequestCancellationAsync(TenantId tenantId, Guid artifactId, Guid operationId, DateTimeOffset now, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task RecordRepairOrphanAsync(TenantId tenantId, Guid artifactId, Guid storageConnectionId, string storageProvider, string storageTarget, string key, long sizeBytes, string checksum, DateTimeOffset now, CancellationToken cancellationToken, bool quarantine = false) => Task.CompletedTask;
        public Task<ReplicaRepairAdmissionResult> AdmitRepairAsync(TenantId tenantId, Guid artifactId, Guid? storageConnectionId, string? storageProvider, DateTimeOffset now, CancellationToken cancellationToken, Guid? excludeOperationId = null) => throw new NotSupportedException();
        public Task<ReplicaRepairAdmissionResult> CreateOperationAsync(ReplicaRepairOperation operation, CancellationToken cancellationToken) => Task.FromResult(new ReplicaRepairAdmissionResult(true, null, null));
        public Task<ReplicaRepairOperation?> ClaimOperationAsync(TenantId tenantId, Guid operationId, Guid leaseToken, string leaseOwner, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ReplicaRepairOperation?> ClaimDueRepairAsync(string leaseOwner, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken, TenantId? tenantId = null, bool backendExecutedOnly = false) => throw new NotSupportedException();
        public Task<ReplicaRepairOperation?> ClaimAgentRepairAsync(BackupPlatform.Application.Agents.AgentIdentity identity, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ReplicaRepairOperation?> FindOperationAsync(TenantId tenantId, Guid operationId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BackupArtifact?> FindArtifactAsync(TenantId tenantId, Guid artifactId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> RecoverStaleRepairsAsync(DateTimeOffset now, TimeSpan initialRetryDelay, CancellationToken cancellationToken) => Task.FromResult(0);
        public Task<IReadOnlyList<ReplicaRepairOperation>> DiscoverEligibleRepairsAsync(DateTimeOffset now, int maxBatch, CancellationToken cancellationToken, TenantId? tenantId = null) => Task.FromResult<IReadOnlyList<ReplicaRepairOperation>>([]);

        public Task<ReplicaRepairOperationView?> GetOperationAsync(TenantId tenantId, Guid artifactId, Guid operationId, CancellationToken cancellationToken)
        {
            var view = new ReplicaRepairOperationView(
                operationId,
                artifactId,
                Guid.Empty,
                "sftp",
                null,
                CompletedReplica?.Id,
                null,
                CompletedReplica is not null ? ReplicaRepairStatus.Succeeded.ToString() : CancelOperationCallCount > 0 ? ReplicaRepairStatus.Cancelled.ToString() : ReplicaRepairStatus.InProgress.ToString(),
                null,
                null,
                Size,
                Size,
                T0,
                T0,
                CompletedReplica is not null ? T0 : null,
                null,
                null);
            return Task.FromResult<ReplicaRepairOperationView?>(view);
        }

        public Task<IReadOnlyList<ReplicaRepairOperationView>> ListOperationsAsync(TenantId tenantId, Guid artifactId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ReplicaRepairOperationView>>([]);
    }
}
