using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;

namespace BackupPlatform.Domain.Backups;

/// <summary>
/// Physical lifecycle of one stored copy: Available, then DeletePending (soft delete: intact, visible, cancellable), then Deleting
/// (a worker holds the lease and the bytes are being removed: irreversible), then Deleted (absence verified) or DeleteFailed (retried later).
/// </summary>
public enum BackupReplicaStatus
{
    Available = 1,
    DeletePending,
    Deleting,
    DeleteFailed,
    Deleted
}

public sealed class BackupReplica : ITenantOwned
{
    private BackupReplica() { TenantId = null!; StorageProvider = null!; StorageTarget = null!; Key = null!; Checksum = null!; ChecksumAlgorithm = null!; VerificationState = null!; VerificationMethod = null!; }
    public BackupReplica(Guid id, TenantId tenantId, Guid artifactId, string storageProvider, string storageTarget,
        string key, long sizeBytes, string checksum, string checksumAlgorithm, string verificationState,
        string verificationMethod, DateTimeOffset createdAt, DateTimeOffset? verifiedAt)
    {
        BackupGuard.Id(id);
        BackupGuard.Id(artifactId);
        ArgumentNullException.ThrowIfNull(tenantId);
        BackupGuard.Reference(storageProvider);
        BackupGuard.Text(storageTarget, 512);
        BackupGuard.RelativeKey(key);
        BackupGuard.Checksum(checksum, checksumAlgorithm);
        BackupGuard.Text(verificationMethod, 64);
        if (sizeBytes <= 0 || verificationState is not ("Verified" or "Unverified" or "Failed"))
            throw new ArgumentException("Invalid replica metadata.");
        if ((verificationState == "Verified") != verifiedAt.HasValue || verifiedAt < createdAt)
            throw new ArgumentException("Invalid replica verification timestamp.");
        Id = id;
        TenantId = tenantId;
        ArtifactId = artifactId;
        StorageProvider = storageProvider;
        StorageTarget = storageTarget;
        Key = key;
        SizeBytes = sizeBytes;
        Checksum = checksum.ToLowerInvariant();
        ChecksumAlgorithm = checksumAlgorithm;
        VerificationState = verificationState;
        VerificationMethod = verificationMethod;
        CreatedAt = BackupGuard.Timestamp(createdAt);
        VerifiedAt = verifiedAt is { } verified ? BackupGuard.Timestamp(verified) : null;
    }
    public Guid Id { get; private set; }
    public TenantId TenantId { get; private set; }
    public Guid ArtifactId { get; private set; }
    public string StorageProvider { get; private set; }
    public BackupReplicaStatus Status { get; private set; } = BackupReplicaStatus.Available;
    public string StorageTarget { get; private set; }
    public string Key { get; private set; }
    public long SizeBytes { get; private set; }
    public string Checksum { get; private set; }
    public string ChecksumAlgorithm { get; private set; }
    public string VerificationState { get; private set; }
    public string VerificationMethod { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? VerifiedAt { get; private set; }
    /// <summary>When the replica entered the delete lifecycle (retention expiry or an administrator).</summary>
    public DateTimeOffset? DeleteRequestedAt { get; private set; }
    public BackupDeleteReason? DeleteReason { get; private set; }
    /// <summary>The administrator who asked for a manual delete; null for retention.</summary>
    public Guid? DeleteRequestedByUserId { get; private set; }
    /// <summary>The earliest instant the physical delete may begin. Until then the replica is intact and the deletion can be cancelled.</summary>
    public DateTimeOffset? DeleteAfter { get; private set; }
    /// <summary>How many physical delete attempts were started.</summary>
    public int DeleteAttempts { get; private set; }
    /// <summary>Fences one delete attempt: only the holder of this token may finish it. Set exactly while <see cref="Status"/> is Deleting.</summary>
    public Guid? DeleteLeaseToken { get; private set; }
    public string? DeleteLeaseOwner { get; private set; }
    /// <summary>A crashed owner's lease simply runs out, after which any node may take the delete over.</summary>
    public DateTimeOffset? DeleteLeaseExpiresAt { get; private set; }
    public DateTimeOffset? LastDeleteAttemptAt { get; private set; }
    /// <summary>When a failed delete is tried again; set exactly while <see cref="Status"/> is DeleteFailed.</summary>
    public DateTimeOffset? DeleteNextAttemptAt { get; private set; }
    /// <summary>Why the last attempt failed (<see cref="BackupDeleteFailureCodes"/>); set exactly while <see cref="Status"/> is DeleteFailed.</summary>
    public string? DeleteFailureCode { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }

    /// <summary>
    /// Whether the bytes can still be used (a restore, once there is one). A replica waiting out its grace period is intact and
    /// stays usable: the grace period exists so a mistaken or premature deletion can still be recovered from. Once the physical delete has
    /// begun the bytes may already be partly gone, so from Deleting on the replica is not usable.
    /// </summary>
    public bool IsUsable => Status is BackupReplicaStatus.Available or BackupReplicaStatus.DeletePending;

    /// <summary>Available to DeletePending. False (and no change) in any other state, so concurrent requesters cannot both win.</summary>
    internal bool RequestDelete(DateTimeOffset now, BackupDeleteReason reason, TimeSpan grace, Guid? requestedBy)
    {
        if (Status != BackupReplicaStatus.Available) return false;
        if (!Enum.IsDefined(reason) || grace < TimeSpan.Zero) throw new ArgumentException("Invalid delete request.");
        Status = BackupReplicaStatus.DeletePending;
        DeleteRequestedAt = BackupGuard.Timestamp(now);
        DeleteReason = reason;
        DeleteRequestedByUserId = requestedBy;
        DeleteAfter = BackupGuard.Timestamp(now + grace);
        return true;
    }

    /// <summary>DeletePending to Available. A delete that has begun (or finished, or failed) is never taken back.</summary>
    internal bool CancelDelete()
    {
        if (Status != BackupReplicaStatus.DeletePending) return false;
        Status = BackupReplicaStatus.Available;
        DeleteRequestedAt = null;
        DeleteReason = null;
        DeleteRequestedByUserId = null;
        DeleteAfter = null;
        return true;
    }

    /// <summary>Whether a worker may take the physical delete over at <paramref name="now"/>: the grace period is over, a failed delete is due again, or a previous owner's lease ran out.</summary>
    public bool IsDeletionDue(DateTimeOffset now) => Status switch
    {
        BackupReplicaStatus.DeletePending => DeleteAfter <= now,
        BackupReplicaStatus.DeleteFailed => DeleteNextAttemptAt <= now,
        BackupReplicaStatus.Deleting => DeleteLeaseExpiresAt <= now,
        _ => false
    };

    /// <summary>Takes the delete over under a fresh lease. False (and no change) when it is not due, so two workers cannot both begin.</summary>
    public bool BeginDelete(Guid leaseToken, string owner, DateTimeOffset now, TimeSpan lease)
    {
        if (!IsDeletionDue(now)) return false;
        BackupGuard.Id(leaseToken);
        BackupGuard.Text(owner, 128);
        if (lease <= TimeSpan.Zero) throw new ArgumentException("Invalid lease duration.");
        Status = BackupReplicaStatus.Deleting;
        DeleteLeaseToken = leaseToken;
        DeleteLeaseOwner = owner;
        DeleteLeaseExpiresAt = BackupGuard.Timestamp(now + lease);
        DeleteAttempts = checked(DeleteAttempts + 1);
        LastDeleteAttemptAt = BackupGuard.Timestamp(now);
        DeleteNextAttemptAt = null;
        return true;
    }

    /// <summary>The bytes are gone and absence was verified. Accepted from the lease holder even after the lease ran out, as long as nobody took the delete over.</summary>
    public bool CompleteDelete(Guid leaseToken, DateTimeOffset now)
    {
        if (Status != BackupReplicaStatus.Deleting || DeleteLeaseToken != leaseToken) return false;
        Status = BackupReplicaStatus.Deleted;
        DeletedAt = BackupGuard.Timestamp(now);
        ClearLease();
        DeleteNextAttemptAt = null;
        DeleteFailureCode = null;
        return true;
    }

    public bool FailDelete(Guid leaseToken, DateTimeOffset now, string failureCode, DateTimeOffset nextAttemptAt)
    {
        if (Status != BackupReplicaStatus.Deleting || DeleteLeaseToken != leaseToken) return false;
        BackupGuard.Reference(failureCode);
        if (nextAttemptAt < now) throw new ArgumentException("A retry cannot be scheduled in the past.");
        Status = BackupReplicaStatus.DeleteFailed;
        DeleteFailureCode = failureCode;
        DeleteNextAttemptAt = BackupGuard.Timestamp(nextAttemptAt);
        ClearLease();
        return true;
    }

    private void ClearLease()
    {
        DeleteLeaseToken = null;
        DeleteLeaseOwner = null;
        DeleteLeaseExpiresAt = null;
    }

    internal bool SameResult(BackupReplica other) => StorageProvider == other.StorageProvider && StorageTarget == other.StorageTarget &&
        Key == other.Key && SizeBytes == other.SizeBytes && Checksum == other.Checksum && ChecksumAlgorithm == other.ChecksumAlgorithm &&
        VerificationState == other.VerificationState && VerificationMethod == other.VerificationMethod && CreatedAt == other.CreatedAt && VerifiedAt == other.VerifiedAt;
}
