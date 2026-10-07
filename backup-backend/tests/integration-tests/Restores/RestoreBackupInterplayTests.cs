using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Restores;
using static BackupPlatform.IntegrationTests.RestoreWorld;

namespace BackupPlatform.IntegrationTests;

/// <summary>
/// A restore never runs beside a backup (or another restore) of the same agent, and the other way round. Both claims decide under the agent's row lock,
/// so whichever commits first holds the agent and the other is refused, never both.
/// </summary>
public sealed class RestoreBackupInterplayTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private readonly RestoreWorld _world = new(fixture);
    private static DateTimeOffset At(int seconds) => Now.AddSeconds(seconds);

    [Fact]
    public async Task ARestoreIsNotHandedOutWhileTheAgentHoldsALiveBackupAndIsAsSoonAsItEnds()
    {
        var seed = await _world.SeedCentralAsync();
        var restores = _world.Store();
        var backups = _world.BackupStore();
        var operation = await _world.CreateAsync(seed, restores, At(0));
        var job = await _world.Harness.AddJobAsync(seed.Agent);
        await _world.Harness.StartRunAsync(backups, job, "backup-1", At(1));
        var backup = await BackupStoreHarness.ClaimAsync(backups, seed.Agent, At(10));
        Assert.NotNull(backup);

        Assert.Null(await ClaimAsync(restores, seed.Agent, At(11)));
        Assert.Equal(RestoreStatus.Pending, (await _world.OperationAsync(operation.Id)).Status);

        await BackupStoreHarness.StartAttemptAsync(backups, backup, At(12));
        await BackupStoreHarness.CompleteAsync(backups, backup, BackupStoreHarness.Cancelled(), At(13));
        var claim = await ClaimAsync(restores, seed.Agent, At(14));

        Assert.Equal(operation.Id, claim!.Operation.Id);
    }

    [Fact]
    public async Task ABackupWhoseLeaseExpiredDoesNotHoldTheAgent()
    {
        var seed = await _world.SeedCentralAsync();
        var restores = _world.Store();
        var backups = _world.BackupStore();
        var operation = await _world.CreateAsync(seed, restores, At(0));
        var job = await _world.Harness.AddJobAsync(seed.Agent);
        await _world.Harness.StartRunAsync(backups, job, "backup-1", At(1));
        Assert.NotNull(await BackupStoreHarness.ClaimAsync(backups, seed.Agent, At(10)));

        Assert.Null(await ClaimAsync(restores, seed.Agent, At(10) + BackupStoreHarness.Lease - TimeSpan.FromSeconds(1)));
        var claim = await ClaimAsync(restores, seed.Agent, At(10) + BackupStoreHarness.Lease);

        Assert.Equal(operation.Id, claim!.Operation.Id);
    }

    [Fact]
    public async Task ABackupIsNotHandedOutWhileTheAgentHoldsALiveRestoreAndIsAsSoonAsItEnds()
    {
        var seed = await _world.SeedCentralAsync();
        var restores = _world.Store();
        var backups = _world.BackupStore();
        await _world.CreateAsync(seed, restores, At(0));
        var job = await _world.Harness.AddJobAsync(seed.Agent);
        await _world.Harness.StartRunAsync(backups, job, "backup-1", At(1));
        var restore = await RunToAsync(restores, seed.Agent, At(10), RestoreStages.DownloadingArtifact);

        Assert.Null(await BackupStoreHarness.ClaimAsync(backups, seed.Agent, At(11)));
        Assert.Null(await BackupStoreHarness.ClaimAsync(_world.BackupStore(), seed.Agent, At(10) + Lease - TimeSpan.FromSeconds(1)));

        Assert.Equal(BackupMutationResult.Accepted, await CompleteAsync(restores, restore, At(20), Failed(RestoreFailureCodes.ChecksumMismatch)));
        Assert.NotNull(await BackupStoreHarness.ClaimAsync(backups, seed.Agent, At(21)));
    }

    [Fact]
    public async Task ABackupIsHandedOutAgainOnceTheRestoresLeaseHasExpired()
    {
        var seed = await _world.SeedCentralAsync();
        var restores = _world.Store();
        var backups = _world.BackupStore();
        await _world.CreateAsync(seed, restores, At(0));
        var job = await _world.Harness.AddJobAsync(seed.Agent);
        await _world.Harness.StartRunAsync(backups, job, "backup-1", At(1));
        await RunToAsync(restores, seed.Agent, At(10));

        Assert.NotNull(await BackupStoreHarness.ClaimAsync(backups, seed.Agent, At(10) + Lease));
    }

    [Fact]
    public async Task ARestoreOfOneAgentDoesNotBlockTheBackupsOrRestoresOfItsSiblings()
    {
        var seed = await _world.SeedCentralAsync();
        var sibling = await _world.AddAgentAsync(seed.TenantId);
        var restores = _world.Store();
        var backups = _world.BackupStore();
        await _world.CreateAsync(seed, restores, At(0));
        var siblingRestore = (await restores.CreateAsync(Command(seed.TenantId, seed.ArtifactId, sibling.AgentId, now: At(1)), default)).Operation!;
        var job = await _world.Harness.AddJobAsync(sibling);
        await _world.Harness.StartRunAsync(backups, job, "backup-1", At(2));
        await RunToAsync(restores, seed.Agent, At(10));

        Assert.NotNull(await BackupStoreHarness.ClaimAsync(backups, sibling, At(11)));
        // The sibling now holds a backup of its own, so its restore waits; the first agent's restore was never the reason.
        Assert.Null(await ClaimAsync(restores, sibling, At(12)));
        Assert.Equal(RestoreStatus.Pending, (await _world.OperationAsync(siblingRestore.Id)).Status);
    }

    [Fact]
    public async Task ARestoreAndABackupClaimingTheSameAgentAtOnceNeverBothGetIt()
    {
        for (var iteration = 0; iteration < 15; iteration++)
        {
            var seed = await _world.SeedCentralAsync();
            await _world.CreateAsync(seed, _world.Store(), At(0));
            var setup = _world.BackupStore();
            var job = await _world.Harness.AddJobAsync(seed.Agent);
            await _world.Harness.StartRunAsync(setup, job, "backup-1", At(1));
            var gate = new RaceGate();
            var restoring = gate.Run(() => ClaimAsync(_world.Store(), seed.Agent, At(10)));
            var backing = gate.Run(() => BackupStoreHarness.ClaimAsync(_world.BackupStore(), seed.Agent, At(10)));
            gate.Open();

            var restore = await restoring;
            var backup = await backing;

            Assert.True((restore is null) != (backup is null), "Exactly one of the two jobs may hold the agent.");
        }
    }
}
