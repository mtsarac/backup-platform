using System.Text.Json;
using BackupPlatform.Agent.Core.Backup;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.StorageProviders.Abstractions;

namespace BackupPlatform.Agent.Core.Tasks;

public enum BackupTaskFailureCode
{
    PreCheckFailed,
    SqlBackupFailed,
    VerificationFailed,
    HashFailed,
    StorageWriteFailed,
    ReplicaVerificationFailed,
    InternalError,
    Cancelled,
    DatabaseUnavailable,
    OperationTimedOut,
    StorageUnavailable,
    DatabaseNotFound,
    InvalidConfiguration,
    UnsupportedCapability,
    AuthenticationFailed,
    PermissionDenied,
    StagingInvalid
}

internal static class BackupTaskResultMapper
{
    public static BackupTaskCompletionRequest Map(BackupTaskSnapshot task, DatabaseBackupPipelineResult result)
    {
        var artifact = result.Status == DatabaseBackupStatus.Succeeded ? MapArtifact(task, result) : null;
        if (result.Status == DatabaseBackupStatus.Succeeded && artifact is null)
        {
            return Failure(task.LeaseToken, BackupTaskFailureCode.InternalError);
        }
        return new BackupTaskCompletionRequest
        {
            LeaseToken = task.LeaseToken,
            Status = result.Status.ToString(),
            FailureCode = result.Status == DatabaseBackupStatus.Succeeded ? null : MapFailure(result.Failure).ToString(),
            Artifact = artifact
        };
    }

    public static BackupTaskCompletionRequest Failure(Guid leaseToken, BackupTaskFailureCode code) => new()
    {
        LeaseToken = leaseToken,
        Status = code == BackupTaskFailureCode.Cancelled ? "Cancelled" : "Failed",
        FailureCode = code.ToString()
    };

    private static BackupTaskFailureCode MapFailure(BackupFailure? failure) => failure?.Kind == BackupFailureKind.Cancelled
        ? BackupTaskFailureCode.Cancelled
        : failure?.Cause switch
        {
            BackupFailureCause.DatabaseUnavailable => BackupTaskFailureCode.DatabaseUnavailable,
            BackupFailureCause.Timeout => BackupTaskFailureCode.OperationTimedOut,
            BackupFailureCause.DatabaseNotFound => BackupTaskFailureCode.DatabaseNotFound,
            BackupFailureCause.InvalidConfiguration => BackupTaskFailureCode.InvalidConfiguration,
            BackupFailureCause.Unsupported => BackupTaskFailureCode.UnsupportedCapability,
            BackupFailureCause.AuthenticationFailed => BackupTaskFailureCode.AuthenticationFailed,
            BackupFailureCause.PermissionDenied => BackupTaskFailureCode.PermissionDenied,
            BackupFailureCause.StagingInvalid => BackupTaskFailureCode.StagingInvalid,
            BackupFailureCause.StorageUnavailable => BackupTaskFailureCode.StorageUnavailable,
            _ => MapKind(failure?.Kind)
        };

    private static BackupTaskFailureCode MapKind(BackupFailureKind? kind) => kind switch
    {
        BackupFailureKind.PreCheckFailed => BackupTaskFailureCode.PreCheckFailed,
        BackupFailureKind.SqlBackupFailed => BackupTaskFailureCode.SqlBackupFailed,
        BackupFailureKind.VerificationFailed => BackupTaskFailureCode.VerificationFailed,
        BackupFailureKind.SourceHashFailed => BackupTaskFailureCode.HashFailed,
        BackupFailureKind.StorageWriteFailed => BackupTaskFailureCode.StorageWriteFailed,
        BackupFailureKind.ReplicaVerificationFailed => BackupTaskFailureCode.ReplicaVerificationFailed,
        BackupFailureKind.Cancelled => BackupTaskFailureCode.Cancelled,
        _ => BackupTaskFailureCode.InternalError
    };

    private static BackupTaskArtifact? MapArtifact(BackupTaskSnapshot task, DatabaseBackupPipelineResult result)
    {
        if (result.SourceFile is not { } source || result.Artifact is not { } artifact || result.Replica is not { } replica
            || replica.VerificationStatus != StorageVerificationStatus.Verified
            || replica.SizeBytes != artifact.SizeBytes || replica.Checksum != artifact.Checksum)
        {
            return null;
        }
        return new BackupTaskArtifact
        {
            SizeBytes = artifact.SizeBytes,
            SourceChecksum = source.Checksum.Hex,
            ArtifactChecksum = artifact.Checksum.Hex,
            ChecksumAlgorithm = "sha256",
            CreatedAt = result.BackupFinishedAt ?? result.FinishedAt,
            SourceMetadataJson = JsonSerializer.Serialize(new
            {
                Engine = task.Configuration.Engine,
                DatabaseName = result.Source?.DatabaseName ?? task.Configuration.Database,
                BackupType = result.BackupType.ToString(),
                result.BackupStartedAt,
                BackupFinishedAt = result.BackupFinishedAt ?? result.FinishedAt,
                Instance = result.Source is { } metadata ? new
                {
                    Engine = metadata.Instance.Engine.Value,
                    metadata.Instance.ServerName,
                    metadata.Instance.InstanceName,
                    metadata.Instance.Version,
                    metadata.Instance.Edition,
                    metadata.Instance.HostPlatform
                } : null,
                Details = result.Source?.Details,
                BackupVerification = new
                {
                    State = result.Verification.Status.ToString(),
                    result.Verification.Method,
                    result.Verification.StartedAt,
                    result.Verification.FinishedAt
                }
            }, JsonSerializerOptions.Web),
            AppliedOptionsJson = JsonSerializer.Serialize(result.Options?.Applied, JsonSerializerOptions.Web),
            Replicas = [new BackupTaskReplica
            {
                StorageProvider = replica.Location.Provider.Value,
                StorageTarget = task.Configuration.StorageProvider == "sftp"
                    ? task.Configuration.StorageConnectionId!.Value.ToString("D") : replica.Location.Provider.Value,
                Key = replica.Location.Key.Value,
                SizeBytes = replica.SizeBytes,
                Checksum = replica.Checksum.Hex,
                ChecksumAlgorithm = "sha256",
                VerificationState = replica.VerificationStatus.ToString(),
                VerificationMethod = replica.VerificationMethod.ToString(),
                CreatedAt = result.BackupFinishedAt ?? result.StartedAt,
                VerifiedAt = replica.VerifiedAt
            }]
        };
    }
}
