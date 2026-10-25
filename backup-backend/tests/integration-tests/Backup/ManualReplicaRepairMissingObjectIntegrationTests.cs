using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Infrastructure.Backups;
using BackupPlatform.Infrastructure.Restores;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackupPlatform.IntegrationTests.Backup;

public sealed partial class ManualReplicaRepairIntegrationTests
{
    [Fact]
    public async Task A_stored_object_that_vanished_is_noticed_by_a_repair_read_and_its_destination_can_then_be_rebuilt()
    {
        var setup = await SetupAsync(verified: new HashSet<string> { "central", "sftp" });
        var tenant = setup.Agent.TenantId;
        var repairs = new ReplicaRepairService(setup.RepairStore, setup.Resolver, TimeProvider.System, NullLogger<ReplicaRepairService>.Instance,
            new PostgreSqlReplicaMissingReporter(World.Options()));
        var central = (InMemoryStorageProvider)setup.Resolver.Resolve(tenant, "central", "central")!;
        central.Storage.Clear();

        var refused = await repairs.StartRepairAsync(tenant, setup.Artifact.Id, new(StorageConnectionId: setup.Central.Id), null, default);
        Assert.Equal(ReplicaRepairFailureCodes.AlreadyVerified, refused.Code);

        // Repairing the missing Drive copy reads the central object first and finds it gone; the Drive target cannot use the SFTP copy from the backend, so it does not succeed.
        var toDrive = await repairs.StartRepairAsync(tenant, setup.Artifact.Id, new(StorageConnectionId: setup.Drive.Id), null, default);
        Assert.NotEqual("Succeeded", (await repairs.ExecuteRepairAsync(tenant, setup.Artifact.Id, toDrive.Operation!.Id))!.Status);

        await using (var context = fixture.CreatePlatformContext())
        {
            var lost = await context.BackupReplicas.SingleAsync(r => r.ArtifactId == setup.Artifact.Id && r.StorageProvider == "central");
            Assert.Equal(("Failed", BackupReplica.MissingVerificationMethod, null), (lost.VerificationState, lost.VerificationMethod, lost.VerifiedAt));
        }

        var rebuilt = await repairs.StartRepairAsync(tenant, setup.Artifact.Id, new(StorageConnectionId: setup.Central.Id), null, default);
        Assert.True(rebuilt.Success, rebuilt.Message);
        Assert.Equal("Succeeded", (await repairs.ExecuteRepairAsync(tenant, setup.Artifact.Id, rebuilt.Operation!.Id))!.Status);

        await using var verify = fixture.CreatePlatformContext();
        var artifact = await verify.BackupArtifacts.Include(a => a.Replicas).SingleAsync(a => a.Id == setup.Artifact.Id);
        Assert.Equal(BackupReplicationStatuses.Degraded, BackupReplicationEvaluator.ForArtifact(2, 3, artifact.Replicas).Status);
        Assert.Equal(1, artifact.Replicas.Count(r => r.StorageProvider == "central" && r.VerificationState == "Verified"));
    }

    [Fact]
    public async Task Reporting_a_missing_object_ignores_unknown_and_unverified_replicas_and_is_idempotent()
    {
        var setup = await SetupAsync(verified: new HashSet<string> { "central" });
        var reporter = new PostgreSqlReplicaMissingReporter(World.Options());
        var replicaId = setup.Artifact.Replicas.Single().Id;

        await reporter.ReportMissingAsync(setup.Agent.TenantId, Guid.NewGuid(), default);
        await reporter.ReportMissingAsync(setup.Agent.TenantId, replicaId, default);
        await reporter.ReportMissingAsync(setup.Agent.TenantId, replicaId, default);

        await using var context = fixture.CreatePlatformContext();
        Assert.Equal("Failed", (await context.BackupReplicas.SingleAsync(r => r.Id == replicaId)).VerificationState);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_restore_probe_marks_the_primary_only_when_its_object_is_gone_not_when_it_is_unreadable(bool objectGone)
    {
        var setup = await SetupAsync(verified: new HashSet<string> { "central", "sftp" });
        var world = new RestoreWorld(fixture);
        await world.EnableRestoreAsync(setup.Agent);
        var now = DateTimeOffset.UtcNow;
        var store = new PostgreSqlRestoreStore(World.Options(), setup.Resolver);
        await store.CreateAsync(RestoreWorld.Command(setup.Agent.TenantId, setup.Artifact.Id, setup.Agent.AgentId, now: now), default);
        var primary = setup.Artifact.Replicas.Single(replica => replica.StorageProvider == "central");
        var provider = (InMemoryStorageProvider)setup.Resolver.Resolve(setup.Agent.TenantId, primary.StorageProvider, primary.StorageTarget)!;
        if (objectGone) provider.Storage.Clear(); else provider.ShouldFailRead = true;

        var claim = (await store.ClaimNextAsync(setup.Agent.TenantId, setup.Agent.AgentId, now, RestoreWorld.Lease, RestoreWorld.Unclaimed, default))!;

        Assert.NotEqual(primary.Id, claim.Replica.Id);
        await using var context = fixture.CreatePlatformContext();
        Assert.Equal(objectGone ? "Failed" : "Verified", (await context.BackupReplicas.SingleAsync(r => r.Id == primary.Id)).VerificationState);
    }
}
