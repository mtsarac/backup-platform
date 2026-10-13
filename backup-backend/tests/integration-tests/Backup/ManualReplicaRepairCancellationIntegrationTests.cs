using System.Collections.Concurrent;
using System.Data.Common;
using System.Reflection;
using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Backups;
using BackupPlatform.Infrastructure.Persistence;
using BackupPlatform.StorageProviders.Abstractions;
using BackupPlatform.ManagedService.Quota;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackupPlatform.IntegrationTests.Backup;

public sealed partial class ManualReplicaRepairIntegrationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RepairCancellationRejectsWrongTenantOrArtifactWithoutStoppingRunningTransfer(bool wrongTenant)
    {
        var setup = await SetupAsync(verified: new HashSet<string> { "central" });
        var tenantId = setup.Agent.TenantId;
        var started = await setup.RepairService.StartRepairAsync(tenantId, setup.Artifact.Id, new(StorageConnectionId: setup.Sftp.Id), null, default);
        Assert.True(started.Success);
        var destination = BlockRepairDestination(setup);
        var service = CancellationService(setup, destination);
        using var shutdown = new CancellationTokenSource();
        var execution = service.ExecuteRepairAsync(tenantId, setup.Artifact.Id, started.Operation!.Id, externalToken: shutdown.Token);
        try
        {
            await destination.PutStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.False(await service.CancelRepairAsync(
                wrongTenant ? TenantId.New() : tenantId,
                wrongTenant ? setup.Artifact.Id : Guid.NewGuid(), started.Operation.Id, default));

            Assert.False(destination.TransferToken.IsCancellationRequested);
            Assert.False(execution.IsCompleted);
            var running = await setup.RepairStore.GetOperationAsync(tenantId, setup.Artifact.Id, started.Operation.Id, default);
            Assert.Equal("InProgress", running!.Status);
            Assert.Null(running.CancellationRequestedAt);

            destination.ResumePut.TrySetResult();
            var completed = await execution.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal("Succeeded", completed!.Status);
        }
        finally
        {
            await StopCancellationTestAsync(started.Operation!.Id, shutdown, execution);
        }
    }

    [Fact]
    public async Task AuthorizedRepairCancellationPersistsIntentBeforeStoppingRunningTransfer()
    {
        var setup = await SetupAsync(verified: new HashSet<string> { "central" });
        var tenantId = setup.Agent.TenantId;
        var started = await setup.RepairService.StartRepairAsync(tenantId, setup.Artifact.Id, new(StorageConnectionId: setup.Sftp.Id), null, default);
        Assert.True(started.Success);
        var destination = BlockRepairDestination(setup);
        var persistedBeforeStop = false;
        destination.OnCancelled = async () =>
        {
            await using var context = fixture.CreatePlatformContext();
            var operation = await context.ReplicaRepairOperations.AsNoTracking().SingleAsync(op => op.Id == started.Operation!.Id);
            persistedBeforeStop = operation.CancellationRequestedAt is not null;
        };
        var service = CancellationService(setup, destination);
        var gate = new RepairRowLockGate();
        var options = new DbContextOptionsBuilder<PlatformDbContext>(World.Options()).AddInterceptors(gate).Options;
        var cancellingService = new ReplicaRepairService(new PostgreSqlReplicaRepairStore(options), setup.Resolver, TimeProvider.System, NullLogger<ReplicaRepairService>.Instance);
        Task<bool>? cancellation = null;
        using var shutdown = new CancellationTokenSource();
        var execution = service.ExecuteRepairAsync(tenantId, setup.Artifact.Id, started.Operation!.Id, externalToken: shutdown.Token);
        try
        {
            await destination.PutStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
            cancellation = cancellingService.CancelRepairAsync(tenantId, setup.Artifact.Id, started.Operation.Id, default);
            await gate.RowLocked.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.False(destination.TransferToken.IsCancellationRequested);
            Assert.False(cancellation.IsCompleted);
            gate.Release.TrySetResult();
            Assert.True(await cancellation.WaitAsync(TimeSpan.FromSeconds(15)));
            var cancelled = await execution.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(destination.TransferToken.IsCancellationRequested);
            Assert.True(persistedBeforeStop);
            Assert.Equal("Cancelled", cancelled!.Status);
            Assert.NotNull(cancelled.CancellationRequestedAt);
            Assert.False(await service.CancelRepairAsync(tenantId, setup.Artifact.Id, started.Operation.Id, default));
        }
        finally
        {
            gate.Release.TrySetResult();
            try
            {
                if (cancellation is not null) await cancellation.WaitAsync(TimeSpan.FromSeconds(15));
            }
            finally
            {
                await StopCancellationTestAsync(started.Operation!.Id, shutdown, execution);
            }
        }
    }

    [Theory]
    [InlineData(ReplicaRepairStatus.Cancelled)]
    [InlineData(ReplicaRepairStatus.Failed)]
    [InlineData(ReplicaRepairStatus.Succeeded)]
    public async Task TerminalRepairCancellationHasNoLocalEffectEvenWhileExecutorIsRegistered(ReplicaRepairStatus status)
    {
        var setup = await SetupAsync(verified: new HashSet<string> { "central" });
        var tenantId = setup.Agent.TenantId;
        var started = await setup.RepairService.StartRepairAsync(tenantId, setup.Artifact.Id, new(StorageConnectionId: setup.Sftp.Id), null, default);
        Assert.True(started.Success);
        var destination = BlockRepairDestination(setup);
        var service = CancellationService(setup, destination);
        using var shutdown = new CancellationTokenSource();
        var execution = service.ExecuteRepairAsync(tenantId, setup.Artifact.Id, started.Operation!.Id, externalToken: shutdown.Token);
        try
        {
            await destination.PutStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await using (var context = fixture.CreatePlatformContext())
            {
                var operation = await context.ReplicaRepairOperations.SingleAsync(op => op.Id == started.Operation.Id);
                var now = DateTimeOffset.UtcNow;
                switch (status)
                {
                    case ReplicaRepairStatus.Cancelled: operation.Cancel(operation.LeaseToken!.Value, now); break;
                    case ReplicaRepairStatus.Failed: operation.Fail(operation.LeaseToken!.Value, ReplicaRepairFailureCodes.InternalError, "Finished by another executor.", now); break;
                    case ReplicaRepairStatus.Succeeded: operation.Succeed(operation.LeaseToken!.Value, setup.Artifact.Replicas.Single().Id, now); break;
                    default: throw new ArgumentOutOfRangeException(nameof(status));
                }
                await context.SaveChangesAsync();
            }

            Assert.False(await service.CancelRepairAsync(tenantId, setup.Artifact.Id, started.Operation.Id, default));
            Assert.False(await service.CancelRepairAsync(tenantId, setup.Artifact.Id, Guid.NewGuid(), default));
            Assert.False(destination.TransferToken.IsCancellationRequested);
            Assert.False(execution.IsCompleted);
            var terminal = await setup.RepairStore.GetOperationAsync(tenantId, setup.Artifact.Id, started.Operation.Id, default);
            Assert.Equal(status.ToString(), terminal!.Status);
            Assert.Null(terminal.CancellationRequestedAt);
        }
        finally
        {
            await StopCancellationTestAsync(started.Operation!.Id, shutdown, execution);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RepairCancellationAndClaimSerializeWithoutLosingCancellationIntent(bool cancellationFirst)
    {
        var setup = await SetupAsync(verified: new HashSet<string> { "central" });
        var tenantId = setup.Agent.TenantId;
        var started = await setup.RepairService.StartRepairAsync(tenantId, setup.Artifact.Id, new(StorageConnectionId: setup.Sftp.Id), null, default);
        Assert.True(started.Success);
        var gate = new RepairRowLockGate();
        var options = new DbContextOptionsBuilder<PlatformDbContext>(World.Options()).AddInterceptors(gate).Options;
        var heldStore = new PostgreSqlReplicaRepairStore(options);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task<bool>? cancelling = null;
        Task<ReplicaRepairOperation?>? claiming = null;
        var now = DateTimeOffset.UtcNow;
        try
        {
            if (cancellationFirst)
                cancelling = heldStore.RequestCancellationAsync(tenantId, setup.Artifact.Id, started.Operation!.Id, now, deadline.Token);
            else
                claiming = heldStore.ClaimOperationAsync(tenantId, started.Operation!.Id, Guid.NewGuid(), "held-claim", now, TimeSpan.FromMinutes(10), deadline.Token);

            await gate.RowLocked.Task.WaitAsync(deadline.Token);
            cancelling ??= setup.RepairStore.RequestCancellationAsync(tenantId, setup.Artifact.Id, started.Operation!.Id, now, deadline.Token);
            claiming ??= setup.RepairStore.ClaimOperationAsync(tenantId, started.Operation!.Id, Guid.NewGuid(), "competing-claim", now, TimeSpan.FromMinutes(10), deadline.Token);
            gate.Release.TrySetResult();
            Assert.True(await cancelling);
            var claimed = await claiming;
            var operation = await setup.RepairStore.GetOperationAsync(tenantId, setup.Artifact.Id, started.Operation!.Id, default);
            if (cancellationFirst)
            {
                Assert.Null(claimed);
                Assert.Equal("Cancelled", operation!.Status);
                Assert.Equal(0, operation.AttemptCount);
            }
            else
            {
                Assert.NotNull(claimed);
                Assert.Equal("InProgress", operation!.Status);
                Assert.NotNull(operation.CancellationRequestedAt);
                Assert.Equal(1, operation.AttemptCount);
                Assert.True(await setup.RepairStore.CancelOperationAsync(tenantId, operation.Id, claimed!.LeaseToken!.Value, DateTimeOffset.UtcNow, default));
            }
        }
        finally
        {
            gate.Release.TrySetResult();
            try
            {
                await Task.WhenAll((Task?)cancelling ?? Task.CompletedTask, (Task?)claiming ?? Task.CompletedTask);
            }
            finally
            {
                await EndCancellationTestAsync(started.Operation!.Id);
            }
        }
    }

    [Fact]
    public async Task ReadmissionExceptionSchedulesRetryAndRemovesLocalCancellationRegistration()
    {
        var setup = await SetupAsync(verified: new HashSet<string> { "central" });
        var tenantId = setup.Agent.TenantId;
        var started = await setup.RepairService.StartRepairAsync(tenantId, setup.Artifact.Id, new(StorageConnectionId: setup.Sftp.Id), null, default);
        Assert.True(started.Success);
        var claimed = await setup.RepairStore.ClaimOperationAsync(tenantId, started.Operation!.Id, Guid.NewGuid(), "readmission-fault", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(10), default);
        Assert.NotNull(claimed);
        var fault = new RepairAdmissionFault();
        var options = new DbContextOptionsBuilder<PlatformDbContext>(World.Options()).AddInterceptors(fault).Options;
        var store = new PostgreSqlReplicaRepairStore(options);
        var service = new ReplicaRepairService(store, setup.Resolver, TimeProvider.System, NullLogger<ReplicaRepairService>.Instance);
        var registrations = (ConcurrentDictionary<Guid, CancellationTokenSource>)typeof(ReplicaRepairService)
            .GetField("_activeLocalCts", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

        try
        {
            var result = await service.ExecuteRepairAsync(tenantId, setup.Artifact.Id, claimed!.Id, claimedOperation: claimed);

            Assert.True(fault.Triggered);
            Assert.False(registrations.ContainsKey(claimed.Id));
            Assert.Equal("Pending", result!.Status);
            Assert.Equal(ReplicaRepairFailureCodes.InternalError, result.FailureCode);
            Assert.NotNull(result.NextRetryAt);
            Assert.True(await service.CancelRepairAsync(tenantId, setup.Artifact.Id, claimed.Id, default));
        }
        finally
        {
            if (registrations.TryRemove(claimed!.Id, out var leaked)) leaked.Dispose();
            await EndCancellationTestAsync(claimed.Id);
        }
    }

    [Fact]
    public async Task QuotaPolicyReadmissionAndCancellationDoNotInvertOperationAndTenantLocks()
    {
        var setup = await SetupAsync(QuotaAccountingMode.PhysicalReplicaBytes,
            new HashSet<string> { "central", "google-drive" });
        var tenantId = setup.Agent.TenantId;
        var now = DateTimeOffset.UtcNow;
        var started = await setup.RepairService.StartRepairAsync(tenantId, setup.Artifact.Id, new(StorageConnectionId: setup.Sftp.Id), null, default);
        var claimed = await setup.RepairStore.ClaimOperationAsync(tenantId, started.Operation!.Id, Guid.NewGuid(),
            "readmission-policy-change", now, TimeSpan.FromMinutes(10), default);
        Assert.NotNull(claimed);
        var policy = await BackupPlatform.IntegrationTests.Quota.AdmissionTestKit.CreatePolicyAsync(World,
            QuotaEnforcementMode.WarnOnly, QuotaAccountingMode.LogicalArtifactBytes, QuotaScopeRule.All);
        await World.AssignAsync(tenantId, await World.PlanAsync(1000, policy));
        var gate = new RepairRowLockGate();
        var cancellationOptions = new DbContextOptionsBuilder<PlatformDbContext>(World.Options()).AddInterceptors(gate).Options;
        var name = $"repair-policy-readmission-{Guid.NewGuid():N}";
        var connectionString = new Npgsql.NpgsqlConnectionStringBuilder(fixture.ConnectionString) { ApplicationName = name }.ConnectionString;
        var readmissionOptions = new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(connectionString).Options;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var cancelling = new PostgreSqlReplicaRepairStore(cancellationOptions)
            .RequestCancellationAsync(tenantId, setup.Artifact.Id, claimed!.Id, now, deadline.Token);
        Task<ReplicaRepairAdmissionResult>? readmitting = null;
        try
        {
            await gate.RowLocked.Task.WaitAsync(deadline.Token);
            readmitting = new PostgreSqlReplicaRepairStore(readmissionOptions).AdmitRepairAsync(tenantId, setup.Artifact.Id,
                setup.Sftp.Id, null, now, deadline.Token, excludeOperationId: claimed.Id);
            await WaitForDatabaseLockAsync(name, deadline.Token);
            gate.Release.TrySetResult();
            Assert.True(await cancelling.WaitAsync(deadline.Token));
            await readmitting.WaitAsync(deadline.Token);
            var operation = await setup.RepairStore.GetOperationAsync(tenantId, setup.Artifact.Id, claimed.Id, default);
            Assert.NotNull(operation!.CancellationRequestedAt);
            Assert.Equal(1, operation.AttemptCount);
        }
        finally
        {
            gate.Release.TrySetResult();
            try
            {
                await Task.WhenAll(cancelling, (Task?)readmitting ?? Task.CompletedTask);
            }
            finally
            {
                await EndCancellationTestAsync(claimed.Id);
            }
        }
    }

    private async Task StopCancellationTestAsync(Guid operationId, CancellationTokenSource shutdown, Task<ReplicaRepairOperationView?> execution)
    {
        shutdown.Cancel();
        try
        {
            await execution.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            await EndCancellationTestAsync(operationId);
        }
    }

    private async Task EndCancellationTestAsync(Guid operationId)
    {
        await using var context = fixture.CreatePlatformContext();
        var operation = await context.ReplicaRepairOperations.SingleAsync(op => op.Id == operationId);
        if (operation.IsTerminal) return;
        operation.CancelStale(DateTimeOffset.UtcNow);
        await context.SaveChangesAsync();
    }

    private static BlockingRepairDestination BlockRepairDestination(MultiSetup setup) =>
        new(setup.Resolver.Resolve(setup.Agent.TenantId, "sftp", setup.Sftp.Id.ToString("D"))!);

    private static ReplicaRepairService CancellationService(MultiSetup setup, BlockingRepairDestination destination) =>
        new(setup.RepairStore, new RepairCancellationResolver(setup.Resolver, setup.Sftp.Id.ToString("D"), destination),
            TimeProvider.System, NullLogger<ReplicaRepairService>.Instance);

    private sealed class RepairCancellationResolver(FakeResolver inner, string target, IStorageProvider destination) : IBackupStorageResolver
    {
        public IReadOnlyList<BackupManagedStorage> ManagedStorages => inner.ManagedStorages;
        public IStorageProvider? Resolve(TenantId tenantId, string storageProvider, string storageTarget) =>
            storageProvider == "sftp" && storageTarget == target ? destination : inner.Resolve(tenantId, storageProvider, storageTarget);
    }

    private sealed class BlockingRepairDestination(IStorageProvider inner) : IStorageProvider
    {
        public StorageProviderId Id => inner.Id;
        public StorageTopology Topology => inner.Topology;
        public StorageCapabilities Capabilities => inner.Capabilities;
        public TaskCompletionSource PutStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ResumePut { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken TransferToken { get; private set; }
        public Func<Task>? OnCancelled { get; set; }

        public async Task<StorageObjectMetadata> PutAsync(StorageObjectKey key, Stream content, StoragePutOptions options, IProgress<long>? progress, CancellationToken cancellationToken)
        {
            TransferToken = cancellationToken;
            try
            {
                await using var buffer = new MemoryStream();
                var prefix = new byte[1];
                await content.ReadExactlyAsync(prefix, cancellationToken);
                buffer.Write(prefix);
                PutStarted.TrySetResult();
                await ResumePut.Task.WaitAsync(cancellationToken);
                await content.CopyToAsync(buffer, cancellationToken);
                buffer.Position = 0;
                return await inner.PutAsync(key, buffer, options, progress, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                if (OnCancelled is not null) await OnCancelled();
                throw;
            }
        }

        public Task<Stream> OpenReadAsync(StorageObjectKey key, CancellationToken cancellationToken) => inner.OpenReadAsync(key, cancellationToken);
        public Task<StorageVerificationResult> VerifyAsync(StorageObjectKey key, StorageVerificationExpectation expected, CancellationToken cancellationToken) => inner.VerifyAsync(key, expected, cancellationToken);
        public Task<bool> DeleteAsync(StorageObjectKey key, CancellationToken cancellationToken) => inner.DeleteAsync(key, cancellationToken);
        public Task<bool> ExistsAsync(StorageObjectKey key, CancellationToken cancellationToken) => inner.ExistsAsync(key, cancellationToken);
        public Task<StorageObjectMetadata?> GetMetadataAsync(StorageObjectKey key, CancellationToken cancellationToken) => inner.GetMetadataAsync(key, cancellationToken);
        public IAsyncEnumerable<StorageEntry> EnumerateAsync(StorageObjectKey? prefix, DateTimeOffset modifiedBefore, CancellationToken cancellationToken) => inner.EnumerateAsync(prefix, modifiedBefore, cancellationToken);
    }

    private sealed class RepairRowLockGate : DbCommandInterceptor
    {
        public TaskCompletionSource RowLocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("replica_repair_operations", StringComparison.Ordinal) && command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal))
            {
                RowLocked.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    private sealed class RepairAdmissionFault : DbCommandInterceptor
    {
        public bool Triggered { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!Triggered && command.CommandText.Contains("backup_artifacts", StringComparison.Ordinal))
            {
                Triggered = true;
                throw new InvalidOperationException("Injected re-admission database failure.");
            }
            return ValueTask.FromResult(result);
        }
    }
}
