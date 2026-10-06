using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Infrastructure.Backups;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BackupPlatform.IntegrationTests;

/// <summary>
/// Everything a retention store test needs on top of <see cref="RetentionKit"/>: stores that behave like separate backend processes (each has
/// its own options and connection pool), direct row reads that do not go through the code under test, and a start barrier for races.
/// </summary>
internal sealed class RetentionWorld(PostgreSqlFixture database)
{
    /// <summary>When the seeded backups were accepted by the backend. Policies are relative to it, so every expectation is an explicit instant.</summary>
    public static readonly DateTimeOffset Accepted = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);
    /// <summary>The smallest step the database keeps exactly (it stores microseconds; a millisecond is the readable one).</summary>
    public static readonly TimeSpan Step = TimeSpan.FromMilliseconds(1);

    // A racing test opens one connection per racer; keep each store's pool small so they cannot starve the rest of the suite.
    private const int MaxStoreConnections = 16;

    public PostgreSqlFixture Database { get; } = database;
    public BackupStoreHarness Harness { get; } = new(database);

    /// <summary>A store with its own options and pool: what another backend node would hold.</summary>
    public PostgreSqlBackupRetentionStore Store() => new(new DbContextOptionsBuilder<PlatformDbContext>()
        .UseNpgsql(new NpgsqlConnectionStringBuilder(Database.ConnectionString) { MaxPoolSize = MaxStoreConnections }.ConnectionString).Options);

    /// <summary>A new tenant, agent and job with <paramref name="policy"/>, and one backup of it accepted at <paramref name="acceptedAt"/>.</summary>
    public async Task<SeededBackup> SeedAsync(BackupRetentionPolicy? policy = null, DateTimeOffset? acceptedAt = null)
    {
        var agent = await Harness.AddAgentAsync();
        var job = await RetentionKit.AddJobAsync(Harness, agent, policy);
        return await RetentionKit.SeedAsync(Harness, agent, job, acceptedAt ?? Accepted);
    }

    public async Task<BackupReplica> ReplicaAsync(Guid replicaId)
    {
        await using var context = Database.CreatePlatformContext();
        return await context.BackupReplicas.AsNoTracking().SingleAsync(replica => replica.Id == replicaId);
    }

    public async Task<BackupArtifact> ArtifactAsync(Guid artifactId)
    {
        await using var context = Database.CreatePlatformContext();
        return await context.BackupArtifacts.AsNoTracking().Include(artifact => artifact.Replicas).SingleAsync(artifact => artifact.Id == artifactId);
    }

    /// <summary>Soft-deletes the backup by hand at <paramref name="now"/> and returns the instant its grace period ends.</summary>
    public async Task<DateTimeOffset> RequestDeleteAsync(PostgreSqlBackupRetentionStore store, SeededBackup backup, DateTimeOffset now, Guid? userId = null)
    {
        var result = await store.RequestDeleteAsync(backup.TenantId, backup.ArtifactId, userId ?? Guid.NewGuid(), now, default);
        Assert.Equal(BackupArtifactDeleteKind.Requested, result.Kind);
        return (await ReplicaAsync(backup.ReplicaId)).DeleteAfter!.Value;
    }

    /// <summary>Claims everything due at <paramref name="now"/> and returns the claim of <paramref name="backup"/>'s replica (null when it was not handed out).</summary>
    public static async Task<ClaimedReplicaDeletion?> ClaimAsync(PostgreSqlBackupRetentionStore store, SeededBackup backup, DateTimeOffset now, string owner = "node-a") =>
        Mine(await store.ClaimDueDeletionsAsync(owner, now, Lease, 500, default), backup).SingleOrDefault();

    /// <summary>The database is shared by every test of a class, so a claim or a scan also returns what earlier tests left behind: filter to this test's own replica.</summary>
    public static IEnumerable<ClaimedReplicaDeletion> Mine(IEnumerable<ClaimedReplicaDeletion> claims, SeededBackup backup) =>
        claims.Where(claim => claim.ReplicaId == backup.ReplicaId);
}

/// <summary>Releases every participant at once, so what is raced really overlaps in the database.</summary>
internal sealed class RaceGate
{
    private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<T> Run<T>(Func<Task<T>> action) => Task.Run(async () =>
    {
        await _open.Task;
        return await action();
    });

    public void Open() => _open.SetResult();
}
