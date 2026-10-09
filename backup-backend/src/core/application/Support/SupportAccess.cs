using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Downloads;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Support;

namespace BackupPlatform.Application.Support;

/// <summary>
/// The authorization context of ONE support operation: who (the real PlatformAdmin), which tenant, which session and what the session grants.
/// It cannot be constructed outside this assembly: the only producer is <see cref="SupportAccessService.AuthorizeAsync"/>, which builds it from a
/// session the store has just verified (owner, not ended, not past expiry, permission granted) under the session's row lock. A caller that holds
/// one has therefore been authorized for exactly that tenant, and every tenant-scoped read or write of a support operation takes its tenant from here
/// and never from the request.
/// </summary>
public sealed class SupportAccessContext
{
    internal SupportAccessContext(SupportAccessSession session)
    {
        ActorId = session.PlatformAdminId;
        TenantId = session.TenantId;
        SessionId = session.Id;
        GrantedPermissions = session.Permissions;
    }

    public UserId ActorId { get; }
    public TenantId TenantId { get; }
    public Guid SessionId { get; }
    public SupportPermissions GrantedPermissions { get; }

    public bool Has(SupportPermissions permission) => (GrantedPermissions & permission) == permission;
}

/// <summary>Whether a tenant must approve support access before it starts. Today it never does; the seam exists so the workflow can be added without rewiring.</summary>
public interface ISupportApprovalPolicy
{
    bool RequiresTenantApproval(TenantId tenantId);
}

/// <summary>Configuration section <c>Support</c>. <see cref="TenantApprovalRequired"/> is false by product decision, and is a setting, not a constant.</summary>
public sealed class SupportAccessOptions
{
    public bool TenantApprovalRequired { get; init; }
}

public sealed class ConfiguredSupportApprovalPolicy(SupportAccessOptions options) : ISupportApprovalPolicy
{
    public bool RequiresTenantApproval(TenantId tenantId) => options.TenantApprovalRequired;
}

/// <summary>A request field is invalid. Carries the field and a stable code, never the offending value.</summary>
public sealed class InvalidSupportRequestException(string field, string code) : Exception($"Invalid support request field '{field}' ({code}).")
{
    public string Field { get; } = field;
    public string Code { get; } = code;
}

public enum SupportOpenOutcome { Opened, Replayed, TenantNotFound, IdempotencyConflict, ActiveSessionExists, ApprovalRequired }

public sealed record SupportOpenCommand(Guid SessionId, TenantId TenantId, UserId Admin, string Reason, SupportPermissions Permissions, TimeSpan Duration,
    string IdempotencyKey, string Fingerprint, DateTimeOffset Now);

public sealed record SupportOpenResult(SupportOpenOutcome Outcome, SupportAccessSession? Session = null, Guid? ExistingSessionId = null);

public enum SupportEndOutcome { Ended, AlreadyEnded, NotFound, NotOwner }

public sealed record SupportEndResult(SupportEndOutcome Outcome, SupportAccessSession? Session = null);

/// <summary>What a support operation is, for the audit record written together with its authorization.</summary>
public sealed record SupportAuditSpec(string Action, string TargetType, string TargetId, IReadOnlyDictionary<string, object?>? Details = null);

public sealed record SupportAuthorizationCommand(Guid SessionId, UserId Actor, SupportPermissions Required, DateTimeOffset Now, SupportAuditSpec? Audit);

/// <summary><see cref="Session"/> is set when allowed; <see cref="Denial"/> when refused; both null when no such session exists.</summary>
public sealed record SupportAuthorization(SupportAccessSession? Session, SupportAccessDenial? Denial, Guid? DeniedTenantId = null)
{
    public bool NotFound => Session is null && Denial is null;
}

public sealed record SupportAuthorizationResult(SupportAccessContext? Context, SupportAccessDenial? Denial)
{
    public bool NotFound => Context is null && Denial is null;
}

