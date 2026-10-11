using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Persistence;
using BackupPlatform.ManagedService.Quota;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Infrastructure.ManagedService;

/// <summary>
/// The incremental part of usage accounting, tied to the one place every backup lifecycle write passes through: the save. It is captured from
/// the change tracker before the save and applied right after it, in the same transaction, so the projection can neither run ahead of nor lag
/// behind the rows it describes, and no store has to remember to call it.
/// </summary>
/// <remarks>
/// Idempotence: the adjustment for an artifact is <c>contribution(replicas as saved) - contribution(the same replicas with this save reverted)</c>,
/// both by the policy's own accumulator. A duplicate callback changes nothing and so adds zero; a replica that moves between two occupying states
/// (a soft delete) adds zero; only publishing a replica or verifying its absence moves the number.
/// Locking: the tenant lock is taken AFTER the save has written its rows, never before. Every caller of a save may already hold row locks (an
/// agent, an artifact, claimed replicas) and some of them would otherwise wait on the tenant lock while another save waits on their rows. With
/// the tenant lock last, the order everywhere is: agent / artifact / replica rows → tenant → projection and reservation rows. The figures are read
/// under the tenant lock, so they include every writer that committed before us, and a writer that commits after us reads ours.
/// A projection that is missing or computed under another policy is recomputed from the saved state instead of adjusted.
/// </remarks>
internal sealed class StorageUsageProjection
{
    private readonly SortedDictionary<Guid, TenantChange> _tenants = [];

    private StorageUsageProjection()
    {
    }

    /// <summary>Null when the save touches neither a replica's existence/status nor a run's ending: nothing to project.</summary>
    public static StorageUsageProjection? Capture(PlatformDbContext context)
    {
        context.ChangeTracker.DetectChanges();
        StorageUsageProjection? projection = null;
        foreach (var entry in context.ChangeTracker.Entries())
        {
            switch (entry.Entity)
            {
                case BackupReplica replica when entry.State is EntityState.Added or EntityState.Deleted
                    || (entry.State == EntityState.Modified && entry.Property(nameof(BackupReplica.Status)).IsModified
                        && !Equals(entry.Property(nameof(BackupReplica.Status)).OriginalValue, replica.Status)):
                {
                    projection ??= new StorageUsageProjection();
                    // What the replica was before this save; none for a replica this save creates.
                    StoredReplica? original = entry.State == EntityState.Added
                        ? null
                        : new StoredReplica(replica.Id, replica.SizeBytes, replica.StorageConnectionId, (BackupReplicaStatus)entry.Property(nameof(BackupReplica.Status)).OriginalValue!);
                    projection.Tenant(replica.TenantId).Artifact(replica.ArtifactId).Reverts[replica.Id] = original;
                    break;
                }
                case BackupArtifact artifact when entry.State == EntityState.Deleted:
                    projection ??= new StorageUsageProjection();
                    projection.Tenant(artifact.TenantId).Artifact(artifact.Id).DeletedSize = artifact.SizeBytes;
                    break;
                case BackupRun run when entry.State == EntityState.Modified && run.IsTerminal
                    && entry.Property(nameof(BackupRun.Status)).OriginalValue is BackupStatus original && !BackupRun.IsTerminalStatus(original):
                    projection ??= new StorageUsageProjection();
                    projection.Tenant(run.TenantId).EndedRuns.Add(run.Id);
                    break;
            }
        }
        return projection;
    }

    /// <summary>After the save: under each tenant's lock, release what the ended runs held and bring the projection to the saved state.</summary>
    public async Task ApplyAsync(PlatformDbContext context, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (_, change) in _tenants)
        {
            if (!await TenantUsageEngine.LockTenantAsync(context, change.TenantId, cancellationToken))
            {
                continue;
            }
            var released = 0;
            foreach (var runId in change.EndedRuns)
            {
                released += await TenantUsageEngine.ReleaseReservationAsync(context, change.TenantId, runId, QuotaReservation.ReasonRunFinished, now, cancellationToken);
            }
            var policy = await TenantUsageEngine.ResolvePolicyAsync(context, change.TenantId, cancellationToken);
            var row = await TenantUsageEngine.ReadAsync(context, change.TenantId, cancellationToken);
            if (row is null || !row.IsComputedUnder(policy))
            {
                // Computed from the sources as saved, so it already contains this save (and the releases above).
                await TenantUsageEngine.EnsureCurrentAsync(context, change.TenantId, policy, now, cancellationToken);
                continue;
            }
            var delta = 0L;
            foreach (var (artifactId, artifact) in change.Artifacts)
            {
                delta += await DeltaAsync(context, change.TenantId, policy, artifactId, artifact, cancellationToken);
            }
            if (delta != 0)
            {
                await TenantUsageEngine.AdjustUsedAsync(context, change.TenantId, delta, cancellationToken);
            }
            if (released > 0 || delta != 0)
            {
                await TenantUsageEngine.RefreshReservedAsync(context, change.TenantId, cancellationToken);
            }
        }
    }

    private static async Task<long> DeltaAsync(
        PlatformDbContext context, TenantId tenantId, QuotaPolicy policy, Guid artifactId, ArtifactChange artifact, CancellationToken cancellationToken)
    {
        var saved = await TenantUsageEngine.ReadArtifactAsync(context, tenantId, artifactId, cancellationToken);
        if (saved is null)
        {
            if (artifact.DeletedSize is not { } size) return 0;
            saved = new StoredArtifact(artifactId, size, [], await TenantUsageEngine.LoadConnectionFactsAsync(context, tenantId, cancellationToken));
        }
        var reverted = saved.Replicas.ToDictionary(replica => replica.Id);
        foreach (var (replicaId, original) in artifact.Reverts)
        {
            if (original is null)
            {
                reverted.Remove(replicaId);
            }
            else
            {
                reverted[replicaId] = original;
            }
        }
        return QuotaAccounting.Contribution(policy, saved.Replicas.Select(saved.ToFact))
            - QuotaAccounting.Contribution(policy, reverted.Values.Select(saved.ToFact));
    }

    private TenantChange Tenant(TenantId tenantId)
    {
        if (!_tenants.TryGetValue(tenantId.Value, out var change))
        {
            _tenants[tenantId.Value] = change = new TenantChange(tenantId);
        }
        return change;
    }

    private sealed class TenantChange(TenantId tenantId)
    {
        public TenantId TenantId { get; } = tenantId;
        public SortedDictionary<Guid, ArtifactChange> Artifacts { get; } = [];
        public HashSet<Guid> EndedRuns { get; } = [];

        public ArtifactChange Artifact(Guid artifactId)
        {
            if (!Artifacts.TryGetValue(artifactId, out var artifact))
            {
                Artifacts[artifactId] = artifact = new ArtifactChange();
            }
            return artifact;
        }
    }

    /// <summary>The replicas of one artifact this save touches, each as it was before the save (null: the save created it).</summary>
    private sealed class ArtifactChange
    {
        public Dictionary<Guid, StoredReplica?> Reverts { get; } = [];
        public long? DeletedSize { get; set; }
    }
}
