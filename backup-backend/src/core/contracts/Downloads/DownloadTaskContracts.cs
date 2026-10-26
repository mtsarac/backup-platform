namespace BackupPlatform.Contracts.Downloads;

public static class DownloadTaskTypes
{
    public const string BackupDownload = "backup-download";
    public const string Capability = "storage.download.direct-v1";
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
    public Guid? ConnectionId { get; init; }
    public bool Decrypt { get; init; }
    public string? SourceChecksum { get; init; }
}

/// <summary>Completion records the authenticated agent's verified stream and the peer's disk acknowledgement; artifact bytes never enter the control plane.</summary>
public sealed record DownloadTaskCompletionRequest
{
    public Guid LeaseToken { get; init; }
    public string Status { get; init; } = string.Empty;
    public string? FailureCode { get; init; }
    public long TransferredBytes { get; init; }
    public string? Checksum { get; init; }
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
    public const string DirectPathUnavailable = "DirectPathUnavailable";
}

/// <summary>Bounded SDP metadata only. An absent SDP polls the peer's description; a supplied SDP is immutable and single use.</summary>
public sealed record DownloadSignalRequest
{
    public Guid LeaseToken { get; init; }
    public string? Sdp { get; init; }
}
public sealed record DownloadSignal(string? Sdp);