/// <summary>
/// Lifecycle and authorization writes of support sessions. Every write is atomic with its audit; the session row is the lock that makes close, revoke
/// and data access agree on one order: access takes it shared, ending takes it exclusive.
/// </summary>
public interface ISupportAccessStore
{
    /// <summary>One transaction: idempotency lookup, expiry of this admin's lapsed session for the tenant, one-active-session rule, insert and its opened audit.</summary>
    Task<SupportOpenResult> OpenAsync(SupportOpenCommand command, CancellationToken cancellationToken);

    /// <summary>
    /// Locks the session shared, judges it at <c>Now</c> (owner, not ended, not expired, permission), and for an allowed operation inserts <c>Audit</c> in the
    /// same transaction: an audit that cannot be persisted throws, so the access never happens. A refusal is returned, not thrown, and writes nothing.
    /// </summary>
    Task<SupportAuthorization> AuthorizeAsync(SupportAuthorizationCommand command, CancellationToken cancellationToken);

    /// <summary>Owner closes. In-flight downloads of the session are cancelled and its not yet started restores withdrawn in the same transaction.</summary>
    Task<SupportEndResult> CloseAsync(Guid sessionId, UserId by, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Any administrator revokes. Like close, and additionally asks running restores of the session to cancel (M5's cooperative cancellation).</summary>
    Task<SupportEndResult> RevokeAsync(Guid sessionId, UserId by, string reason, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Records the expiry (and its audit) of every Active session past its window. Running work is deliberately left alone. Returns the sessions expired.</summary>
    Task<int> ExpireDueAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken);

    /// <summary>Appends one audit event (a refusal, for instance). Throws when it cannot be persisted.</summary>
    Task AppendAuditAsync(AuditEvent auditEvent, CancellationToken cancellationToken);
}

public sealed record SupportSessionFilter(TenantId? TenantId, SupportSessionStatus? Status, UserId? Admin, int Page, int PageSize);

public sealed record SupportSessionRow(SupportAccessSession Session, string? TenantName, string? AdminName, string? ClosedByName);

public sealed record SupportAuditRow(Guid Id, DateTimeOffset At, string Action, Guid ActorUserId, string? ActorName, string TargetType, string TargetId, string DetailsJson);

public sealed record SupportActivitySummary(int ArtifactsViewed, int DownloadsStarted, int DownloadsCompleted, int DownloadsFailed, int RestoresRequested, int RestoresCancelled);

public sealed record SupportTenantSessionRow(SupportSessionRow Row, SupportActivitySummary Summary);

public sealed record SupportPage<T>(IReadOnlyList<T> Items, long TotalCount, int Page, int PageSize);

/// <summary>Reads of support sessions and their history. Nothing here authorizes; callers do. Tenant-qualified reads never reveal another tenant's session.</summary>
public interface ISupportQueryStore
{
    Task<SupportSessionRow?> GetAsync(Guid sessionId, CancellationToken cancellationToken);
    Task<SupportPage<SupportSessionRow>> ListAsync(SupportSessionFilter filter, DateTimeOffset now, CancellationToken cancellationToken);
    Task<SupportPage<SupportAuditRow>> ListAuditAsync(Guid sessionId, int page, int pageSize, CancellationToken cancellationToken);
    Task<SupportPage<DownloadSession>> ListDownloadsAsync(TenantId tenantId, Guid sessionId, int page, int pageSize, CancellationToken cancellationToken);

    Task<SupportPage<SupportTenantSessionRow>> ListTenantSessionsAsync(TenantId tenantId, int page, int pageSize, CancellationToken cancellationToken);
    Task<SupportTenantSessionRow?> GetTenantSessionAsync(TenantId tenantId, Guid sessionId, CancellationToken cancellationToken);
    Task<SupportPage<SupportAuditRow>?> ListTenantActivityAsync(TenantId tenantId, Guid sessionId, int page, int pageSize, CancellationToken cancellationToken);
}
