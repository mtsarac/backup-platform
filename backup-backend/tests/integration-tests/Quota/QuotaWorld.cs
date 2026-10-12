using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Backups;
using BackupPlatform.Infrastructure.ManagedService;
using BackupPlatform.Infrastructure.Persistence;
using BackupPlatform.ManagedService.Administration;
using BackupPlatform.ManagedService.Plans;
using BackupPlatform.ManagedService.Quota;
using BackupPlatform.ManagedService.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Completion = BackupPlatform.Application.Backups.BackupCompletion;

namespace BackupPlatform.IntegrationTests.Quota;

/// <summary>One stored replica to publish: where it lives. Every replica of an artifact has the artifact's size (a database invariant).</summary>
internal sealed record ReplicaSpec(string Provider, string Target)
{
    public static ReplicaSpec Central { get; } = new("central", "managed");
    public static ReplicaSpec GoogleDriveLike { get; } = new("googledrive", "drive-1");
    public static ReplicaSpec SftpLike { get; } = new("sftp", "sftp-1");
}

/// <summary>
/// Real PostgreSQL, real stores, real admission. Every method returns independent objects (own pool) so racing callers behave like separate backend
/// nodes. Times are explicit; the same fixed clock (<see cref="T0"/>) is used unless a test says otherwise.
/// </summary>
internal sealed class QuotaWorld(PostgreSqlFixture database)
{
    public const long Gb = 1_000_000_000;
    public static readonly DateTimeOffset T0 = BackupStoreHarness.T0;

    public PostgreSqlFixture Database { get; } = database;
    public BackupStoreHarness Harness { get; } = new(database);
    public QuotaOptions QuotaOptions { get; init; } = new();

    public DbContextOptions<PlatformDbContext> Options(int maxPoolSize = 16) => new DbContextOptionsBuilder<PlatformDbContext>()
        .UseNpgsql(new NpgsqlConnectionStringBuilder(Database.ConnectionString) { MaxPoolSize = maxPoolSize }.ConnectionString).Options;

    public PostgreSqlBackupAdmissionPolicy Admission() => new(Options(), QuotaOptions, NullLogger<PostgreSqlBackupAdmissionPolicy>.Instance);

    /// <summary>A run store whose new-run gate is the real admission policy (or the one given, e.g. a counting wrapper).</summary>
    public PostgreSqlBackupRunStore RunStore(IBackupAdmissionPolicy? admission = null, BackupExecutionOptions? limits = null) =>
        new(Options(), admission ?? Admission(), limits ?? BackupStoreHarness.Limits(), () => 0);

    public PostgreSqlBackupScheduleStore ScheduleStore(IBackupAdmissionPolicy? admission = null) => new(Options(), admission ?? Admission());

    public PostgreSqlStorageUsageService Usage() => new(Options(), QuotaOptions, NullLogger<PostgreSqlStorageUsageService>.Instance);

    public PostgreSqlQuotaPolicyStore PolicyStore() => new(Options(), Usage(), TimeProvider.System, NullLogger<PostgreSqlQuotaPolicyStore>.Instance);

    public PostgreSqlManagedServiceStore ManagedStore() => new(Options(), Usage(), TimeProvider.System);

    private UserId? _actor;

    /// <summary>A real user: subscriptions reference the assigning administrator by foreign key.</summary>
    public async Task<UserId> ActorAsync()
    {
        if (_actor is not null)
        {
            return _actor;
        }
        await using var world = await AgentWorld.CreateAsync(Database);
        return _actor = (await world.SeedUserAsync(platformAdmin: true)).UserId;
    }

    /// <summary>A new plan (a row); <paramref name="limitBytes"/> null is an unlimited plan.</summary>
    public async Task<PlanId> PlanAsync(long? limitBytes, string? policyKey = null)
    {
        var plan = new Plan(PlanId.New(), $"Quota plan {Guid.NewGuid():N}", limitBytes, policyKey, true, DateTimeOffset.UtcNow);
        var change = await ManagedStore().CreatePlanAsync(plan, await ActorAsync(), DateTimeOffset.UtcNow, default);
        Assert.Equal(ManagedServiceOutcome.Changed, change.Outcome);
        return plan.Id;
    }

    public async Task AssignAsync(TenantId tenantId, PlanId planId, StorageLimitOverride? limitOverride = null, DateTimeOffset? endsAt = null)
    {
        var change = await ManagedStore().AssignPlanAsync(tenantId, planId, limitOverride ?? StorageLimitOverride.None, endsAt, await ActorAsync(), DateTimeOffset.UtcNow, default);
        Assert.Equal(ManagedServiceOutcome.Changed, change.Outcome);
    }

    /// <summary>A tenant with one enrolled agent and an active subscription to a new plan of <paramref name="limitBytes"/> (null: unlimited; omit for 5 GB).</summary>
    public async Task<StoreAgent> AgentAsync(long? limitBytes = 5 * Gb, string? policyKey = null, bool subscribe = true)
    {
        var agent = await Harness.AddAgentAsync();
        await using (var context = Database.CreatePlatformContext())
        {
            context.AgentCapabilities.Add(new BackupPlatform.Agents.Capabilities.AgentCapabilityRecord(agent.AgentId, agent.TenantId,
                BackupPlatform.Contracts.Backups.BackupTaskProtocol.QuotaBoundedUploadCapability,
                BackupPlatform.Domain.Capabilities.CapabilitySupport.Supported, null, "quota", T0));
            await context.SaveChangesAsync();
        }
        if (subscribe)
        {
            await AssignAsync(agent.TenantId, await PlanAsync(limitBytes, policyKey));
        }
        return agent;
    }

