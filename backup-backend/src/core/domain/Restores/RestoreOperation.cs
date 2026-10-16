using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;

namespace BackupPlatform.Domain.Restores;

/// <summary>
/// One request to restore a verified backup artifact into a SQL Server. It is NOT a backup run: it produces no artifact, has its own
/// lifecycle and its own agent work item, and it never retries on its own, because a restore changes the target and repeating one is a
/// decision. It pins the replica it reads from its creation until it ends, so retention cannot remove the bytes under it.
/// <code>
/// Pending ──claim──▶ Leased ──start──▶ Running ──┬─ online ───────▶ Succeeded
///   │                                            ├─ failure ──────▶ Failed        (target state reported)
///   │                                            ├─ cancelled ────▶ Cancelled     (target state reported)
///   │                                            └─ lease lost ───▶ AgentLost     (target state Unknown once it may have been altered)
///   ├─ cancel ─▶ Cancelled (NotModified)        Leased / Running ──cancel──▶ cooperative: the agent ends it
///   └─ unclaimed too long ─▶ Cancelled (NotClaimed)
/// </code>
/// A terminal outcome is immutable. Every terminal transition releases the replica pin in the same change.
/// </summary>
public sealed class RestoreOperation : ITenantOwned
{
    public const int MaxResultJsonLength = 65536;
    private static readonly HashSet<string> SystemDatabases = new(StringComparer.OrdinalIgnoreCase) { "master", "model", "msdb", "tempdb", "mssqlsystemresource" };

    private readonly List<RestoreAttempt> _attempts = [];
    private IReadOnlyList<RestoreAttempt>? _attemptView;

    private RestoreOperation()
    {
        TenantId = null!; AgentId = null!; Target = null!; DatabaseName = null!; Options = null!; IdempotencyKey = null!; RequestFingerprint = null!; Pin = null!; Cleanup = RestoreCleanup.None;
    }

    public RestoreOperation(Guid id, TenantId tenantId, Guid artifactId, Guid replicaId, RestoreSourceAccess sourceAccess, AgentId agentId,
        RestoreTarget target, string databaseName, RestoreMode mode, bool overwriteConfirmed, RestoreOptions? options, string idempotencyKey,
        string requestFingerprint, Guid? requestedByUserId, DateTimeOffset createdAt, RestorePurpose purpose = RestorePurpose.Execution, Guid? precheckId = null,
        Guid? supportSessionId = null, string engine = "mssql")
    {
        BackupGuard.Id(id);
        BackupGuard.Id(artifactId);
        BackupGuard.Id(replicaId);
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(agentId);
        ArgumentNullException.ThrowIfNull(target);
        BackupGuard.Text(idempotencyKey, 128);
        BackupGuard.Text(requestFingerprint, 128);
        if (!Enum.IsDefined(mode)) throw new ArgumentException("Unknown restore mode.");
        if (!Enum.IsDefined(sourceAccess)) throw new ArgumentException("Unknown source access.");
        if (!Enum.IsDefined(purpose)) throw new ArgumentException("Unknown restore purpose.");
        if (purpose == RestorePurpose.Verification)
        {
            // The backend names the disposable database before anything exists, so a lost agent can never leave an unknown leftover; only a new database may ever be created.
            ValidateDatabaseName(databaseName, engine, verification: true);
            if (mode != RestoreMode.NewDatabase || supportSessionId is not null) throw new ArgumentException("A verification only creates a new disposable database.");
        }
        else if (purpose != RestorePurpose.Discovery) ValidateDatabaseName(databaseName, engine);
        else if (databaseName != string.Empty) throw new ArgumentException("Discovery has no target database.");
        // Only an execution can acknowledge an overwrite; read-only preparation never does.
        if (purpose == RestorePurpose.Execution && mode == RestoreMode.ReplaceExistingDatabase && !overwriteConfirmed)
            throw new ArgumentException("Replacing an existing database requires explicit confirmation.");
        if (mode == RestoreMode.NewDatabase && overwriteConfirmed)
            throw new ArgumentException("Only a replace restore can confirm an overwrite.");
        if (purpose != RestorePurpose.Execution && overwriteConfirmed)
            throw new ArgumentException("Read-only preparation cannot acknowledge an overwrite.");
        // A support restore can only ever create a new database: the customer's existing databases are never replaced through a support session.
        if (supportSessionId is { } support)
        {
            BackupGuard.Id(support);
            if (mode != RestoreMode.NewDatabase) throw new ArgumentException("A support restore can only create a new database.");
            if (requestedByUserId is null) throw new ArgumentException("A support restore records the platform administrator who requested it.");
        }
        Purpose = purpose;
        Cleanup = purpose == RestorePurpose.Verification ? RestoreCleanup.ForVerification(databaseName) : RestoreCleanup.None;
        PrecheckId = precheckId;
        SupportSessionId = supportSessionId;
        Id = id;
        TenantId = tenantId;
        ArtifactId = artifactId;
        ReplicaId = replicaId;
        SourceAccess = sourceAccess;
        AgentId = agentId;
        Target = target.Validate() with { };
        DatabaseName = databaseName;
        Mode = mode;
        OverwritePolicy = mode == RestoreMode.ReplaceExistingDatabase ? RestoreOverwritePolicy.ReplaceExisting : RestoreOverwritePolicy.Forbidden;
        Options = options ?? RestoreOptions.Default;
        IdempotencyKey = idempotencyKey;
        RequestFingerprint = requestFingerprint;
        RequestedByUserId = requestedByUserId;
        CreatedAt = BackupGuard.Timestamp(createdAt);
        Status = RestoreStatus.Pending;
        Pin = new RestoreReplicaPin(Guid.NewGuid(), tenantId, id, replicaId, createdAt);
    }

