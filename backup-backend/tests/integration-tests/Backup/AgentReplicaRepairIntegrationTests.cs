using BackupPlatform.Api.Backups;
using BackupPlatform.Application.Backups;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using BackupPlatform.Agents.Capabilities;
using BackupPlatform.Application.Agents;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Capabilities;
using BackupPlatform.Domain.Storage;
using BackupPlatform.Infrastructure.Backups;
using BackupPlatform.IntegrationTests.Quota;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static BackupPlatform.IntegrationTests.BackupStoreHarness;

namespace BackupPlatform.IntegrationTests.Backup;

public sealed class AgentReplicaRepairIntegrationTests
{
    private static readonly TimeSpan RepairLease = TimeSpan.FromMinutes(10);
    private static PostgreSqlReplicaRepairStore Store(BackupStoreHarness kit) => new(new QuotaWorld(kit.Database).Options());
    private static AgentIdentity Identity(StoreAgent agent) => new(agent.AgentId, agent.TenantId, Guid.NewGuid(), 1);

    [Fact]
    public Task CapabilityMustBeExplicitlySupported() => WithIsolatedDatabaseAsync(async kit =>
    {
        var (agent, operations) = await SeedAsync(kit, "sftp");
        var store = Store(kit);
        Assert.Null(await store.ClaimAgentRepairAsync(Identity(agent), T0, RepairLease, default));
        foreach (var support in new[] { CapabilitySupport.Unknown, CapabilitySupport.Unsupported, CapabilitySupport.Supported })
        {
            await CapabilityAsync(kit, agent, support);
            var claimed = await store.ClaimAgentRepairAsync(Identity(agent), T0, RepairLease, default);
            if (support == CapabilitySupport.Supported)
            {
                Assert.NotNull(claimed);
                Assert.Equal(operations[0].Id, claimed.Id);
                Assert.Equal(ReplicaRepairOperation.AgentLeaseOwner(agent.AgentId.Value), claimed.LeaseOwner);
            }
            else Assert.Null(claimed);
        }
    });

    [Fact]
    public Task ClaimsOnlyOwnTenantAndOnlyAgentExecutedTargets() => WithIsolatedDatabaseAsync(async kit =>
    {
        var (first, firstOperations) = await SeedAsync(kit, "sftp");
        var (second, secondOperations) = await SeedAsync(kit, "google-drive");
        await CapabilityAsync(kit, first, CapabilitySupport.Supported);
        await CapabilityAsync(kit, second, CapabilitySupport.Supported);
        var store = Store(kit);
        var claim = await store.ClaimAgentRepairAsync(Identity(second), T0, RepairLease, default);
        Assert.NotNull(claim);
        Assert.Equal(secondOperations[0].Id, claim.Id);
        Assert.Equal(second.TenantId, claim.TenantId);
        Assert.Equal(ReplicaRepairStatus.Pending, (await store.FindOperationAsync(first.TenantId, firstOperations[0].Id, default))!.Status);

        var (backend, _) = await SeedAsync(kit, "central", "s3", "smb");
        await CapabilityAsync(kit, backend, CapabilitySupport.Supported);
        Assert.Null(await store.ClaimAgentRepairAsync(Identity(backend), T0, RepairLease, default));
    });

    [Fact]
    public Task ConcurrentPollsCannotGiveOneAgentTwoLiveRepairs() => WithIsolatedDatabaseAsync(async kit =>
    {
        var (agent, _) = await SeedAsync(kit, "sftp", "google-drive");
        await CapabilityAsync(kit, agent, CapabilitySupport.Supported);
        var identity = Identity(agent);
        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Store(kit).ClaimAgentRepairAsync(identity, T0, RepairLease, default)));
        Assert.Single(claims.OfType<ReplicaRepairOperation>());
        Assert.Null(await Store(kit).ClaimAgentRepairAsync(identity, T0.AddMinutes(1), RepairLease, default));

