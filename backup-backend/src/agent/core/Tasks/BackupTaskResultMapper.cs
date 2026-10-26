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
    StagingInvalid,
    QuotaExceeded
}

internal static class BackupTaskResultMapper
{
    public static BackupTaskCompletionRequest Map(BackupTaskSnapshot task, DatabaseBackupPipelineResult result)
    {
        if (result.Status == DatabaseBackupStatus.Paused)
            return new BackupTaskCompletionRequest { LeaseToken = task.LeaseToken, Status = "Paused", FailureCode = result.PauseReason };
        var artifact = MapArtifact(task, result);
        if (result.Status == DatabaseBackupStatus.Succeeded && (artifact is null || artifact.Replicas.Count == 0))
        {
            return Failure(task.LeaseToken, BackupTaskFailureCode.InternalError);
        }
        return new BackupTaskCompletionRequest
        {
            LeaseToken = task.LeaseToken,
            Status = result.Status.ToString(),
            FailureCode = result.Status == DatabaseBackupStatus.Succeeded ? null : MapFailure(result.Failure).ToString(),
            Artifact = artifact,
            Destinations = MapDestinations(result)
        };
    }

    /// <summary>
    /// One entry per destination, whatever the run's status: this is how a Failed run still says which destinations failed and how. A destination only
    /// counts as Verified when its replica provably matches the artifact; anything else is reported as the failure it is.
    /// </summary>
    private static BackupTaskDestinationResult[] MapDestinations(DatabaseBackupPipelineResult result) => [.. result.Destinations.Select(destination =>
    {
        var sound = IsSound(destination, result.Artifact);
        var cancelled = !sound && destination.State == BackupDestinationState.Cancelled;
        return new BackupTaskDestinationResult
        {
            StorageProvider = destination.Destination.Storage.Value,
            StorageConnectionId = destination.Destination.ConnectionId,
            State = sound ? "Verified" : cancelled ? "Cancelled" : "Failed",
            Upload = destination.Uploaded ? "Succeeded"
                : destination.State == BackupDestinationState.Failed && destination.Failure is { Stage: BackupExecutionStage.Uploading, ReasonCode: not BackupPipelineReasons.StorageNotAvailable } ? "Failed" : "NotAttempted",
            Verification = sound ? "Verified" : destination.Failure?.Stage == BackupExecutionStage.VerifyingReplica ? "Failed" : "NotRun",
            FailureCode = sound ? null : cancelled ? BackupTaskFailureCode.Cancelled.ToString()
                : (destination.Failure is null ? BackupTaskFailureCode.ReplicaVerificationFailed : MapFailure(destination.Failure)).ToString(),
            Key = destination.Uploaded ? destination.Key?.Value : null,
            ObjectMayRemain = destination.ObjectMayRemain && destination.Key is not null,
            StartedAt = destination.StartedAt,
            CompletedAt = destination.FinishedAt
        };
    })];

    private static bool IsSound(BackupDestinationResult destination, BackupArtifactInfo? artifact) =>
        destination is { State: BackupDestinationState.Verified, Replica: { VerificationStatus: StorageVerificationStatus.Verified } replica }
        && artifact is not null && replica.SizeBytes == artifact.SizeBytes && replica.Checksum == artifact.Checksum;

    public static BackupTaskCompletionRequest Failure(Guid leaseToken, BackupTaskFailureCode code) => new()
    {
        LeaseToken = leaseToken,
        Status = code == BackupTaskFailureCode.Cancelled ? "Cancelled" : "Failed",
        FailureCode = code.ToString()
    };

    private static BackupTaskFailureCode MapFailure(BackupFailure? failure) => failure?.ReasonCode == BackupPipelineReasons.QuotaExceeded
        ? BackupTaskFailureCode.QuotaExceeded
        : failure?.Kind == BackupFailureKind.Cancelled
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
        if (result.SourceFile is not { } source || result.Artifact is not { } artifact)
        {
            return null;
        }
        var copies = result.Destinations.Where(destination => IsSound(destination, artifact)).ToArray();
        if (copies.Length == 0 && !result.Destinations.Any(destination => destination.ObjectMayRemain))
        {
            return null;
        }
        return new BackupTaskArtifact
        {
            SizeBytes = artifact.SizeBytes,
            SourceChecksum = source.Checksum.Hex,
            ArtifactChecksum = artifact.Checksum.Hex,
            ChecksumAlgorithm = "sha256",
            Format = artifact.Transforms.Any(transform => transform.Kind == ArtifactTransformKind.Encryption) ? ArtifactFormats.Bpe2 : ArtifactFormats.Plain,
            EncryptionKeyId = artifact.Seal?.KeyId,
            EncryptionKeyVersion = artifact.Seal?.KeyVersion,
            EncryptionWrappedDekBase64 = artifact.Seal is { } seal ? Convert.ToBase64String(seal.WrappedDek) : null,
            EncryptionRecipients = artifact.Seal?.Recipients.Select(recipient => new BackupTaskArtifactRecipient { Type = recipient.Type, KeyId = recipient.KeyId, KeyVersion = recipient.KeyVersion }).ToArray() ?? [],
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
            Replicas = [.. copies.Select(copy => new BackupTaskReplica
            {
                StorageProvider = copy.Replica!.Location.Provider.Value,
                StorageTarget = AgentStorageProviders.External(copy.Destination.Storage.Value) && copy.Destination.ConnectionId is { } connection
                    ? connection.ToString("D")
                    : task.Configuration.Destinations.FirstOrDefault(d => d.StorageConnectionId == copy.Destination.ConnectionId && d.StorageProvider == AgentStorageProviders.Local)?.StorageTarget
                        ?? copy.Replica.Location.Provider.Value,
                StorageConnectionId = copy.Destination.ConnectionId,
                Key = copy.Replica.Location.Key.Value,
                SizeBytes = copy.Replica.SizeBytes,
                Checksum = copy.Replica.Checksum.Hex,
                ChecksumAlgorithm = "sha256",
                VerificationState = copy.Replica.VerificationStatus.ToString(),
                VerificationMethod = copy.Replica.VerificationMethod.ToString(),
                CreatedAt = result.BackupFinishedAt ?? result.StartedAt,
                VerifiedAt = copy.Replica.VerifiedAt
            })]
        };
    }
}
