using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Infrastructure.Backups;
using BackupPlatform.IntegrationTests.Quota;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static BackupPlatform.IntegrationTests.BackupStoreHarness;

namespace BackupPlatform.IntegrationTests.Backup;

public sealed class ReplicaRepairConcurrencyIntegrationTests
{
    private static readonly TimeSpan RepairLease = TimeSpan.FromMinutes(10);

    [Theory]
    [InlineData("direct")]
    [InlineData("due")]
    [InlineData("mixed")]
    public Task CompetingNodesOnDifferentOperationsCannotExceedGlobalCapacity(string path) => WithIsolatedDatabaseAsync(async kit =>
    {
        var waiting = new List<ReplicaRepairOperation>();
        for (var tenant = 0; tenant < 3; tenant++)
            waiting.AddRange((await SeedAsync(kit, pending: 4, active: 1)).Where(op => op.Status == ReplicaRepairStatus.Pending));

        var claimed = await RaceClaimsAsync(kit, waiting, path);

        Assert.Single(claimed);
        await AssertCapacityAndWaitingAsync(kit, active: 4, pending: waiting.Count - 1);
    });

    [Theory]
    [InlineData("direct")]
    [InlineData("due")]
    [InlineData("mixed")]
    public Task CompetingNodesCannotExceedTenantCapacityOrConsumeWaitingAttempts(string path) => WithIsolatedDatabaseAsync(async kit =>
    {
        var waiting = await SeedAsync(kit, pending: 12);

        var claimed = await RaceClaimsAsync(kit, waiting, path);

        Assert.Equal(2, claimed.Count);
        Assert.Equal(2, claimed.Select(op => op.Id).Distinct().Count());
        await AssertCapacityAndWaitingAsync(kit, active: 2, pending: 10);
    });

    [Fact]
    public Task SaturatedTenantAtQueueHeadDoesNotStarveOtherTenants() => WithIsolatedDatabaseAsync(async kit =>
    {
        var saturated = await SeedAsync(kit, pending: 8, active: 2, dueAt: T0.AddMinutes(-1));
        var other = Assert.Single(await SeedAsync(kit, pending: 1));
        var store = Store(kit);

        Assert.Null(await store.ClaimOperationAsync(saturated[2].TenantId, saturated[2].Id, Guid.NewGuid(), "direct-blocked", T0, RepairLease, default));
        Assert.Null(await store.ClaimDueRepairAsync("tenant-blocked", T0, RepairLease, default, saturated[2].TenantId));
        var claimed = await store.ClaimDueRepairAsync("fair-node", T0, RepairLease, default);

        Assert.NotNull(claimed);
        Assert.Equal(other.Id, claimed.Id);
        await AssertCapacityAndWaitingAsync(kit, active: 3, pending: 8, dueAt: T0.AddMinutes(-1));
    });

    [Fact]
    public Task ExpiredInProgressStillOccupiesGlobalCapacityUntilRecovery() => WithIsolatedDatabaseAsync(async kit =>
    {
        var expired = new List<ReplicaRepairOperation>();
        for (var tenant = 0; tenant < 4; tenant++)
            expired.Add(Assert.Single(await SeedAsync(kit, pending: 0, active: 1)));
        var waiting = Assert.Single(await SeedAsync(kit, pending: 1));
        var store = Store(kit);
        var now = T0.AddMinutes(20);

        Assert.Null(await store.ClaimDueRepairAsync("due-blocked", now, RepairLease, default));
        Assert.Null(await store.ClaimOperationAsync(waiting.TenantId, waiting.Id, Guid.NewGuid(), "direct-blocked", now, RepairLease, default));
        Assert.Null(await store.ClaimOperationAsync(expired[0].TenantId, expired[0].Id, Guid.NewGuid(), "not-recovery", now, RepairLease, default));
        await AssertCapacityAndWaitingAsync(kit, active: 4, pending: 1);

        Assert.Equal(4, await store.RecoverStaleRepairsAsync(now, TimeSpan.FromMinutes(1), default));
        await using (var context = kit.Database.CreatePlatformContext())
        {
            var recovered = await context.ReplicaRepairOperations.AsNoTracking().Where(op => op.Id != waiting.Id).ToListAsync();
            Assert.All(recovered, op =>
            {
                Assert.Equal(ReplicaRepairStatus.Pending, op.Status);
                Assert.Equal(1, op.AttemptCount);
                Assert.Equal(now.AddMinutes(1), op.NextRetryAt);
                Assert.Null(op.LeaseToken);
            });
        }
        Assert.Null(await store.ClaimOperationAsync(expired[0].TenantId, expired[0].Id, Guid.NewGuid(), "too-early", now, RepairLease, default));
        Assert.NotNull(await store.ClaimOperationAsync(waiting.TenantId, waiting.Id, Guid.NewGuid(), "after-recovery", now, RepairLease, default));
        var retried = await store.ClaimOperationAsync(expired[0].TenantId, expired[0].Id, Guid.NewGuid(), "retry", now.AddMinutes(1), RepairLease, default);
        Assert.NotNull(retried);
        Assert.Equal(2, retried.AttemptCount);
    });

