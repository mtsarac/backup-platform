using BackupPlatform.Agents.Capabilities;
using BackupPlatform.Application.Backups;
using BackupPlatform.Application.Restores;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Capabilities;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Restores;
using BackupPlatform.Infrastructure.Backups;
using BackupPlatform.Infrastructure.Persistence;
using BackupPlatform.Infrastructure.Restores;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BackupPlatform.IntegrationTests;

/// <summary>A tenant with one agent that can restore, and one accepted backup of it.</summary>
internal sealed record RestoreSeed(StoreAgent Agent, SeededBackup Backup)
{
    public TenantId TenantId => Agent.TenantId;
    public AgentId AgentId => Agent.AgentId;
    public Guid ArtifactId => Backup.ArtifactId;
    public Guid ReplicaId => Backup.ReplicaId;
}

/// <summary>
/// What a restore store test needs on top of <see cref="RetentionWorld"/>: backups seeded through production code (so every row is what the platform really
/// writes), stores that behave like separate backend nodes, explicit instants, and direct row reads and writes that do not go through the code under test.
/// </summary>
internal sealed class RestoreWorld(PostgreSqlFixture database)
{
    /// <summary>When the seeded backups were accepted.</summary>
    public static readonly DateTimeOffset Accepted = RetentionWorld.Accepted;
    /// <summary>The instant restores are requested at unless a test says otherwise: an hour after the backup was accepted.</summary>
    public static readonly DateTimeOffset Now = Accepted.AddHours(1);
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan Unclaimed = TimeSpan.FromHours(1);
    public static readonly string Fingerprint = new('f', 64);
    private const int MaxStoreConnections = 16;

    public PostgreSqlFixture Database { get; } = database;
    public RetentionWorld Retention { get; } = new(database);
    public BackupStoreHarness Harness => Retention.Harness;

    /// <summary>A store with its own options and pool: what another backend node would hold.</summary>
    public PostgreSqlRestoreStore Store() => new(Options());

    /// <summary>A store that probes backend-readable replicas through <paramref name="storage"/> before it hands out a restore.</summary>
    public PostgreSqlRestoreStore Store(IBackupStorageResolver storage) => new(Options(), storage);

    public PostgreSqlBackupRunStore BackupStore() => Harness.Store(BackupStoreHarness.Limits());

    public PostgreSqlAgentReplicaDeletionStore AgentDeletionStore() => new(Options(), new BackupRetentionOptions());

    private DbContextOptions<PlatformDbContext> Options() => new DbContextOptionsBuilder<PlatformDbContext>()
        .UseNpgsql(new NpgsqlConnectionStringBuilder(Database.ConnectionString) { MaxPoolSize = MaxStoreConnections }.ConnectionString).Options;

    // ---- seeding ----

    /// <summary>A new tenant and agent (able to restore) with one backup in central storage, which any agent can reach.</summary>
    public async Task<RestoreSeed> SeedCentralAsync(BackupRetentionPolicy? policy = null, string? sourceMetadataJson = null)
    {
        var agent = await Harness.AddAgentAsync();
        await EnableRestoreAsync(agent);
        var job = await RetentionKit.AddJobAsync(Harness, agent, policy);
        return new RestoreSeed(agent, await RetentionKit.SeedAsync(Harness, agent, job, Accepted, storageProvider: "central", storageTarget: "central", sourceMetadataJson: sourceMetadataJson));
    }

    /// <summary>A new tenant and agent (able to restore) with one backup on the agent's own disk.</summary>
    public async Task<RestoreSeed> SeedAgentLocalAsync(BackupRetentionPolicy? policy = null)
    {
        var agent = await Harness.AddAgentAsync();
        await EnableRestoreAsync(agent);
        var job = await RetentionKit.AddJobAsync(Harness, agent, policy, BackupStorageExecutionLocation.Agent);
        return new RestoreSeed(agent, await RetentionKit.SeedAsync(Harness, agent, job, Accepted, storageProvider: "local", storageTarget: "local"));
    }

