namespace BackupPlatform.Contracts.Backups;

/// <summary>
/// Agent-executed replica lifecycle: physical deletion of SFTP and Google Drive replicas, and repair of an SFTP or Google Drive target (from external replicas)
/// or of any external target from a replica on the claiming agent's own local store, or of a missing copy on the claiming agent's own local volume from an external replica.
/// An agent without this capability is never handed such work.
/// The backend only authorizes access and records its outcome.
/// </summary>
public static class ReplicaLifecycleProtocol
{
    public const string Capability = "replica.agent-lifecycle";
    public const string RepairTaskType = "replica-repair";
}

/// <summary>One verified replica a repair may copy from. The agent reads it directly from the provider; the key is the replica's stored key, never a client choice.</summary>
public sealed record ReplicaRepairSourceContract
{
    public Guid ReplicaId { get; init; }
    public string StorageProvider { get; init; } = string.Empty;
    /// <summary>Empty for a provider-addressed (external) source; the agent's local volume ("local" for the default store) for a source on the agent's own disk.</summary>
    public string StorageTarget { get; init; } = string.Empty;
    /// <summary>Empty for a source on the agent's local store, which has no connection.</summary>
    public Guid StorageConnectionId { get; init; }
    public string StorageKey { get; init; } = string.Empty;
}

public sealed record ReplicaRepairTargetContract
{
    public string StorageProvider { get; init; } = string.Empty;
    public Guid StorageConnectionId { get; init; }
    /// <summary>The agent's local volume to write to ("local" for its default store); empty for an external target.</summary>
    public string StorageTarget { get; init; } = string.Empty;
}

/// <summary>A claimed repair: the immutable intent and the lease that fences every call the agent makes about it. Carries no credential and no content.</summary>
public sealed record ReplicaRepairTaskSnapshot
{
    public int Version { get; init; } = 1;
    public Guid OperationId { get; init; }
    public Guid TenantId { get; init; }
    public Guid AgentId { get; init; }
    public Guid ArtifactId { get; init; }
    public Guid LeaseToken { get; init; }
    public DateTimeOffset LeaseExpiresAt { get; init; }
    /// <summary>Length and SHA-256 (lower-case hex) of the stored artifact bytes every replica must equal.</summary>
    public long SizeBytes { get; init; }
    public string Checksum { get; init; } = string.Empty;
    public ReplicaRepairTargetContract Target { get; init; } = new();
    /// <summary>Candidates in preference order; the agent pins the first it can open.</summary>
    public IReadOnlyList<ReplicaRepairSourceContract> Sources { get; init; } = [];
}

public sealed record ReplicaRepairLeaseRequest
{
    public Guid LeaseToken { get; init; }
}

public sealed record ReplicaRepairLeaseResponse
{
    public DateTimeOffset LeaseExpiresAt { get; init; }
    public bool CancellationRequested { get; init; }
}

/// <summary>Pins the source replica against retention and records the durable target intent before any byte is written.</summary>
public sealed record ReplicaRepairPinRequest
{
    public Guid LeaseToken { get; init; }
    public Guid SourceReplicaId { get; init; }
    public string TargetKey { get; init; } = string.Empty;
}

public sealed record ReplicaRepairProgressRequest
{
    public Guid LeaseToken { get; init; }
    public long TransferredBytes { get; init; }
}

/// <summary>Asks whether the agent may delete a partial target: the lease must be live and no verified replica may exist on the destination.</summary>
public sealed record ReplicaRepairCleanupRequest
{
    public Guid LeaseToken { get; init; }
    public string TargetKey { get; init; } = string.Empty;
}

public sealed record ReplicaRepairCleanupResponse
{
    public bool Allowed { get; init; }
}

public static class ReplicaRepairOutcomes
{
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    /// <summary>The target key already held content this repair does not own; the backend quarantines it.</summary>
    public const string TargetConflict = "target_conflict";
}

/// <summary>
/// The agent's report. Success carries only what the backend cannot know itself (how the target was verified, the Drive object record);
/// the target key is the one pinned earlier. <see cref="OrphanKey"/> names a target object the agent could not prove removed.
/// </summary>
public sealed record ReplicaRepairCompletionRequest
{
    public Guid LeaseToken { get; init; }
    public string Outcome { get; init; } = string.Empty;
    public string? FailureCode { get; init; }
    public string? Reason { get; init; }
    public string? VerificationMethod { get; init; }
    public TaskStorageCatalogRecord? DriveRecord { get; init; }
    public string? OrphanKey { get; init; }
    public bool OrphanQuarantine { get; init; }
}
