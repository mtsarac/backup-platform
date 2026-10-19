namespace BackupPlatform.Contracts.Backups;

/// <summary>Capability an agent advertises when it executes destination-list snapshots: every destination of a backup (one artifact, N independently verified replicas) and reports each one. Required for every new run.</summary>
public static class BackupTaskProtocol
{
    public const string MultiDestinationCapability = "backup.multi-destination";
    public const string QuotaBoundedUploadCapability = "backup.quota-bounded-upload";

    /// <summary>The agent paces its upload bytes to the limits the backend sets (<see cref="UploadPolicyResponse"/>). A run that a limit applies to is never given to an agent without it.</summary>
    public const string UploadBandwidthLimitCapability = "backup.upload-bandwidth-limit";
}

/// <summary>Immutable execution input. Credential material is resolved locally by reference, never carried in this envelope.</summary>
public sealed record BackupExecutionConfiguration
{
    public string Engine { get; init; } = "mssql";
    public string Database { get; init; } = string.Empty;
    public string Host { get; init; } = string.Empty;
    public int? Port { get; init; }
    public bool IntegratedSecurity { get; init; }
    public string? DatabaseCredentialReference { get; init; }
    /// <summary>Write-only: a login typed in the UI. The API exchanges it for a <see cref="DatabaseCredentialReference"/> and never stores, echoes or forwards it.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public BackupPlatform.Contracts.Agents.DatabaseLoginInput? DatabaseLogin { get; init; }
    public bool Encrypt { get; init; } = true;
    public bool TrustServerCertificate { get; init; }
    public string StagingReference { get; init; } = "default";
    public string StoragePrefix { get; init; } = string.Empty;
    public bool CopyOnly { get; init; } = true;
    public string ChecksumPolicy { get; init; } = "Preferred";
    public string CompressionPolicy { get; init; } = "Preferred";
    /// <summary>The only destination source (at least one, exactly one primary). A job may leave a destination's connection null (the tenant default for its provider); a run always carries the resolved one.</summary>
    public IReadOnlyList<BackupDestinationContract> Destinations { get; init; } = [];
    public int RequiredVerifiedReplicaCount { get; init; } = 1;
    /// <summary>"Off" (default when omitted), "Optional" or "Required". The backend turns it into <see cref="BackupTaskSnapshot.Encryption"/>.</summary>
    public string EncryptionPolicy { get; init; } = "Off";
}

public sealed record BackupDestinationContract
{
    public string StorageProvider { get; init; } = "local";
    public Guid? StorageConnectionId { get; init; }
    /// <summary>For a local destination, the agent-declared volume the run writes to; null is the agent's default local storage. Fixed by the backend when the run is admitted, never taken from a client.</summary>
    public string? StorageTarget { get; init; }
    /// <summary>"Backend" or "Agent"; required, never inferred from the provider.</summary>
    public string? StorageExecutionLocation { get; init; }
    public bool IsPrimary { get; init; }
}
/// <summary>A database-authoritative claim, not a push command. Tokens fence every mutation of this attempt.</summary>
public sealed record BackupTaskSnapshot
{
    public int Version { get; init; } = 1;
    public Guid RunId { get; init; }
    public Guid AttemptId { get; init; }
    public int AttemptNumber { get; init; }
    public Guid TenantId { get; init; }
    public Guid JobId { get; init; }
    public Guid LeaseToken { get; init; }
    public DateTimeOffset LeaseExpiresAt { get; init; }
    public string BackupType { get; init; } = "Full";
    public required BackupExecutionConfiguration Configuration { get; init; }
    public bool CancellationRequested { get; init; }
    /// <summary>Hard-quota bound checked before any transfer; null means no hard bound, zero refuses storage.</summary>
    public long? MaxArtifactBytes { get; init; }
    /// <summary>Null means the job does not encrypt. Carries the tenant's PUBLIC wrap key only; no private or symmetric key ever travels to the agent.</summary>
    public BackupEncryptionInstruction? Encryption { get; init; }
}

public sealed record BackupTaskStartRequest
{
    public Guid LeaseToken { get; init; }
    public string SoftwareVersion { get; init; } = string.Empty;
    public string ProtocolVersion { get; init; } = string.Empty;
    public string Platform { get; init; } = string.Empty;
}

public sealed record BackupTaskLeaseRequest
{
    public Guid LeaseToken { get; init; }
}

public sealed record BackupTaskLeaseResponse
{
    public DateTimeOffset LeaseExpiresAt { get; init; }
    public bool CancellationRequested { get; init; }
}

public sealed record BackupTaskProgressRequest
{
    public Guid LeaseToken { get; init; }
    public string Stage { get; init; } = string.Empty;
    public double? Percent { get; init; }
}

/// <summary>
/// Terminal metadata only. Tenant-facing failure text is selected by the backend from FailureCode.
/// <see cref="Destinations"/> is the independent per-destination outcome of the one artifact: it is what separates "the engine backed up" from
/// "every required copy exists". A run configured with several destinations must report every one of them.
/// </summary>
public sealed record BackupTaskCompletionRequest
{
    public Guid LeaseToken { get; init; }
    public string Status { get; init; } = string.Empty;
    public string? FailureCode { get; init; }
    /// <summary>On failure/cancellation, metadata with no verified replicas is permitted only to account for ObjectMayRemain destinations and schedule their cleanup.</summary>
    public BackupTaskArtifact? Artifact { get; init; }
    public IReadOnlyList<BackupTaskDestinationResult> Destinations { get; init; } = [];
}

