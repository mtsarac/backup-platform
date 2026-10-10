namespace BackupPlatform.Contracts.Backups;

/// <summary>Capability an agent advertises when it executes every destination of a multi-destination backup (one artifact, N independently verified replicas) and reports each one.</summary>
public static class BackupTaskProtocol
{
    public const string MultiDestinationCapability = "backup.multi-destination";
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
    public bool Encrypt { get; init; } = true;
    public bool TrustServerCertificate { get; init; }
    public string StagingReference { get; init; } = "default";
    public string StorageProvider { get; init; } = "local";
    public string? StorageExecutionLocation { get; init; }
    /// <summary>The destination connection. A job may omit it (the tenant's default for <see cref="StorageProvider"/>); a run always carries the resolved one.</summary>
    public Guid? StorageConnectionId { get; init; }
    public string StoragePrefix { get; init; } = string.Empty;
    public bool CopyOnly { get; init; } = true;
    public string ChecksumPolicy { get; init; } = "Preferred";
    public string CompressionPolicy { get; init; } = "Preferred";
    public IReadOnlyList<BackupDestinationContract> Destinations { get; init; } = [];
    public int RequiredVerifiedReplicaCount { get; init; } = 1;
}

public sealed record BackupDestinationContract
{
    public string StorageProvider { get; init; } = "local";
    public Guid? StorageConnectionId { get; init; }
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
}

public sealed record BackupTaskReplica
{
    public string StorageProvider { get; init; } = string.Empty;
    public string StorageTarget { get; init; } = string.Empty;
    /// <summary>The snapshot destination this copy lives on. Null from agents that predate multi-destination runs (legal for single-destination runs only).</summary>
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
