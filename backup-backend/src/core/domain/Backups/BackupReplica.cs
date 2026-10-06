using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;

namespace BackupPlatform.Domain.Backups;

/// <summary>
/// Physical lifecycle of one stored copy: Available, then DeletePending (soft delete: intact, visible, cancellable), then Deleting
/// (durably dispatched or leased physical removal: irreversible), then Deleted (absence verified) or DeleteFailed.
/// </summary>
public enum BackupReplicaStatus
{
    Available = 1,
    DeletePending,
    Deleting,
    DeleteFailed,
    Deleted
}

public enum BackupStorageExecutionLocation { Backend = 1, Agent = 2 }

public sealed class BackupReplica : ITenantOwned
{
    private BackupReplica() { TenantId = null!; StorageProvider = null!; StorageTarget = null!; Key = null!; Checksum = null!; ChecksumAlgorithm = null!; VerificationState = null!; VerificationMethod = null!; }
    public BackupReplica(Guid id, TenantId tenantId, Guid artifactId, string storageProvider, string storageTarget,
        string key, long sizeBytes, string checksum, string checksumAlgorithm, string verificationState,
        string verificationMethod, DateTimeOffset createdAt, DateTimeOffset? verifiedAt,
        BackupStorageExecutionLocation? executionLocation = null, Guid? owningAgentId = null)
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
        var location = executionLocation ?? (storageProvider == "local" ? BackupStorageExecutionLocation.Agent : BackupStorageExecutionLocation.Backend);
        if (!Enum.IsDefined(location) || (location == BackupStorageExecutionLocation.Agent &&
            (storageProvider != "local" || owningAgentId is null || owningAgentId == Guid.Empty)))
            throw new ArgumentException("Agent replicas require a local provider and an owning agent.");
        ExecutionLocation = location;
        OwningAgentId = location == BackupStorageExecutionLocation.Agent ? owningAgentId : null;
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
    public BackupStorageExecutionLocation ExecutionLocation { get; private set; }
    public Guid? OwningAgentId { get; private set; }
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
    /// <summary>Fences a claimed physical attempt. An unclaimed agent dispatch deliberately has no lease.</summary>
    public Guid? DeleteLeaseToken { get; private set; }
    public string? DeleteLeaseOwner { get; private set; }
    /// <summary>A crashed owner's lease runs out; only the configured executor may take it over.</summary>
    public DateTimeOffset? DeleteLeaseExpiresAt { get; private set; }
    public DateTimeOffset? LastDeleteAttemptAt { get; private set; }
    /// <summary>Retry time, or the next bounded check of an unclaimed agent dispatch. Null for terminal failures.</summary>
    public DateTimeOffset? DeleteNextAttemptAt { get; private set; }
    /// <summary>The last failure, or agent_unavailable while dispatch waits for the owning agent.</summary>
    public string? DeleteFailureCode { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }
    public Guid? DeleteOperationId { get; private set; }
    public Guid? DeleteCompletionToken { get; private set; }
    public bool? DeleteCompletionAbsenceVerified { get; private set; }
    public string? DeleteCompletionFailureCode { get; private set; }
    public bool WaitingForAgent => ExecutionLocation == BackupStorageExecutionLocation.Agent &&
        Status == BackupReplicaStatus.Deleting && DeleteLeaseToken is null;

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

    /// <summary>Whether grace, retry, dispatch recheck or crash recovery is due. This never authorizes a backend to physically delete an agent-owned object.</summary>
    public bool IsDeletionDue(DateTimeOffset now) => Status switch
    {
        BackupReplicaStatus.DeletePending => DeleteAfter <= now,
        BackupReplicaStatus.DeleteFailed => DeleteNextAttemptAt <= now,
        BackupReplicaStatus.Deleting when WaitingForAgent => DeleteNextAttemptAt <= now,
        BackupReplicaStatus.Deleting => DeleteLeaseExpiresAt <= now,
        _ => false
    };

    /// <summary>Claims only a backend-owned physical delete under a fresh lease, and only when due.</summary>
    public bool BeginDelete(Guid leaseToken, string owner, DateTimeOffset now, TimeSpan lease)
    {
        if (ExecutionLocation != BackupStorageExecutionLocation.Backend || !IsDeletionDue(now)) return false;
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
        if (ExecutionLocation != BackupStorageExecutionLocation.Backend || Status != BackupReplicaStatus.Deleting || DeleteLeaseToken != leaseToken) return false;
        Status = BackupReplicaStatus.Deleted;
        DeletedAt = BackupGuard.Timestamp(now);
        ClearLease();
        DeleteNextAttemptAt = null;
        DeleteFailureCode = null;
        return true;
    }

    public bool FailDelete(Guid leaseToken, DateTimeOffset now, string failureCode, DateTimeOffset nextAttemptAt)
    {
        if (ExecutionLocation != BackupStorageExecutionLocation.Backend || Status != BackupReplicaStatus.Deleting || DeleteLeaseToken != leaseToken) return false;
        BackupGuard.Reference(failureCode);
        if (nextAttemptAt < now) throw new ArgumentException("A retry cannot be scheduled in the past.");
        Status = BackupReplicaStatus.DeleteFailed;
        DeleteFailureCode = failureCode;
        DeleteNextAttemptAt = BackupGuard.Timestamp(nextAttemptAt);
        ClearLease();
        return true;
    }

