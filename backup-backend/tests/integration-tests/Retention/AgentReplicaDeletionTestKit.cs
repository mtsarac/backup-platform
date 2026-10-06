using BackupPlatform.Application.Agents;
using BackupPlatform.Application.Backups;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Infrastructure.Backups;

namespace BackupPlatform.IntegrationTests;

internal static class AgentReplicaDeletionTestKit
{
    public static readonly DateTimeOffset Due = RetentionServiceWorld.Accepted.AddHours(1);
    public static readonly BackupRetentionOptions Options = new()
    {
        DeleteLeaseDuration = TimeSpan.FromSeconds(30),
        DeleteRetryInitialDelay = TimeSpan.FromSeconds(10),
        DeleteRetryMaxDelay = TimeSpan.FromSeconds(40)
    };

    public static AgentIdentity Identity(StoreAgent agent) => new(agent.AgentId, agent.TenantId, Guid.NewGuid(), 1);

    public static AgentReplicaDeletionService Service(RetentionServiceWorld world, TimeProvider clock) =>
        new(new PostgreSqlAgentReplicaDeletionStore(world.DbOptions(), Options), Options, clock);

    public static async Task<SeededWorld> SeedAsync(RetentionServiceWorld world, string prefix = "managed/base", BackupRetentionPolicy? policy = null)
    {
        var agent = await world.Harness.AddAgentAsync();
        var job = await RetentionKit.AddJobAsync(world.Harness, agent, policy ?? RetentionKit.Policy(enabled: false, grace: TimeSpan.FromSeconds(1)),
            BackupStorageExecutionLocation.Agent, prefix);
        var backup = await RetentionKit.SeedAsync(world.Harness, agent, job, RetentionServiceWorld.Accepted);
        world.Place(backup.Key);
        return new SeededWorld(agent, job, backup);
    }

    public static async Task RequestAsync(RetentionServiceWorld world, SeededWorld seed)
    {
        var result = await world.Store(Options).RequestDeleteAsync(seed.Backup.TenantId, seed.Backup.ArtifactId, Guid.NewGuid(), Due.AddSeconds(-1), default);
        Assert.Equal(BackupArtifactDeleteKind.Requested, result.Kind);
    }

    public static async Task DispatchAsync(RetentionServiceWorld world, MutableClock clock)
    {
        var result = await world.Service("retention", clock, options: Options).RunCycleAsync(default);
        Assert.Equal(0, result.DeletionsClaimed);
        Assert.Equal(0, result.Deleted);
        Assert.Equal(0, result.Failed);
        Assert.Equal(0, world.Storage.TotalDeleteCalls);
    }

    public static async Task<DeleteReplicaTaskSnapshot> ClaimAsync(RetentionServiceWorld world, SeededWorld seed, MutableClock clock)
    {
        await RequestAsync(world, seed);
        await DispatchAsync(world, clock);
        var claim = await Service(world, clock).ClaimAsync(Identity(seed.Agent), default);
        Assert.NotNull(claim);
        return claim;
    }

    public static DeleteReplicaLeaseRequest Lease(DeleteReplicaTaskSnapshot task) =>
        new() { OperationId = task.OperationId, LeaseToken = task.LeaseToken };

    public static DeleteReplicaCompletionRequest Success(DeleteReplicaTaskSnapshot task) =>
        new() { OperationId = task.OperationId, LeaseToken = task.LeaseToken, AbsenceVerified = true };

    public static DeleteReplicaCompletionRequest Failure(DeleteReplicaTaskSnapshot task, string code) =>
        new() { OperationId = task.OperationId, LeaseToken = task.LeaseToken, FailureCode = code };
}