    public Task<BackupJob> JobAsync(StoreAgent agent, BackupOverlapPolicy overlap = BackupOverlapPolicy.Queue, BackupStorageExecutionLocation location = BackupStorageExecutionLocation.Backend) =>
        AddJobAsync(agent, overlap, location);

    private async Task<BackupJob> AddJobAsync(StoreAgent agent, BackupOverlapPolicy overlap, BackupStorageExecutionLocation location)
    {
        var configuration = BackupStoreHarness.Configuration(location == BackupStorageExecutionLocation.Agent ? "local" : "central", location);
        var job = new BackupJob(Guid.NewGuid(), agent.TenantId, "Database", agent.AgentId, configuration, T0, null, overlap);
        return (await RunStore().CreateJobAsync(job, default))!;
    }

    /// <summary>
    /// Seeds historical usage through the real claim/start/completion lifecycle, with explicit test-only admission bypass.
    /// New-run admission itself is exercised separately by the calling test.
    /// </summary>
    public async Task<(BackupRun Run, Guid ArtifactId)> PublishAsync(
        PostgreSqlBackupRunStore store, StoreAgent agent, BackupJob job, long sizeBytes, IReadOnlyList<ReplicaSpec> replicas, DateTimeOffset at, string? key = null)
    {
        await using (var capabilityContext = Database.CreatePlatformContext())
        {
            var capability = BackupPlatform.Contracts.Backups.BackupTaskProtocol.QuotaBoundedUploadCapability;
            if (!await capabilityContext.AgentCapabilities.AnyAsync(record => record.AgentId == agent.AgentId && record.Identifier == capability))
            {
                capabilityContext.AgentCapabilities.Add(new BackupPlatform.Agents.Capabilities.AgentCapabilityRecord(agent.AgentId, agent.TenantId,
                    capability, BackupPlatform.Domain.Capabilities.CapabilitySupport.Supported, null, "historical-fixture", at));
                await capabilityContext.SaveChangesAsync();
            }
        }
        var started = await RunStore(AdmitAllBackupAdmission.Instance).StartRunAsync(agent.TenantId, job.Id, key ?? $"run-{Guid.NewGuid():N}", at, default);
        Assert.Equal(BackupStartOutcome.Started, started.Outcome);
        await Harness.SnapshotDestinationsAsync(started.Run!.Id, [.. replicas.Select(replica => replica.Provider).Distinct()]);
        var claim = await BackupStoreHarness.ClaimAsync(store, agent, at);
        Assert.NotNull(claim);
        await BackupStoreHarness.StartAttemptAsync(store, claim!, at);
        await BackupStoreHarness.CompleteAsync(store, claim!, Success(sizeBytes, replicas, at), at);
        await using var context = Database.CreatePlatformContext();
        var artifactId = await context.BackupArtifacts.AsNoTracking().Where(artifact => artifact.AttemptId == claim!.Attempt.Id).Select(artifact => artifact.Id).SingleAsync();
        return (started.Run!, artifactId);
    }

    public static Completion Success(long sizeBytes, IReadOnlyList<ReplicaSpec> replicas, DateTimeOffset at) => new("Succeeded", null, new BackupArtifactResult(
        sizeBytes, BackupStoreHarness.SourceHash, BackupStoreHarness.ArtifactHash, "sha256", at, "{\"DatabaseName\":\"Database\"}", "{\"CopyOnly\":true}",
        replicas.Select(replica => new BackupReplicaResult(replica.Provider, replica.Target, $"{replica.Provider}/{Guid.NewGuid():N}/backup.bak", sizeBytes,
            BackupStoreHarness.ArtifactHash, "sha256", "Verified", "ReadBack", at, at)).ToArray()));

    /// <summary>The projection row as stored (null when none), read straight from the table.</summary>
    public async Task<TenantStorageUsage?> StoredUsageAsync(TenantId tenantId)
    {
        await using var context = Database.CreatePlatformContext();
        return await context.TenantStorageUsages.AsNoTracking().SingleOrDefaultAsync(row => row.TenantId == tenantId);
    }

    /// <summary>Overwrites the projection's figures behind everyone's back (to simulate drift).</summary>
    public async Task CorruptUsageAsync(TenantId tenantId, long usedBytes, long reservedBytes)
    {
        await using var context = Database.CreatePlatformContext();
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE tenant_storage_usage SET \"UsedBytes\" = {usedBytes}, \"ReservedBytes\" = {reservedBytes} WHERE \"TenantId\" = {tenantId.Value}");
    }

    /// <summary>The source-of-truth usage computed by a deliberately independent, simple SQL query (no accumulator): occupying replicas = every status except Deleted.</summary>
    public async Task<(long Logical, long Physical)> SourceUsageAsync(TenantId tenantId, string? provider = null)
    {
        await using var connection = new NpgsqlConnection(Database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT
              COALESCE((SELECT SUM(a."SizeBytes") FROM backup_artifacts a WHERE a."TenantId" = @t AND EXISTS (
                  SELECT 1 FROM backup_replicas r WHERE r."ArtifactId" = a."Id" AND r."TenantId" = a."TenantId" AND r."Status" <> 'Deleted' AND (@p::text IS NULL OR r."StorageProvider" = @p))), 0),
              COALESCE((SELECT SUM(r."SizeBytes") FROM backup_replicas r WHERE r."TenantId" = @t AND r."Status" <> 'Deleted' AND (@p::text IS NULL OR r."StorageProvider" = @p)), 0)
            """, connection);
        command.Parameters.AddWithValue("t", tenantId.Value);
        command.Parameters.AddWithValue("p", (object?)provider ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetInt64(0), reader.GetInt64(1));
    }
}
