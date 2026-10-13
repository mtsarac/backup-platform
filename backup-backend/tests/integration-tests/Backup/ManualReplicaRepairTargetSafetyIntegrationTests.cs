using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Infrastructure.Backups;
using BackupPlatform.ManagedService.Quota;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackupPlatform.IntegrationTests.Backup;

public sealed partial class ManualReplicaRepairIntegrationTests
{
    [Fact]
    public async Task RepairKeepsLongExistingScopeWhenLeaseTargetExceeds512Characters()
    {
        var setup = await SetupAsync(verified: new HashSet<string> { "central", "google-drive" });
        var source = setup.Artifact.Replicas.Single(r => r.StorageProvider == "central");
        var prefix = $"{new string('p', 114)}/{new string('q', 114)}/{new string('r', 114)}/";
        var sourceKey = prefix + source.Key;
        Assert.InRange(sourceKey.Length, 1, 512);
        _ = new BackupPlatform.StorageProviders.Abstractions.StorageObjectKey(sourceKey);
        await using (var context = fixture.CreatePlatformContext())
            await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE backup_replicas SET \"Key\" = {sourceKey} WHERE \"Id\" = {source.Id}");
        var sourceProvider = (InMemoryStorageProvider)setup.Resolver.Resolve(setup.Agent.TenantId, "central", "central")!;
        sourceProvider.Storage[sourceKey] = Content;
        var started = await setup.RepairService.StartRepairAsync(setup.Agent.TenantId, setup.Artifact.Id, new(StorageConnectionId: setup.Sftp.Id), null, default);
        var result = await setup.RepairService.ExecuteRepairAsync(setup.Agent.TenantId, setup.Artifact.Id, started.Operation!.Id);
        Assert.Equal("Succeeded", result!.Status);
        Assert.InRange(result.TargetKey!.Length, 513, 1024);
        Assert.StartsWith(prefix, result.TargetKey);
        var destination = (InMemoryStorageProvider)setup.Resolver.Resolve(setup.Agent.TenantId, "sftp", setup.Sftp.Id.ToString("D"))!;
        Assert.Equal(Content, destination.Storage[result.TargetKey]);
        await using var stored = fixture.CreatePlatformContext();
        Assert.Equal(result.TargetKey, (await stored.ReplicaRepairOperations.AsNoTracking().SingleAsync(r => r.Id == result.Id)).TargetKey);
        var downgrade = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            stored.GetService<IMigrator>().MigrateAsync("20261013112445_RepairTargetQuarantine"));
        Assert.Equal(Npgsql.PostgresErrorCodes.RaiseException, downgrade.SqlState);
        Assert.Equal(result.TargetKey, (await stored.ReplicaRepairOperations.AsNoTracking().SingleAsync(r => r.Id == result.Id)).TargetKey);
    }

    [Fact]
    public async Task DelayedOldCleanupCannotDeleteTheTakeoverReplica()
    {
        var setup = await SetupAsync(verified: new HashSet<string> { "central", "google-drive" });
        var clock = new MutableClock();
        var service = new ReplicaRepairService(setup.RepairStore, setup.Resolver, clock, NullLogger<ReplicaRepairService>.Instance);
        var started = await service.StartRepairAsync(setup.Agent.TenantId, setup.Artifact.Id, new(StorageConnectionId: setup.Sftp.Id), null, default);
        var destination = (InMemoryStorageProvider)setup.Resolver.Resolve(setup.Agent.TenantId, "sftp", setup.Sftp.Id.ToString("D"))!;
        var deleting = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        destination.ShouldFailVerify = true;
        destination.BeforeDelete = async key =>
        {
            deleting.TrySetResult(key.Value);
            await releaseDelete.Task.WaitAsync(deadline.Token);
        };
        var oldExecution = service.ExecuteRepairAsync(setup.Agent.TenantId, setup.Artifact.Id, started.Operation!.Id, externalToken: deadline.Token);
        try
        {
            var oldKey = await deleting.Task.WaitAsync(deadline.Token);
            clock.Advance(TimeSpan.FromMinutes(11));
            await setup.RepairStore.RecoverStaleRepairsAsync(clock.GetUtcNow(), TimeSpan.Zero, deadline.Token);
            var replacement = await new PostgreSqlReplicaRepairStore(World.Options()).ClaimOperationAsync(
                setup.Agent.TenantId, started.Operation.Id, Guid.NewGuid(), "takeover", clock.GetUtcNow(), TimeSpan.FromMinutes(10), deadline.Token);
            Assert.NotNull(replacement);
            destination.ShouldFailVerify = false;
            var result = await service.ExecuteRepairAsync(setup.Agent.TenantId, setup.Artifact.Id, replacement.Id,
                externalToken: deadline.Token, claimedOperation: replacement);
            Assert.Equal("Succeeded", result!.Status);
            Assert.NotEqual(oldKey, result.TargetKey);
            Assert.Equal(Content, destination.Storage[result.TargetKey!]);
            await using var context = fixture.CreatePlatformContext();
            var oldTarget = await context.BackupReplicas.SingleAsync(r => r.TenantId == setup.Agent.TenantId && r.Key == oldKey);
            Assert.Equal(BackupReplicaStatus.DeletePending, oldTarget.Status);
            releaseDelete.TrySetResult();
            await oldExecution.WaitAsync(deadline.Token);
            Assert.False(destination.Storage.ContainsKey(oldKey));
            Assert.Equal(Content, destination.Storage[result.TargetKey!]);
            var published = await context.BackupReplicas.SingleAsync(r => r.Id == result.TargetReplicaId);
            Assert.Equal("Verified", published.VerificationState);
            Assert.Equal(BackupReplicaStatus.Available, published.Status);
            var durable = await context.ReplicaRepairOperations.AsNoTracking().SingleAsync(r => r.Id == replacement.Id);
            Assert.Equal(ReplicaRepairStatus.Succeeded, durable.Status);
            Assert.Equal(published.Id, durable.TargetReplicaId);
        }
        finally
        {
            releaseDelete.TrySetResult();
            await oldExecution.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task StaleRestartKeepsLegacyAndConflictingTargetsQuarantined()
    {
        var setup = await SetupAsync(verified: new HashSet<string> { "central", "google-drive" });
        var clock = new MutableClock();
        var source = setup.Artifact.Replicas.Single(r => r.StorageProvider == "central");
        var operation = new ReplicaRepairOperation(Guid.NewGuid(), setup.Agent.TenantId, setup.Artifact.Id, setup.Sftp.Id,
            "sftp", source.Id, clock.GetUtcNow().AddMinutes(-20), totalBytes: Content.Length);
        operation.Claim(Guid.NewGuid(), "crashed", clock.GetUtcNow().AddMinutes(-20), TimeSpan.FromMinutes(1));
        operation.SetTargetIntent(source.Id, source.Key);
        Assert.True((await setup.RepairStore.CreateOperationAsync(operation, default)).Allowed);
        var destination = (InMemoryStorageProvider)setup.Resolver.Resolve(setup.Agent.TenantId, "sftp", setup.Sftp.Id.ToString("D"))!;
        byte[] foreign = [42, 43, 44];
        destination.Storage[source.Key] = foreign;
        await setup.RepairStore.RecoverStaleRepairsAsync(clock.GetUtcNow(), TimeSpan.Zero, default);
        destination.BeforePut = key => destination.Storage[key.Value] = foreign;
        var service = new ReplicaRepairService(setup.RepairStore, setup.Resolver, clock, NullLogger<ReplicaRepairService>.Instance);
        var result = await service.ExecuteRepairAsync(setup.Agent.TenantId, setup.Artifact.Id, operation.Id);
        Assert.Equal("Failed", result!.Status);
        Assert.Equal(ReplicaRepairFailureCodes.TargetConflict, result.FailureCode);
        Assert.NotEqual(source.Key, result.TargetKey);
        await using var context = fixture.CreatePlatformContext();
        var conflicts = await context.BackupReplicas.Where(r => r.TenantId == setup.Agent.TenantId && r.StorageConnectionId == setup.Sftp.Id).ToListAsync();
        Assert.Equal(2, conflicts.Count);
        Assert.All(conflicts, r => Assert.Equal(BackupReplicaStatus.Quarantined, r.Status));
        Assert.Equal(foreign, destination.Storage[source.Key]);
        Assert.Equal(foreign, destination.Storage[result.TargetKey!]);
        Assert.Empty(destination.DeletedKeys);
    }

    [Fact]
    public async Task ActiveRepairProtectsItsTargetAndTerminalAttemptsPartialUploads()
    {
        var setup = await SetupAsync(verified: new HashSet<string> { "central", "google-drive" });
        var now = DateTimeOffset.UtcNow;
        var operation = new ReplicaRepairOperation(Guid.NewGuid(), setup.Agent.TenantId, setup.Artifact.Id, setup.Sftp.Id,
            "sftp", setup.Artifact.Replicas.First().Id, now, totalBytes: Content.Length);
        var token = Guid.NewGuid();
        operation.Claim(token, "writing", now, TimeSpan.FromMinutes(10));
        operation.SetTargetIntent(setup.Artifact.Replicas.First().Id, $"target-{operation.Id:N}.bak");
        Assert.True((await setup.RepairStore.CreateOperationAsync(operation, default)).Allowed);
        var retention = new PostgreSqlBackupRetentionStore(World.Options());
        var activity = (await retention.GetAttemptActivityAsync([setup.Artifact.AttemptId], default))[setup.Artifact.AttemptId];
        Assert.False(activity.AttemptIsLive);
        Assert.False(activity.RunIsActive);
        Assert.True(activity.RepairIsActive);
        Assert.Contains(operation.TargetKey!, await retention.FindReferencedKeysAsync("sftp", [operation.TargetKey!], default));
        await setup.RepairStore.FailOperationAsync(operation.TenantId, operation.Id, token, ReplicaRepairFailureCodes.InternalError, null, now, default);
        Assert.False((await retention.GetAttemptActivityAsync([setup.Artifact.AttemptId], default))[setup.Artifact.AttemptId].RepairIsActive);
        Assert.DoesNotContain(operation.TargetKey!, await retention.FindReferencedKeysAsync("sftp", [operation.TargetKey!], default));
    }

    [Fact]
    public async Task UnexpectedPutFailurePreservesWrittenTargetBeforeRetry()
    {
        var setup = await SetupAsync(verified: new HashSet<string> { "central", "google-drive" });
        var destination = (InMemoryStorageProvider)setup.Resolver.Resolve(setup.Agent.TenantId, "sftp", setup.Sftp.Id.ToString("D"))!;
        destination.BeforePut = key =>
        {
            destination.Storage[key.Value] = Content;
            throw new IOException("Response lost after publishing the target.");
        };
        destination.BeforeDelete = _ => throw new IOException("Cleanup connection unavailable.");
        var started = await setup.RepairService.StartRepairAsync(setup.Agent.TenantId, setup.Artifact.Id, new(StorageConnectionId: setup.Sftp.Id), null, default);
        var result = await setup.RepairService.ExecuteRepairAsync(setup.Agent.TenantId, setup.Artifact.Id, started.Operation!.Id);
        Assert.Equal("Pending", result!.Status);
        Assert.Equal(ReplicaRepairFailureCodes.InternalError, result.FailureCode);
        Assert.Equal(Content, destination.Storage[result.TargetKey!]);
        await using var context = fixture.CreatePlatformContext();
        var orphan = Assert.Single(await context.BackupReplicas.Where(r => r.TenantId == setup.Agent.TenantId && r.Key == result.TargetKey).ToListAsync());
        Assert.Equal(BackupReplicaStatus.DeletePending, orphan.Status);
        Assert.True(BackupReplicaStorage.Occupies(orphan.Status));
        Assert.Equal(Content.Length, orphan.SizeBytes);
    }

    [Fact]
    public async Task RepeatedOrphanRecordingKeepsOneDurableReplica()
    {
        var setup = await SetupAsync(QuotaAccountingMode.PhysicalReplicaBytes, new HashSet<string> { "central", "google-drive" });
        var key = $"orphan-{Guid.NewGuid():N}.bak";
        var now = DateTimeOffset.UtcNow;
        for (var attempt = 0; attempt < 2; attempt++)
            await setup.RepairStore.RecordRepairOrphanAsync(setup.Agent.TenantId, setup.Artifact.Id, setup.Sftp.Id, "sftp",
                setup.Sftp.Id.ToString("D"), key, Content.Length, Checksum, now, default);
        await using var context = fixture.CreatePlatformContext();
        var orphan = Assert.Single(await context.BackupReplicas.Where(r => r.TenantId == setup.Agent.TenantId && r.Key == key).ToListAsync());
        Assert.Equal(BackupReplicaStatus.DeletePending, orphan.Status);
        Assert.Equal(Content.Length, orphan.SizeBytes);
    }

    [Fact]
    public async Task ProviderTimeoutSchedulesRetryInsteadOfUserCancellation()
    {
        var setup = await SetupAsync(verified: new HashSet<string> { "central", "google-drive" });
        var destination = (InMemoryStorageProvider)setup.Resolver.Resolve(setup.Agent.TenantId, "sftp", setup.Sftp.Id.ToString("D"))!;
        destination.BeforePut = _ => throw new OperationCanceledException("Provider transfer deadline exceeded.", new CancellationToken(true));
        var started = await setup.RepairService.StartRepairAsync(setup.Agent.TenantId, setup.Artifact.Id, new(StorageConnectionId: setup.Sftp.Id), null, default);
        var result = await setup.RepairService.ExecuteRepairAsync(setup.Agent.TenantId, setup.Artifact.Id, started.Operation!.Id);
        Assert.Equal("Pending", result!.Status);
        Assert.Equal(ReplicaRepairFailureCodes.OperationTimedOut, result.FailureCode);
        Assert.Null(result.CancellationRequestedAt);
        Assert.Equal(1, result.AttemptCount);
        Assert.NotNull(result.NextRetryAt);
    }

    [Fact]
    public async Task UnreadableExistingTargetIsNotDeletedWithoutWriteOwnership()
    {
        var setup = await SetupAsync(verified: new HashSet<string> { "central", "google-drive" });
        var destination = (InMemoryStorageProvider)setup.Resolver.Resolve(setup.Agent.TenantId, "sftp", setup.Sftp.Id.ToString("D"))!;
        byte[] foreign = [42, 43, 44];
        destination.BeforePut = key =>
        {
            destination.Storage[key.Value] = foreign;
            throw new BackupPlatform.StorageProviders.Abstractions.StorageException(
                BackupPlatform.StorageProviders.Abstractions.StorageFailureKind.Io, "Existing target could not be verified before upload.");
        };
        var started = await setup.RepairService.StartRepairAsync(setup.Agent.TenantId, setup.Artifact.Id, new(StorageConnectionId: setup.Sftp.Id), null, default);
        var result = await setup.RepairService.ExecuteRepairAsync(setup.Agent.TenantId, setup.Artifact.Id, started.Operation!.Id);
        Assert.Equal("Pending", result!.Status);
        Assert.Equal(ReplicaRepairFailureCodes.StorageWriteFailed, result.FailureCode);
        Assert.Empty(destination.DeletedKeys);
        Assert.Equal(foreign, destination.Storage[result.TargetKey!]);
        await using var context = fixture.CreatePlatformContext();
        var uncertain = Assert.Single(await context.BackupReplicas.Where(r => r.TenantId == setup.Agent.TenantId && r.Key == result.TargetKey).ToListAsync());
        Assert.Equal(BackupReplicaStatus.Quarantined, uncertain.Status);
    }

    [Fact]
    public async Task ProviderFailureKeepsItsExactRemainingPartialKeyDurable()
    {
        var setup = await SetupAsync(QuotaAccountingMode.PhysicalReplicaBytes, new HashSet<string> { "central", "google-drive" });
        var destination = (InMemoryStorageProvider)setup.Resolver.Resolve(setup.Agent.TenantId, "sftp", setup.Sftp.Id.ToString("D"))!;
        string? partialKey = null;
        destination.BeforePut = key =>
        {
            var slash = key.Value.LastIndexOf('/');
            partialKey = $"{key.Value[..(slash + 1)]}.{key.Value[(slash + 1)..]}.partial";
            destination.Storage[partialKey] = Content;
            throw new BackupPlatform.StorageProviders.Abstractions.StorageException(
                BackupPlatform.StorageProviders.Abstractions.StorageFailureKind.Io, "Unpublished upload could not be removed.",
                objectMayRemain: true, remainingObjectKey: new(partialKey));
        };
        destination.BeforeDelete = _ => throw new IOException("Cleanup connection unavailable.");
        var started = await setup.RepairService.StartRepairAsync(setup.Agent.TenantId, setup.Artifact.Id, new(StorageConnectionId: setup.Sftp.Id), null, default);
        var result = await setup.RepairService.ExecuteRepairAsync(setup.Agent.TenantId, setup.Artifact.Id, started.Operation!.Id);
        Assert.Equal("Pending", result!.Status);
        Assert.Equal(ReplicaRepairFailureCodes.StorageWriteFailed, result.FailureCode);
        Assert.NotNull(partialKey);
        Assert.Equal(Content, destination.Storage[partialKey]);
        await using var context = fixture.CreatePlatformContext();
        var orphan = Assert.Single(await context.BackupReplicas.Where(r => r.TenantId == setup.Agent.TenantId && r.Key == partialKey).ToListAsync());
        Assert.Equal(BackupReplicaStatus.DeletePending, orphan.Status);
        Assert.True(BackupReplicaStorage.Occupies(orphan.Status));
    }

    [Fact]
    public async Task LateOldPutCannotBecomeUnaccountedAfterRecoveryAndRetention()
    {
        var setup = await SetupAsync(QuotaAccountingMode.PhysicalReplicaBytes, new HashSet<string> { "central", "google-drive" });
        var clock = new MutableClock();
        var service = new ReplicaRepairService(setup.RepairStore, setup.Resolver, clock, NullLogger<ReplicaRepairService>.Instance);
        var started = await service.StartRepairAsync(setup.Agent.TenantId, setup.Artifact.Id, new(StorageConnectionId: setup.Sftp.Id), null, default);
        var destination = (InMemoryStorageProvider)setup.Resolver.Resolve(setup.Agent.TenantId, "sftp", setup.Sftp.Id.ToString("D"))!;
        var writing = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePut = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        destination.BeforePutAsync = async key =>
        {
            writing.TrySetResult(key.Value);
            await releasePut.Task.WaitAsync(deadline.Token);
        };
        var oldExecution = service.ExecuteRepairAsync(setup.Agent.TenantId, setup.Artifact.Id, started.Operation!.Id, externalToken: deadline.Token);
        try
        {
            var oldKey = await writing.Task.WaitAsync(deadline.Token);
            clock.Advance(TimeSpan.FromMinutes(11));
            await setup.RepairStore.RecoverStaleRepairsAsync(clock.GetUtcNow(), TimeSpan.Zero, deadline.Token);
            var retention = new PostgreSqlBackupRetentionStore(World.Options());
            var claims = await retention.ClaimDueDeletionsAsync("old-target-cleanup", clock.GetUtcNow(), TimeSpan.FromMinutes(1), 500, deadline.Token);
            foreach (var claim in claims.Where(c => c.Key == oldKey))
            {
                await destination.DeleteAsync(new(claim.Key), deadline.Token);
                Assert.True(await retention.CompleteDeletionAsync(claim.ReplicaId, claim.LeaseToken, clock.GetUtcNow(), deadline.Token));
            }
            var replacement = await setup.RepairStore.ClaimOperationAsync(setup.Agent.TenantId, started.Operation.Id, Guid.NewGuid(),
                "takeover", clock.GetUtcNow(), TimeSpan.FromMinutes(10), deadline.Token);
            Assert.NotNull(replacement);
            destination.BeforePutAsync = null;
            var result = await service.ExecuteRepairAsync(setup.Agent.TenantId, setup.Artifact.Id, replacement.Id,
                externalToken: deadline.Token, claimedOperation: replacement);
            Assert.Equal("Succeeded", result!.Status);
            releasePut.TrySetResult();
            await oldExecution.WaitAsync(deadline.Token);
            Assert.Equal(Content, destination.Storage[result.TargetKey!]);
            await using var context = fixture.CreatePlatformContext();
            var oldTarget = await context.BackupReplicas.SingleAsync(r => r.TenantId == setup.Agent.TenantId && r.Key == oldKey);
            Assert.True(!destination.Storage.ContainsKey(oldKey) || BackupReplicaStorage.Occupies(oldTarget.Status),
                "A late old upload must remain accounted until its absence is final.");
        }
        finally
        {
            releasePut.TrySetResult();
            await oldExecution.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Theory]
    [InlineData(1, false, "Failed", 0)]
    [InlineData(3, false, "Pending", 8)]
    [InlineData(3, true, "Cancelled", 0)]
    public async Task StaleTargetRecoveryKeepsAccountingAcrossRetryExhaustionAndCancellation(
        int maxAttempts, bool cancelled, string expectedStatus, long expectedReserved)
    {
        var setup = await SetupAsync(QuotaAccountingMode.PhysicalReplicaBytes, new HashSet<string> { "central", "google-drive" });
        var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var source = setup.Artifact.Replicas.Single(r => r.StorageProvider == "central");
        var token = Guid.NewGuid();
        var operation = new ReplicaRepairOperation(Guid.NewGuid(), setup.Agent.TenantId, setup.Artifact.Id, setup.Sftp.Id, "sftp",
            source.Id, now.AddMinutes(-20), totalBytes: Content.Length, maxAttempts: maxAttempts);
        operation.Claim(token, "dead-worker", now.AddMinutes(-20), TimeSpan.FromMinutes(1));
        operation.SetTargetIntent(source.Id, BackupPlatform.Contracts.Backups.ManagedBackupLayout.RepairFileName(operation.Id, token));
        if (cancelled) operation.RequestCancellation(now.AddMinutes(-19));
        Assert.True((await setup.RepairStore.CreateOperationAsync(operation, default)).Allowed);
        Assert.Equal(1, await setup.RepairStore.RecoverStaleRepairsAsync(now, TimeSpan.FromSeconds(10), default));
        Assert.Equal(0, await setup.RepairStore.RecoverStaleRepairsAsync(now, TimeSpan.FromSeconds(10), default));
        var result = await setup.RepairStore.GetOperationAsync(setup.Agent.TenantId, setup.Artifact.Id, operation.Id, default);
        Assert.Equal(expectedStatus, result!.Status);
        Assert.Equal(1, result.AttemptCount);
        Assert.Equal(maxAttempts, result.MaxAttempts);
        var usage = await World.StoredUsageAsync(setup.Agent.TenantId);
        Assert.Equal(Content.Length * 3L, usage!.UsedBytes);
        Assert.Equal(expectedReserved, usage.ReservedBytes);
        await using var context = fixture.CreatePlatformContext();
        var target = Assert.Single(await context.BackupReplicas.Where(r => r.TenantId == setup.Agent.TenantId && r.Key == operation.TargetKey).ToListAsync());
        Assert.True(BackupReplicaStorage.Occupies(target.Status));
        Assert.False(target.IsRestorable(now));
    }

    [Fact]
    public async Task ConflictProjectionFailureRollsBackQuarantineAndOperationFailureTogether()
    {
        var setup = await SetupAsync(QuotaAccountingMode.PhysicalReplicaBytes, new HashSet<string> { "central", "google-drive" });
        var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var started = await setup.RepairService.StartRepairAsync(setup.Agent.TenantId, setup.Artifact.Id, new(StorageConnectionId: setup.Sftp.Id), null, default);
        var operation = await setup.RepairStore.ClaimOperationAsync(setup.Agent.TenantId, started.Operation!.Id, Guid.NewGuid(), "conflict",
            now, TimeSpan.FromMinutes(10), default);
        var source = setup.Artifact.Replicas.Single(r => r.StorageProvider == "central");
        var key = BackupPlatform.Contracts.Backups.ManagedBackupLayout.RepairFileName(operation!.Id, operation.LeaseToken!.Value);
        Assert.True(await setup.RepairStore.PinSourceAsync(operation.TenantId, operation.Id, operation.LeaseToken.Value, source.Id, key, now, default));
        var before = await World.StoredUsageAsync(setup.Agent.TenantId);
        var fault = new ConflictProjectionFault();
        var options = new DbContextOptionsBuilder<BackupPlatform.Infrastructure.Persistence.PlatformDbContext>(World.Options()).AddInterceptors(fault).Options;
        await Assert.ThrowsAsync<IOException>(() => new PostgreSqlReplicaRepairStore(options)
            .FailTargetConflictAsync(operation.TenantId, operation.Id, operation.LeaseToken.Value, now, default));
        Assert.True(fault.Triggered);
        await using (var context = fixture.CreatePlatformContext())
        {
            Assert.False(await context.BackupReplicas.AnyAsync(r => r.TenantId == operation.TenantId && r.Key == key));
            Assert.Equal(ReplicaRepairStatus.InProgress, (await context.ReplicaRepairOperations.SingleAsync(r => r.Id == operation.Id)).Status);
        }
        var after = await World.StoredUsageAsync(setup.Agent.TenantId);
        Assert.Equal((before!.UsedBytes, before.ReservedBytes), (after!.UsedBytes, after.ReservedBytes));
        Assert.True(await setup.RepairStore.FailTargetConflictAsync(operation.TenantId, operation.Id, operation.LeaseToken.Value, now, default));
        Assert.False(await setup.RepairStore.FailTargetConflictAsync(operation.TenantId, operation.Id, operation.LeaseToken.Value, now, default));
        await using var completed = fixture.CreatePlatformContext();
        var quarantine = Assert.Single(await completed.BackupReplicas.Where(r => r.TenantId == operation.TenantId && r.Key == key).ToListAsync());
        Assert.Equal(BackupReplicaStatus.Quarantined, quarantine.Status);
        var failed = await setup.RepairStore.GetOperationAsync(operation.TenantId, operation.ArtifactId, operation.Id, default);
        Assert.Equal("Failed", failed!.Status);
        Assert.Equal(ReplicaRepairFailureCodes.TargetConflict, failed.FailureCode);
    }

    private sealed class ConflictProjectionFault : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public bool Triggered { get; private set; }
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> NonQueryExecutingAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("UPDATE tenant_storage_usage", StringComparison.Ordinal))
            {
                Triggered = true;
                throw new IOException("Usage projection write unavailable after the conflict rows were saved.");
            }
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
