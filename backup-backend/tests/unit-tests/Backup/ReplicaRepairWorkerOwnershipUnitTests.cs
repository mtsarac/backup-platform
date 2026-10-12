using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Time.Testing;
using BackupPlatform.Api.Backups;
using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.StorageProviders.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BackupPlatform.UnitTests.Backup;

public sealed class ReplicaRepairWorkerOwnershipUnitTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly TenantId Tenant = TenantId.New();
    private const long Size = 1024 * 1024;
    private static readonly byte[] ArtifactData = new byte[Size];
    private static readonly string Checksum;

    static ReplicaRepairWorkerOwnershipUnitTests()
    {
        Random.Shared.NextBytes(ArtifactData);
        Checksum = Convert.ToHexString(SHA256.HashData(ArtifactData)).ToLowerInvariant();
    }

    private static (BackupArtifact Artifact, BackupReplica SourceReplica) CreateArtifact(string provider = "central")
    {
        var artifact = new BackupArtifact(Guid.NewGuid(), Tenant, Guid.NewGuid(), Size, new('a', 64), Checksum, "sha256", T0, "{}", "{}");
        var replica = new BackupReplica(
            Guid.NewGuid(),
            Tenant,
            artifact.Id,
            provider,
            provider,
            $"{Tenant.Value:N}/backup.bak",
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
        artifact.AddReplica(replica);
        artifact.Seal(T0, new BackupRetentionPolicy { Enabled = true, KeepFor = TimeSpan.FromDays(30), DeleteGracePeriod = TimeSpan.FromDays(1) });
        return (artifact, replica);
    }

    [Fact]
    public async Task WorkerTask_KeepsScopeAlive_UntilExecutionCompletes()
    {
        var (artifact, source) = CreateArtifact();
        var targetConnId = Guid.NewGuid();

        var operationId = Guid.NewGuid();
        var claimedOp = new ReplicaRepairOperation(operationId, Tenant, artifact.Id, targetConnId, "sftp", source.Id, T0);

        var putStartedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var putFinishTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var fakeProvider = new FakeTrackingStorageProvider
        {
            OnPutAsync = async _ =>
            {
                putStartedTcs.SetResult(true);
                await putFinishTcs.Task;
            }
        };

        var fakeStore = new FakeWorkerRepairStore(artifact, source, targetConnId)
        {
            CurrentOperation = claimedOp
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<ScopeLifetimeTracker>();
        services.AddScoped<IReplicaRepairStore>(_ => fakeStore);
        services.AddScoped<IBackupStorageResolver>(sp => new FakeStorageResolver(fakeProvider, sp));
        services.AddScoped<TimeProvider>(_ => TimeProvider.System);
        services.AddScoped<ReplicaRepairService>();

        var rootProvider = services.BuildServiceProvider();
        var worker = new AutomaticReplicaRepairWorker(
            rootProvider.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            NullLogger<AutomaticReplicaRepairWorker>.Instance);

        var count = await worker.TickAsync(Tenant, default);
        Assert.Equal(1, count);

        try
        {
            await putStartedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(fakeProvider.CurrentScopeTracker);
            Assert.False(fakeProvider.CurrentScopeTracker.IsDisposed, "DI scope must remain alive while repair task is running.");
        }
        finally
        {
            putFinishTcs.TrySetResult(true);
        }

        await worker.WaitForRunningTasksAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(fakeProvider.CurrentScopeTracker!.IsDisposed, "DI scope must be disposed after repair task completes.");
    }

    [Fact]
    public async Task ManualRepair_CreatesPendingDurableOperation_ExecutedViaWorker()
    {
        var (artifact, source) = CreateArtifact();
        var targetConnId = Guid.NewGuid();

        var fakeProvider = new FakeTrackingStorageProvider();
        var fakeStore = new FakeWorkerRepairStore(artifact, source, targetConnId);
        var resolver = new FakeStorageResolver(fakeProvider);

        var service = new ReplicaRepairService(fakeStore, resolver, TimeProvider.System, NullLogger<ReplicaRepairService>.Instance);

        // Manual repair API call creates durable operation
        var result = await service.StartRepairAsync(
            Tenant,
            artifact.Id,
            new StartReplicaRepairRequest(StorageConnectionId: targetConnId),
            null,
            default);

        Assert.True(result.Success);
        Assert.NotNull(result.Operation);
        Assert.Equal("Pending", result.Operation.Status);
        Assert.Equal(0, fakeProvider.PutCallCount); // Did NOT start detached background execution!

        // Worker claims and executes through unified execution path
        var services = new ServiceCollection();
        services.AddSingleton<IReplicaRepairStore>(fakeStore);
        services.AddSingleton<IBackupStorageResolver>(resolver);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(service);

        var worker = new AutomaticReplicaRepairWorker(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            NullLogger<AutomaticReplicaRepairWorker>.Instance);

        var executed = await worker.TickAsync(Tenant, default);
        Assert.Equal(1, executed);

        await worker.WaitForRunningTasksAsync();

        Assert.Equal(1, fakeProvider.PutCallCount);
        Assert.Equal(ReplicaRepairStatus.Succeeded, fakeStore.CurrentOperation?.Status);
    }

    [Fact]
    public async Task CrossScopeCancellation_ViaDatabaseHeartbeat_CancelsActiveTransfer()
    {
        var (artifact, source) = CreateArtifact();
        var targetConnId = Guid.NewGuid();

        var time = new FakeTimeProvider(T0);
        var fakeStore = new FakeWorkerRepairStore(artifact, source, targetConnId);
        var fakeProvider = new FakeTrackingStorageProvider();

        Guid operationId = Guid.Empty;
        fakeProvider.OnVerifyAsync = async ct =>
        {
            fakeStore.SimulateCrossNodeCancellation(operationId);

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            fakeStore.OnRenewLease = () => tcs.TrySetResult(true);
            // The heartbeat timer may not be registered yet; keep advancing until a renewal is observed.
            while (!tcs.Task.IsCompleted)
            {
                time.Advance(TimeSpan.FromSeconds(20));
                await Task.Delay(5);
            }

            var cancelTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var reg = ct.Register(() => cancelTcs.TrySetResult(true));
            await cancelTcs.Task;

            ct.ThrowIfCancellationRequested();
            return new StorageVerificationResult(new StorageLocation(fakeProvider.Id, new StorageObjectKey("test")), StorageVerificationStatus.Verified, StorageVerificationMethod.ReadBack, Size, new ContentChecksum(ChecksumAlgorithm.Sha256, Checksum));
        };

        var resolver = new FakeStorageResolver(fakeProvider);
        var service1 = new ReplicaRepairService(fakeStore, resolver, time, NullLogger<ReplicaRepairService>.Instance);

        var startResult = await service1.StartRepairAsync(Tenant, artifact.Id, new(StorageConnectionId: targetConnId), null, default);
        operationId = startResult.Operation!.Id;

        var outcome = await service1.ExecuteRepairAsync(Tenant, artifact.Id, operationId);

        Assert.NotNull(outcome);
        Assert.Equal(1, fakeStore.CancelOperationCallCount);
        Assert.Equal(1, fakeProvider.DeleteCallCount); // Fenced cleanup performed
    }

    [Fact]
    public async Task PendingCancellation_ImmediatelyTransitionsToTerminalCancelled_WithoutConsumingAttempts()
    {
        var (artifact, source) = CreateArtifact();
        var op = new ReplicaRepairOperation(Guid.NewGuid(), Tenant, artifact.Id, Guid.NewGuid(), "sftp", source.Id, T0);
        var fakeStore = new FakeWorkerRepairStore(op);

        Assert.Equal(ReplicaRepairStatus.Pending, op.Status);
        Assert.Equal(0, op.AttemptCount);

        // Cancel before claimed
        var cancelled = await fakeStore.RequestCancellationAsync(Tenant, op.Id, T0.AddSeconds(5), default);
        Assert.True(cancelled);
        Assert.Equal(ReplicaRepairStatus.Cancelled, op.Status);
        Assert.True(op.IsTerminal);
        Assert.Equal(0, op.AttemptCount);

        // ClaimDueRepairAsync and ClaimOperationAsync must NOT claim it
        var claimedDue = await fakeStore.ClaimDueRepairAsync("worker-1", T0.AddSeconds(10), TimeSpan.FromMinutes(10), default, Tenant);
        Assert.Null(claimedDue);

        var claimedOp = await fakeStore.ClaimOperationAsync(Tenant, op.Id, Guid.NewGuid(), "worker-1", T0.AddSeconds(10), TimeSpan.FromMinutes(10), default);
        Assert.Null(claimedOp);
        Assert.Equal(0, op.AttemptCount);
    }

    [Fact]
    public async Task HostShutdown_StopsActiveRepair_WithoutMarkingCancelled()
    {
        var (artifact, source) = CreateArtifact();
        var targetConnId = Guid.NewGuid();

        var putStartedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fakeProvider = new FakeTrackingStorageProvider
        {
            OnPutAsync = async ct =>
            {
                putStartedTcs.SetResult(true);
                var cancelTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var reg = ct.Register(() => cancelTcs.TrySetResult(true));
                await cancelTcs.Task;
                ct.ThrowIfCancellationRequested();
            }
        };

        var fakeStore = new FakeWorkerRepairStore(artifact, source, targetConnId);
        var resolver = new FakeStorageResolver(fakeProvider);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IReplicaRepairStore>(fakeStore);
        services.AddSingleton<IBackupStorageResolver>(resolver);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ReplicaRepairService>();

        var worker = new AutomaticReplicaRepairWorker(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            NullLogger<AutomaticReplicaRepairWorker>.Instance);

        var startResult = await services.BuildServiceProvider().GetRequiredService<ReplicaRepairService>()
            .StartRepairAsync(Tenant, artifact.Id, new(StorageConnectionId: targetConnId), null, default);

        await worker.TickAsync(Tenant, default);
        await putStartedTcs.Task;

        // Simulate host shutdown via StopAsync
        using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StopAsync(stopCts.Token);

        Assert.Equal(0, fakeStore.CancelOperationCallCount);
        Assert.Equal(0, fakeProvider.DeleteCallCount);
        // Operation is not cancelled; remains in InProgress for restart recovery
        Assert.Equal(ReplicaRepairStatus.InProgress, fakeStore.CurrentOperation?.Status);
    }

    [Fact]
    public void StaleRecovery_AfterHostShutdown_RecoversOperationForRetry()
    {
        var (artifact, source) = CreateArtifact();
        var op = new ReplicaRepairOperation(Guid.NewGuid(), Tenant, artifact.Id, Guid.NewGuid(), "sftp", source.Id, T0);
        op.Claim(Guid.NewGuid(), "worker-1", T0, TimeSpan.FromMinutes(5));

        Assert.Equal(ReplicaRepairStatus.InProgress, op.Status);
        Assert.Equal(1, op.AttemptCount);

        // Host shut down and lease expired
        var now = T0.AddMinutes(6);
        op.RecoverStale("lease_expired", "Lease expired after host shutdown.", now.AddSeconds(30), now);

        Assert.Equal(ReplicaRepairStatus.Pending, op.Status);
        Assert.Equal(now.AddSeconds(30), op.NextRetryAt);
        Assert.Null(op.LeaseToken);
        Assert.False(op.IsTerminal);
    }

    [Fact]
    public void StaleRecovery_WithPendingCancellation_TransitionsToCancelled()
    {
        var (artifact, source) = CreateArtifact();
        var op = new ReplicaRepairOperation(Guid.NewGuid(), Tenant, artifact.Id, Guid.NewGuid(), "sftp", source.Id, T0);
        op.Claim(Guid.NewGuid(), "worker-1", T0, TimeSpan.FromMinutes(5));

        // User requested cancellation while running, but worker died before heartbeat
        op.RequestCancellation(T0.AddMinutes(2));

        var now = T0.AddMinutes(6);
        op.CancelStale(now);

        Assert.Equal(ReplicaRepairStatus.Cancelled, op.Status);
        Assert.True(op.IsTerminal);
        Assert.Null(op.NextRetryAt);
        Assert.Null(op.LeaseToken);
    }

    [Fact]
    public async Task LostLease_DuringTransfer_FencesWorker_AndNeverDeletesTarget()
    {
        var (artifact, source) = CreateArtifact();
        var targetConnId = Guid.NewGuid();

        var time = new FakeTimeProvider(T0);
        var fakeStore = new FakeWorkerRepairStore(artifact, source, targetConnId)
        {
            ShouldRenewSucceed = false // Lease renewal fails (lost lease)
        };
        var fakeProvider = new FakeTrackingStorageProvider();
        fakeProvider.OnVerifyAsync = async ct =>
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            fakeStore.OnRenewLease = () => tcs.TrySetResult(true);
            // The heartbeat timer may not be registered yet; keep advancing until a renewal is observed.
            while (!tcs.Task.IsCompleted)
            {
                time.Advance(TimeSpan.FromSeconds(20));
                await Task.Delay(5);
            }

            var cancelTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var reg = ct.Register(() => cancelTcs.TrySetResult(true));
            await cancelTcs.Task;

            ct.ThrowIfCancellationRequested();
            return new StorageVerificationResult(new StorageLocation(fakeProvider.Id, new StorageObjectKey("test")), StorageVerificationStatus.Verified, StorageVerificationMethod.ReadBack, Size, new ContentChecksum(ChecksumAlgorithm.Sha256, Checksum));
        };

        var resolver = new FakeStorageResolver(fakeProvider);
        var service = new ReplicaRepairService(fakeStore, resolver, time, NullLogger<ReplicaRepairService>.Instance);
        var startResult = await service.StartRepairAsync(Tenant, artifact.Id, new(StorageConnectionId: targetConnId), null, default);
        var opId = startResult.Operation!.Id;

        await service.ExecuteRepairAsync(Tenant, artifact.Id, opId);

        Assert.Equal(0, fakeStore.CancelOperationCallCount);
        Assert.Equal(0, fakeProvider.DeleteCallCount); // Never delete target on lost lease!
    }

    public sealed class ScopeLifetimeTracker : IAsyncDisposable, IDisposable
    {
        public bool IsDisposed { get; private set; }
        public void Dispose() => IsDisposed = true;
        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeWorkerRepairStore : IReplicaRepairStore
    {
        public ReplicaRepairOperation? CurrentOperation;
        private readonly BackupArtifact? _artifact;
        private readonly BackupReplica? _source;
        private readonly Guid _targetConnId;
        public int CancelOperationCallCount;
        public bool ShouldRenewSucceed { get; set; } = true;
        public bool CancellationRequestedInDb { get; set; }
        public Action? OnRenewLease { get; set; }

        public FakeWorkerRepairStore(ReplicaRepairOperation op)
        {
            CurrentOperation = op;
        }

        public FakeWorkerRepairStore(BackupArtifact artifact, BackupReplica source, Guid targetConnId)
        {
            _artifact = artifact;
            _source = source;
            _targetConnId = targetConnId;
        }

        public void SimulateCrossNodeCancellation(Guid operationId)
        {
            CancellationRequestedInDb = true;
            CurrentOperation?.RequestCancellation(DateTimeOffset.UtcNow);
        }

        public Task<ReplicaRepairAdmissionResult> AdmitRepairAsync(TenantId tenantId, Guid artifactId, Guid? storageConnectionId, string? storageProvider, DateTimeOffset now, CancellationToken cancellationToken, Guid? excludeOperationId = null)
        {
            return Task.FromResult(new ReplicaRepairAdmissionResult(
                true,
                null,
                null,
                _artifact,
                new BackupDestination("sftp", _targetConnId),
                _source,
                [_source!]));
        }

        public Task<ReplicaRepairAdmissionResult> CreateOperationAsync(ReplicaRepairOperation operation, CancellationToken cancellationToken)
        {
            CurrentOperation = operation;
            return Task.FromResult(new ReplicaRepairAdmissionResult(true, null, null));
        }

        public Task<ReplicaRepairOperation?> ClaimDueRepairAsync(string leaseOwner, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken, TenantId? tenantId = null)
        {
            if (CurrentOperation is null || CurrentOperation.Status != ReplicaRepairStatus.Pending) return Task.FromResult<ReplicaRepairOperation?>(null);
            if (CurrentOperation.CancellationRequestedAt != null)
            {
                CurrentOperation.CancelPending(now);
                return Task.FromResult<ReplicaRepairOperation?>(null);
            }
            CurrentOperation.Claim(Guid.NewGuid(), leaseOwner, now, leaseDuration);
            return Task.FromResult<ReplicaRepairOperation?>(CurrentOperation);
        }

        public Task<ReplicaRepairOperation?> ClaimOperationAsync(TenantId tenantId, Guid operationId, Guid leaseToken, string leaseOwner, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken)
        {
            if (CurrentOperation is null) return Task.FromResult<ReplicaRepairOperation?>(null);
            if (CurrentOperation.Status == ReplicaRepairStatus.Pending)
            {
                if (CurrentOperation.CancellationRequestedAt != null)
                {
                    CurrentOperation.CancelPending(now);
                    return Task.FromResult<ReplicaRepairOperation?>(null);
                }
                CurrentOperation.Claim(leaseToken, leaseOwner, now, leaseDuration);
                return Task.FromResult<ReplicaRepairOperation?>(CurrentOperation);
            }
            return Task.FromResult<ReplicaRepairOperation?>(null);
        }

        public Task<ReplicaRepairLeaseRenewal> RenewLeaseAsync(TenantId tenantId, Guid operationId, Guid leaseToken, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken)
        {
            OnRenewLease?.Invoke();
            if (!ShouldRenewSucceed) return Task.FromResult(ReplicaRepairLeaseRenewal.Lost);
            var isCancelled = CancellationRequestedInDb || CurrentOperation?.CancellationRequestedAt != null;
            return Task.FromResult(ReplicaRepairLeaseRenewal.Active(isCancelled));
        }

        public Task<bool> RequestCancellationAsync(TenantId tenantId, Guid operationId, DateTimeOffset now, CancellationToken cancellationToken)
        {
            if (CurrentOperation is null || CurrentOperation.IsTerminal) return Task.FromResult(false);
            if (CurrentOperation.Status == ReplicaRepairStatus.Pending)
            {
                CurrentOperation.CancelPending(now);
                return Task.FromResult(true);
            }
            CurrentOperation.RequestCancellation(now);
            CancellationRequestedInDb = true;
            return Task.FromResult(true);
        }

        public Task<bool> CancelOperationAsync(TenantId tenantId, Guid operationId, Guid leaseToken, DateTimeOffset now, CancellationToken cancellationToken)
        {
            CancelOperationCallCount++;
            CurrentOperation?.Cancel(leaseToken, now);
            return Task.FromResult(true);
        }

        public Task<bool> CanCleanupTargetAsync(TenantId tenantId, Guid operationId, Guid leaseToken, string targetKey, DateTimeOffset now, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<bool> PinSourceAsync(TenantId tenantId, Guid operationId, Guid leaseToken, Guid sourceReplicaId, string targetKey, DateTimeOffset now, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<bool> UpdateProgressAsync(TenantId tenantId, Guid operationId, Guid leaseToken, long transferredBytes, long totalBytes, DateTimeOffset now, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<BackupReplica> CompleteOperationAsync(TenantId tenantId, Guid operationId, Guid leaseToken, BackupReplica newReplica, DateTimeOffset now, CancellationToken cancellationToken)
        {
            CurrentOperation?.Succeed(leaseToken, newReplica.Id, now);
            return Task.FromResult(newReplica);
        }

        public Task<bool> FailOperationAsync(TenantId tenantId, Guid operationId, Guid leaseToken, string failureCode, string? reason, DateTimeOffset now, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<bool> ScheduleRetryAsync(TenantId tenantId, Guid operationId, Guid leaseToken, string failureCode, string? reason, DateTimeOffset nextRetryAt, DateTimeOffset now, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task RecordRepairOrphanAsync(TenantId tenantId, Guid artifactId, Guid storageConnectionId, string storageProvider, string storageTarget, string key, long sizeBytes, string checksum, DateTimeOffset now, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<int> RecoverStaleRepairsAsync(DateTimeOffset now, TimeSpan initialRetryDelay, CancellationToken cancellationToken) => Task.FromResult(0);

        public Task<IReadOnlyList<ReplicaRepairOperation>> DiscoverEligibleRepairsAsync(DateTimeOffset now, int maxBatch, CancellationToken cancellationToken, TenantId? tenantId = null) => Task.FromResult<IReadOnlyList<ReplicaRepairOperation>>([]);

        public Task<int> GetActiveRepairsCountAsync(TenantId? tenantId, CancellationToken cancellationToken) => Task.FromResult(0);

        public Task<ReplicaRepairOperationView?> GetOperationAsync(TenantId tenantId, Guid artifactId, Guid operationId, CancellationToken cancellationToken)
        {
            if (CurrentOperation is null) return Task.FromResult<ReplicaRepairOperationView?>(null);
            var op = CurrentOperation;
            return Task.FromResult<ReplicaRepairOperationView?>(new ReplicaRepairOperationView(
                op.Id, op.ArtifactId, op.StorageConnectionId, op.StorageProvider, op.SourceReplicaId, op.TargetReplicaId,
                op.TargetKey, op.Status.ToString(), op.FailureCode, op.FailureReason, op.TransferredBytes, op.TotalBytes,
                op.CreatedAt, op.StartedAt, op.CompletedAt, op.CancellationRequestedAt, op.RequestedByUserId, op.AttemptCount,
                op.MaxAttempts, op.NextRetryAt, op.LastAttemptAt, op.LastFailureCode, op.IsExhausted));
        }

        public Task<IReadOnlyList<ReplicaRepairOperationView>> ListOperationsAsync(TenantId tenantId, Guid artifactId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ReplicaRepairOperationView>>([]);
    }

    private sealed class FakeTrackingStorageProvider : IStorageProvider
    {
        public StorageProviderId Id { get; } = new("fake");
        public StorageTopology Topology => StorageTopology.BackendRelayed;
        public ScopeLifetimeTracker? CurrentScopeTracker;
        public int PutCallCount;
        public int DeleteCallCount;
        public Func<CancellationToken, Task>? OnPutAsync;
        public Func<CancellationToken, Task<StorageVerificationResult>>? OnVerifyAsync;

        public StorageCapabilities Capabilities => new([StorageCapabilityIds.Read, StorageCapabilityIds.Delete]);

        public Task<Stream> OpenReadAsync(StorageObjectKey key, CancellationToken cancellationToken)
        {
            return Task.FromResult<Stream>(new MemoryStream(ArtifactData));
        }

        public async Task<StorageObjectMetadata> PutAsync(StorageObjectKey key, Stream content, StoragePutOptions options, IProgress<long>? progress, CancellationToken cancellationToken)
        {
            PutCallCount++;
            if (OnPutAsync is not null)
            {
                await OnPutAsync(cancellationToken);
            }
            else
            {
                await content.CopyToAsync(Stream.Null, cancellationToken);
            }
            return new StorageObjectMetadata(new StorageLocation(Id, key), Size, new ContentChecksum(ChecksumAlgorithm.Sha256, Checksum), DateTimeOffset.UtcNow);
        }

        public Task<StorageVerificationResult> VerifyAsync(StorageObjectKey key, StorageVerificationExpectation expectation, CancellationToken cancellationToken)
        {
            if (OnVerifyAsync is not null)
            {
                return OnVerifyAsync(cancellationToken);
            }
            return Task.FromResult(new StorageVerificationResult(new StorageLocation(Id, key), StorageVerificationStatus.Verified, StorageVerificationMethod.ReadBack, Size, new ContentChecksum(ChecksumAlgorithm.Sha256, Checksum)));
        }

        public Task<bool> DeleteAsync(StorageObjectKey key, CancellationToken cancellationToken)
        {
            DeleteCallCount++;
            return Task.FromResult(true);
        }

        public Task<bool> ExistsAsync(StorageObjectKey key, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<StorageObjectMetadata?> GetMetadataAsync(StorageObjectKey key, CancellationToken cancellationToken) => Task.FromResult<StorageObjectMetadata?>(null);
        public IAsyncEnumerable<StorageEntry> EnumerateAsync(StorageObjectKey? prefix, DateTimeOffset modifiedBefore, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeStorageResolver(IStorageProvider provider, IServiceProvider? sp = null) : IBackupStorageResolver
    {
        public IReadOnlyList<BackupManagedStorage> ManagedStorages => [];
        public IStorageProvider? Resolve(TenantId tenantId, string providerName, string target)
        {
            if (provider is FakeTrackingStorageProvider ftp && sp is not null)
            {
                ftp.CurrentScopeTracker = sp.GetService<ScopeLifetimeTracker>();
            }
            return provider;
        }
    }
}
