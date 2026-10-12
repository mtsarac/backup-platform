namespace BackupPlatform.Domain.Backups;

public static class ReplicaRepairSourceEvaluator
{
    private static readonly Dictionary<string, int> ProviderPriority = new(StringComparer.OrdinalIgnoreCase)
    {
        ["central"] = 1,
        ["sftp"] = 2,
        ["google-drive"] = 3,
        ["local"] = 4
    };
    public static int GetProviderPriority(string? provider) =>
        provider is not null && ProviderPriority.TryGetValue(provider, out var p) ? p : 99;


    public static bool IsEligibleSource(BackupReplica replica, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(replica);
        if (replica.VerificationState != "Verified" || replica.VerifiedAt is null) return false;
        if (replica.Status != BackupReplicaStatus.Available) return false;
        if (replica.DeleteLeaseExpiresAt is { } lease && lease > now) return false;
        if (replica.DeleteNextAttemptAt is not null) return false;
        return true;
    }

    public static IReadOnlyList<BackupReplica> SelectCandidateSources(BackupArtifact artifact, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        return artifact.Replicas
            .Where(r => IsEligibleSource(r, now))
            .OrderBy(r => ProviderPriority.GetValueOrDefault(r.StorageProvider, 99))
            .ThenBy(r => r.CreatedAt)
            .ToList();
    }
}
