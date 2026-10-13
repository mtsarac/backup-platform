using BackupPlatform.Application.Backups;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Domain.Backups;
using static BackupPlatform.IntegrationTests.AgentReplicaDeletionTestKit;

namespace BackupPlatform.IntegrationTests;

public sealed class AgentReplicaDeletionServiceTests
{
    [Fact]
    public async Task OfflineDispatchIsDurableVisibleAndNeverPerformsBackendPhysicalIo()
    {
        await RetentionServiceWorld.RunAsync(async world =>
        {
            var seed = await SeedAsync(world, policy: RetentionKit.Policy(keepFor: TimeSpan.FromMinutes(59), grace: TimeSpan.FromMinutes(1)));
            var clock = new MutableClock(Due.AddMinutes(-1));
            var marked = await world.Service("expiry", clock, options: Options).RunCycleAsync(default);
            Assert.Equal(1, marked.ReplicasMarkedForDeletion);
            var pending = await world.ReplicaAsync(seed.Backup.ReplicaId);
            Assert.Equal(BackupReplicaStatus.DeletePending, pending.Status);
            Assert.Equal(BackupDeleteReason.RetentionExpired, pending.DeleteReason);
            Assert.Equal(Due, pending.DeleteAfter);
            RetentionServiceWorld.SetClock(clock, Due);
            Assert.Null(await Service(world, clock).ClaimAsync(AgentReplicaDeletionTestKit.Identity(seed.Agent), default));
            await DispatchAsync(world, clock);
            var replica = await world.ReplicaAsync(seed.Backup.ReplicaId);
            Assert.Equal(BackupStorageExecutionLocation.Agent, replica.ExecutionLocation);
            Assert.Equal(seed.Agent.AgentId.Value, replica.OwningAgentId);
            Assert.Equal(BackupReplicaStatus.Deleting, replica.Status);
            Assert.True(replica.WaitingForAgent);
            Assert.Equal(DeleteReplicaFailureCodes.AgentUnavailable, replica.DeleteFailureCode);
            Assert.Equal(Due + Options.DeleteRetryInitialDelay, replica.DeleteNextAttemptAt);
            Assert.NotNull(replica.DeleteOperationId);
            Assert.Null(replica.DeleteLeaseToken);
            Assert.Equal(0, replica.DeleteAttempts);
            Assert.True(world.Exists(seed.Backup.Key));
            var visible = await world.Artifacts(clock).GetAsync(seed.Backup.TenantId, seed.Backup.ArtifactId, default);
            Assert.NotNull(visible);
            var view = Assert.Single(visible.Replicas);
            Assert.Equal("Deleting", view.Status);
            Assert.Equal("Agent", view.DeletionExecutor);
            Assert.True(view.WaitingForAgent);
            Assert.Equal(DeleteReplicaFailureCodes.AgentUnavailable, view.DeleteFailureCode);
            Assert.Empty(await world.Store(Options).ClaimDueDeletionsAsync("other-backend", Due, Options.DeleteLeaseDuration, 100, default));
            for (var cycle = 0; cycle < 3; cycle++)
            {
                clock.Advance(TimeSpan.FromSeconds(1));
                await DispatchAsync(world, clock);
                var unchanged = await world.ReplicaAsync(seed.Backup.ReplicaId);
                Assert.Equal(replica.DeleteOperationId, unchanged.DeleteOperationId);
                Assert.Equal(replica.DeleteNextAttemptAt, unchanged.DeleteNextAttemptAt);
                Assert.Equal(0, unchanged.DeleteAttempts);
            }
            RetentionServiceWorld.SetClock(clock, replica.DeleteNextAttemptAt!.Value);
            await DispatchAsync(world, clock);
            var rechecked = await world.ReplicaAsync(seed.Backup.ReplicaId);
            Assert.True(rechecked.WaitingForAgent);
            Assert.Equal(replica.DeleteOperationId, rechecked.DeleteOperationId);
            Assert.InRange(rechecked.DeleteNextAttemptAt!.Value - clock.GetUtcNow(), Options.DeleteRetryInitialDelay, Options.DeleteRetryMaxDelay);
            Assert.Equal(0, world.Storage.TotalDeleteCalls);
            Assert.True(world.Exists(seed.Backup.Key));
        });
    }

