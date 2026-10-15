using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Restores;

namespace BackupPlatform.Application.Restores;

/// <summary>The agent capability a target agent must have synced as Supported before a restore may be created for it.</summary>
public static class RestoreAgentCapability
{
    public const string Identifier = "mssql.restore.database";
    public const string PrecheckIdentifier = "mssql.restore.precheck";
    public const string DiscoveryIdentifier = "mssql.restore.discovery";

    public static string ForPurpose(RestorePurpose purpose, string engine = "mssql") => (engine, purpose) switch
    {
        ("mssql", RestorePurpose.Execution) => Identifier,
        ("mssql", RestorePurpose.Precheck) => PrecheckIdentifier,
        ("mssql", RestorePurpose.Discovery) => DiscoveryIdentifier,
        ("postgres", RestorePurpose.Execution) => "postgres.restore.full",
        ("postgres", RestorePurpose.Precheck) => "postgres.restore.precheck",
        ("postgres", RestorePurpose.Discovery) => "postgres.restore.discovery",
        _ => throw new ArgumentException("Unknown restore engine or purpose.")
    };
}

/// <summary>
/// A validated restore request. <see cref="ReplicaId"/> null means "choose the best eligible replica for the agent". Nothing here is checked against the
/// database yet: the store decides eligibility under its locks.
/// </summary>
public sealed record RestoreCommand(TenantId TenantId, Guid ArtifactId, Guid? ReplicaId, AgentId AgentId, RestoreTarget Target, string DatabaseName,
    RestoreMode Mode, bool OverwriteConfirmed, RestoreOptions Options, string IdempotencyKey, string RequestFingerprint, Guid? RequestedByUserId, DateTimeOffset Now,
    RestorePurpose Purpose = RestorePurpose.Execution, Guid? PrecheckId = null, Guid? SupportSessionId = null);

public enum RestoreCreateOutcome
{
    /// <summary>A new operation (with its first attempt and its replica pin) was created.</summary>
    Created,
    /// <summary>The Idempotency-Key was already used for this artifact with the same request: the original operation is returned.</summary>
    Replayed,
    /// <summary>No such artifact in the tenant.</summary>
    ArtifactNotFound,
    /// <summary>No such agent in the tenant.</summary>
    AgentNotFound,
    /// <summary>The agent exists but is disabled or revoked.</summary>
    AgentNotActive,
    /// <summary>The agent has not synced <see cref="RestoreAgentCapability.Identifier"/> as Supported.</summary>
    AgentCannotRestore,
    /// <summary>No replica may serve this restore; <see cref="RestoreCreateResult.Rejection"/> says why.</summary>
    SourceRejected,
    /// <summary>The same Idempotency-Key was used with a different request.</summary>
    IdempotencyConflict,
    PrecheckRejected,
    /// <summary>The command named a support session and the database's view of it (owner, still active, RestoreBackup granted) refused. Nothing was created.</summary>
    SupportAccessDenied
}

public sealed record RestoreCreateResult(RestoreCreateOutcome Outcome, RestoreOperation? Operation = null, RestoreSourceRejection? Rejection = null,
    Domain.Support.SupportAccessDenial? SupportDenial = null);

/// <summary>A claimed restore attempt with the replica it reads (the persisted location, never a caller-supplied one).</summary>
public sealed record RestoreClaim(RestoreOperation Operation, RestoreAttempt Attempt, BackupReplica Replica, string Engine = "mssql");

public sealed record RestoreLeaseCommand(TenantId TenantId, AgentId AgentId, Guid AttemptId, Guid LeaseToken, DateTimeOffset Now)
{
    public string? SoftwareVersion { get; init; }
    public string? ProtocolVersion { get; init; }
    public string? Platform { get; init; }
}

public sealed record RestoreProgress(string Stage, double? Percent);

/// <summary>The outcome of a start or a renewal: the lease the agent now holds and whether the user asked to cancel.</summary>
public sealed record RestoreLeaseOutcome(BackupMutationResult Result, DateTimeOffset? LeaseExpiresAt = null, bool CancellationRequested = false);

