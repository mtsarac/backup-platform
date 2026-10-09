using System.Text.Json;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Storage;
using BackupPlatform.ManagedService.Administration;
using BackupPlatform.ManagedService.Quota;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.IntegrationTests.Quota;

/// <summary>Helpers shared by the usage accounting, reconcile/rebuild and upgrade tests. Every helper works on the tenant it is given.</summary>
internal static class UsageTestKit
{
    public const long Gb = QuotaWorld.Gb;

    /// <summary>The replica an agent-local job produces (provider "local", executed by the agent).</summary>
    public static ReplicaSpec AgentLocal { get; } = new("local", "agent-disk");

    public static ReplicaSpec[] AllThree { get; } = [ReplicaSpec.Central, ReplicaSpec.GoogleDriveLike, ReplicaSpec.SftpLike];

    public static QuotaScopeRule Ownership(StorageOwnership ownership) =>
        QuotaScopeRule.Create([new QuotaScopeSelector(QuotaScopeKind.OwnershipType, [ownership.ToString()])]);

    public static QuotaScopeRule Providers(params string[] providers) =>
        QuotaScopeRule.Create([new QuotaScopeSelector(QuotaScopeKind.ProviderType, providers)]);

    /// <summary>A new, enabled policy with a unique key (BlockNewBackups unless told otherwise).</summary>
    public static async Task<QuotaPolicy> CreatePolicyAsync(
        this QuotaWorld world, QuotaAccountingMode mode, QuotaScopeRule scope,
        QuotaEnforcementMode enforcement = QuotaEnforcementMode.BlockNewBackups, int warning = 80, int critical = 90)
    {
        var key = $"usage-{Guid.NewGuid():N}";
        var policy = new QuotaPolicy(QuotaPolicyId.New(), key, $"Usage policy {key}", mode, scope, enforcement, warning, critical, true, DateTimeOffset.UtcNow);
        var change = await world.PolicyStore().CreateAsync(policy, await world.ActorAsync(), DateTimeOffset.UtcNow, default);
        Assert.Equal(ManagedServiceOutcome.Changed, change.Outcome);
        return policy;
    }

    /// <summary>A tenant (one agent, 100 GB plan) whose plan counts usage by a new policy of the given mode and scope.</summary>
    public static async Task<(StoreAgent Agent, QuotaPolicy Policy)> TenantOnPolicyAsync(
        this QuotaWorld world, QuotaAccountingMode mode, QuotaScopeRule scope, long? limitBytes = 100 * Gb)
    {
        var policy = await world.CreatePolicyAsync(mode, scope);
        return (await world.AgentAsync(limitBytes, policy.Key), policy);
    }

    /// <summary>Publishes one artifact of <paramref name="sizeBytes"/> through the real run lifecycle at the fixed test time.</summary>
    public static Task<(BackupRun Run, Guid ArtifactId)> PublishCopiesAsync(
        this QuotaWorld world, StoreAgent agent, BackupJob job, long sizeBytes, params ReplicaSpec[] replicas) =>
        world.PublishAsync(world.RunStore(), agent, job, sizeBytes, replicas, QuotaWorld.T0);

    /// <summary>The stored projection and a fresh read through the service both say <paramref name="expectedBytes"/>.</summary>
    public static async Task AssertUsedAsync(this QuotaWorld world, TenantId tenantId, long expectedBytes)
    {
        var stored = await world.StoredUsageAsync(tenantId);
        Assert.NotNull(stored);
        Assert.Equal(expectedBytes, stored.UsedBytes);
        var view = await world.Usage().GetUsageAsync(tenantId, default);
        Assert.NotNull(view);
        Assert.Equal(expectedBytes, view.UsedBytes);
    }

    /// <summary>The independent SQL oracle (no accumulator, no scopes) says exactly this logical and physical total, optionally for one provider.</summary>
    public static async Task AssertOracleAsync(this QuotaWorld world, TenantId tenantId, long logicalBytes, long physicalBytes, string? provider = null) =>
        Assert.Equal((logicalBytes, physicalBytes), await world.SourceUsageAsync(tenantId, provider));

    /// <summary>A reconcile finds nothing to correct: the incrementally maintained figure is what the accumulator computes from the sources.</summary>
    public static async Task AssertNoDriftAsync(this QuotaWorld world, TenantId tenantId)
    {
        var result = await world.Usage().ReconcileAsync(tenantId, null, DateTimeOffset.UtcNow, default);
        Assert.NotNull(result);
        Assert.False(result.Drifted, $"Projection drifted: previous {result.PreviousUsedBytes}, recomputed {result.UsedBytes}.");
    }

    /// <summary>Loads the artifact with its replicas through a platform context, applies <paramref name="change"/>, and saves like production code does.</summary>
    public static async Task ChangeArtifactAsync(this QuotaWorld world, Guid artifactId, Action<BackupArtifact> change)
    {
        await using var context = world.Database.CreatePlatformContext();
        var artifact = await context.BackupArtifacts.Include(row => row.Replicas).SingleAsync(row => row.Id == artifactId);
        change(artifact);
        await context.SaveChangesAsync();
    }

    public static BackupReplica ReplicaOf(BackupArtifact artifact, string provider) => artifact.Replicas.Single(replica => replica.StorageProvider == provider);

    public static async Task<List<AuditEvent>> AuditAsync(this QuotaWorld world, string action, string targetId)
    {
        await using var context = world.Database.CreatePlatformContext();
        return await context.AuditEvents.AsNoTracking().Where(row => row.Action == action && row.TargetId == targetId).OrderBy(row => row.At).ThenBy(row => row.Id).ToListAsync();
    }

    public static Task<List<AuditEvent>> AuditOfTenantUsageAsync(this QuotaWorld world, string action, TenantId tenantId) =>
        world.AuditAsync(action, tenantId.Value.ToString("D"));

    public static JsonElement DetailsOf(AuditEvent audit) => JsonDocument.Parse(audit.DetailsJson).RootElement;

    public static async Task DeleteProjectionAsync(this QuotaWorld world, TenantId tenantId)
    {
        await using var context = world.Database.CreatePlatformContext();
        await context.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM tenant_storage_usage WHERE \"TenantId\" = {tenantId.Value}");
    }
}