    [Fact]
    public async Task AgentPollClaimsPromptlyAndSnapshotUsesPersistedRunOwnershipAndUnexpandedBasePrefix()
    {
        await RetentionServiceWorld.RunAsync(async world =>
        {
            var seed = await SeedAsync(world);
            var clock = new MutableClock(Due);
            await RequestAsync(world, seed);
            await DispatchAsync(world, clock);
            var dispatchedOperation = (await world.ReplicaAsync(seed.Backup.ReplicaId)).DeleteOperationId;
            await using (var context = world.Harness.Database.CreatePlatformContext())
            {
                var job = await context.BackupJobs.FindAsync(seed.Job.Id);
                job!.Reconfigure(BackupStoreHarness.Configuration("local", BackupStorageExecutionLocation.Backend) with { StoragePrefix = "changed" });
                await context.SaveChangesAsync();
            }
            clock.Advance(TimeSpan.FromSeconds(1));
            var service = Service(world, clock);
            var task = await service.ClaimAsync(AgentReplicaDeletionTestKit.Identity(seed.Agent), default);
            Assert.NotNull(task);
            Assert.Equal(dispatchedOperation, task.OperationId);
            Assert.Equal(seed.Backup.ReplicaId, task.ReplicaId);
            Assert.Equal(seed.Agent.TenantId.Value, task.TenantId);
            Assert.Equal(seed.Agent.AgentId.Value, task.AgentId);
            Assert.Equal(seed.Backup.JobId, task.JobId);
            Assert.Equal(seed.Backup.RunId, task.RunId);
            Assert.Equal(seed.Backup.AttemptId, task.AttemptId);
            Assert.Equal("local", task.StorageProvider);
            Assert.Equal("local", task.StorageTarget);
            Assert.Equal(seed.Backup.Key, task.StorageKey);
            Assert.Equal("managed/base", task.StoragePrefix);
            Assert.Equal(clock.GetUtcNow() + Options.DeleteLeaseDuration, task.LeaseExpiresAt);
            Assert.NotEqual(Guid.Empty, task.OperationId);
            Assert.NotEqual(Guid.Empty, task.LeaseToken);
            Assert.Null(await Service(world, clock).ClaimAsync(AgentReplicaDeletionTestKit.Identity(seed.Agent), default));
            var claimed = await world.ReplicaAsync(task.ReplicaId);
            Assert.False(claimed.WaitingForAgent);
            Assert.Null(claimed.DeleteFailureCode);
            Assert.Null(claimed.DeleteNextAttemptAt);
            Assert.Equal(task.OperationId, claimed.DeleteOperationId);
            Assert.Equal(task.LeaseToken, claimed.DeleteLeaseToken);
            Assert.Equal(1, claimed.DeleteAttempts);
        });
    }

    [Fact]
    public async Task RenewalExtendsBackendClockLeaseAndFencesWrongOperationTokenAndExpiredHolder()
    {
        await RetentionServiceWorld.RunAsync(async world =>
        {
            var seed = await SeedAsync(world);
            var clock = new MutableClock(Due);
            var task = await ClaimAsync(world, seed, clock);
            var identity = AgentReplicaDeletionTestKit.Identity(seed.Agent);
            clock.Advance(TimeSpan.FromSeconds(10));
            Assert.Null(await Service(world, clock).RenewAsync(identity, task.ReplicaId, Lease(task) with { OperationId = Guid.NewGuid() }, default));
            Assert.Null(await Service(world, clock).RenewAsync(identity, task.ReplicaId, Lease(task) with { LeaseToken = Guid.NewGuid() }, default));
            Assert.Equal(task.LeaseExpiresAt, (await world.ReplicaAsync(task.ReplicaId)).DeleteLeaseExpiresAt);
            var renewed = await Service(world, clock).RenewAsync(identity, task.ReplicaId, Lease(task), default);
            Assert.NotNull(renewed);
            Assert.Equal(clock.GetUtcNow() + Options.DeleteLeaseDuration, renewed.LeaseExpiresAt);
            RetentionServiceWorld.SetClock(clock, task.LeaseExpiresAt);
            Assert.Null(await Service(world, clock).ClaimAsync(identity, default));
            RetentionServiceWorld.SetClock(clock, renewed.LeaseExpiresAt);
            Assert.Null(await Service(world, clock).RenewAsync(identity, task.ReplicaId, Lease(task), default));
            Assert.Equal(BackupMutationResult.Conflict, await Service(world, clock).CompleteAsync(identity, task.ReplicaId, Success(task), default));
        });
    }

