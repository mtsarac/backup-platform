namespace BackupPlatform.Contracts.Restores;

/// <summary>Task type of a claimed restore in <see cref="BackupPlatform.Contracts.Agents.AgentTaskEnvelope.Type"/>.</summary>
public static class RestoreTaskTypes
{
    public const string DatabaseRestore = "database-restore";
    public const string DatabaseRestorePrecheck = "database-restore-precheck";
    public const string DatabaseRestoreDiscovery = "database-restore-discovery";
    public const string PrecheckCapability = "mssql.restore.precheck";
    public const string DiscoveryCapability = "mssql.restore.discovery";
    public const string DatabaseRestoreVerification = "database-restore-verification";
    public const string DatabaseRestoreCleanup = "database-restore-cleanup";
    public const string MssqlVerificationCapability = "mssql.restore.verification";
    public const string PostgresVerificationCapability = "postgres.restore.verification";
}

/// <summary>
/// A database-authoritative claim of one restore attempt, not a push command. Carries references only: the replica's storage location, the
/// NAME of a credential the agent resolves locally, and the safe request snapshot. Never a secret, never a filesystem path. Tokens fence every
/// mutation of this attempt.
/// </summary>
public sealed record RestoreTaskSnapshot
{
    public int Version { get; init; } = 1;
    /// <summary>Source artifact engine; absent only on legacy SQL Server snapshots.</summary>
    public string Engine { get; init; } = "mssql";
    /// <summary><c>Execution</c>, <c>Precheck</c>, <c>Discovery</c> or <c>Verification</c>; paired with a distinct envelope task type.</summary>
    public string Purpose { get; init; } = "Execution";
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
    /// <summary>Persisted storage target; for backend-relayed SFTP, the nonempty storage connection GUID, never credentials or a remote path.</summary>
    public string StorageTarget { get; init; } = string.Empty;
    public string StorageKey { get; init; } = string.Empty;
    /// <summary><c>Agent</c> (target agent's own disk) or <c>Backend</c> (mounted central storage or backend-relayed SFTP).</summary>
    public string StorageExecutionLocation { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public string Checksum { get; init; } = string.Empty;
    public string ChecksumAlgorithm { get; init; } = "sha256";
    /// <summary><c>AgentLocal</c>, <c>CentralStorage</c>, or <c>BackendRelayed</c>; SFTP is read through the backend, which alone resolves storage credentials.</summary>
    public string Access { get; init; } = string.Empty;
    /// <summary><see cref="BackupPlatform.Contracts.Backups.ArtifactFormats"/> of the artifact. When v2-bpe the agent decrypts in staging and verifies the plaintext against <see cref="SourceChecksum"/>.</summary>
    public string Format { get; init; } = BackupPlatform.Contracts.Backups.ArtifactFormats.Plain;
    /// <summary>SHA-256 of the plaintext backup file (the artifact's SourceChecksum); the decrypted bytes must match before anything is restored.</summary>
    public string SourceChecksum { get; init; } = string.Empty;
}

/// <summary>
/// Agent to backend, fenced by the restore attempt's lease: ask the backend to unwrap the DEK of THE attempt's own artifact. The request has no
/// body and carries no key material: the backend resolves attempt, operation and artifact itself and unwraps only the wrapped DEK it stored for
/// that artifact, so the endpoint cannot be used to decrypt arbitrary blobs. Route: <c>POST /api/agent/restore-tasks/{attemptId}/artifact-key</c>.
/// The backend verifies the key belongs to the attempt's tenant and audits the call.
/// </summary>
public sealed record ArtifactKeyUnwrapResponse
{
    /// <summary>The DEK in the clear, for this restore only. It must never be logged, persisted or put in a task payload.</summary>
    public string DekBase64 { get; init; } = string.Empty;
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
    public RestoreFileSelection? Files { get; init; }
}

public sealed record RestoreFileSelection(IReadOnlyList<string>? Paths = null, string RelativeDirectory = "");

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
    public RestoreDiscoveryResult? Discovery { get; init; }
    public IReadOnlyList<RestoreFileMappingResult> FileMappings { get; init; } = [];
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public DateTimeOffset? RestoreStartedAt { get; init; }
    public DateTimeOffset? RestoreFinishedAt { get; init; }
    /// <summary>True when the replica bytes were re-hashed during this restore and matched the recorded checksum.</summary>
    public bool ChecksumVerified { get; init; }
    /// <summary>True when the engine's full archive verification was run on the staged copy and passed.</summary>
    public bool BackupVerified { get; init; }
    /// <summary>The state the database engine reports after the attempt (for example <c>ONLINE</c>); null when it could not be read.</summary>
    public string? DatabaseState { get; init; }
    /// <summary>A fixed sentence describing what was left on the target when it is not simply a healthy online database.</summary>
    public string? TargetStateNote { get; init; }
    /// <summary>What an isolated verification proved about the restored disposable database and what became of that database; null for every other purpose.</summary>
    public RestoreVerificationResult? Verification { get; init; }
    /// <summary>The <c>bp_old_*</c> rollback copy a replace restore kept (PostgreSQL); the platform removes it only when an operator asks.</summary>
    public string? RetainedDatabaseName { get; init; }
}

