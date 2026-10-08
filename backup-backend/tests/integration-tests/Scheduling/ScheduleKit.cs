using BackupPlatform.Agents.Connectivity;
using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Backups;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace BackupPlatform.IntegrationTests.Scheduling;

/// <summary>
/// The real scheduling store and use-case against a real database, driven with explicit times. Every call that builds a store or a service returns
/// an independent instance with its own options, i.e. what a separate backend node would hold.
/// </summary>
internal sealed class ScheduleKit(PostgreSqlFixture database)
{
    private const int MaxConnections = 16;

    public BackupStoreHarness Runs { get; } = new(database);
    public PostgreSqlFixture Database { get; } = database;

    public static DateTimeOffset H(int hours, int minutes = 0, int seconds = 0) => BackupStoreHarness.T0.AddHours(hours).AddMinutes(minutes).AddSeconds(seconds);

    private DbContextOptions<PlatformDbContext> Options() => new DbContextOptionsBuilder<PlatformDbContext>()
        .UseNpgsql(new NpgsqlConnectionStringBuilder(Database.ConnectionString) { MaxPoolSize = MaxConnections }.ConnectionString).Options;

    public PostgreSqlBackupScheduleStore Store(AgentConnectivityOptions? connectivity = null) => new(Options(), AdmitAllBackupAdmission.Instance, connectivity);

    public BackupScheduleService Service(TimeProvider time, IBackupScheduleCoordinator? coordinator = null, RecordingSignal? signal = null,
        BackupSchedulingOptions? options = null, BackupExecutionOptions? limits = null) =>
        new(Store(), Runs.Store(limits), coordinator ?? new RecordingCoordinator(), signal ?? new RecordingSignal(), time, options ?? new BackupSchedulingOptions(),
            NullLogger<BackupScheduleService>.Instance);

    public async Task<BackupJob> AddJobAsync(StoreAgent agent, string cron = "0 * * * *", string zone = "UTC", TimeSpan? grace = null,
        BackupOverlapPolicy overlap = BackupOverlapPolicy.Skip, DateTimeOffset? from = null, bool scheduleEnabled = true, BackupRetryPolicy? retry = null,
        bool seconds = false)
    {
        var schedule = BackupSchedule.Create(scheduleEnabled, cron, zone, grace ?? TimeSpan.FromMinutes(30), from ?? BackupStoreHarness.T0, seconds);
        var job = new BackupJob(Guid.NewGuid(), agent.TenantId, "Scheduled", agent.AgentId, BackupStoreHarness.Configuration(), BackupStoreHarness.T0, retry, overlap, schedule);
        return (await Runs.Store().CreateJobAsync(job, default))!;
    }

    /// <summary>Records a sign of life for the agent at <paramref name="at"/> (what a heartbeat or poll does).</summary>
    public async Task SeenAsync(StoreAgent agent, DateTimeOffset at)
    {
        await using var connection = new NpgsqlConnection(Database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO agent_connectivity ("AgentId", "TenantId", "LastSeenAt") VALUES (@agent, @tenant, @at)
            ON CONFLICT ("AgentId") DO UPDATE SET "LastSeenAt" = EXCLUDED."LastSeenAt"
            """, connection);
        command.Parameters.AddWithValue("agent", agent.AgentId.Value);
        command.Parameters.AddWithValue("tenant", agent.TenantId.Value);
        command.Parameters.AddWithValue("at", at);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<BackupScheduleOccurrence>> OccurrencesAsync(BackupJob job) =>
        (await Store().ListOccurrencesAsync(job.TenantId, job.Id, 200, default)).OrderBy(occurrence => occurrence.ScheduledAt).ToArray();

    public async Task<IReadOnlyList<BackupRun>> RunsOfAsync(BackupJob job) =>
        (await Runs.Store().ListRunsAsync(job.TenantId, job.Id, 200, default)).OrderBy(run => run.CreatedAt).ThenBy(run => run.Id).ToArray();

    public async Task<BackupRun> RunAsync(BackupJob job, Guid runId) => (await Runs.Store().GetRunAsync(job.TenantId, runId, default))!;

    public async Task<BackupJob> JobAsync(BackupJob job) => (await Runs.Store().GetJobAsync(job.TenantId, job.Id, default))!;

    public Task DisableAsync(BackupJob job) => Store().ModifyJobAsync(job.TenantId, job.Id, target => target.Disable(), default);

    public Task EnableAsync(BackupJob job, DateTimeOffset now) => Store().ModifyJobAsync(job.TenantId, job.Id, target => target.Enable(now), default);

    /// <summary>Claims and starts the agent's next attempt, as the Agent.Host does; null when nothing is claimable.</summary>
    public async Task<BackupClaim?> ClaimAndStartAsync(PostgreSqlBackupRunStore store, StoreAgent agent, DateTimeOffset at)
    {
        var claim = await BackupStoreHarness.ClaimAsync(store, agent, at);
        if (claim is not null) await BackupStoreHarness.StartAttemptAsync(store, claim, at.AddSeconds(1));
        return claim;
    }
}

/// <summary>A scheduler stand-in that remembers what it was asked to hold; can be told to fail, like an unreachable Quartz store.</summary>
internal sealed class RecordingCoordinator : IBackupScheduleCoordinator
{
    public Dictionary<Guid, BackupScheduleTarget> Held { get; } = [];
    public bool Failing { get; set; }
    public int Calls { get; private set; }

    public Task SynchronizeAsync(Guid jobId, BackupScheduleTarget? desired, CancellationToken cancellationToken)
    {
        Calls++;
        if (Failing) throw new InvalidOperationException("The scheduler store is unreachable.");
        if (desired is null) Held.Remove(jobId);
        else Held[jobId] = desired;
        return Task.CompletedTask;
    }

    public Task<BackupScheduleReconciliation> ReconcileAsync(IReadOnlyCollection<BackupScheduleTarget> desired, CancellationToken cancellationToken)
    {
        Calls++;
        if (Failing) throw new InvalidOperationException("The scheduler store is unreachable.");
        var wanted = desired.ToDictionary(target => target.JobId);
        var removed = Held.Keys.Where(id => !wanted.ContainsKey(id)).ToList();
        foreach (var id in removed) Held.Remove(id);
        int created = 0, rewritten = 0, unchanged = 0;
        foreach (var (id, target) in wanted)
        {
            if (!Held.TryGetValue(id, out var current)) created++;
            else if (current == target) { unchanged++; continue; }
            else rewritten++;
            Held[id] = target;
        }
        return Task.FromResult(new BackupScheduleReconciliation(created, rewritten, removed.Count, unchanged));
    }
}

internal sealed class RecordingSignal : IBackupWorkSignal
{
    public List<AgentId> Hints { get; } = [];

    public Task WorkAvailableAsync(AgentId agentId, CancellationToken cancellationToken)
    {
        lock (Hints) Hints.Add(agentId);
        return Task.CompletedTask;
    }
}