    [Fact]
    public Task ExpiredInProgressStillOccupiesTenantCapacity() => WithIsolatedDatabaseAsync(async kit =>
    {
        var operations = await SeedAsync(kit, pending: 1, active: 2);
        var waiting = operations.Single(op => op.Status == ReplicaRepairStatus.Pending);
        var other = Assert.Single(await SeedAsync(kit, pending: 1));
        var now = T0.AddMinutes(20);
        var store = Store(kit);

        Assert.Null(await store.ClaimOperationAsync(waiting.TenantId, waiting.Id, Guid.NewGuid(), "direct-blocked", now, RepairLease, default));
        Assert.Null(await store.ClaimDueRepairAsync("tenant-blocked", now, RepairLease, default, waiting.TenantId));
        var claimed = await store.ClaimDueRepairAsync("other-tenant", now, RepairLease, default);

        Assert.NotNull(claimed);
        Assert.Equal(other.Id, claimed.Id);
        await AssertCapacityAndWaitingAsync(kit, active: 3, pending: 1);
    });

    [Theory]
    [InlineData("direct")]
    [InlineData("due")]
    public Task BothClaimPathsRespectDueTimeCancellationAndPendingOnly(string path) => WithIsolatedDatabaseAsync(async kit =>
    {
        var operation = Assert.Single(await SeedAsync(kit, pending: 1, dueAt: T0.AddMinutes(1)));
        var store = Store(kit);
        Task<ReplicaRepairOperation?> Claim(DateTimeOffset now, CancellationToken token = default) => path == "direct"
            ? store.ClaimOperationAsync(operation.TenantId, operation.Id, Guid.NewGuid(), "node", now, RepairLease, token)
            : store.ClaimDueRepairAsync("node", now, RepairLease, token, operation.TenantId);

        Assert.Null(await Claim(T0));
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Claim(T0.AddMinutes(1), cancellation.Token));
        }
        await AssertCapacityAndWaitingAsync(kit, active: 0, pending: 1, dueAt: T0.AddMinutes(1));
        Assert.NotNull(await Claim(T0.AddMinutes(1)));
        Assert.Null(await Claim(T0.AddMinutes(2)));
        Assert.Null(await Claim(T0.AddMinutes(20)));
        await using (var context = kit.Database.CreatePlatformContext())
        {
            var stored = await context.ReplicaRepairOperations.SingleAsync();
            Assert.Equal(ReplicaRepairStatus.InProgress, stored.Status);
            Assert.Equal(1, stored.AttemptCount);
        }

