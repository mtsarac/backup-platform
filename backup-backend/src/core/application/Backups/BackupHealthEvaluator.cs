using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Restores;
using BackupPlatform.Domain.Storage;

namespace BackupPlatform.Application.Backups;

public sealed class BackupHealthEvaluator(IBackupHealthStore store, TimeProvider time)
{
    public static readonly TimeSpan CapacityMaximumAge = TimeSpan.FromDays(1);

    public async Task<BackupJobHealth?> GetJobAsync(TenantId tenantId, Guid jobId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var evidence = await store.ReadAsync(tenantId, jobId, now, cancellationToken);
        return evidence.Count == 0 ? null : Evaluate(evidence[0], now);
    }

    public async Task<BackupHealthSummary> GetSummaryAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var evidence = await store.ReadAsync(tenantId, null, now, cancellationToken);
        var jobs = evidence.Select(item => Evaluate(item, now)).ToArray();
        double? average = jobs.Length == 0 ? null : Math.Round(jobs.Average(job => job.Score), 2);
        var anomalies = await store.ReadRecentSizeAnomaliesAsync(tenantId, now, cancellationToken);
        return new(now, jobs.Length, average, average is null ? null : Classify(average.Value), jobs.Count(job => job.State == "Healthy"),
            jobs.Count(job => job.State == "Degraded"), jobs.Count(job => job.State == "Critical"), jobs,
            await store.ReadCapacityRisksAsync(tenantId, now, cancellationToken), anomalies.Items, anomalies.Total);
    }

    private static BackupJobHealth Evaluate(BackupHealthEvidence evidence, DateTimeOffset now)
    {
        var freshness = Freshness(evidence, now);
        var required = evidence.Job.Configuration.RequiredVerifiedReplicaCount;
        var replicas = Math.Min(required, evidence.VerifiedReplicas);
        var replication = new BackupHealthFactor((int)Math.Floor(30d * replicas / required), 30, replicas >= required ? "Available" : "Degraded",
            replicas >= required ? "replicas_verified" : replicas == 0 ? "no_verified_replica" : "replicas_missing");
        var restore = evidence.LatestVerificationStatus switch
        {
            RestoreStatus.Failed or RestoreStatus.AgentLost => new BackupHealthFactor(0, 20, "Failed", "restore_failed"),
            RestoreStatus.Succeeded when evidence.LatestVerificationAt >= now.AddDays(-30) && evidence.LatestVerificationAt <= now => new(20, 20, "Passed", "restore_passed"),
            _ => new(10, 20, "Unavailable", "restore_untested")
        };
        var capacity = Capacity(evidence.DestinationCapacity, now);
        var score = freshness.Score + replication.Score + restore.Score + capacity.Score;
        return new(evidence.Job.Id, evidence.Job.Name, score, Classify(score), now, new(freshness, replication, restore, capacity));
    }

    private static BackupHealthFactor Freshness(BackupHealthEvidence evidence, DateTimeOffset now)
    {
        if (evidence.Job.Schedule is not { } schedule) return new(0, 40, "Unavailable", "schedule_unavailable");
        if (evidence.LastSucceededAt is not { } last || last > now) return new(0, 40, "Unavailable", "backup_missing");
        // Use actual consecutive UTC occurrences, preserving irregular cron periods and DST rather than inventing a daily interval.
        var first = schedule.NextOccurrenceAfter(last);
        var second = first is { } occurrence ? schedule.NextOccurrenceAfter(occurrence) : null;
        if (first is null || second is null || second <= first) return new(0, 40, "Unavailable", "schedule_unavailable");
        var period = (second.Value - first.Value).TotalSeconds;
        var age = Math.Max(0, (now - last).TotalSeconds);
        var score = (int)Math.Floor(40 * Math.Clamp(2 - age / period, 0, 1));
        return new(score, 40, score == 40 ? "Available" : "Degraded", score == 40 ? "backup_fresh" : "backup_stale");
    }

    private static BackupHealthFactor Capacity(IReadOnlyList<StorageCapacitySnapshot?> snapshots, DateTimeOffset now)
    {
        if (snapshots.Count == 0 || snapshots.Any(snapshot => snapshot is null || !HasKnownCapacity(snapshot, now)))
            return new(0, 10, "Unavailable", "capacity_unavailable");
        var worst = snapshots.Max(snapshot => (double)(snapshot!.UsedBytes ?? snapshot.TotalBytes!.Value - snapshot.FreeBytes!.Value) / snapshot.TotalBytes!.Value);
        return worst > .90 ? new(0, 10, "Critical", "capacity_critical")
            : worst >= .80 ? new(5, 10, "Warning", "capacity_warning") : new(10, 10, "Available", "capacity_available");
    }

    public static bool HasKnownCapacity(StorageCapacitySnapshot snapshot, DateTimeOffset now) =>
        snapshot.Status is StorageCapacityStatus.Available or StorageCapacityStatus.Warning or StorageCapacityStatus.Critical &&
        snapshot.ReportedAt <= now && snapshot.ReportedAt >= now - CapacityMaximumAge &&
        snapshot.TotalBytes is > 0 && (snapshot.UsedBytes is not null || snapshot.FreeBytes is not null) &&
        (snapshot.UsedBytes is null || snapshot.UsedBytes >= 0 && snapshot.UsedBytes <= snapshot.TotalBytes) &&
        (snapshot.FreeBytes is null || snapshot.FreeBytes >= 0 && snapshot.FreeBytes <= snapshot.TotalBytes) &&
        (snapshot.UsedBytes is null || snapshot.FreeBytes is null || snapshot.UsedBytes == snapshot.TotalBytes - snapshot.FreeBytes);

    private static string Classify(double score) => score >= 80 ? "Healthy" : score >= 50 ? "Degraded" : "Critical";
}
