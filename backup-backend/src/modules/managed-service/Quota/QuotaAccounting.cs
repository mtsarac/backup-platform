using BackupPlatform.Domain.Backups;

namespace BackupPlatform.ManagedService.Quota;

/// <summary>One stored replica with everything accounting looks at. Sizes are the recorded bytes, never a provider's own report.</summary>
public readonly record struct ReplicaFact(
    Guid ArtifactId, long ArtifactSizeBytes, long ReplicaSizeBytes, ReplicaStorageFacts Storage, BackupReplicaStatus Status);

/// <summary>
/// THE definition of usage. Replicas are fed in one by one (a whole tenant, or the few replicas of one artifact) and the accumulator applies,
/// in this order: does the replica still occupy storage (<see cref="BackupReplicaStorage"/>), is it inside the policy's scope, and the policy's
/// accounting mode. Rebuild, reconcile and incremental updates all use this class, so they cannot disagree.
/// </summary>
public sealed class QuotaUsageAccumulator(QuotaAccountingMode mode, QuotaScopeRule scope)
{
    private readonly HashSet<Guid> _countedArtifacts = [];

    public long TotalBytes { get; private set; }

    public void Add(ReplicaFact replica)
    {
        if (!BackupReplicaStorage.Occupies(replica.Status) || !scope.Matches(replica.Storage))
        {
            return;
        }
        switch (mode)
        {
            case QuotaAccountingMode.LogicalArtifactBytes:
                // Once per artifact, whatever the number of copies.
                if (_countedArtifacts.Add(replica.ArtifactId))
                {
                    TotalBytes = Saturating(TotalBytes, replica.ArtifactSizeBytes);
                }
                break;
            case QuotaAccountingMode.PhysicalReplicaBytes:
                TotalBytes = Saturating(TotalBytes, replica.ReplicaSizeBytes);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }

    private static long Saturating(long total, long bytes) => bytes <= 0 ? total : total > long.MaxValue - bytes ? long.MaxValue : total + bytes;
}

public static class QuotaAccounting
{
    public static QuotaUsageAccumulator NewAccumulator(QuotaPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return new QuotaUsageAccumulator(policy.AccountingMode, policy.IncludedStorageScopes);
    }

    /// <summary>What one artifact's replicas add to the usage under <paramref name="policy"/>.</summary>
    public static long Contribution(QuotaPolicy policy, IEnumerable<ReplicaFact> replicasOfOneArtifact)
    {
        var accumulator = NewAccumulator(policy);
        foreach (var replica in replicasOfOneArtifact)
        {
            accumulator.Add(replica);
        }
        return accumulator.TotalBytes;
    }
}