        var other = new StoreAgent(agent.TenantId, await kit.AddAgentToAsync(agent.TenantId));
        await CapabilityAsync(kit, other, CapabilitySupport.Supported);
        Assert.NotNull(await Store(kit).ClaimAgentRepairAsync(Identity(other), T0.AddMinutes(1), RepairLease, default));
    });

    [Fact]
    public Task BackendClaimsExcludeSftpAndDriveButDefaultClaimRemainsCompatible() => WithIsolatedDatabaseAsync(async kit =>
    {
        var (agent, operations) = await SeedAsync(kit, "sftp", "google-drive", "central");
        var store = Store(kit);
        var backend = await store.ClaimDueRepairAsync("backend", T0, RepairLease, default, agent.TenantId, backendExecutedOnly: true);
        Assert.NotNull(backend);
        Assert.Equal(operations[2].Id, backend.Id);
        Assert.Null(await store.ClaimDueRepairAsync("backend", T0, RepairLease, default, agent.TenantId, backendExecutedOnly: true));
        Assert.NotNull(await store.ClaimDueRepairAsync("legacy-test", T0, RepairLease, default, agent.TenantId));
    });

    [Fact]
    public Task PendingCancellationAndFutureDueTimeAreRespected() => WithIsolatedDatabaseAsync(async kit =>
    {
        var (agent, operations) = await SeedAsync(kit, "sftp");
        await CapabilityAsync(kit, agent, CapabilitySupport.Supported);
        var store = Store(kit);
        Assert.Null(await store.ClaimAgentRepairAsync(Identity(agent), T0.AddSeconds(-1), RepairLease, default));
        await using (var context = kit.Database.CreatePlatformContext())
        {
            var operation = await context.ReplicaRepairOperations.SingleAsync(op => op.Id == operations[0].Id);
            operation.RequestCancellation(T0);
            await context.SaveChangesAsync();
        }
        Assert.Null(await store.ClaimAgentRepairAsync(Identity(agent), T0, RepairLease, default));
        var cancelled = await store.FindOperationAsync(agent.TenantId, operations[0].Id, default);
        Assert.Equal(ReplicaRepairStatus.Cancelled, cancelled!.Status);
        Assert.Equal(0, cancelled.AttemptCount);
    });

    [Fact]
    public Task WorkerLeavesExternalToExternalRepairsForAgents() => WithIsolatedDatabaseAsync(async kit =>
    {
        var (agent, operations) = await SeedAsync(kit, "sftp", "google-drive");
        var services = new ServiceCollection();
        services.AddSingleton<IReplicaRepairStore>(Store(kit));
        await using var provider = services.BuildServiceProvider();
        using var worker = new AutomaticReplicaRepairWorker(provider.GetRequiredService<IServiceScopeFactory>(),
            new MutableClock(T0), NullLogger<AutomaticReplicaRepairWorker>.Instance);

        Assert.Equal(0, await worker.TickAsync(agent.TenantId));
        foreach (var operation in operations)
        {
            var stored = await Store(kit).FindOperationAsync(agent.TenantId, operation.Id, default);
            Assert.Equal(ReplicaRepairStatus.Pending, stored!.Status);
            Assert.Equal(0, stored.AttemptCount);
        }
    });

    [Fact]
    public Task CentralSourceRepairKeepsItsBackendClaimPath() => WithIsolatedDatabaseAsync(async kit =>
    {
        var (agent, operations) = await SeedAsync(kit, "sftp");
        await CapabilityAsync(kit, agent, CapabilitySupport.Supported);
        await using (var context = kit.Database.CreatePlatformContext())
        {
            var operation = await context.ReplicaRepairOperations.SingleAsync(op => op.Id == operations[0].Id);
            var centralConnectionId = Guid.NewGuid();
            context.StorageConnections.Add(new StorageConnection(centralConnectionId, agent.TenantId, "Repair central source", "central",
                StorageOwnership.PlatformManaged, StorageQuotaScopes.Managed, new Dictionary<string, string>(), false, T0));
            var central = new BackupReplica(Guid.NewGuid(), agent.TenantId, operation.ArtifactId, "central", "central", "scope/central.bak",
                100, ArtifactHash, "sha256", "Verified", "ReadBack", T0, T0,
                BackupStorageExecutionLocation.Backend, storageConnectionId: centralConnectionId);
            context.BackupReplicas.Add(central);
            operation.SetSourceReplica(central.Id);
            await context.SaveChangesAsync();
        }
        var store = Store(kit);
        Assert.Null(await store.ClaimAgentRepairAsync(Identity(agent), T0, RepairLease, default));
        Assert.NotNull(await store.ClaimDueRepairAsync("backend", T0, RepairLease, default, agent.TenantId, backendExecutedOnly: true));
    });

    [Fact]
    public Task AgentClaimsShareTenantAndGlobalCapacityWithBackendClaims() => WithIsolatedDatabaseAsync(async kit =>
    {
        var store = Store(kit);
        foreach (var index in Enumerable.Range(0, 2))
        {
            var (agent, _) = await SeedAsync(kit, "sftp", "google-drive", "sftp");
            var second = new StoreAgent(agent.TenantId, await kit.AddAgentToAsync(agent.TenantId));
            var third = new StoreAgent(agent.TenantId, await kit.AddAgentToAsync(agent.TenantId));
            foreach (var owner in new[] { agent, second, third }) await CapabilityAsync(kit, owner, CapabilitySupport.Supported);
            if (index == 0)
                Assert.NotNull(await store.ClaimDueRepairAsync("existing-backend", T0, RepairLease, default, agent.TenantId));
            else
                Assert.NotNull(await store.ClaimAgentRepairAsync(Identity(agent), T0, RepairLease, default));
            Assert.NotNull(await store.ClaimAgentRepairAsync(Identity(second), T0, RepairLease, default));
            Assert.Null(await store.ClaimAgentRepairAsync(Identity(third), T0, RepairLease, default));
        }
        var (waiting, _) = await SeedAsync(kit, "sftp");
        await CapabilityAsync(kit, waiting, CapabilitySupport.Supported);
        Assert.Null(await store.ClaimAgentRepairAsync(Identity(waiting), T0, RepairLease, default));
    });

    private static async Task CapabilityAsync(BackupStoreHarness kit, StoreAgent agent, CapabilitySupport support)
    {
        await using var context = kit.Database.CreatePlatformContext();
        await context.AgentCapabilities.Where(row => row.TenantId == agent.TenantId && row.AgentId == agent.AgentId
            && row.Identifier == ReplicaLifecycleProtocol.Capability).ExecuteDeleteAsync();
        context.AgentCapabilities.Add(new AgentCapabilityRecord(agent.AgentId, agent.TenantId, ReplicaLifecycleProtocol.Capability, support, null, "repair-tests", T0));
        await context.SaveChangesAsync();
    }

    private static async Task<(StoreAgent Agent, ReplicaRepairOperation[] Operations)> SeedAsync(BackupStoreHarness kit, params string[] providers)
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
        var sourceConnectionId = Guid.NewGuid();
        context.StorageConnections.Add(new StorageConnection(sourceConnectionId, agent.TenantId, "Repair source", "s3",
            StorageOwnership.TenantManaged, StorageQuotaScopes.Tenant, new Dictionary<string, string>(), false, T0));
        var source = new BackupReplica(Guid.NewGuid(), agent.TenantId, artifact.Id, "s3", sourceConnectionId.ToString("D"), "scope/source.bak",
            artifact.SizeBytes, artifact.ArtifactChecksum, artifact.ChecksumAlgorithm, "Verified", "ReadBack", T0, T0,
            BackupStorageExecutionLocation.Backend, storageConnectionId: sourceConnectionId);
        context.BackupReplicas.Add(source);
        var operations = providers.Select((provider, index) => new ReplicaRepairOperation(Guid.NewGuid(), agent.TenantId, artifact.Id,
            Guid.NewGuid(), provider, source.Id, T0.AddSeconds(index), nextRetryAt: T0)).ToArray();
        context.ReplicaRepairOperations.AddRange(operations);
        await context.SaveChangesAsync();
        return (agent, operations);
    }
}
