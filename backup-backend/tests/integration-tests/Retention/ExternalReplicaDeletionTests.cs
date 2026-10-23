using BackupPlatform.Agents.Capabilities;
using BackupPlatform.Application.Backups;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Capabilities;
using Microsoft.EntityFrameworkCore;
using static BackupPlatform.IntegrationTests.AgentReplicaDeletionTestKit;

namespace BackupPlatform.IntegrationTests;

/// <summary>
/// SFTP and Google Drive replicas are removed by an agent, never by the backend: retention only dispatches them, a lifecycle-capable agent of the
/// tenant claims them under a fenced lease, and only the verified absence of the object records the replica Deleted (which is what returns its quota).
/// </summary>
public sealed class ExternalReplicaDeletionTests
{
    private static async Task<SeededWorld> SeedExternalAsync(RetentionServiceWorld world, string provider)
    {
        var agent = await world.Harness.AddAgentAsync();
        var job = await RetentionKit.AddJobAsync(world.Harness, agent, RetentionKit.Policy(enabled: false, grace: TimeSpan.FromSeconds(1)),
            BackupStorageExecutionLocation.Backend, "managed/base");
        var backup = await RetentionKit.SeedAsync(world.Harness, agent, job, RetentionServiceWorld.Accepted, storageProvider: provider, storageTarget: provider);
        var connectionId = (await world.ReplicaAsync(backup.ReplicaId)).StorageConnectionId;
        await using var context = world.Harness.Database.CreatePlatformContext();
        await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE backup_replicas SET \"StorageTarget\" = {connectionId.ToString("D")} WHERE \"Id\" = {backup.ReplicaId}");
        return new SeededWorld(agent, job, backup);
    }

    private static async Task EnableLifecycleAsync(RetentionServiceWorld world, StoreAgent agent)
    {
        await using var context = world.Harness.Database.CreatePlatformContext();
        context.AgentCapabilities.Add(new AgentCapabilityRecord(agent.AgentId, agent.TenantId, ReplicaLifecycleProtocol.Capability, CapabilitySupport.Supported, null, "revision-1",
            RetentionServiceWorld.Accepted));
        await context.SaveChangesAsync();
    }

    private static async Task<DeleteReplicaTaskSnapshot> DispatchAndClaimAsync(RetentionServiceWorld world, SeededWorld seed, MutableClock clock, StoreAgent claimant)
    {
        await RequestAsync(world, seed);
        var cycle = await world.Service("retention", clock, options: Options).RunCycleAsync(default);
        // The backend only dispatches: no physical IO, no claim of its own.
        Assert.Equal((0, 0, 0), (cycle.DeletionsClaimed, cycle.Deleted, cycle.Failed));
        Assert.Equal(0, world.Storage.TotalDeleteCalls);
        var claimed = await Service(world, clock).ClaimAsync(AgentReplicaDeletionTestKit.Identity(claimant), default);
        return claimed ?? throw new Xunit.Sdk.XunitException("The lifecycle-capable agent was expected to claim the dispatched removal.");
    }

    [Theory]
    [InlineData("sftp")]
    [InlineData("google-drive")]
    public async Task RetentionDispatchesAnExternalReplicaAndOnlyALifecycleCapableAgentOfTheTenantClaimsItWithItsConnection(string provider)
    {
        await RetentionServiceWorld.RunAsync(async world =>
        {
            var seed = await SeedExternalAsync(world, provider);
            var clock = new MutableClock(Due);
            await RequestAsync(world, seed);
            await world.Service("retention", clock, options: Options).RunCycleAsync(default);
            var dispatched = await world.ReplicaAsync(seed.Backup.ReplicaId);
            Assert.Equal((BackupReplicaStatus.Deleting, true, "Agent"), (dispatched.Status, dispatched.WaitingForAgent, BackupOrchestrator.ToView(dispatched).DeletionExecutor));
            Assert.Equal(DeleteReplicaFailureCodes.AgentUnavailable, dispatched.DeleteFailureCode);
            // The owning-agent-less replica is invisible to an agent that cannot run the removal, to other tenants, and to the backend's own claim.
            var identity = AgentReplicaDeletionTestKit.Identity(seed.Agent);
            Assert.Null(await Service(world, clock).ClaimAsync(identity, default));
            await EnableLifecycleAsync(world, seed.Agent);
            var stranger = await world.Harness.AddAgentAsync();
            await EnableLifecycleAsync(world, stranger);
            Assert.Null(await Service(world, clock).ClaimAsync(AgentReplicaDeletionTestKit.Identity(stranger), default));
            Assert.Empty(await world.Store(Options).ClaimDueDeletionsAsync("other-backend", Due, Options.DeleteLeaseDuration, 100, default, dispatchExternalToAgents: true));
            var colleague = await world.Harness.AddAgentToAsync(seed.Agent.TenantId);
            var sibling = new StoreAgent(seed.Agent.TenantId, colleague);
            await EnableLifecycleAsync(world, sibling);

            var task = await Service(world, clock).ClaimAsync(AgentReplicaDeletionTestKit.Identity(sibling), default);

            Assert.NotNull(task);
            Assert.Equal((provider, seed.Backup.ReplicaId, seed.Backup.Key), (task.StorageProvider, task.ReplicaId, task.StorageKey));
            Assert.Equal((await world.ReplicaAsync(task.ReplicaId)).StorageConnectionId.ToString("D"), task.StorageTarget);
            Assert.Equal(("managed/base", seed.Backup.JobId, seed.Backup.RunId, seed.Backup.AttemptId), (task.StoragePrefix, task.JobId, task.RunId, task.AttemptId));
            Assert.Null(await Service(world, clock).ClaimAsync(AgentReplicaDeletionTestKit.Identity(seed.Agent), default));
        });
    }

    [Fact]
    public async Task StorageAccessIsReleasedOnlyToTheLiveLeaseHolderOfThatVeryRemoval()
    {
        await RetentionServiceWorld.RunAsync(async world =>
        {
            var seed = await SeedExternalAsync(world, "sftp");
            await EnableLifecycleAsync(world, seed.Agent);
            var clock = new MutableClock(Due);
            var task = await DispatchAndClaimAsync(world, seed, clock, seed.Agent);
            var identity = AgentReplicaDeletionTestKit.Identity(seed.Agent);
            var service = Service(world, clock);
            var connectionId = (await world.ReplicaAsync(task.ReplicaId)).StorageConnectionId;

            Assert.Equal(new DeletionStorageTarget("sftp", connectionId), await service.AuthorizeStorageAsync(identity, task.ReplicaId, task.LeaseToken, default));
            Assert.Null(await service.AuthorizeStorageAsync(identity, task.ReplicaId, Guid.NewGuid(), default));
            Assert.Null(await service.AuthorizeStorageAsync(identity, task.ReplicaId, Guid.Empty, default));
            Assert.Null(await service.AuthorizeStorageAsync(identity, Guid.NewGuid(), task.LeaseToken, default));
            var otherAgent = await world.Harness.AddAgentToAsync(seed.Agent.TenantId);
            Assert.Null(await service.AuthorizeStorageAsync(identity with { AgentId = otherAgent }, task.ReplicaId, task.LeaseToken, default));
            var otherTenant = await world.Harness.AddAgentAsync();
            Assert.Null(await service.AuthorizeStorageAsync(AgentReplicaDeletionTestKit.Identity(otherTenant), task.ReplicaId, task.LeaseToken, default));
            RetentionServiceWorld.SetClock(clock, task.LeaseExpiresAt);
            Assert.Null(await service.AuthorizeStorageAsync(identity, task.ReplicaId, task.LeaseToken, default));
        });
    }

    [Fact]
    public async Task VerifiedAbsenceRecordsTheReplicaDeletedAndAnotherAgentCannotCompleteItsClaim()
    {
        await RetentionServiceWorld.RunAsync(async world =>
        {
            var seed = await SeedExternalAsync(world, "google-drive");
            await EnableLifecycleAsync(world, seed.Agent);
            var clock = new MutableClock(Due);
            var task = await DispatchAndClaimAsync(world, seed, clock, seed.Agent);
            var identity = AgentReplicaDeletionTestKit.Identity(seed.Agent);
            var otherAgent = await world.Harness.AddAgentToAsync(seed.Agent.TenantId);
            await EnableLifecycleAsync(world, new StoreAgent(seed.Agent.TenantId, otherAgent));

            Assert.Null(await Service(world, clock).RenewAsync(identity with { AgentId = otherAgent }, task.ReplicaId, Lease(task), default));
            Assert.Equal(BackupMutationResult.NotFound, await Service(world, clock).CompleteAsync(identity with { AgentId = otherAgent }, task.ReplicaId, Success(task), default));
            Assert.Equal(BackupReplicaStatus.Deleting, (await world.ReplicaAsync(task.ReplicaId)).Status);

            Assert.NotNull(await Service(world, clock).RenewAsync(identity, task.ReplicaId, Lease(task), default));
            Assert.Equal(BackupMutationResult.Accepted, await Service(world, clock).CompleteAsync(identity, task.ReplicaId, Success(task), default));
            var deleted = await world.ReplicaAsync(task.ReplicaId);
            Assert.Equal((BackupReplicaStatus.Deleted, clock.GetUtcNow()), (deleted.Status, deleted.DeletedAt));
            Assert.Equal(identity.AgentId.Value, deleted.DeleteAgentId);
            // A replayed receipt is accepted idempotently; a changed outcome never rewrites it.
            Assert.Equal(BackupMutationResult.Accepted, await Service(world, clock).CompleteAsync(identity, task.ReplicaId, Success(task), default));
            Assert.Equal(BackupMutationResult.NotFound, await Service(world, clock).CompleteAsync(identity with { AgentId = otherAgent }, task.ReplicaId, Success(task), default));
            Assert.Equal(BackupMutationResult.Conflict, await Service(world, clock).CompleteAsync(identity, task.ReplicaId, Failure(task, DeleteReplicaFailureCodes.IoError), default));
            Assert.Equal(0, world.Storage.TotalDeleteCalls);
            await using var auditContext = world.Harness.Database.CreatePlatformContext();
            var audit = Assert.Single(await auditContext.AuditEvents.Where(item => item.TargetId == task.ReplicaId.ToString("D")).ToListAsync());
            Assert.Equal(BackupPlatform.Domain.Audit.AuditActions.BackupReplicaPhysicalDeleteCompleted, audit.Action);
            Assert.DoesNotContain(task.StorageKey, audit.DetailsJson);
            using var details = System.Text.Json.JsonDocument.Parse(audit.DetailsJson);
            Assert.True(details.RootElement.GetProperty("absenceVerified").GetBoolean());
        });
    }

    [Fact]
    public async Task ARefusedAccessIsRetriedAndTheReplicaKeepsItsQuotaUntilAbsenceIsProven()
    {
        await RetentionServiceWorld.RunAsync(async world =>
        {
            var seed = await SeedExternalAsync(world, "sftp");
            await EnableLifecycleAsync(world, seed.Agent);
            var clock = new MutableClock(Due);
            var task = await DispatchAndClaimAsync(world, seed, clock, seed.Agent);
            var identity = AgentReplicaDeletionTestKit.Identity(seed.Agent);

            Assert.Equal(BackupMutationResult.Accepted, await Service(world, clock).CompleteAsync(identity, task.ReplicaId, Failure(task, DeleteReplicaFailureCodes.AccessDenied), default));

            var failed = await world.ReplicaAsync(task.ReplicaId);
            Assert.Equal((BackupReplicaStatus.DeleteFailed, DeleteReplicaFailureCodes.AccessDenied), (failed.Status, failed.DeleteFailureCode));
            Assert.NotNull(failed.DeleteNextAttemptAt);
            Assert.True(BackupReplicaStorage.Occupies(failed.Status));
            // The retry goes back to an agent, never to the backend.
            RetentionServiceWorld.SetClock(clock, failed.DeleteNextAttemptAt!.Value);
            await world.Service("retention", clock, options: Options).RunCycleAsync(default);
            Assert.True((await world.ReplicaAsync(task.ReplicaId)).WaitingForAgent);
            Assert.Equal(0, world.Storage.TotalDeleteCalls);
        });
    }
}
