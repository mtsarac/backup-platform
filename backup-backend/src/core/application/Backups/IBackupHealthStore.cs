using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Restores;
using BackupPlatform.Domain.Storage;

namespace BackupPlatform.Application.Backups;

public sealed record BackupHealthFactor(int Score, int MaxScore, string Status, string Reason);
public sealed record BackupHealthFactors(BackupHealthFactor Freshness, BackupHealthFactor Replication, BackupHealthFactor RestoreVerification, BackupHealthFactor StorageCapacity);
public sealed record BackupJobHealth(Guid JobId, string JobName, int Score, string State, DateTimeOffset EvaluatedAt, BackupHealthFactors Factors);
public sealed record BackupCapacityRisk(Guid ConnectionId, string ConnectionName, string Status, long? TotalBytes, long? UsedBytes, long? FreeBytes, DateTimeOffset ReportedAt);
public sealed record BackupRecentSizeAnomaly(Guid RunId, Guid JobId, string JobName, DateTimeOffset CompletedAt, long SizeBytes, long BaselineMeanBytes, double DeviationRatio);
public sealed record BackupHealthSummary(DateTimeOffset EvaluatedAt, int TotalJobs, double? AverageScore, string? State, int Healthy, int Degraded, int Critical,
    IReadOnlyList<BackupJobHealth> Jobs, IReadOnlyList<BackupCapacityRisk> CapacityRisks, IReadOnlyList<BackupRecentSizeAnomaly> RecentSizeAnomalies, int RecentSizeAnomaliesTotal);
public sealed record BackupHealthEvidence(BackupJob Job, DateTimeOffset? LastSucceededAt, int VerifiedReplicas, RestoreStatus? LatestVerificationStatus,
    DateTimeOffset? LatestVerificationAt, IReadOnlyList<StorageCapacitySnapshot?> DestinationCapacity);

/// <summary>The evidence the health evaluation reads.</summary>
public interface IBackupHealthReader
{
    Task<IReadOnlyList<BackupHealthEvidence>> ReadAsync(TenantId tenantId, Guid? jobId, DateTimeOffset now, CancellationToken cancellationToken);
    Task<IReadOnlyList<BackupCapacityRisk>> ReadCapacityRisksAsync(TenantId tenantId, DateTimeOffset now, CancellationToken cancellationToken);
    Task<(IReadOnlyList<BackupRecentSizeAnomaly> Items, int Total)> ReadRecentSizeAnomaliesAsync(TenantId tenantId, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>Health evidence of the request's tenant (filtered tenant context).</summary>
public interface IBackupHealthStore : IBackupHealthReader
{

}

/// <summary>Resolves the tenant of a job before a request has one, and reads health evidence for platform-driven work; unfiltered by tenant.</summary>
public interface IBackupHealthPlatformStore : IBackupHealthReader
{
    Task<TenantId?> FindJobTenantAsync(Guid jobId, CancellationToken cancellationToken);
}
