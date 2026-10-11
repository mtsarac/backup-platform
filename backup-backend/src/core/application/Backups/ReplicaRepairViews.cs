using BackupPlatform.Domain.Backups;

namespace BackupPlatform.Application.Backups;

public sealed record ReplicaRepairOperationView(
    Guid Id,
    Guid ArtifactId,
    Guid StorageConnectionId,
    string StorageProvider,
    Guid? SourceReplicaId,
    Guid? TargetReplicaId,
    string Status,
    string? FailureCode,
    string? FailureReason,
    long TransferredBytes,
    long TotalBytes,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? CancellationRequestedAt,
    Guid? RequestedByUserId,
    int AttemptCount = 0,
    int MaxAttempts = 5,
    DateTimeOffset? NextRetryAt = null,
    DateTimeOffset? LastAttemptAt = null,
    string? LastFailureCode = null,
    bool IsExhausted = false);

public sealed record StartReplicaRepairRequest(
    Guid? StorageConnectionId = null,
    string? StorageProvider = null);

public sealed record ReplicaRepairAdmissionResult(
    bool Allowed,
    string? DenialCode,
    string? DenialReason,
    BackupArtifact? Artifact = null,
    BackupDestination? TargetDestination = null,
    BackupReplica? SelectedSource = null,
    IReadOnlyList<BackupReplica>? CandidateSources = null);
