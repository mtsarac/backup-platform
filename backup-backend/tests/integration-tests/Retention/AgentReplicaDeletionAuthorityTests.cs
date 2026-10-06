using BackupPlatform.Application.Backups;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Domain.Backups;
using static BackupPlatform.IntegrationTests.AgentReplicaDeletionTestKit;

namespace BackupPlatform.IntegrationTests;

public sealed class AgentReplicaDeletionAuthorityTests
{
    [Fact]
    public async Task ExplicitBackendLocalStorageStaysBackendOwnedAndIsNeverHandedToTheAgent()
    {
        await RetentionServiceWorld.RunAsync(async world =>
        {
            var seed = await world.SeedAsync(RetentionKit.Policy(enabled: false, grace: TimeSpan.FromSeconds(1)));
            var clock = new MutableClock(Due);
            await RequestAsync(world, seed);
            var replica = await world.ReplicaAsync(seed.Backup.ReplicaId);
            Assert.Equal("local", replica.StorageProvider);
            Assert.Equal(BackupStorageExecutionLocation.Backend, replica.ExecutionLocation);
            Assert.Null(replica.OwningAgentId);
            var identity = AgentReplicaDeletionTestKit.Identity(seed.Agent);
            Assert.Null(await Service(world, clock).ClaimAsync(identity, default));
            var unauthorized = new DeleteReplicaTaskSnapshot { OperationId = Guid.NewGuid(), LeaseToken = Guid.NewGuid() };
            Assert.Null(await Service(world, clock).RenewAsync(identity, replica.Id, Lease(unauthorized), default));
            Assert.Equal(BackupMutationResult.NotFound, await Service(world, clock).CompleteAsync(identity, replica.Id, Success(unauthorized), default));
            var result = await world.Service("backend", clock, options: Options).RunCycleAsync(default);
            Assert.Equal(1, result.DeletionsClaimed);
            Assert.Equal(1, result.Deleted);
            Assert.Equal(1, world.Storage.DeleteCalls(seed.Backup.Key));
            Assert.False(world.Exists(seed.Backup.Key));
            Assert.Equal(BackupReplicaStatus.Deleted, (await world.ReplicaAsync(replica.Id)).Status);
            Assert.Null(await Service(world, clock).ClaimAsync(identity, default));
        });
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, DeleteReplicaFailureCodes.IoError)]
    [InlineData(false, "arbitrary_exception_details")]
    public async Task CompletionWithoutVerifiedAbsenceOrAKnownExclusiveFailureCannotFinalizeAClaim(bool verified, string? code)
    {
        await RetentionServiceWorld.RunAsync(async world =>
        {
            var seed = await SeedAsync(world);
            var clock = new MutableClock(Due);
            var task = await ClaimAsync(world, seed, clock);
            var invalid = Success(task) with { AbsenceVerified = verified, FailureCode = code };
            Assert.Equal(BackupMutationResult.Conflict, await Service(world, clock).CompleteAsync(AgentReplicaDeletionTestKit.Identity(seed.Agent), task.ReplicaId, invalid, default));
            var replica = await world.ReplicaAsync(task.ReplicaId);
            Assert.Equal(BackupReplicaStatus.Deleting, replica.Status);
            Assert.Equal(task.OperationId, replica.DeleteOperationId);
            Assert.Equal(task.LeaseToken, replica.DeleteLeaseToken);
            Assert.Null(replica.DeletedAt);
            Assert.Null(replica.DeleteFailureCode);
            Assert.True(world.Exists(seed.Backup.Key));
        });
    }
}
