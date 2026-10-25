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

public sealed class ReplicaRepairDurableIntentUnitTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 12, 10, 0, 0, TimeSpan.Zero);
    private static readonly TenantId Tenant = TenantId.New();
    private static readonly byte[] ArtifactData = [100, 101, 102, 103, 104, 105];
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
    public async Task IdempotentPut_WhenSourceStreamNotConsumed_AdoptsExistingVerifiedTarget_And_CompletesSuccessfully()
    {
        var time = new FakeTimeProvider(T0);
        var targetConnId = Guid.NewGuid();
        var (artifact, sourceReplica, admission) = CreateSetup(targetConnId);
        var store = new FakeRepairStore();
        var provider = new FakeStorageProvider(ArtifactData, Checksum)
        {
            // Simulate SFTP/Google Drive idempotent PUT returning metadata without reading content stream
            SimulateIdempotentPutWithoutConsumingStream = true
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
        Assert.NotNull(store.CompletedReplica);
        Assert.Equal("Verified", store.CompletedReplica.VerificationState);
        Assert.NotNull(store.PinnedTargetKey);
        Assert.Empty(provider.DeletedKeys);
        Assert.Equal(0, store.RecordedOrphanCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IntegrityMismatch_FailsWithFencedCleanup_WithoutPublishingReplica(bool corruptSource)
    {
        var targetConnId = Guid.NewGuid();
        var (artifact, sourceReplica, admission) = CreateSetup(targetConnId);
        var store = new FakeRepairStore();
        var sourceData = (byte[])ArtifactData.Clone();
        if (corruptSource)
            sourceData[0] ^= 1;
        var provider = new FakeStorageProvider(sourceData, Checksum);
        if (!corruptSource)
        {
            provider.OnVerifyAsync = _ => Task.FromResult(new StorageVerificationResult(
                new StorageLocation(provider.Id, new StorageObjectKey(sourceReplica.Key)),
                StorageVerificationStatus.ChecksumMismatch,
                StorageVerificationMethod.ReadBack,
                Size,
                new ContentChecksum(ChecksumAlgorithm.Sha256, new string('a', 64))));
        }
        var service = new ReplicaRepairService(store, new FakeStorageResolver(provider),
            new FakeTimeProvider(T0), NullLogger<ReplicaRepairService>.Instance);
        var claimedOp = new ReplicaRepairOperation(Guid.NewGuid(), Tenant, artifact.Id, targetConnId, "sftp", sourceReplica.Id, T0, maxAttempts: 1);
        claimedOp.Claim(Guid.NewGuid(), "worker-1", T0, TimeSpan.FromMinutes(10));

        var result = await service.ExecuteRepairAsync(Tenant, artifact.Id, claimedOp.Id, admission,
            externalToken: CancellationToken.None, claimedOperation: claimedOp);

        Assert.NotNull(result);
        Assert.Equal(ReplicaRepairStatus.Failed.ToString(), result.Status);
        Assert.Equal(corruptSource ? ReplicaRepairFailureCodes.SourceChecksumMismatch
            : ReplicaRepairFailureCodes.DestinationReadbackFailed, result.FailureCode);
        Assert.Null(result.TargetReplicaId);
        Assert.Null(store.CompletedReplica);
        Assert.True(store.CanCleanupChecked);
        Assert.Equal(store.PinnedTargetKey, Assert.Single(provider.DeletedKeys));
        Assert.Equal(corruptSource ? 0 : 1, provider.VerifyCalls);
        Assert.Equal(0, store.RecordedOrphanCount);
    }

    [Theory]
    [InlineData(false, false, 1, 0)] // deletion could not be confirmed: a durable orphan is recorded
    [InlineData(true, false, 0, 1)]  // deletion succeeded: nothing to track
    [InlineData(false, true, 0, 0)]  // object already gone (NotFound): absence counts as verified
    public async Task VerifyAsync_WhenThrowsException_PerformsFencedCleanup_And_RecordsDurableOrphan_OnlyIfDeletionUnconfirmed(bool deleteResult, bool deleteNotFound, int expectedOrphans, int expectedDeletes)
    {
        var time = new FakeTimeProvider(T0);
        var targetConnId = Guid.NewGuid();
        var (artifact, sourceReplica, admission) = CreateSetup(targetConnId);
        var store = new FakeRepairStore();
        var provider = new FakeStorageProvider(ArtifactData, Checksum)
        {
            OnVerifyAsync = _ => throw new StorageException(StorageFailureKind.Io, "Readback failed"),
            DeleteResult = deleteResult,
            DeleteThrows = deleteNotFound ? new StorageException(StorageFailureKind.NotFound, "File not found") : null
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
        Assert.Equal(ReplicaRepairFailureCodes.StorageWriteFailed, result.FailureCode);
        Assert.Null(result.TargetReplicaId);
        Assert.Null(store.CompletedReplica);
        Assert.True(store.CanCleanupChecked);
        Assert.Equal(expectedDeletes, provider.DeletedKeys.Count);
        Assert.Equal(expectedOrphans, store.RecordedOrphanCount);
        Assert.Equal(expectedOrphans == 1 ? store.PinnedTargetKey : null, store.LastRecordedOrphanKey);
    }

    [Fact]
    public async Task DbCompletion_WhenFails_EnsuresRemoteObjectTrackedViaCleanupOrOrphan()
    {
        var time = new FakeTimeProvider(T0);
        var targetConnId = Guid.NewGuid();
        var (artifact, sourceReplica, admission) = CreateSetup(targetConnId);
        var store = new FakeRepairStore
        {
            CompleteOperationThrows = new InvalidOperationException("DB deadlock or concurrency conflict")
        };
        var provider = new FakeStorageProvider(ArtifactData, Checksum)
        {
            DeleteResult = false // Cannot confirm delete
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
        Assert.True(store.CanCleanupChecked);
        Assert.Equal(1, store.RecordedOrphanCount);
    }

    [Fact]
    public async Task StaleOwner_WhenCanCleanupReturnsFalse_NeverDeletesRemoteTarget()
    {
        var time = new FakeTimeProvider(T0);
        var targetConnId = Guid.NewGuid();
        var (artifact, sourceReplica, admission) = CreateSetup(targetConnId);
        var store = new FakeRepairStore
        {
            CanCleanupResult = false // Fenced: another worker owns lease or verified replica exists
        };
        var provider = new FakeStorageProvider(ArtifactData, Checksum)
        {
            OnVerifyAsync = _ => throw new StorageException(StorageFailureKind.Io, "Mismatch")
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
        Assert.True(store.CanCleanupChecked);
        Assert.Empty(provider.DeletedKeys); // DeleteAsync was NEVER called!
        Assert.Equal(0, store.RecordedOrphanCount);
    }

    [Fact]
    public void ReplicaRepairOperation_SetTargetIntent_PersistsTargetKeyAndSourceReplica()
    {
        var op = new ReplicaRepairOperation(Guid.NewGuid(), Tenant, Guid.NewGuid(), Guid.NewGuid(), "sftp", null, T0);
        var sourceReplicaId = Guid.NewGuid();
        var targetKey = "tenant/job/run/attempt/backup.bak";

        op.SetTargetIntent(sourceReplicaId, targetKey);

        Assert.Equal(sourceReplicaId, op.SourceReplicaId);
        Assert.Equal(targetKey, op.TargetKey);
    }

    [Fact]
    public void ReplicaRepairOperation_SetTargetIntent_ThrowsOnTerminalOperation()
    {
        var op = new ReplicaRepairOperation(Guid.NewGuid(), Tenant, Guid.NewGuid(), Guid.NewGuid(), "sftp", null, T0);
        var token = Guid.NewGuid();
        op.Claim(token, "worker-1", T0, TimeSpan.FromMinutes(5));
        op.Fail(token, ReplicaRepairFailureCodes.InternalError, "Failed", T0.AddMinutes(1));

        Assert.Throws<InvalidOperationException>(() => op.SetTargetIntent(Guid.NewGuid(), "key.bak"));
    }

    [Fact]
    public async Task BackendRepair_DoesNotRelayExternalSourceAfterCentralSourceCannotBeOpened()
    {
        var targetId = Guid.NewGuid();
        var (artifact, source, admission) = CreateSetup(targetId);
        var external = new BackupReplica(Guid.NewGuid(), Tenant, artifact.Id, "s3", "source", "scope/external.bak",
            Size, Checksum, "sha256", "Verified", "ReadBack", T0, T0, BackupStorageExecutionLocation.Backend,
            storageConnectionId: Guid.NewGuid());
        admission = admission with { CandidateSources = [source, external] };
        var store = new FakeRepairStore();
        var provider = new FakeStorageProvider(ArtifactData, Checksum) { OpenReadThrows = new StorageException(StorageFailureKind.NotFound, "Unavailable central source.") };
        var service = new ReplicaRepairService(store, new FakeStorageResolver(provider), new FakeTimeProvider(T0), NullLogger<ReplicaRepairService>.Instance);
        var operation = new ReplicaRepairOperation(Guid.NewGuid(), Tenant, artifact.Id, targetId, "sftp", source.Id, T0);
        operation.Claim(Guid.NewGuid(), "backend-node", T0, TimeSpan.FromMinutes(10));

        await service.ExecuteRepairAsync(Tenant, artifact.Id, operation.Id, admission, claimedOperation: operation);

        Assert.Equal(1, provider.OpenReadCalls);
        Assert.Null(store.CompletedReplica);
        Assert.Equal(ReplicaRepairFailureCodes.SourceInaccessible, store.FailureCode);
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

        public bool SimulateIdempotentPutWithoutConsumingStream { get; set; }
        public Func<CancellationToken, Task<StorageVerificationResult>>? OnVerifyAsync { get; set; }
        public bool DeleteResult { get; set; } = true;
        public Exception? DeleteThrows { get; set; }
        public int VerifyCalls { get; private set; }
        public Exception? OpenReadThrows { get; set; }
        public int OpenReadCalls { get; private set; }

        public Task<StorageObjectMetadata> PutAsync(StorageObjectKey key, Stream content, StoragePutOptions options, IProgress<long>? progress, CancellationToken cancellationToken)
        {
            if (!SimulateIdempotentPutWithoutConsumingStream)
            {
                using var ms = new MemoryStream();
                content.CopyTo(ms);
            }
            progress?.Report(data.Length);
            return Task.FromResult(new StorageObjectMetadata(new StorageLocation(Id, key), data.Length, new ContentChecksum(ChecksumAlgorithm.Sha256, checksum), DateTimeOffset.UtcNow));
        }

        public Task<Stream> OpenReadAsync(StorageObjectKey key, CancellationToken cancellationToken)
        {
            OpenReadCalls++;
            if (OpenReadThrows is not null) throw OpenReadThrows;
            return Task.FromResult<Stream>(new MemoryStream(data));
        }

        public Task<StorageVerificationResult> VerifyAsync(StorageObjectKey key, StorageVerificationExpectation expected, CancellationToken cancellationToken)
        {
            VerifyCalls++;
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
            if (DeleteThrows is not null)
                throw DeleteThrows;
            if (DeleteResult)
                DeletedKeys.Add(key.Value);
            return Task.FromResult(DeleteResult);
        }

        public Task<bool> ExistsAsync(StorageObjectKey key, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<StorageObjectMetadata?> GetMetadataAsync(StorageObjectKey key, CancellationToken cancellationToken) => Task.FromResult<StorageObjectMetadata?>(null);
        public IAsyncEnumerable<StorageEntry> EnumerateAsync(StorageObjectKey? prefix, DateTimeOffset modifiedBefore, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeRepairStore : IReplicaRepairStore
    {
        public string? PinnedTargetKey;
        public bool CanCleanupChecked;
        public bool CanCleanupResult = true;
        public BackupReplica? CompletedReplica;
        public Exception? CompleteOperationThrows;
        public int RecordedOrphanCount;
        public string? LastRecordedOrphanKey;
        public string? FailureCode;
        public ReplicaRepairStatus Status = ReplicaRepairStatus.InProgress;

        public Task<bool> PinSourceAsync(TenantId tenantId, Guid operationId, Guid leaseToken, Guid sourceReplicaId, string targetKey, DateTimeOffset now, CancellationToken cancellationToken)
        {
            PinnedTargetKey = targetKey;
            return Task.FromResult(true);
        }

        public Task<bool> CanCleanupTargetAsync(TenantId tenantId, Guid operationId, Guid leaseToken, string targetKey, DateTimeOffset now, CancellationToken cancellationToken)
        {
            CanCleanupChecked = true;
            return Task.FromResult(CanCleanupResult);
        }

        public Task<BackupReplica> CompleteOperationAsync(TenantId tenantId, Guid operationId, Guid leaseToken, BackupReplica newReplica, DateTimeOffset now, CancellationToken cancellationToken)
        {
            if (CompleteOperationThrows is not null)
                throw CompleteOperationThrows;
            CompletedReplica = newReplica;
            return Task.FromResult(newReplica);
        }

        public Task RecordRepairOrphanAsync(TenantId tenantId, Guid artifactId, Guid storageConnectionId, string storageProvider, string storageTarget, string key, long sizeBytes, string checksum, DateTimeOffset now, CancellationToken cancellationToken, bool quarantine = false)
        {
            RecordedOrphanCount++;
            LastRecordedOrphanKey = key;
            return Task.CompletedTask;
        }

        public Task<ReplicaRepairLeaseRenewal> RenewLeaseAsync(TenantId tenantId, Guid operationId, Guid leaseToken, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken) => Task.FromResult<ReplicaRepairLeaseRenewal>(true);
        public Task<bool> CancelOperationAsync(TenantId tenantId, Guid operationId, Guid leaseToken, DateTimeOffset now, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<bool> FailOperationAsync(TenantId tenantId, Guid operationId, Guid leaseToken, string failureCode, string? reason, DateTimeOffset now, CancellationToken cancellationToken)
        {
            FailureCode = failureCode;
            Status = ReplicaRepairStatus.Failed;
            return Task.FromResult(true);
        }
        public Task<bool> FailTargetConflictAsync(TenantId tenantId, Guid operationId, Guid leaseToken, DateTimeOffset now, CancellationToken cancellationToken) =>
            FailOperationAsync(tenantId, operationId, leaseToken, ReplicaRepairFailureCodes.TargetConflict, null, now, cancellationToken);

        public Task<bool> ScheduleRetryAsync(TenantId tenantId, Guid operationId, Guid leaseToken, string failureCode, string? reason, DateTimeOffset nextRetryAt, DateTimeOffset now, CancellationToken cancellationToken)
        {
            FailureCode = failureCode;
            Status = ReplicaRepairStatus.Pending;
            return Task.FromResult(true);
        }
        public Task<bool> UpdateProgressAsync(TenantId tenantId, Guid operationId, Guid leaseToken, long transferredBytes, long totalBytes, DateTimeOffset now, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<bool> RequestCancellationAsync(TenantId tenantId, Guid artifactId, Guid operationId, DateTimeOffset now, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<ReplicaRepairAdmissionResult> AdmitRepairAsync(TenantId tenantId, Guid artifactId, Guid? storageConnectionId, string? storageProvider, DateTimeOffset now, CancellationToken cancellationToken, Guid? excludeOperationId = null) => throw new NotSupportedException();
        public Task<ReplicaRepairAdmissionResult> CreateOperationAsync(ReplicaRepairOperation operation, CancellationToken cancellationToken) => Task.FromResult(new ReplicaRepairAdmissionResult(true, null, null));
        public Task<ReplicaRepairOperation?> ClaimOperationAsync(TenantId tenantId, Guid operationId, Guid leaseToken, string leaseOwner, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ReplicaRepairOperation?> ClaimDueRepairAsync(string leaseOwner, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken, TenantId? tenantId = null, bool backendExecutedOnly = false) => throw new NotSupportedException();
        public Task<ReplicaRepairOperation?> ClaimAgentRepairAsync(BackupPlatform.Application.Agents.AgentIdentity identity, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ReplicaRepairOperation?> FindOperationAsync(TenantId tenantId, Guid operationId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BackupArtifact?> FindArtifactAsync(TenantId tenantId, Guid artifactId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BackupDestination?> FindRunDestinationAsync(TenantId tenantId, Guid artifactId, Guid storageConnectionId, CancellationToken cancellationToken) => throw new NotSupportedException();
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
                PinnedTargetKey,
                CompletedReplica is not null ? ReplicaRepairStatus.Succeeded.ToString() : Status.ToString(),
                FailureCode,
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
