using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;

namespace BackupPlatform.Domain.Backups;

public sealed class ReplicaRepairOperation : ITenantOwned
{
    private ReplicaRepairOperation()
    {
        TenantId = null!;
        StorageProvider = null!;
    }

    public ReplicaRepairOperation(
        Guid id,
        TenantId tenantId,
        Guid artifactId,
        Guid storageConnectionId,
        string storageProvider,
        Guid? sourceReplicaId,
        DateTimeOffset createdAt,
        Guid? requestedByUserId = null,
        long totalBytes = 0)
    {
        BackupGuard.Id(id);
        BackupGuard.Id(artifactId);
        ArgumentNullException.ThrowIfNull(tenantId);
        BackupGuard.Reference(storageProvider);

        Id = id;
        TenantId = tenantId;
        ArtifactId = artifactId;
        StorageConnectionId = storageConnectionId;
        StorageProvider = storageProvider;
        SourceReplicaId = sourceReplicaId;
        CreatedAt = BackupGuard.Timestamp(createdAt);
        RequestedByUserId = requestedByUserId;
        TotalBytes = Math.Max(0, totalBytes);
        Status = ReplicaRepairStatus.Pending;
    }

    public Guid Id { get; private set; }
    public TenantId TenantId { get; private set; }
    public Guid ArtifactId { get; private set; }
    public Guid StorageConnectionId { get; private set; }
    public string StorageProvider { get; private set; }
    public Guid? SourceReplicaId { get; private set; }
    public Guid? TargetReplicaId { get; private set; }
    public ReplicaRepairStatus Status { get; private set; }
    public string? LeaseOwner { get; private set; }
    public Guid? LeaseToken { get; private set; }
    public DateTimeOffset? LeaseExpiresAt { get; private set; }
    public string? FailureCode { get; private set; }
    public string? FailureReason { get; private set; }
    public long TransferredBytes { get; private set; }
    public long TotalBytes { get; private set; }
    public Guid? RequestedByUserId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public DateTimeOffset? CancellationRequestedAt { get; private set; }

    public bool IsTerminal => Status is ReplicaRepairStatus.Succeeded or ReplicaRepairStatus.Failed or ReplicaRepairStatus.Cancelled;
    public bool HasLease(Guid token, DateTimeOffset now) => token != Guid.Empty && LeaseToken == token && LeaseExpiresAt > now && !IsTerminal;

    public void SetSourceReplica(Guid sourceReplicaId)
    {
        if (IsTerminal) throw new InvalidOperationException("Cannot update source replica on a terminal repair operation.");
        BackupGuard.Id(sourceReplicaId);
        SourceReplicaId = sourceReplicaId;
    }

    public void Claim(Guid token, string owner, DateTimeOffset now, TimeSpan duration)
    {
        if (Status != ReplicaRepairStatus.Pending) throw new InvalidOperationException("Only a pending repair can be claimed.");
        BackupGuard.Id(token);
        BackupGuard.Text(owner, 128);
        if (duration <= TimeSpan.Zero) throw new ArgumentException("Invalid lease duration.");

        Status = ReplicaRepairStatus.InProgress;
        LeaseToken = token;
        LeaseOwner = owner;
        StartedAt = BackupGuard.Timestamp(now);
        LeaseExpiresAt = BackupGuard.Timestamp(now + duration);
    }

    public void RenewLease(Guid token, DateTimeOffset now, TimeSpan duration)
    {
        ValidateLease(token, now);
        if (duration <= TimeSpan.Zero) throw new ArgumentException("Invalid lease duration.");
        LeaseExpiresAt = BackupGuard.Timestamp(now + duration);
    }

    public void UpdateProgress(Guid token, long transferredBytes, long totalBytes, DateTimeOffset now)
    {
        ValidateLease(token, now);
        TransferredBytes = Math.Max(0, transferredBytes);
        if (totalBytes > 0) TotalBytes = totalBytes;
    }

    public void Succeed(Guid token, Guid targetReplicaId, DateTimeOffset now)
    {
        ValidateLease(token, now);
        BackupGuard.Id(targetReplicaId);

        Status = ReplicaRepairStatus.Succeeded;
        TargetReplicaId = targetReplicaId;
        TransferredBytes = TotalBytes > 0 ? TotalBytes : TransferredBytes;
        CompletedAt = BackupGuard.Timestamp(now);
        ClearLease();
    }

    public void Fail(Guid token, string failureCode, string? reason, DateTimeOffset now)
    {
        ValidateLease(token, now);
        BackupGuard.Reference(failureCode);

        Status = ReplicaRepairStatus.Failed;
        FailureCode = failureCode;
        FailureReason = reason;
        CompletedAt = BackupGuard.Timestamp(now);
        ClearLease();
    }

    public void MarkStaleFailed(string failureCode, string? reason, DateTimeOffset now)
    {
        if (IsTerminal) return;
        BackupGuard.Reference(failureCode);

        Status = ReplicaRepairStatus.Failed;
        FailureCode = failureCode;
        FailureReason = reason;
        CompletedAt = BackupGuard.Timestamp(now);
        ClearLease();
    }

    public void RequestCancellation(DateTimeOffset now)
    {
        if (IsTerminal) return;
        CancellationRequestedAt ??= BackupGuard.Timestamp(now);
    }

    public void Cancel(Guid token, DateTimeOffset now)
    {
        ValidateLease(token, now);

        Status = ReplicaRepairStatus.Cancelled;
        FailureCode = ReplicaRepairFailureCodes.Cancelled;
        FailureReason = "Repair operation was cancelled.";
        CompletedAt = BackupGuard.Timestamp(now);
        ClearLease();
    }

    public void CancelPending(DateTimeOffset now)
    {
        if (Status != ReplicaRepairStatus.Pending) throw new InvalidOperationException("Only a pending repair can be cancelled without a lease.");

        Status = ReplicaRepairStatus.Cancelled;
        FailureCode = ReplicaRepairFailureCodes.Cancelled;
        FailureReason = "Repair operation was cancelled before start.";
        CompletedAt = BackupGuard.Timestamp(now);
        ClearLease();
    }

    private void ValidateLease(Guid token, DateTimeOffset now)
    {
        if (Status != ReplicaRepairStatus.InProgress || LeaseToken != token || LeaseExpiresAt <= now)
            throw new InvalidOperationException("Repair operation lease is lost, expired, or invalid.");
    }

    private void ClearLease()
    {
        LeaseToken = null;
        LeaseOwner = null;
        LeaseExpiresAt = null;
    }
}
