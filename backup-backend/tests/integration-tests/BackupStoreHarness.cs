using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Agents;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Infrastructure.Backups;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Completion = BackupPlatform.Application.Backups.BackupCompletion;

namespace BackupPlatform.IntegrationTests;

/// <summary>An enrolled, active agent and the tenant it belongs to.</summary>
internal sealed record StoreAgent(TenantId TenantId, AgentId AgentId);

/// <summary>
/// Drives the real <see cref="PostgreSqlBackupRunStore"/> against a real database. Every <see cref="Store"/> call returns an
/// instance with its own <see cref="DbContextOptions{TContext}"/>, i.e. what a separate backend process would hold. Times are
/// passed explicitly so lifecycles are deterministic; jitter is always 0.
/// </summary>
internal sealed class BackupStoreHarness(PostgreSqlFixture database)
{
    public static readonly DateTimeOffset T0 = new(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);
    public static readonly string SourceHash = new('a', 64);
    public static readonly string ArtifactHash = new('b', 64);

    // The global limit counts live attempts of EVERY tenant in the database. Tests on a shared database therefore lift it out of the way
    // unless they are about it (those use an isolated database).
    public const int Unbounded = 100_000;

    public PostgreSqlFixture Database { get; } = database;

    // The server allows ~100 clients for every test running at once. Racing tests would otherwise open a connection per racer
    // (each of which holds one while it waits for a lock) and could starve the rest of the suite; racers beyond this simply queue for the pool.
    private const int MaxStoreConnections = 16;

    public PostgreSqlBackupRunStore Store(BackupExecutionOptions? limits = null) => new(new DbContextOptionsBuilder<PlatformDbContext>()
        .UseNpgsql(new NpgsqlConnectionStringBuilder(Database.ConnectionString) { MaxPoolSize = MaxStoreConnections }.ConnectionString).Options, AdmitAllBackupAdmission.Instance, limits, () => 0);

    public static BackupExecutionOptions Limits(int agent = 1, int tenant = Unbounded, int global = Unbounded, Dictionary<string, int>? overrides = null) => new()
    {
        AgentMaxConcurrentBackups = agent,
        TenantMaxConcurrentBackups = tenant,
        GlobalMaxConcurrentBackups = global,
        TenantOverrides = overrides ?? []
    };

    /// <summary>Runs <paramref name="test"/> against a database nobody else uses, so global capacity is exactly what the test creates.</summary>
    public static async Task WithIsolatedDatabaseAsync(Func<BackupStoreHarness, Task> test)
    {
        var isolated = new PostgreSqlFixture();
        await isolated.InitializeAsync();
        try
        {
            await test(new BackupStoreHarness(isolated));
        }
        finally
        {
            await isolated.DisposeAsync();
        }
    }

    public async Task<TenantId> AddTenantAsync()
    {
        var tenantId = TenantId.New();
        await using var context = Database.CreatePlatformContext();
        context.Tenants.Add(new Tenant(tenantId, "Backup tenant"));
        await context.SaveChangesAsync();
        return tenantId;
    }

    public async Task<AgentId> AddAgentToAsync(TenantId tenantId)
    {
        var agentId = AgentId.New();
        await using var context = Database.CreatePlatformContext();
        var agent = new BackupPlatform.Domain.Agents.Agent(agentId, tenantId, new AgentSoftware(AgentPlatform.Linux, new Version(1, 0), new AgentProtocolVersion(1, 0)));
        agent.RecordEnrollment();
        context.Agents.Add(agent);
        // Every run is a destination-list snapshot, so admission and claim need an agent that declares it; tests about the missing capability remove the row.
        context.AgentCapabilities.Add(new BackupPlatform.Agents.Capabilities.AgentCapabilityRecord(agentId, tenantId,
            BackupPlatform.Contracts.Backups.BackupTaskProtocol.MultiDestinationCapability, BackupPlatform.Domain.Capabilities.CapabilitySupport.Supported, null, "fixture", T0));
        context.AgentCapabilities.Add(new BackupPlatform.Agents.Capabilities.AgentCapabilityRecord(agentId, tenantId,
            BackupPlatform.Contracts.Backups.TaskStorageProtocol.DirectCapability, BackupPlatform.Domain.Capabilities.CapabilitySupport.Supported, null, "fixture", T0));
        await context.SaveChangesAsync();
        return agentId;
    }

    /// <summary>A new tenant with one enrolled agent.</summary>
    public async Task<StoreAgent> AddAgentAsync()
    {
        var tenantId = await AddTenantAsync();
        return new StoreAgent(tenantId, await AddAgentToAsync(tenantId));
    }

    public async Task<BackupJob> AddJobAsync(StoreAgent agent, BackupOverlapPolicy overlap = BackupOverlapPolicy.Queue, BackupRetryPolicy? retry = null)
    {
        var job = new BackupJob(Guid.NewGuid(), agent.TenantId, "Database", agent.AgentId, Configuration(), T0, retry, overlap);
        return (await Store().CreateJobAsync(job, default))!;
    }

    /// <summary>Queues a run now and returns it; fails the test if the store did not start one.</summary>
    public async Task<BackupRun> StartRunAsync(PostgreSqlBackupRunStore store, BackupJob job, string key, DateTimeOffset at)
    {
        var result = await store.StartRunAsync(job.TenantId, job.Id, key, at, default);
        Assert.Equal(BackupStartOutcome.Started, result.Outcome);
        return result.Run!;
    }

    public static Task<BackupClaim?> ClaimAsync(PostgreSqlBackupRunStore store, StoreAgent agent, DateTimeOffset at) =>
        store.ClaimNextAsync(agent.TenantId, agent.AgentId, at, Lease, default);

