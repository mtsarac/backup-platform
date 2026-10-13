using System.Collections.Concurrent;
using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Backups;
using BackupPlatform.ManagedService.Administration;
using BackupPlatform.ManagedService.Quota;
using BackupPlatform.ManagedService.Subscriptions;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.IntegrationTests.Quota;

/// <summary>Delegates to the real admission and remembers every question asked and every release made, so a test can prove who did (not) call it.</summary>
internal sealed class CountingAdmission(IBackupAdmissionPolicy inner) : IBackupAdmissionPolicy
{
    private readonly ConcurrentQueue<BackupAdmissionRequest> _requests = new();
    private readonly ConcurrentQueue<BackupAdmissionDecision> _decisions = new();
    private readonly ConcurrentQueue<Guid> _released = new();

    /// <summary>Runs right after an ALLOWED decision, before the caller creates its run: lets a test make that creation fail in a controlled way.</summary>
    public Func<BackupAdmissionRequest, Task>? AfterAdmitted { get; init; }

    public int Calls => _requests.Count;

    public IReadOnlyList<BackupAdmissionRequest> Requests => [.. _requests];

    public IReadOnlyList<BackupAdmissionDecision> Decisions => [.. _decisions];

    public IReadOnlyList<Guid> Released => [.. _released];

    public async Task<BackupAdmissionDecision> AdmitNewRunAsync(BackupAdmissionRequest request, CancellationToken cancellationToken)
    {
        _requests.Enqueue(request);
        var decision = await inner.AdmitNewRunAsync(request, cancellationToken);
        _decisions.Enqueue(decision);
        if (decision.Allowed && AfterAdmitted is not null)
        {
            await AfterAdmitted(request);
        }
        return decision;
    }

    public async Task ReleaseAsync(TenantId tenantId, Guid runId, CancellationToken cancellationToken)
    {
        _released.Enqueue(runId);
        await inner.ReleaseAsync(tenantId, runId, cancellationToken);
    }
}

/// <summary>Setup and read helpers for the admission, reservation and scheduled-admission suites. Everything reads straight from the tables.</summary>
internal static class AdmissionTestKit
{
    public const long Gb = QuotaWorld.Gb;
    public static readonly DateTimeOffset T0 = QuotaWorld.T0;

    public static async Task<StoreAgent> AddAgentAsync(QuotaWorld world, TenantId tenantId)
    {
        var agent = new StoreAgent(tenantId, await world.Harness.AddAgentToAsync(tenantId));
        await using var context = world.Database.CreatePlatformContext();
        context.AgentCapabilities.Add(new BackupPlatform.Agents.Capabilities.AgentCapabilityRecord(agent.AgentId, tenantId,
            BackupPlatform.Contracts.Backups.BackupTaskProtocol.QuotaBoundedUploadCapability,
            BackupPlatform.Domain.Capabilities.CapabilitySupport.Supported, null, "quota", T0));
        await context.SaveChangesAsync();
        return agent;
    }

    /// <summary>A job storing on the central provider (platform-managed). <paramref name="retry"/> null is the default 3-attempt policy.</summary>
    public static async Task<BackupJob> CreateJobAsync(
        QuotaWorld world, StoreAgent agent, BackupOverlapPolicy overlap = BackupOverlapPolicy.Queue, BackupRetryPolicy? retry = null)
    {
        var configuration = BackupStoreHarness.Configuration("central", BackupStorageExecutionLocation.Backend);
        var job = new BackupJob(Guid.NewGuid(), agent.TenantId, "Database", agent.AgentId, configuration, T0, retry, overlap);
        return (await world.RunStore().CreateJobAsync(job, default))!;
    }

    public static async Task<BackupRun> StartAsync(PostgreSqlBackupRunStore store, BackupJob job, string key, DateTimeOffset at)
    {
        var result = await store.StartRunAsync(job.TenantId, job.Id, key, at, default);
        Assert.Equal(BackupStartOutcome.Started, result.Outcome);
        return result.Run!;
    }

    /// <summary>Claims the agent's next attempt and starts it, as an agent does.</summary>
    public static async Task<BackupClaim> ClaimStartedAsync(PostgreSqlBackupRunStore store, StoreAgent agent, DateTimeOffset at)
    {
        var claim = await BackupStoreHarness.ClaimAsync(store, agent, at);
        Assert.NotNull(claim);
        await BackupStoreHarness.StartAttemptAsync(store, claim!, at);
        return claim!;
    }