    public Guid Id { get; private set; }
    public TenantId TenantId { get; private set; }
    public RestorePurpose Purpose { get; private set; }
    public Guid? PrecheckId { get; private set; }
    /// <summary>The support access session this restore was requested through; null for an ordinary tenant restore. Fixed at creation.</summary>
    public Guid? SupportSessionId { get; private set; }
    public Guid ArtifactId { get; private set; }
    /// <summary>The replica this restore reads; chosen when the request was accepted and pinned from then on.</summary>
    public Guid ReplicaId { get; private set; }
    public RestoreSourceAccess SourceAccess { get; private set; }
    /// <summary>The target agent: the one that executes the restore next to the target SQL Server.</summary>
    public AgentId AgentId { get; private set; }
    public RestoreTarget Target { get; private set; }
    /// <summary>The name of the database to create or replace on the target.</summary>
    public string DatabaseName { get; private set; }
    public RestoreMode Mode { get; private set; }
    public RestoreOverwritePolicy OverwritePolicy { get; private set; }
    public RestoreOptions Options { get; private set; }
    public string IdempotencyKey { get; private set; }
    /// <summary>Digest of the request body, so a replayed key with a different request is detected instead of silently answered with the first one.</summary>
    public string RequestFingerprint { get; private set; }
    public Guid? RequestedByUserId { get; private set; }
    public RestoreStatus Status { get; private set; }
    public int CurrentAttemptNumber { get; private set; }
    /// <summary>When the request was accepted.</summary>
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public DateTimeOffset? CancellationRequestedAt { get; private set; }
    public string? FailureCode { get; private set; }
    /// <summary>The condition the target database was left in; null until the operation ends.</summary>
    public RestoreTargetState? TargetState { get; private set; }
    /// <summary>The agent's typed result (pre-check items, file mappings, warnings, header summary) as JSON; opaque to the domain.</summary>
    public string? ResultJson { get; private set; }
    public RestoreReplicaPin Pin { get; private set; }
    /// <summary>The platform-created database this operation owes the target's cleanup for (a verification's disposable database, or a retained rollback copy).</summary>
    public RestoreCleanup Cleanup { get; private set; }
    public IReadOnlyList<RestoreAttempt> Attempts => _attemptView ??= _attempts.AsReadOnly();
    public RestoreAttempt CurrentAttempt => _attempts.Single(attempt => attempt.Number == CurrentAttemptNumber);
    public bool IsTerminal => Status is RestoreStatus.Succeeded or RestoreStatus.Failed or RestoreStatus.Cancelled or RestoreStatus.AgentLost;
    public bool IsActive => !IsTerminal;

