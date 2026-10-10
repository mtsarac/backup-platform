namespace BackupPlatform.Contracts.Backups;

/// <summary>Portable task-scoped storage relay. No endpoint address or credential is carried by a task.</summary>
public static class TaskStorageProtocol
{
    public const string Capability = "storage.backend-relay";
    public const string LeaseHeader = "X-Task-Lease";
    public const string KeyHeader = "X-Storage-Key";
    public const string ChecksumHeader = "X-Storage-Sha256";
    /// <summary>Names the snapshot destination (a storage connection id) a backup transfer targets. The backend authorizes it against the run snapshot; it is never trusted as a location.</summary>
    public const string DestinationHeader = "X-Storage-Destination";
    public const string RestoreHeader = "X-Restore-Operation";
}

public sealed record TaskStorageResponse
{
    public bool Exists { get; init; }
    public long SizeBytes { get; init; }
    public string? Checksum { get; init; }
    public DateTimeOffset LastModifiedUtc { get; init; }
    public string? VerificationStatus { get; init; }
    public string? VerificationMethod { get; init; }
    public string? FailureKind { get; init; }
}

public sealed record TaskStorageVerifyRequest(long SizeBytes, string Checksum);