    public static BackupLeaseCommand Command(BackupClaim claim, DateTimeOffset at) => new(claim.Run.TenantId, claim.Run.AgentId,
        claim.Attempt.Id, claim.Attempt.LeaseToken!.Value, at) { SoftwareVersion = "1.0", ProtocolVersion = "1.0", Platform = "Linux" };

    public static async Task StartAttemptAsync(PostgreSqlBackupRunStore store, BackupClaim claim, DateTimeOffset at) =>
        Assert.Equal(BackupMutationResult.Accepted, await store.StartAttemptAsync(Command(claim, at), default));

    public static async Task CompleteAsync(PostgreSqlBackupRunStore store, BackupClaim claim, Completion completion, DateTimeOffset at) =>
        Assert.Equal(BackupMutationResult.Accepted, await store.CompleteAttemptAsync(Command(claim, at), completion, default));

    public static Completion Failed(string code) => new("Failed", code, null);

    public static Completion Cancelled() => new("Cancelled", null, null);

    public static Completion Paused() => new("Paused", "Operator", null);

    public static Completion Success(DateTimeOffset createdAt) => new("Succeeded", null, new BackupArtifactResult(100, SourceHash, ArtifactHash,
        "sha256", createdAt, "{\"DatabaseName\":\"Database\"}", "{\"CopyOnly\":true}",
        [new BackupReplicaResult("local", "default", "tenant/job/run/attempt/backup.bak", 100, ArtifactHash, "sha256", "Verified", "ReadBack", createdAt, createdAt)]));

    public static BackupConfiguration Configuration() => new("mssql", "Database", "localhost", null, true, null,
        true, false, "default", "", true, "Preferred", "Preferred")
        { Destinations = [new BackupDestination("local", null, true) { StorageExecutionLocation = BackupStorageExecutionLocation.Backend }] };

    /// <summary>The default configuration with exactly one primary destination.</summary>
    public static BackupConfiguration Configuration(string provider, BackupStorageExecutionLocation location, Guid? connectionId = null) =>
        Configuration() with { Destinations = [new BackupDestination(provider, connectionId, true) { StorageExecutionLocation = location }] };

    /// <summary>
    /// A completion is validated against the run's snapshot, so a test that seeds copies on providers other than its job's default names them there: the first
    /// provider is the primary, "local" keeps the job's execution location, the others are backend-relayed, and every connection is left to the tenant's
    /// default for its provider (as for a snapshot that predates storage connections). A snapshot that already names every provider is left as it is.
    /// </summary>
    public async Task SnapshotDestinationsAsync(Guid runId, IReadOnlyList<string> providers)
    {
        await using var context = Database.CreatePlatformContext();
        await context.Database.ExecuteSqlInterpolatedAsync($$"""
            UPDATE backup_runs SET "Configuration" = jsonb_set("Configuration", ARRAY['Destinations'], (
                SELECT jsonb_agg(jsonb_build_object('IsPrimary', named.position = 1, 'StorageProvider', named.provider, 'StorageConnectionId', 'null'::jsonb,
                    'Direct', named.provider IN ('sftp', 'google-drive', 's3', 'smb'),
                    'StorageExecutionLocation', CASE WHEN named.provider = 'local' THEN "Configuration" #> ARRAY['Destinations', '0', 'StorageExecutionLocation'] ELSE '1'::jsonb END)
                    ORDER BY named.position)
                FROM unnest({{providers.ToArray()}}::text[]) WITH ORDINALITY AS named(provider, position)))
            WHERE "Id" = {{runId}} AND NOT (
                SELECT bool_and(EXISTS (SELECT 1 FROM jsonb_array_elements("Configuration" -> 'Destinations') existing WHERE existing ->> 'StorageProvider' = wanted))
                FROM unnest({{providers.ToArray()}}::text[]) AS wanted)
            """);
    }

    /// <summary>Live slots straight from the table (independent of the store's own query): Leased/Running attempts whose lease has not expired at <paramref name="at"/>.</summary>
    public static async Task<IReadOnlyList<(Guid Tenant, Guid Agent, long Count)>> LiveSlotsAsync(string connectionString, DateTimeOffset at)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        return await LiveSlotsAsync(connection, at);
    }

    public static async Task<IReadOnlyList<(Guid Tenant, Guid Agent, long Count)>> LiveSlotsAsync(NpgsqlConnection connection, DateTimeOffset at)
    {
        await using var command = new NpgsqlCommand(
            "SELECT \"TenantId\", \"AgentId\", count(*) FROM backup_run_attempts WHERE \"Status\" IN (2,3) AND \"LeaseExpiresAt\" > @at GROUP BY 1, 2", connection);
        command.Parameters.AddWithValue("at", at);
        var slots = new List<(Guid, Guid, long)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            slots.Add((reader.GetGuid(0), reader.GetGuid(1), reader.GetInt64(2)));
        }
        return slots;
    }

    public async Task<int> CountRunsAsync(Guid jobId)
    {
        await using var context = Database.CreatePlatformContext();
        return await context.BackupRuns.CountAsync(run => run.JobId == jobId);
    }

    public async Task<int> CountAttemptsAsync(Guid runId)
    {
        await using var context = Database.CreatePlatformContext();
        return await context.BackupRunAttempts.CountAsync(attempt => attempt.RunId == runId);
    }

    public async Task<int> CountArtifactsAsync(Guid runId)
    {
        await using var context = Database.CreatePlatformContext();
        return await context.BackupArtifacts.CountAsync(artifact => context.BackupRunAttempts.Any(attempt => attempt.Id == artifact.AttemptId && attempt.RunId == runId));
    }
}
