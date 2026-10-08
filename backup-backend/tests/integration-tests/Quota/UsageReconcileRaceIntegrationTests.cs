using BackupPlatform.Domain.Backups;
using BackupPlatform.ManagedService.Quota;

namespace BackupPlatform.IntegrationTests.Quota;

/// <summary>
/// A recomputation must never overwrite the adjustment of a save that committed around it, and no pair of writers may wait on each other's rows:
/// the figure at the end equals what the sources say, whatever the interleaving.
/// </summary>
public sealed class UsageReconcileRaceIntegrationTests(PostgreSqlFixture database) : IClassFixture<PostgreSqlFixture>
{
    private const long Gb = QuotaWorld.Gb;
    private readonly QuotaWorld _world = new(database);

    [Fact]
    public async Task PublishesAndDeletionsRacingReconcilesAndRebuildsNeverLoseOrInventBytes()
    {
        var first = await _world.AgentAsync(100 * Gb);
        var tenantId = first.TenantId;
        var actor = await _world.ActorAsync();
        var agents = new List<StoreAgent> { first };
        for (var index = 1; index < 6; index++)
        {
            agents.Add(new StoreAgent(tenantId, await _world.Harness.AddAgentToAsync(tenantId)));
        }
        var jobs = new List<BackupJob>();
        foreach (var agent in agents)
        {
            jobs.Add(await _world.JobAsync(agent));
        }
        // Artifacts that exist before the race, so the deletions below have something to remove while publishes and recomputations run.
        var existing = new List<Guid>();
        for (var index = 0; index < 3; index++)
        {
            existing.Add((await _world.PublishAsync(_world.RunStore(), agents[index], jobs[index], Gb, [ReplicaSpec.Central], QuotaWorld.T0)).ArtifactId);
        }

        var racers = new List<Task>();
        racers.AddRange(agents.Select((agent, index) =>
            Task.Run(() => _world.PublishAsync(_world.RunStore(), agent, jobs[index], 2 * Gb, [ReplicaSpec.Central, ReplicaSpec.SftpLike], QuotaWorld.T0.AddMinutes(index + 1)))));
        racers.AddRange(existing.Select(artifactId => Task.Run(() => _world.ChangeArtifactAsync(artifactId, artifact => artifact.RequestDelete(QuotaWorld.T0, null)))));
        for (var round = 0; round < 6; round++)
        {
            racers.Add(Task.Run(() => _world.Usage().ReconcileAsync(tenantId, null, DateTimeOffset.UtcNow, default)));
            racers.Add(Task.Run(() => _world.Usage().RebuildAsync(tenantId, actor, DateTimeOffset.UtcNow, default)));
        }
        await Task.WhenAll(racers);

        // Soft-deleted artifacts still occupy storage, so nothing was lost: 3 GB that existed + 6 x 2 GB published.
        await _world.AssertUsedAsync(tenantId, 15 * Gb);
        await _world.AssertOracleAsync(tenantId, 15 * Gb, 15 * Gb + 6 * 2 * Gb);
        await _world.AssertNoDriftAsync(tenantId);
    }
}