    [Fact]
    public async Task CrashedClaimIsRedeliveredWithStableOperationAndFreshTokenAndAlreadyAbsentSuccessFencesStaleCompletion()
    {
        await RetentionServiceWorld.RunAsync(async world =>
        {
            var seed = await SeedAsync(world);
            var clock = new MutableClock(Due);
            var first = await ClaimAsync(world, seed, clock);
            File.Delete(world.PathOf(seed.Backup.Key));
            Assert.False(world.Exists(seed.Backup.Key));
            RetentionServiceWorld.SetClock(clock, first.LeaseExpiresAt);
            var identity = AgentReplicaDeletionTestKit.Identity(seed.Agent);
            var second = await Service(world, clock).ClaimAsync(identity, default);
            Assert.NotNull(second);
            Assert.Equal(first.ReplicaId, second.ReplicaId);
            Assert.Equal(first.OperationId, second.OperationId);
            Assert.NotEqual(first.LeaseToken, second.LeaseToken);
            Assert.Null(await Service(world, clock).RenewAsync(identity, first.ReplicaId, Lease(first), default));
            Assert.Equal(BackupMutationResult.Conflict, await Service(world, clock).CompleteAsync(identity, first.ReplicaId, Success(first), default));
            Assert.Equal(BackupMutationResult.Accepted, await Service(world, clock).CompleteAsync(identity, second.ReplicaId, Success(second), default));
            var deleted = await world.ReplicaAsync(second.ReplicaId);
            Assert.Equal(BackupReplicaStatus.Deleted, deleted.Status);
            Assert.Equal(clock.GetUtcNow(), deleted.DeletedAt);
            Assert.Equal(2, deleted.DeleteAttempts);
            Assert.Equal(BackupArtifactState.Deleted, (await world.ArtifactAsync(seed.Backup.ArtifactId)).State);
            Assert.Equal(BackupMutationResult.Conflict, await Service(world, clock).CompleteAsync(identity, first.ReplicaId, Success(first), default));
            Assert.Equal(0, world.Storage.TotalDeleteCalls);
        });
    }

