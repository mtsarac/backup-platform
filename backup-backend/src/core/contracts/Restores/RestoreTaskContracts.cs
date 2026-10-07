namespace BackupPlatform.Contracts.Restores;

/// <summary>Task type of a claimed restore in <see cref="BackupPlatform.Contracts.Agents.AgentTaskEnvelope.Type"/>.</summary>
public static class RestoreTaskTypes
{
    public const string DatabaseRestore = "database-restore";
}

/// <summary>
/// A database-authoritative claim of one restore attempt, not a push command. Carries references only: the replica's storage location, the
/// NAME of a credential the agent resolves locally, and the safe request snapshot. Never a secret, never a filesystem path. Tokens fence every
/// mutation of this attempt.
/// </summary>
public sealed record RestoreTaskSnapshot
{
    public int Version { get; init; } = 1;
    public Guid OperationId { get; init; }
    public Guid AttemptId { get; init; }
    public int AttemptNumber { get; init; }
    public Guid TenantId { get; init; }
    public Guid LeaseToken { get; init; }
    public DateTimeOffset LeaseExpiresAt { get; init; }
    public Guid ArtifactId { get; init; }
    public Guid ReplicaId { get; init; }
    public required RestoreTaskSource Source { get; init; }
    public required RestoreTaskTarget Target { get; init; }
    /// <summary>The database to create or replace on the target.</summary>
    public string DatabaseName { get; init; } = string.Empty;
    /// <summary><c>NewDatabase</c> or <c>ReplaceExistingDatabase</c>.</summary>
    public string Mode { get; init; } = "NewDatabase";
    public RestoreTaskOptions Options { get; init; } = new();
    public string StagingReference { get; init; } = "default";
    public bool CancellationRequested { get; init; }
}

/// <summary>The replica to read, as recorded when the backup was published. The checksum is the SHA-256 the bytes must still have.</summary>
public sealed record RestoreTaskSource
{
    public string StorageProvider { get; init; } = string.Empty;
    public string StorageTarget { get; init; } = string.Empty;
    public string StorageKey { get; init; } = string.Empty;
    /// <summary><c>Agent</c> (the replica is on the target agent's own disk) or <c>Backend</c> (central storage the agent reaches directly).</summary>
    public string StorageExecutionLocation { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public string Checksum { get; init; } = string.Empty;
    public string ChecksumAlgorithm { get; init; } = "sha256";
    /// <summary><c>AgentLocal</c> or <c>CentralStorage</c>.</summary>
    public string Access { get; init; } = string.Empty;
}

public sealed record RestoreTaskTarget
{
    public string Host { get; init; } = string.Empty;
    public int? Port { get; init; }
    public bool IntegratedSecurity { get; init; }
    public string? DatabaseCredentialReference { get; init; }
    public bool Encrypt { get; init; } = true;
    public bool TrustServerCertificate { get; init; }
}

public sealed record RestoreTaskOptions
{
    public bool TerminateActiveConnections { get; init; }
    public bool VerifyChecksum { get; init; } = true;
    public bool VerifyBackup { get; init; } = true;
}

/// <summary>Terminal metadata only. Tenant-facing failure text is selected by the backend from <see cref="FailureCode"/>.</summary>
public sealed record RestoreTaskCompletionRequest
{
    public Guid LeaseToken { get; init; }
    /// <summary><c>Succeeded</c>, <c>Failed</c> or <c>Cancelled</c>.</summary>
    public string Status { get; init; } = string.Empty;
    public string? FailureCode { get; init; }
    /// <summary><c>NotModified</c>, <c>Online</c>, <c>PartiallyRestored</c> or <c>Unknown</c>: what the agent established about the target.</summary>
    public string TargetState { get; init; } = string.Empty;
    public RestoreTaskResult? Result { get; init; }
}

/// <summary>What the attempt learned and did. Every string is fixed, user-safe text or an identifier: no engine exception text, no credential.</summary>
public sealed record RestoreTaskResult
{
    public IReadOnlyList<RestorePreCheckItemResult> PreCheck { get; init; } = [];
    public IReadOnlyList<RestoreWarningResult> Warnings { get; init; } = [];
    public RestoreSourceBackupSummary? SourceBackup { get; init; }
    public RestoreTargetServerSummary? TargetServer { get; init; }
    public IReadOnlyList<RestoreFileMappingResult> FileMappings { get; init; } = [];
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public DateTimeOffset? RestoreStartedAt { get; init; }
    public DateTimeOffset? RestoreFinishedAt { get; init; }
    /// <summary>True when the replica bytes were re-hashed during this restore and matched the recorded checksum.</summary>
    public bool ChecksumVerified { get; init; }
    /// <summary>True when <c>RESTORE VERIFYONLY</c> was run on the staged copy and passed.</summary>
    public bool BackupVerified { get; init; }
    /// <summary>The state SQL Server reports for the target database after the attempt (for example <c>ONLINE</c>, <c>RESTORING</c>); null when it could not be read.</summary>
    public string? DatabaseState { get; init; }
    /// <summary>A fixed sentence describing what was left on the target when it is not simply a healthy online database.</summary>
    public string? TargetStateNote { get; init; }
}

/// <summary><see cref="Status"/> is <c>Passed</c>, <c>Warning</c>, <c>Failed</c> or <c>Skipped</c>; <see cref="ReasonCode"/> is stable and machine-readable.</summary>
public sealed record RestorePreCheckItemResult(string CheckId, string Status, string ReasonCode, string Message);

public sealed record RestoreWarningResult(string Code, string Message);

public sealed record RestoreSourceBackupSummary
{
    public string DatabaseName { get; init; } = string.Empty;
    public string BackupType { get; init; } = string.Empty;
    public string? SourceServerName { get; init; }
    public string? SourceServerVersion { get; init; }
    /// <summary>Source-server wall-clock dates from the backup header; no target timezone or UTC offset is assumed.</summary>
    public DateTime? BackupStartedAt { get; init; }
    public DateTime? BackupFinishedAt { get; init; }
    public int Position { get; init; }
    public long BackupSizeBytes { get; init; }
    public int? CompatibilityLevel { get; init; }
    public string? Collation { get; init; }
    public bool IsCopyOnly { get; init; }
    public bool HasChecksums { get; init; }
    public bool IsCompressed { get; init; }
}

public sealed record RestoreTargetServerSummary
{
    public string? ServerName { get; init; }
    public string? InstanceName { get; init; }
    public string? Version { get; init; }
    public string? Edition { get; init; }
    public string? HostPlatform { get; init; }
}

/// <summary>One logical file and where it was restored on the TARGET server. The source server's physical name is informational only; it is never used as a destination.</summary>
public sealed record RestoreFileMappingResult
{
    public string LogicalName { get; init; } = string.Empty;
    public string FileType { get; init; } = string.Empty;
    public string? FileGroupName { get; init; }
    public string? SourcePhysicalName { get; init; }
    public string TargetPhysicalName { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
}
