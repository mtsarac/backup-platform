using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;

namespace BackupPlatform.Domain.Storage;

public static class StorageCapacityStatus
{
    public const string Available = "Available";
    public const string Warning = "Warning";
    public const string Critical = "Critical";
    public const string CapacityUnavailable = "CapacityUnavailable";
    /// <summary>
    /// Response-only status for providers that expose no capacity (S3 buckets and SMB shares report no quota to the platform).
    /// It describes the provider, not a measurement, so it is never stored in a snapshot.
    /// </summary>
    public const string NotMeasurable = "NotMeasurable";

    /// <summary>Capacity snapshots older than this are pruned; the newest snapshot and the newest measured one of a connection are always kept.</summary>
    public static readonly TimeSpan SnapshotRetention = TimeSpan.FromDays(90);

    public const double WarningThresholdRatio = 0.80;
    public const double CriticalThresholdRatio = 0.90;

    /// <summary>Without a known total and a known used or free amount nothing can be said about fullness: that is CapacityUnavailable, never Available.</summary>
    public static string Evaluate(long? totalBytes, long? usedBytes, long? freeBytes)
    {
        if (totalBytes is null or <= 0) return CapacityUnavailable;
        var effectiveUsed = usedBytes ?? (freeBytes.HasValue ? totalBytes.Value - freeBytes.Value : (long?)null);
        if (effectiveUsed is null) return CapacityUnavailable;

        var ratio = (double)effectiveUsed.Value / totalBytes.Value;
        if (ratio >= CriticalThresholdRatio) return Critical;
        if (ratio >= WarningThresholdRatio) return Warning;
        return Available;
    }
}

/// <summary>
/// A point-in-time capacity measurement for a storage connection.
/// Total, used, and free bytes reflect physical disk space or provider quota.
/// When no active agent can reach the storage or the query fails, status is CapacityUnavailable.
/// </summary>
public sealed class StorageCapacitySnapshot : ITenantOwned
{
    private StorageCapacitySnapshot()
    {
        TenantId = null!;
        Status = StorageCapacityStatus.CapacityUnavailable;
    }

    public StorageCapacitySnapshot(
        Guid id,
        TenantId tenantId,
        Guid connectionId,
        long? totalBytes,
        long? usedBytes,
        long? freeBytes,
        string status,
        DateTimeOffset reportedAt,
        string? errorMessage = null)
    {
        if (id == Guid.Empty) throw new ArgumentException("Snapshot id cannot be empty.", nameof(id));
        TenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
        if (connectionId == Guid.Empty) throw new ArgumentException("Connection id cannot be empty.", nameof(connectionId));

        Id = id;
        ConnectionId = connectionId;
        TotalBytes = totalBytes;
        UsedBytes = usedBytes;
        FreeBytes = freeBytes;
        Status = string.IsNullOrWhiteSpace(status) ? StorageCapacityStatus.CapacityUnavailable : status;
        ReportedAt = reportedAt;
        ErrorMessage = errorMessage;
    }

    public Guid Id { get; private set; }
    public TenantId TenantId { get; private set; }
    public Guid ConnectionId { get; private set; }
    public long? TotalBytes { get; private set; }
    public long? UsedBytes { get; private set; }
    public long? FreeBytes { get; private set; }
    public string Status { get; private set; }
    public DateTimeOffset ReportedAt { get; private set; }
    public string? ErrorMessage { get; private set; }


    public static StorageCapacitySnapshot AvailableResult(
        TenantId tenantId,
        Guid connectionId,
        long? totalBytes,
        long? usedBytes,
        long? freeBytes,
        DateTimeOffset reportedAt) =>
        new(Guid.NewGuid(), tenantId, connectionId, totalBytes, usedBytes, freeBytes,
            StorageCapacityStatus.Available, reportedAt);

    public static StorageCapacitySnapshot UnavailableResult(
        TenantId tenantId,
        Guid connectionId,
        DateTimeOffset reportedAt,
        string? errorMessage = null) =>
        new(Guid.NewGuid(), tenantId, connectionId, null, null, null, StorageCapacityStatus.CapacityUnavailable, reportedAt, errorMessage);
}