    [Fact]
    public async Task WrongTenantAgentReplicaOperationOrTokenCannotRenewOrCompleteAnOwnedClaim()
    {
        await RetentionServiceWorld.RunAsync(async world =>
        {
            var seed = await SeedAsync(world);
            var clock = new MutableClock(Due);
            var task = await ClaimAsync(world, seed, clock);
            var identity = AgentReplicaDeletionTestKit.Identity(seed.Agent);
            var otherTenant = await world.Harness.AddAgentAsync();
            var otherAgent = await world.Harness.AddAgentToAsync(seed.Agent.TenantId);
            var strangers = new[] { AgentReplicaDeletionTestKit.Identity(otherTenant), identity with { AgentId = otherAgent }, identity with { TenantId = otherTenant.TenantId } };
            foreach (var stranger in strangers)
            {
                Assert.Null(await Service(world, clock).ClaimAsync(stranger, default));
                Assert.Null(await Service(world, clock).RenewAsync(stranger, task.ReplicaId, Lease(task), default));
                Assert.Equal(BackupMutationResult.NotFound, await Service(world, clock).CompleteAsync(stranger, task.ReplicaId, Success(task), default));
            }
            foreach (var invalid in new[] { Success(task) with { OperationId = Guid.NewGuid() }, Success(task) with { LeaseToken = Guid.NewGuid() } })
                Assert.Equal(BackupMutationResult.Conflict, await Service(world, clock).CompleteAsync(identity, task.ReplicaId, invalid, default));
            Assert.Equal(BackupMutationResult.NotFound, await Service(world, clock).CompleteAsync(identity, Guid.NewGuid(), Success(task), default));
            var unchanged = await world.ReplicaAsync(task.ReplicaId);
            Assert.Equal(BackupReplicaStatus.Deleting, unchanged.Status);
            Assert.Equal(task.OperationId, unchanged.DeleteOperationId);
            Assert.Equal(task.LeaseToken, unchanged.DeleteLeaseToken);
            Assert.Null(unchanged.DeletedAt);
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData(DeleteReplicaFailureCodes.AccessDenied)]
    [InlineData(DeleteReplicaFailureCodes.IoError)]
    public async Task ExactDuplicateSuccessAndFailureReceiptsAreAcceptedButChangedOutcomesConflict(string? code)
    {
        await RetentionServiceWorld.RunAsync(async world =>
        {
            var seed = await SeedAsync(world);
            var clock = new MutableClock(Due);
            var task = await ClaimAsync(world, seed, clock);
            var identity = AgentReplicaDeletionTestKit.Identity(seed.Agent);
            var completion = code is null ? Success(task) : Failure(task, code);
            Assert.Equal(BackupMutationResult.Accepted, await Service(world, clock).CompleteAsync(identity, task.ReplicaId, completion, default));
            var first = await world.ReplicaAsync(task.ReplicaId);
            clock.Advance(TimeSpan.FromDays(1));
            Assert.Equal(BackupMutationResult.Accepted, await Service(world, clock).CompleteAsync(identity, task.ReplicaId, completion, default));
            var changed = code is null ? Failure(task, DeleteReplicaFailureCodes.IoError) : Success(task);
            Assert.Equal(BackupMutationResult.Conflict, await Service(world, clock).CompleteAsync(identity, task.ReplicaId, changed, default));
            if (code is not null)
                Assert.Equal(BackupMutationResult.Conflict, await Service(world, clock).CompleteAsync(identity, task.ReplicaId, Failure(task, DeleteReplicaFailureCodes.Unexpected), default));
            var duplicate = await world.ReplicaAsync(task.ReplicaId);
            Assert.Equal(first.Status, duplicate.Status);
            Assert.Equal(first.DeletedAt, duplicate.DeletedAt);
            Assert.Equal(first.DeleteFailureCode, duplicate.DeleteFailureCode);
            Assert.Equal(first.DeleteNextAttemptAt, duplicate.DeleteNextAttemptAt);
            Assert.Equal(1, duplicate.DeleteAttempts);
        });
    }

    [Theory]
    [InlineData(DeleteReplicaFailureCodes.AccessDenied)]
    [InlineData(DeleteReplicaFailureCodes.InvalidLocation)]
    [InlineData(DeleteReplicaFailureCodes.StorageTargetUnknown)]
    public async Task NonretryableFailureStaysFailedWithoutAnyAutomaticDispatch(string code)
    {
        await RetentionServiceWorld.RunAsync(async world =>
        {
            var seed = await SeedAsync(world);
            var clock = new MutableClock(Due);
            var task = await ClaimAsync(world, seed, clock);
            Assert.Equal(BackupMutationResult.Accepted, await Service(world, clock).CompleteAsync(AgentReplicaDeletionTestKit.Identity(seed.Agent), task.ReplicaId, Failure(task, code), default));
            var failed = await world.ReplicaAsync(task.ReplicaId);
            Assert.Equal(BackupReplicaStatus.DeleteFailed, failed.Status);
            Assert.Null(failed.DeleteNextAttemptAt);
            clock.Advance(TimeSpan.FromDays(365));
            await DispatchAsync(world, clock);
            Assert.Null(await Service(world, clock).ClaimAsync(AgentReplicaDeletionTestKit.Identity(seed.Agent), default));
            var terminal = await world.ReplicaAsync(task.ReplicaId);
            Assert.Equal(BackupReplicaStatus.DeleteFailed, terminal.Status);
            Assert.Equal(code, terminal.DeleteFailureCode);
            Assert.Equal(task.OperationId, terminal.DeleteOperationId);
            Assert.Equal(1, terminal.DeleteAttempts);
        });
    }

    [Fact]
    public async Task RetryableFailureWaitsForConfiguredBackoffThenRedispatchesWithFreshFencing()
    {
        await RetentionServiceWorld.RunAsync(async world =>
        {
            var seed = await SeedAsync(world);
            var clock = new MutableClock(Due);
            var task = await ClaimAsync(world, seed, clock);
            var identity = AgentReplicaDeletionTestKit.Identity(seed.Agent);
            for (var attempt = 1; attempt <= 4; attempt++)
            {
                Assert.Equal(BackupMutationResult.Accepted, await Service(world, clock).CompleteAsync(identity, task.ReplicaId, Failure(task, DeleteReplicaFailureCodes.IoError), default));
                var failed = await world.ReplicaAsync(task.ReplicaId);
                var expectedDelay = TimeSpan.FromSeconds(Math.Min(40, 10 * Math.Pow(2, attempt - 1)));
                Assert.Equal(clock.GetUtcNow() + expectedDelay, failed.DeleteNextAttemptAt);
                RetentionServiceWorld.SetClock(clock, failed.DeleteNextAttemptAt!.Value.AddMilliseconds(-1));
                await DispatchAsync(world, clock);
                Assert.Null(await Service(world, clock).ClaimAsync(identity, default));
                RetentionServiceWorld.SetClock(clock, failed.DeleteNextAttemptAt.Value);
                await DispatchAsync(world, clock);
                var next = await Service(world, clock).ClaimAsync(identity, default);
                Assert.NotNull(next);
                Assert.Equal(task.OperationId, next.OperationId);
                Assert.NotEqual(task.LeaseToken, next.LeaseToken);
                Assert.Equal(BackupMutationResult.Conflict, await Service(world, clock).CompleteAsync(identity, task.ReplicaId, Failure(task, DeleteReplicaFailureCodes.IoError), default));
                task = next;
            }
            Assert.Equal(5, (await world.ReplicaAsync(task.ReplicaId)).DeleteAttempts);
            Assert.Equal(0, world.Storage.TotalDeleteCalls);
        });
    }
}
