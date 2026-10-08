namespace BackupPlatform.Contracts.Downloads;

public static class DownloadTaskTypes
{
    public const string BackupDownload = "backup-download";
    public const string Capability = "storage.download";
    public const string LeaseHeader = "X-Backup-Download-Lease";
}

/// <summary>A backend-validated replica reference, never a filesystem path or a credential. The session and lease fence every outbound mutation.</summary>
public sealed record DownloadTaskSnapshot
{
    public int Version { get; init; } = 1;
    public Guid SessionId { get; init; }
    public Guid TenantId { get; init; }
    public Guid ArtifactId { get; init; }
    public Guid ReplicaId { get; init; }
    public Guid LeaseToken { get; init; }
    public DateTimeOffset LeaseExpiresAt { get; init; }
    public string StorageProvider { get; init; } = string.Empty;
    public string StorageTarget { get; init; } = string.Empty;
    public string StorageKey { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public string Checksum { get; init; } = string.Empty;
    public string ChecksumAlgorithm { get; init; } = "sha256";
}

/// <summary>Only failures and cancellations are reported by the agent. Successful completion requires the backend to independently verify all received bytes.</summary>
public sealed record DownloadTaskCompletionRequest
{
    public Guid LeaseToken { get; init; }
    public string Status { get; init; } = string.Empty;
    public string? FailureCode { get; init; }
}

public static class DownloadAgentFailureCodes
{
    public const string InvalidConfiguration = "download_invalid_configuration";
    public const string SourceMissing = "download_source_missing";
    public const string SourceInaccessible = "download_source_inaccessible";
    public const string IntegrityMismatch = "download_integrity_mismatch";
    public const string AgentUnavailable = "download_agent_unavailable";
    public const string Cancelled = "download_cancelled";
    public const string InternalError = "download_internal_error";
}