        var cancelled = Assert.Single(await SeedAsync(kit, pending: 1));
        await using (var context = kit.Database.CreatePlatformContext())
        {
            var stored = await context.ReplicaRepairOperations.SingleAsync(op => op.Id == cancelled.Id);
            stored.RequestCancellation(T0);
            await context.SaveChangesAsync();
        }
        var result = path == "direct"
            ? await store.ClaimOperationAsync(cancelled.TenantId, cancelled.Id, Guid.NewGuid(), "cancelled-node", T0, RepairLease, default)
            : await store.ClaimDueRepairAsync("cancelled-node", T0, RepairLease, default, cancelled.TenantId);
        Assert.Null(result);
        await using var final = kit.Database.CreatePlatformContext();
        var finalOperation = await final.ReplicaRepairOperations.SingleAsync(op => op.Id == cancelled.Id);
        Assert.Equal(ReplicaRepairStatus.Cancelled, finalOperation.Status);
        Assert.Equal(0, finalOperation.AttemptCount);
    });

    private static PostgreSqlReplicaRepairStore Store(BackupStoreHarness kit) => new(new QuotaWorld(kit.Database).Options());

    private static async Task<IReadOnlyList<ReplicaRepairOperation>> SeedAsync(
        BackupStoreHarness kit, int pending, int active = 0, DateTimeOffset? dueAt = null)
    {
        var agent = await kit.AddAgentAsync();
        var job = await kit.AddJobAsync(agent);
        var runStore = kit.Store();
        await kit.StartRunAsync(runStore, job, Guid.NewGuid().ToString("N"), T0);
        var claim = (await ClaimAsync(runStore, agent, T0))!;
        await StartAttemptAsync(runStore, claim, T0);
        await CompleteAsync(runStore, claim, Success(T0), T0);
        await using var context = kit.Database.CreatePlatformContext();
        var artifact = await context.BackupArtifacts.SingleAsync(a => a.AttemptId == claim.Attempt.Id);
        var operations = Enumerable.Range(0, active + pending).Select(index =>
        {
            var operation = new ReplicaRepairOperation(Guid.NewGuid(), agent.TenantId, artifact.Id, Guid.NewGuid(), "sftp", null,
                T0, nextRetryAt: dueAt ?? T0);
            if (index < active) operation.Claim(Guid.NewGuid(), "existing-node", T0, RepairLease);
            return operation;
        }).ToArray();
        context.ReplicaRepairOperations.AddRange(operations);
        await context.SaveChangesAsync();
        return operations;
    }

    private static async Task<IReadOnlyList<ReplicaRepairOperation>> RaceClaimsAsync(
        BackupStoreHarness kit, IReadOnlyList<ReplicaRepairOperation> waiting, string path)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var claims = waiting.Select((operation, index) =>
        {
            var node = Store(kit);
            return ClaimAsync();
            async Task<ReplicaRepairOperation?> ClaimAsync()
            {
                await start.Task;
                return path == "direct" || (path == "mixed" && index % 2 == 0)
                    ? await node.ClaimOperationAsync(operation.TenantId, operation.Id, Guid.NewGuid(), $"node-{index}", T0, RepairLease, default)
                    : await node.ClaimDueRepairAsync($"node-{index}", T0, RepairLease, default, operation.TenantId);
            }
        }).ToArray();
        start.SetResult();
        return (await Task.WhenAll(claims)).OfType<ReplicaRepairOperation>().ToArray();
    }

    private static async Task AssertCapacityAndWaitingAsync(BackupStoreHarness kit, int active, int pending, DateTimeOffset? dueAt = null)
    {
        await using var context = kit.Database.CreatePlatformContext();
        var operations = await context.ReplicaRepairOperations.AsNoTracking().ToListAsync();
        var inProgress = operations.Where(op => op.Status == ReplicaRepairStatus.InProgress).ToArray();
        Assert.Equal(active, inProgress.Length);
        Assert.True(inProgress.Length <= IReplicaRepairStore.MaxGlobalConcurrentRepairs);
        Assert.All(inProgress.GroupBy(op => op.TenantId), group => Assert.True(group.Count() <= IReplicaRepairStore.MaxTenantConcurrentRepairs));
        var waiting = operations.Where(op => op.Status == ReplicaRepairStatus.Pending).ToArray();
        Assert.Equal(pending, waiting.Length);
        Assert.All(waiting, op =>
        {
            Assert.Equal(0, op.AttemptCount);
            Assert.Null(op.StartedAt);
            Assert.Null(op.LeaseToken);
            Assert.Equal(dueAt ?? T0, op.NextRetryAt);
        });
    }
}