/// <summary>
/// The outcome of an isolated verification. <see cref="Checks"/> are the engine's own checks of the restored database (database online, integrity, data readable) with
/// the platform's Passed / Failed items; <see cref="Cleanup"/> is what became of the disposable database. The sizes describe the read test.
/// </summary>
public sealed record RestoreVerificationResult
{
    public IReadOnlyList<RestorePreCheckItemResult> Checks { get; init; } = [];
    public int? UserTableCount { get; init; }
    public int? ReadableTableCount { get; init; }
    /// <summary><c>Dropped</c>, <c>NotCreated</c> or <c>Failed</c>; anything but Dropped/NotCreated leaves the removal owed and tracked by the backend.</summary>
    public string Cleanup { get; init; } = RestoreVerificationCleanupOutcomes.NotCreated;
    /// <summary>A stable code when <see cref="Cleanup"/> is Failed.</summary>
    public string? CleanupReasonCode { get; init; }
}

public static class RestoreVerificationCleanupOutcomes
{
    public const string Dropped = "Dropped";
    public const string NotCreated = "NotCreated";
    public const string Failed = "Failed";

    public static bool IsRemoved(string? outcome) => outcome is Dropped or NotCreated;
}

/// <summary>
/// A claimed removal of a platform-created database (a verification's disposable database or a retained rollback copy): references only, like a restore task. The lease
/// token fences the report. <see cref="DatabaseName"/> must match the kind's reserved pattern or the agent refuses it.
/// </summary>
public sealed record RestoreCleanupSnapshot
{
    public int Version { get; init; } = 1;
    public string Engine { get; init; } = "mssql";
    public Guid OperationId { get; init; }
    public Guid TenantId { get; init; }
    public Guid LeaseToken { get; init; }
    public DateTimeOffset LeaseExpiresAt { get; init; }
    /// <summary><c>VerificationDatabase</c> or <c>RollbackCopy</c>.</summary>
    public string Kind { get; init; } = string.Empty;
    public string DatabaseName { get; init; } = string.Empty;
    public required RestoreTaskTarget Target { get; init; }
}

public sealed record RestoreCleanupCompletionRequest
{
    public Guid LeaseToken { get; init; }
    /// <summary><c>Dropped</c>, <c>NotFound</c> or <c>Failed</c>.</summary>
    public string Outcome { get; init; } = string.Empty;
    /// <summary>A stable machine code; required for Failed.</summary>
    public string? FailureCode { get; init; }
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

/// <summary>One logical file in the restore plan. Physical filesystem paths are omitted from tenant-facing results.</summary>
public sealed record RestoreFileMappingResult
{
    public string LogicalName { get; init; } = string.Empty;
    public string FileType { get; init; } = string.Empty;
    public string? FileGroupName { get; init; }
    public string? SourcePhysicalName { get; init; }
    public string? TargetPhysicalName { get; init; }
    public long SizeBytes { get; init; }
}

public sealed record RestoreDiscoveryResult
{
    public required RestoreDiscoveredInstance Instance { get; init; }
    public IReadOnlyList<RestoreDiscoveredDatabase> Databases { get; init; } = [];
}

public sealed record RestoreDiscoveredInstance(
    string Engine, string ServerName, string? InstanceName = null, string? Version = null, string? Edition = null, string? HostPlatform = null);

/// <summary><see cref="Availability"/> is <c>Unknown</c>, <c>Online</c> or <c>Unavailable</c>.</summary>
public sealed record RestoreDiscoveredDatabase(string Name, bool IsSystem, string Availability);