    public bool DispatchAgentDeletion(Guid operationId, DateTimeOffset now, DateTimeOffset nextCheckAt)
    {
        if (ExecutionLocation != BackupStorageExecutionLocation.Agent || !IsDeletionDue(now)) return false;
        BackupGuard.Id(operationId);
        if (nextCheckAt <= now) throw new ArgumentException("A dispatch check must be scheduled in the future.");
        Status = BackupReplicaStatus.Deleting;
        DeleteOperationId ??= operationId;
        ClearLease();
        ClearCompletion();
        DeleteFailureCode = BackupDeleteFailureCodes.AgentUnavailable;
        DeleteNextAttemptAt = BackupGuard.Timestamp(nextCheckAt);
        return true;
    }

    public bool ClaimAgentDeletion(Guid operationId, Guid token, DateTimeOffset now, TimeSpan lease)
    {
        if (ExecutionLocation != BackupStorageExecutionLocation.Agent || Status != BackupReplicaStatus.Deleting ||
            (!WaitingForAgent && DeleteLeaseExpiresAt > now)) return false;
        BackupGuard.Id(operationId);
        BackupGuard.Id(token);
        if (lease <= TimeSpan.Zero) throw new ArgumentException("Invalid lease duration.");
        DeleteOperationId ??= operationId;
        DeleteLeaseToken = token;
        DeleteLeaseOwner = $"agent:{OwningAgentId:N}";
        DeleteLeaseExpiresAt = BackupGuard.Timestamp(now + lease);
        DeleteAttempts = checked(DeleteAttempts + 1);
        LastDeleteAttemptAt = BackupGuard.Timestamp(now);
        DeleteNextAttemptAt = null;
        DeleteFailureCode = null;
        ClearCompletion();
        return true;
    }

    public bool RenewAgentDeletion(Guid operationId, Guid token, DateTimeOffset now, TimeSpan lease)
    {
        if (!OwnsAgentDeletion(operationId, token, now)) return false;
        if (lease <= TimeSpan.Zero) throw new ArgumentException("Invalid lease duration.");
        DeleteLeaseExpiresAt = BackupGuard.Timestamp(now + lease);
        return true;
    }

    public bool CompleteAgentDeletion(Guid operationId, Guid token, DateTimeOffset now, bool absenceVerified,
        string? failureCode, DateTimeOffset? nextAttemptAt)
    {
        if (!OwnsAgentDeletion(operationId, token, now)) return false;
        if (absenceVerified == (failureCode is not null)) throw new ArgumentException("A deletion must prove absence or report a failure.");
        if (failureCode is not null) BackupGuard.Reference(failureCode);
        if (nextAttemptAt < now || (absenceVerified && nextAttemptAt is not null)) throw new ArgumentException("Invalid deletion retry.");
        Status = absenceVerified ? BackupReplicaStatus.Deleted : BackupReplicaStatus.DeleteFailed;
        DeletedAt = absenceVerified ? BackupGuard.Timestamp(now) : null;
        DeleteFailureCode = failureCode;
        DeleteNextAttemptAt = nextAttemptAt is { } next ? BackupGuard.Timestamp(next) : null;
        DeleteCompletionToken = token;
        DeleteCompletionAbsenceVerified = absenceVerified;
        DeleteCompletionFailureCode = failureCode;
        ClearLease();
        return true;
    }

    public bool SameAgentCompletion(Guid operationId, Guid token, bool absenceVerified, string? failureCode) =>
        ExecutionLocation == BackupStorageExecutionLocation.Agent && DeleteOperationId == operationId &&
        DeleteCompletionToken == token && DeleteCompletionAbsenceVerified == absenceVerified && DeleteCompletionFailureCode == failureCode;

    private bool OwnsAgentDeletion(Guid operationId, Guid token, DateTimeOffset now) =>
        ExecutionLocation == BackupStorageExecutionLocation.Agent && Status == BackupReplicaStatus.Deleting &&
        DeleteOperationId == operationId && DeleteLeaseToken == token && DeleteLeaseExpiresAt > now;

    private void ClearCompletion()
    {
        DeleteCompletionToken = null;
        DeleteCompletionAbsenceVerified = null;
        DeleteCompletionFailureCode = null;
    }

    private void ClearLease()
    {
        DeleteLeaseToken = null;
        DeleteLeaseOwner = null;
        DeleteLeaseExpiresAt = null;
    }

    internal bool SameResult(BackupReplica other) => StorageProvider == other.StorageProvider && StorageTarget == other.StorageTarget &&
        ExecutionLocation == other.ExecutionLocation && OwningAgentId == other.OwningAgentId &&
        Key == other.Key && SizeBytes == other.SizeBytes && Checksum == other.Checksum && ChecksumAlgorithm == other.ChecksumAlgorithm &&
        VerificationState == other.VerificationState && VerificationMethod == other.VerificationMethod && CreatedAt == other.CreatedAt && VerifiedAt == other.VerifiedAt;
}
