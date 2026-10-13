using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;

namespace BackupPlatform.Domain.Backups;

public enum BackupStorageCleanupStatus
{
    Pending = 1,
    Completed = 2
}

public sealed class BackupStorageCleanup : ITenantOwned
{
    private BackupStorageCleanup() { TenantId = null!; StorageProvider = null!; StorageName = null!; TargetIdentity = null!; Key = null!; }

    public BackupStorageCleanup(
        Guid id,
        TenantId tenantId,
        Guid jobId,
        Guid runId,
        Guid attemptId,
        string storageProvider,
        string storageName,
        string targetIdentity,
        string key,
        bool isPartial,
        DateTimeOffset createdAt,
        Guid leaseToken,
        DateTimeOffset leaseExpiresAt)
    {
        BackupGuard.Id(id);
        BackupGuard.Id(jobId);
        BackupGuard.Id(runId);
        BackupGuard.Id(attemptId);
        BackupGuard.Id(leaseToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(storageProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(storageName);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        Id = id;
        TenantId = tenantId;
        JobId = jobId;
        RunId = runId;
        AttemptId = attemptId;
        StorageProvider = storageProvider.Trim();
        StorageName = storageName.Trim();
        TargetIdentity = targetIdentity.Trim();
        Key = key.Trim();
        IsPartial = isPartial;
        Status = BackupStorageCleanupStatus.Pending;
        CreatedAt = BackupGuard.Timestamp(createdAt);
        LeaseToken = leaseToken;
        LeaseExpiresAt = BackupGuard.Timestamp(leaseExpiresAt);
    }

    public Guid Id { get; private set; }
    public TenantId TenantId { get; private set; }
    public Guid JobId { get; private set; }
    public Guid RunId { get; private set; }
    public Guid AttemptId { get; private set; }
    public string StorageProvider { get; private set; }
    public string StorageName { get; private set; }
    public string TargetIdentity { get; private set; }
    public string Key { get; private set; }
    public bool IsPartial { get; private set; }
    public BackupStorageCleanupStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public Guid? LeaseToken { get; private set; }
    public DateTimeOffset? LeaseExpiresAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    public bool Claim(Guid leaseToken, DateTimeOffset now, TimeSpan leaseDuration)
    {
        if (Status != BackupStorageCleanupStatus.Pending) return false;
        if (LeaseExpiresAt is { } expires && expires > now) return false;
        BackupGuard.Id(leaseToken);
        LeaseToken = leaseToken;
        LeaseExpiresAt = BackupGuard.Timestamp(now + leaseDuration);
        return true;
    }

    public bool Complete(Guid leaseToken, DateTimeOffset now)
    {
        if (Status == BackupStorageCleanupStatus.Completed) return true;
        if (Status != BackupStorageCleanupStatus.Pending || LeaseToken != leaseToken) return false;
        Status = BackupStorageCleanupStatus.Completed;
        CompletedAt = BackupGuard.Timestamp(now);
        LeaseToken = null;
        LeaseExpiresAt = null;
        return true;
    }
}