public sealed record RestoreQuery(RestoreStatus? Status = null, Guid? ArtifactId = null, Guid? AgentId = null, int Page = 1, int PageSize = 25, Guid? SupportSessionId = null);

public sealed record RestorePage(IReadOnlyList<RestoreOperation> Items, long TotalCount, int Page, int PageSize);

/// <summary>
/// All mutations are atomic, tenant-qualified and durably committed before returning; every lifecycle write holds the target agent's row lock,
/// which fences concurrent polls, callbacks, cancellations and the expiry sweeps of one agent. An operation of another tenant is never found,
/// whatever the caller passes. The replica pin lives and dies with the operation inside the same transaction: creating one locks the replica row,
/// ending one releases the pin in the same save, and retention's delete claims honour the pin (a database trigger is the backstop).
/// </summary>
public interface IRestoreStore
{
    /// <summary>
    /// One transaction: agent lock, idempotency lookup (replay or conflict), eligibility checks (agent Active and able to restore, replica chosen
    /// and re-validated under its row lock), then operation + first attempt + replica pin.
    /// </summary>
    Task<RestoreCreateResult> CreateAsync(RestoreCommand command, CancellationToken cancellationToken);

    Task<RestoreOperation?> GetAsync(TenantId tenantId, Guid restoreId, CancellationToken cancellationToken);

    /// <summary>Newest first. Attempts and the pin are loaded.</summary>
    Task<RestorePage> ListAsync(TenantId tenantId, RestoreQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Pending: withdrawn at once (Cancelled, target NotModified, pin released). Leased or Running: the request is recorded and delivered to the agent on its next
    /// lease renewal. Terminal: no change. Null when the operation does not exist in the tenant.
    /// </summary>
    /// <remarks>
    /// <paramref name="supportSessionId"/> set: the cancellation is requested through that support session; only a restore created through it is found (anything else is null)
    /// and the audit says so. Cancelling is a safety action, so it is authorized by the caller's check of the session and not re-judged here.
    /// </remarks>
    Task<RestoreOperation?> RequestCancellationAsync(TenantId tenantId, Guid restoreId, DateTimeOffset now, CancellationToken cancellationToken, Guid? requestedByUserId = null,
        Guid? supportSessionId = null);

    /// <summary>
    /// Claims the agent's oldest Pending restore. Expires the agent's stale leases and unclaimed restores first, and hands out nothing while the agent
    /// holds a live (unexpired Leased/Running) backup attempt or restore attempt: a restore never runs beside another job of the same agent.
    /// </summary>
    Task<RestoreClaim?> ClaimNextAsync(TenantId tenantId, AgentId agentId, DateTimeOffset now, TimeSpan leaseDuration, TimeSpan unclaimedExpiry, CancellationToken cancellationToken);

    /// <summary>Leased to Running, and the lease extended in the same transaction.</summary>
    Task<RestoreLeaseOutcome> StartAsync(RestoreLeaseCommand command, TimeSpan leaseDuration, CancellationToken cancellationToken);

    Task<RestoreLeaseOutcome> RenewLeaseAsync(RestoreLeaseCommand command, TimeSpan leaseDuration, CancellationToken cancellationToken);

    Task<BackupMutationResult> ReportProgressAsync(RestoreLeaseCommand command, RestoreProgress progress, CancellationToken cancellationToken);

    /// <summary>Terminal outcome. Idempotent: the same completion again is accepted without a change; a different one, or one under a lost lease, is a conflict.</summary>
    Task<BackupMutationResult> CompleteAsync(RestoreLeaseCommand command, RestoreCompletion completion, CancellationToken cancellationToken);

    /// <summary>Expired Leased/Running attempts end AgentLost (target Unknown or NotModified by the stage reached) and release their pin. Returns the operations expired.</summary>
    Task<int> ExpireStaleAsync(DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Pending restores older than <paramref name="maximumWait"/> end Cancelled (NotClaimed) and release their pin. Returns the operations withdrawn.</summary>
    Task<int> ExpireUnclaimedAsync(DateTimeOffset now, TimeSpan maximumWait, CancellationToken cancellationToken);
}