/// <summary>
/// What happened to one snapshot destination. <see cref="State"/> is Verified, Failed or Cancelled; a Verified destination has a matching entry in
/// <see cref="BackupTaskArtifact.Replicas"/>. <see cref="ObjectMayRemain"/> says an object was written under <see cref="Key"/> and could not be proven removed.
/// </summary>
public sealed record BackupTaskDestinationResult
{
    public string StorageProvider { get; init; } = string.Empty;
    public Guid? StorageConnectionId { get; init; }
    public string State { get; init; } = string.Empty;
    public string Upload { get; init; } = "NotAttempted";
    public string Verification { get; init; } = "NotRun";
    public string? FailureCode { get; init; }
    public string? Key { get; init; }
    public bool ObjectMayRemain { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset CompletedAt { get; init; }
}

public sealed record BackupTaskArtifact
{
    public long SizeBytes { get; init; }
    public string SourceChecksum { get; init; } = string.Empty;
    public string ArtifactChecksum { get; init; } = string.Empty;
    public string ChecksumAlgorithm { get; init; } = "sha256";
    public DateTimeOffset CreatedAt { get; init; }
    public string SourceMetadataJson { get; init; } = "{}";
    public string AppliedOptionsJson { get; init; } = "{}";
    public IReadOnlyList<BackupTaskReplica> Replicas { get; init; } = [];
    /// <summary><see cref="ArtifactFormats.Plain"/> or <see cref="ArtifactFormats.Bpe2"/>: what the agent actually wrote. The backend rejects a result that contradicts a Required policy.</summary>
    public string Format { get; init; } = ArtifactFormats.Plain;
    /// <summary>Key id and version of the tenant-wrap recipient the artifact was sealed to; null when <see cref="Format"/> is plain.</summary>
    public string? EncryptionKeyId { get; init; }
    public int? EncryptionKeyVersion { get; init; }
    /// <summary>The tenant-wrap recipient's wrapped DEK, copied from the artifact header. It is ciphertext only the backend's private wrap key can open; the backend stores it so restore-time unwrap never accepts key material from an agent.</summary>
    public string? EncryptionWrappedDekBase64 { get; init; }
    /// <summary>Every recipient read back from the artifact header (type, key id, key version; no wrapped keys). Empty when <see cref="Format"/> is plain; the backend checks each against the keys it issued.</summary>
    public IReadOnlyList<BackupTaskArtifactRecipient> EncryptionRecipients { get; init; } = [];
}

public sealed record BackupTaskArtifactRecipient
{
    /// <summary>One of <see cref="BackupRecipientTypes"/>.</summary>
    public string Type { get; init; } = string.Empty;
    public string KeyId { get; init; } = string.Empty;
    public int KeyVersion { get; init; }
}

/// <summary>Wire names of the <c>bpe</c> recipient types; identical to the header's <c>type</c> strings.</summary>
public static class BackupRecipientTypes
{
    public const string TenantWrap = "tenant-wrap";
    public const string PlatformRecovery = "platform-recovery";
    public const string TenantRecovery = "tenant-recovery";
}

/// <summary>One public key the agent seals the DEK to. <see cref="KeyId"/> is SHA-256(SPKI)[..16] in hex, which the agent re-derives before wrapping so a task whose key and id disagree is refused.</summary>
public sealed record BackupEncryptionRecipient
{
    public string Type { get; init; } = string.Empty;
    public int KeyVersion { get; init; }
    public string KeyId { get; init; } = string.Empty;
    public string PublicKeySpkiBase64 { get; init; } = string.Empty;
}

public static class ArtifactFormats
{
    public const string Plain = "v1-plain";
    public const string Bpe2 = "v2-bpe";
}

/// <summary>
/// Per-job encryption instruction: exactly one tenant-wrap recipient, plus the platform-recovery recipient when the platform has one configured and the
/// tenant's optional tenant-recovery recipient. <see cref="Required"/> makes the agent fail rather than produce a plaintext artifact. Public keys only.
/// </summary>
public sealed record BackupEncryptionInstruction
{
    public bool Required { get; init; }
    public IReadOnlyList<BackupEncryptionRecipient> Recipients { get; init; } = [];
}

public sealed record BackupTaskReplica
{
    public string StorageProvider { get; init; } = string.Empty;
    public string StorageTarget { get; init; } = string.Empty;
    /// <summary>The snapshot destination this copy lives on. May be omitted only when the replica's provider identifies exactly one snapshot destination.</summary>
    public Guid? StorageConnectionId { get; init; }
    public string Key { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public string Checksum { get; init; } = string.Empty;
    public string ChecksumAlgorithm { get; init; } = "sha256";
    public string VerificationState { get; init; } = string.Empty;
    public string VerificationMethod { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? VerifiedAt { get; init; }
}