    /// <summary>A second backup of the same agent (its own artifact and replica).</summary>
    public async Task<SeededBackup> SeedAnotherAsync(RestoreSeed seed, BackupRetentionPolicy? policy = null)
    {
        var job = await RetentionKit.AddJobAsync(Harness, seed.Agent, policy);
        return await RetentionKit.SeedAsync(Harness, seed.Agent, job, Accepted, storageProvider: "central", storageTarget: "central");
    }

    /// <summary>Another agent of the same tenant, optionally able to restore.</summary>
    public async Task<StoreAgent> AddAgentAsync(TenantId tenantId, bool canRestore = true)
    {
        var agent = new StoreAgent(tenantId, await Harness.AddAgentToAsync(tenantId));
        if (canRestore) await EnableRestoreAsync(agent);
        return agent;
    }

    /// <summary>Records the agent's capability snapshot with the restore capability, the way a capability sync would.</summary>
    public async Task EnableRestoreAsync(StoreAgent agent, CapabilitySupport support = CapabilitySupport.Supported)
    {
        await using var context = Database.CreatePlatformContext();
        context.AgentCapabilities.Add(new AgentCapabilityRecord(agent.AgentId, agent.TenantId, RestoreAgentCapability.Identifier, support, null, "revision-1", Accepted));
        await context.SaveChangesAsync();
    }

    // ---- commands ----

    public static RestoreCommand Command(TenantId tenantId, Guid artifactId, AgentId agentId, string? key = null, Guid? replicaId = null, string? fingerprint = null,
        DateTimeOffset? now = null, RestoreMode mode = RestoreMode.NewDatabase, string databaseName = "Restored", RestoreOptions? options = null, Guid? requestedBy = null) =>
        new(tenantId, artifactId, replicaId, agentId, new RestoreTarget("sql-target", 1433, true, null, true, false), databaseName, mode, mode == RestoreMode.ReplaceExistingDatabase,
            options ?? RestoreOptions.Default, key ?? Guid.NewGuid().ToString("N"), fingerprint ?? Fingerprint, requestedBy, now ?? Now);

    public static RestoreCommand Command(RestoreSeed seed, string? key = null, Guid? replicaId = null, string? fingerprint = null, DateTimeOffset? now = null,
        RestoreMode mode = RestoreMode.NewDatabase, string databaseName = "Restored") =>
        Command(seed.TenantId, seed.ArtifactId, seed.AgentId, key, replicaId, fingerprint, now, mode, databaseName);

    /// <summary>Creates a restore of the seed's backup by its agent and fails the test if the store refused.</summary>
    public async Task<RestoreOperation> CreateAsync(RestoreSeed seed, PostgreSqlRestoreStore? store = null, DateTimeOffset? now = null, string? key = null,
        RestoreMode mode = RestoreMode.NewDatabase, string databaseName = "Restored")
    {
        var result = await (store ?? Store()).CreateAsync(Command(seed, key, now: now, mode: mode, databaseName: databaseName), default);
        Assert.Equal(RestoreCreateOutcome.Created, result.Outcome);
        return result.Operation!;
    }

    public static Task<RestoreClaim?> ClaimAsync(PostgreSqlRestoreStore store, StoreAgent agent, DateTimeOffset at) =>
        store.ClaimNextAsync(agent.TenantId, agent.AgentId, at, Lease, Unclaimed, default);

    /// <summary>Claims the agent's next restore and fails the test if nothing was handed out.</summary>
    public static async Task<RestoreClaim> MustClaimAsync(PostgreSqlRestoreStore store, StoreAgent agent, DateTimeOffset at) =>
        await ClaimAsync(store, agent, at) ?? throw new Xunit.Sdk.XunitException("The agent was expected to claim a restore but nothing was handed out.");

