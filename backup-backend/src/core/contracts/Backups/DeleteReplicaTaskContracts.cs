namespace BackupPlatform.Contracts.Backups;

/// <summary>Explicit backend authorization for one managed object; never contains a filesystem path.</summary>
public sealed record DeleteReplicaTaskSnapshot
{
    public int Version { get; init; } = 1;
    public Guid ReplicaId { get; init; }
    public Guid TenantId { get; init; }
    public Guid AgentId { get; init; }
    public Guid OperationId { get; init; }
    public Guid LeaseToken { get; init; }
    public DateTimeOffset LeaseExpiresAt { get; init; }
    public Guid JobId { get; init; }
    public Guid RunId { get; init; }
    public Guid AttemptId { get; init; }
    public string StorageProvider { get; init; } = string.Empty;
    public string StorageTarget { get; init; } = string.Empty;
    public string StorageKey { get; init; } = string.Empty;
    public string StoragePrefix { get; init; } = string.Empty;
}

public sealed record DeleteReplicaLeaseRequest
{
    public Guid OperationId { get; init; }
    public Guid LeaseToken { get; init; }
}

public sealed record DeleteReplicaLeaseResponse
{
    public DateTimeOffset LeaseExpiresAt { get; init; }
}

/// <summary>Success requires provider deletion followed by explicit absence verification.</summary>
public sealed record DeleteReplicaCompletionRequest
{
    public Guid OperationId { get; init; }
    public Guid LeaseToken { get; init; }
    public bool AbsenceVerified { get; init; }
    public string? FailureCode { get; init; }
}

public static class DeleteReplicaFailureCodes
{
    public const string StorageTargetUnknown = "storage_target_unknown";
    public const string InvalidLocation = "invalid_location";
    public const string InvalidConfiguration = "invalid_configuration";
    public const string AccessDenied = "access_denied";
    public const string IoError = "io_error";
    public const string DeleteNotSupported = "delete_not_supported";
    public const string DeleteNotVerified = "delete_not_verified";
    public const string Unexpected = "unexpected";
    public const string AgentUnavailable = "agent_unavailable";
}
