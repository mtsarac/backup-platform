namespace BackupPlatform.Contracts.Backups;

/// <summary>Portable task-scoped storage relay. No endpoint address or credential is carried by a task.</summary>
public static class TaskStorageProtocol
{
    public const string Capability = "storage.backend-relay";
    public const string DirectCapability = "storage.agent-direct";
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
    public bool ObjectMayRemain { get; init; }
    public string? RemainingObjectKey { get; init; }
}

public sealed record TaskStorageVerifyRequest(long SizeBytes, string Checksum);

public sealed record TaskStorageAccessResponse
{
    public required string ProviderType { get; init; }
    public string? AccessToken { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public string? FolderId { get; init; }
    public string? Host { get; init; }
    public int Port { get; init; } = 22;
    public string? Username { get; init; }
    public string? AuthenticationType { get; init; }
    public string? RemoteBaseDirectory { get; init; }
    public string? HostKeyFingerprint { get; init; }
    public string? Password { get; init; }
    public string? PrivateKey { get; init; }
    public string? Passphrase { get; init; }
    public IReadOnlyDictionary<string, string>? Configuration { get; init; }
    public string? AccessKey { get; init; }
    public string? SecretKey { get; init; }
    public string? SessionToken { get; init; }
    public string? Domain { get; init; }

    public override string ToString() =>
        $"TaskStorageAccessResponse {{ ProviderType = {ProviderType}, Credentials = [REDACTED] }}";
}

public sealed record TaskStorageCatalogRecord(
    string FileId,
    string ParentFolderId,
    string FileName,
    long SizeBytes,
    string? Checksum,
    string? Md5Checksum);

public sealed record TaskStorageCatalogSaveRequest(
    string StorageKey,
    TaskStorageCatalogRecord Record);