    public static RestoreLeaseCommand Command(RestoreClaim claim, DateTimeOffset at, Guid? token = null) =>
        new(claim.Operation.TenantId, claim.Operation.AgentId, claim.Attempt.Id, token ?? claim.Attempt.LeaseToken!.Value, at)
        { SoftwareVersion = "1.2.3", ProtocolVersion = "1.0", Platform = "Linux" };

    public static async Task StartAsync(PostgreSqlRestoreStore store, RestoreClaim claim, DateTimeOffset at)
    {
        var outcome = await store.StartAsync(Command(claim, at), Lease, default);
        Assert.Equal(BackupMutationResult.Accepted, outcome.Result);
    }

    public static Task<BackupMutationResult> ProgressAsync(PostgreSqlRestoreStore store, RestoreClaim claim, DateTimeOffset at, string stage, double? percent = null) =>
        store.ReportProgressAsync(Command(claim, at), new RestoreProgress(stage, percent), default);

    public static Task<BackupMutationResult> CompleteAsync(PostgreSqlRestoreStore store, RestoreClaim claim, DateTimeOffset at, RestoreCompletion completion, Guid? token = null) =>
        store.CompleteAsync(Command(claim, at, token), completion, default);

    public static RestoreCompletion Succeeded(string? resultJson = "{\"databaseState\":\"ONLINE\"}") => new(RestoreStatus.Succeeded, null, RestoreTargetState.Online, resultJson);

    public static RestoreCompletion Failed(string code = RestoreFailureCodes.SqlRestoreFailed, RestoreTargetState state = RestoreTargetState.NotModified) => new(RestoreStatus.Failed, code, state, null);

    public static RestoreCompletion Cancelled(RestoreTargetState state = RestoreTargetState.PartiallyRestored) => new(RestoreStatus.Cancelled, RestoreFailureCodes.Cancelled, state, null);

    /// <summary>Claims, starts and moves a restore to <paramref name="stage"/>; returns the claim (leased from <paramref name="at"/>).</summary>
    public static async Task<RestoreClaim> RunToAsync(PostgreSqlRestoreStore store, StoreAgent agent, DateTimeOffset at, string? stage = null)
    {
        var claim = await MustClaimAsync(store, agent, at);
        await StartAsync(store, claim, at);
        if (stage is not null) Assert.Equal(BackupMutationResult.Accepted, await ProgressAsync(store, claim, at, stage));
        return claim;
    }

    // ---- direct reads and writes (not through the code under test) ----

    public async Task<RestoreOperation> OperationAsync(Guid restoreId)
    {
        await using var context = Database.CreatePlatformContext();
        return await context.RestoreOperations.AsNoTracking().Include(operation => operation.Attempts).Include(operation => operation.Pin)
            .AsSplitQuery().SingleAsync(operation => operation.Id == restoreId);
    }

    public async Task<int> CountOperationsAsync(Guid artifactId)
    {
        await using var context = Database.CreatePlatformContext();
        return await context.RestoreOperations.CountAsync(operation => operation.ArtifactId == artifactId);
    }

    public async Task<int> ActivePinsAsync(Guid replicaId)
    {
        await using var context = Database.CreatePlatformContext();
        return await context.RestoreReplicaPins.CountAsync(pin => pin.ReplicaId == replicaId && pin.ReleasedAt == null);
    }

    public Task<BackupReplica> ReplicaAsync(Guid replicaId) => Retention.ReplicaAsync(replicaId);

    /// <summary>The instant the replica's grace period ends after <paramref name="seed"/>'s backup was soft-deleted at <paramref name="at"/>.</summary>
    public Task<DateTimeOffset> SoftDeleteAsync(RestoreSeed seed, DateTimeOffset at) => Retention.RequestDeleteAsync(Retention.Store(), seed.Backup, at);

    /// <summary>Runs one non-query statement as the database owner, bypassing every layer of the platform.</summary>
    public async Task<int> ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(Database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return await command.ExecuteNonQueryAsync();
    }

    public async Task<T?> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(Database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        var value1 = await command.ExecuteScalarAsync();
        return value1 is null or DBNull ? default : (T)value1;
    }
}