    public static async Task FailNextAsync(PostgreSqlBackupRunStore store, StoreAgent agent, string code, DateTimeOffset at)
    {
        var claim = await ClaimStartedAsync(store, agent, at);
        await BackupStoreHarness.CompleteAsync(store, claim, BackupStoreHarness.Failed(code), at.AddSeconds(5));
    }

    public static async Task<BackupRun> RunAsync(QuotaWorld world, TenantId tenantId, Guid runId) =>
        (await world.RunStore().GetRunAsync(tenantId, runId, default))!;

    public static async Task<List<QuotaReservation>> ReservationsAsync(PostgreSqlFixture database, TenantId tenantId)
    {
        await using var context = database.CreatePlatformContext();
        return await context.QuotaReservations.AsNoTracking().Where(row => row.TenantId == tenantId).OrderBy(row => row.CreatedAt).ThenBy(row => row.RunId).ToListAsync();
    }

    public static async Task<List<QuotaReservation>> UnreleasedAsync(PostgreSqlFixture database, TenantId tenantId) =>
        (await ReservationsAsync(database, tenantId)).Where(row => row.ReleasedAt is null).ToList();

    public static async Task<QuotaReservation> ReservationOfAsync(PostgreSqlFixture database, TenantId tenantId, Guid runId) =>
        Assert.Single(await ReservationsAsync(database, tenantId), row => row.RunId == runId);

    /// <summary>A reservation written behind everyone's back (a crashed node, a missed callback).</summary>
    public static async Task InsertReservationAsync(
        PostgreSqlFixture database, TenantId tenantId, Guid runId, long bytes, DateTimeOffset createdAt, DateTimeOffset expiresAt)
    {
        await using var context = database.CreatePlatformContext();
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO quota_reservations ("RunId", "TenantId", "Bytes", "CreatedAt", "ExpiresAt")
            VALUES ({runId}, {tenantId.Value}, {bytes}, {createdAt}, {expiresAt})
            """);
    }

    public static async Task<List<AuditEvent>> AuditAsync(PostgreSqlFixture database, TenantId tenantId, string action)
    {
        await using var context = database.CreatePlatformContext();
        return await context.AuditEvents.AsNoTracking().Where(row => row.TenantId == tenantId && row.Action == action).OrderBy(row => row.At).ToListAsync();
    }

    /// <summary>A new enabled policy of the given behaviour; returns its key, ready to be named by a plan.</summary>
    public static async Task<string> CreatePolicyAsync(
        QuotaWorld world, QuotaEnforcementMode enforcement, QuotaAccountingMode accounting = QuotaAccountingMode.LogicalArtifactBytes, QuotaScopeRule? scopes = null)
    {
        var key = $"it-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var policy = new QuotaPolicy(QuotaPolicyId.New(), key, $"Policy {Guid.NewGuid():N}", accounting, scopes ?? QuotaScopeRule.All, enforcement, 80, 90, true, now);
        var change = await world.PolicyStore().CreateAsync(policy, await world.ActorAsync(), now, default);
        Assert.Equal(ManagedServiceOutcome.Changed, change.Outcome);
        return key;
    }

    public static async Task SuspendAsync(QuotaWorld world, TenantId tenantId) =>
        Assert.Equal(ManagedServiceOutcome.Changed, (await world.ManagedStore().SuspendAsync(tenantId, await world.ActorAsync(), DateTimeOffset.UtcNow, default)).Outcome);

    public static async Task ResumeAsync(QuotaWorld world, TenantId tenantId) =>
        Assert.Equal(ManagedServiceOutcome.Changed, (await world.ManagedStore().ResumeAsync(tenantId, await world.ActorAsync(), DateTimeOffset.UtcNow, default)).Outcome);

    public static async Task SetOverrideAsync(QuotaWorld world, TenantId tenantId, StorageLimitOverride limitOverride) =>
        Assert.Equal(ManagedServiceOutcome.Changed,
            (await world.ManagedStore().SetStorageLimitOverrideAsync(tenantId, limitOverride, await world.ActorAsync(), DateTimeOffset.UtcNow, default)).Outcome);
}