    public static void ValidateDatabaseName(string? name, string engine = "mssql", bool verification = false)
    {
        ValidateDatabaseNameSyntax(name);
        // The platform's own prefixes are reserved: a user restore can never name a database the cleanup would later treat as disposable or as a rollback copy.
        if (verification ? !PlatformDatabaseNames.IsVerification(name) : PlatformDatabaseNames.IsReserved(name))
            throw new ArgumentException("This database name is reserved for the platform.");
        if (engine == "mssql" && SystemDatabases.Contains(name!)) throw new ArgumentException("A system database cannot be a restore target.");
        if (engine == "postgres" && (System.Text.Encoding.UTF8.GetByteCount(name!) > 63 ||
            name!.Equals("postgres", StringComparison.OrdinalIgnoreCase) || name.Equals("template0", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("template1", StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Invalid PostgreSQL target database name.");
        if (engine is not ("mssql" or "postgres")) throw new ArgumentException("Unknown database engine.");
    }

    public static void ValidateDatabaseNameSyntax(string? name)
    {
        if (name is null || name.Length is < 1 or > 128 || name.Any(char.IsControl) || name != name.Trim())
            throw new ArgumentException("Invalid target database name.");
    }

    public RestoreAttempt QueueFirstAttempt(Guid attemptId, DateTimeOffset now)
    {
        if (CurrentAttemptNumber != 0) throw new InvalidOperationException("The restore already has its first attempt.");
        var attempt = new RestoreAttempt(attemptId, TenantId, Id, AgentId, 1, now);
        _attempts.Add(attempt);
        CurrentAttemptNumber = 1;
        return attempt;
    }

    public bool SwitchReplica(BackupReplica newReplica, RestoreSourceAccess access)
    {
        if (IsTerminal || Status != RestoreStatus.Pending || !Pin.IsActive || CancellationRequestedAt is not null) return false;
        if (newReplica.TenantId != TenantId || newReplica.ArtifactId != ArtifactId) return false;
        ReplicaId = newReplica.Id;
        SourceAccess = access;
        Pin.SwitchReplica(newReplica.Id);
        return true;
    }

    public void Claim(Guid token, DateTimeOffset now, TimeSpan duration)
    {
        if (Status != RestoreStatus.Pending) throw new InvalidOperationException("Only a pending restore can be claimed.");
        CurrentAttempt.Claim(token, now, duration);
        Status = RestoreStatus.Leased;
    }

    public void Start(Guid token, DateTimeOffset now, string software, string protocol, string platform)
    {
        CurrentAttempt.Start(token, now, software, protocol, platform);
        Status = RestoreStatus.Running;
        StartedAt ??= now;
    }

    public void Renew(Guid token, DateTimeOffset now, TimeSpan duration) => CurrentAttempt.Renew(token, now, duration);

    public void Progress(Guid token, DateTimeOffset now, string stage, double? percent) => CurrentAttempt.Progress(token, now, stage, percent);

    public void Complete(Guid token, DateTimeOffset now, RestoreCompletion completion)
    {
        ArgumentNullException.ThrowIfNull(completion);
        var attempt = CurrentAttempt;
        if (attempt.IsTerminal && !string.Equals(ResultJson, completion.ResultJson, StringComparison.Ordinal))
            throw new InvalidOperationException("Terminal outcome is immutable.");
        if (!attempt.Complete(token, now, completion)) return;
        if (Purpose != RestorePurpose.Execution && completion.TargetState != RestoreTargetState.NotModified)
            throw new InvalidOperationException("Read-only preparation cannot change the target.");
        if (Purpose == RestorePurpose.Execution && completion.Status == RestoreStatus.Succeeded && completion.TargetState != RestoreTargetState.Online)
            throw new InvalidOperationException("An execution must leave the restored database online.");
        if (completion.RetainedDatabaseName is not null && (Purpose != RestorePurpose.Execution || Mode != RestoreMode.ReplaceExistingDatabase || completion.Status != RestoreStatus.Succeeded))
            throw new InvalidOperationException("Only a successful replace restore retains a rollback database.");
        EndTerminal(attempt.Status, attempt.FailureCode, attempt.TargetState!.Value, completion.ResultJson, now, completion.TemporaryDatabaseRemoved, completion.RetainedDatabaseName);
    }

    /// <summary>
    /// Pending: withdrawn at once, the target was never touched. Leased or Running: only recorded; the agent sees it on lease renewal, stops
    /// the SQL statement and reports the real condition of the target. Terminal: no change.
    /// </summary>
    public void RequestCancellation(DateTimeOffset now)
    {
        if (IsTerminal || CancellationRequestedAt.HasValue) return;
        CancellationRequestedAt = BackupGuard.Timestamp(now);
        if (Status != RestoreStatus.Pending) return;
        CurrentAttempt.CancelPending(now, RestoreFailureCodes.Cancelled);
        EndTerminal(RestoreStatus.Cancelled, RestoreFailureCodes.Cancelled, RestoreTargetState.NotModified, null, now);
    }

    /// <summary>An expired lease ends the attempt AgentLost. False (and no change) when nothing expired.</summary>
    public bool Expire(DateTimeOffset now)
    {
        var attempt = CurrentAttempt;
        if (!attempt.Expire(now, Purpose)) return false;
        EndTerminal(RestoreStatus.AgentLost, RestoreFailureCodes.AgentLost, attempt.TargetState!.Value, null, now, temporaryDatabaseRemoved: false);
        return true;
    }

    /// <summary>Withdraws a restore no agent claimed within <paramref name="maximumWait"/>, so a pin cannot outlive a request nobody is executing.</summary>
    public bool ExpireUnclaimed(DateTimeOffset now, TimeSpan maximumWait)
    {
        if (Status != RestoreStatus.Pending || CreatedAt + maximumWait > now) return false;
        CurrentAttempt.CancelPending(now, RestoreFailureCodes.NotClaimed);
        EndTerminal(RestoreStatus.Cancelled, RestoreFailureCodes.NotClaimed, RestoreTargetState.NotModified, null, now);
        return true;
    }

    private void EndTerminal(RestoreStatus status, string? failureCode, RestoreTargetState targetState, string? resultJson, DateTimeOffset now,
        bool temporaryDatabaseRemoved = true, string? retainedDatabaseName = null)
    {
        Status = status;
        FailureCode = failureCode;
        TargetState = targetState;
        ResultJson = resultJson;
        CompletedAt = BackupGuard.Timestamp(now);
        Pin.Release(now);
        Cleanup = retainedDatabaseName is not null ? RestoreCleanup.Retain(retainedDatabaseName, now) : Cleanup.Ended(temporaryDatabaseRemoved, now);
    }

    /// <summary>An operator asks for the removal of the retained rollback copy, or for a retry now of a cleanup that is backing off or parked. False when there is nothing to request.</summary>
    public bool RequestCleanup(DateTimeOffset now, Guid? userId)
    {
        if (!IsTerminal || !Cleanup.TryRequest(now, userId, out var next)) return false;
        Cleanup = next;
        return true;
    }

    /// <summary>Leases the owed removal to the agent. The lease token fences the report; an earlier lease that expired can no longer report.</summary>
    public bool TryLeaseCleanup(Guid token, DateTimeOffset now, TimeSpan duration)
    {
        if (!IsTerminal || !Cleanup.CanLease(now)) return false;
        Cleanup = Cleanup.Leased(token, now, duration);
        return true;
    }

    /// <exception cref="InvalidOperationException">The lease is not the current one.</exception>
    public void ReportCleanup(Guid token, bool removed, string? failureCode, DateTimeOffset now) => Cleanup = Cleanup.Reported(token, removed, failureCode, now);
}
