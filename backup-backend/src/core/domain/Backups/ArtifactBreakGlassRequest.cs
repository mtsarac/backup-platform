using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;

namespace BackupPlatform.Domain.Backups;

public enum BreakGlassStatus
{
    Pending = 1,
    Approved = 2,
    Denied = 3,
    /// <summary>The single use was spent: a decrypted download was prepared from it.</summary>
    Consumed = 4,
    Expired = 5
}

/// <summary>Why a break-glass step was refused. Stable machine codes via <see cref="BreakGlassDenials.Code"/>.</summary>
public enum BreakGlassDenial
{
    SelfApproval = 1,
    NotPending = 2,
    Expired = 3,
    NotRequester = 4,
    NotApproved = 5,
    AlreadyConsumed = 6
}

public static class BreakGlassDenials
{
    public static string Code(BreakGlassDenial denial) => denial switch
    {
        BreakGlassDenial.SelfApproval => "self_approval_forbidden",
        BreakGlassDenial.NotPending => "request_not_pending",
        BreakGlassDenial.Expired => "request_expired",
        BreakGlassDenial.NotRequester => "not_requester",
        BreakGlassDenial.NotApproved => "request_not_approved",
        BreakGlassDenial.AlreadyConsumed => "request_consumed",
        _ => throw new ArgumentOutOfRangeException(nameof(denial))
    };
}

/// <summary>
/// A PlatformAdmin's request to read ONE tenant's ONE encrypted artifact in plaintext through the backend (tenant wrap key). Dual control: a DIFFERENT
/// PlatformAdmin must approve; the grant is time-boxed (<see cref="ExpiresAt"/> covers approval AND use) and single use. Tenant, artifact, requester, reason and
/// window are fixed at creation; the status only moves forward. Like a support session, validity is judged against a server-clock instant passed in by
/// the caller, never against the stored status alone, so an expired request stops working at its expiry instant whether or not anything recorded that yet.
/// </summary>
public sealed class ArtifactBreakGlassRequest : ITenantOwned
{
    public const int MaximumReasonLength = 500;
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(1);
    public static readonly TimeSpan MaximumTtl = TimeSpan.FromHours(4);

    private ArtifactBreakGlassRequest() { TenantId = null!; RequestedBy = null!; Reason = null!; }

    private ArtifactBreakGlassRequest(Guid id, TenantId tenantId, Guid artifactId, UserId requestedBy, string reason, DateTimeOffset now, TimeSpan ttl)
    {
        Id = id;
        TenantId = tenantId;
        ArtifactId = artifactId;
        RequestedBy = requestedBy;
        Reason = reason;
        Status = BreakGlassStatus.Pending;
        CreatedAt = BackupGuard.Timestamp(now);
        ExpiresAt = CreatedAt + ttl;
    }

    public Guid Id { get; private set; }
    public TenantId TenantId { get; private set; }
    public Guid ArtifactId { get; private set; }
    public UserId RequestedBy { get; private set; }
    public string Reason { get; private set; }
    public BreakGlassStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    /// <summary>The approver, or the administrator who denied. Never the requester.</summary>
    public UserId? DecidedBy { get; private set; }
    public DateTimeOffset? DecidedAt { get; private set; }
    /// <summary>The denial reason; null for an approval.</summary>
    public string? DecisionReason { get; private set; }
    public DateTimeOffset? ConsumedAt { get; private set; }
    public Guid? DownloadSessionId { get; private set; }

    public static ArtifactBreakGlassRequest Open(Guid id, TenantId tenantId, Guid artifactId, UserId requestedBy, string? reason, DateTimeOffset now, TimeSpan ttl)
    {
        BackupGuard.Id(id);
        BackupGuard.Id(artifactId);
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(requestedBy);
        if (ttl <= TimeSpan.Zero || ttl > MaximumTtl) throw new ArgumentOutOfRangeException(nameof(ttl), "A break-glass request lives between one minute and four hours.");
        return new ArtifactBreakGlassRequest(id, tenantId, artifactId, requestedBy, ValidateReason(reason), now, ttl);
    }

    /// <summary>The trimmed reason; throws <see cref="ArgumentException"/> when it is blank, too long or contains control characters.</summary>
    public static string ValidateReason(string? reason)
    {
        var trimmed = reason?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > MaximumReasonLength ||
            trimmed.Any(character => char.IsControl(character) && character is not ('\n' or '\r' or '\t')))
            throw new ArgumentException("A break-glass request needs a reason of at most 500 characters.", nameof(reason));
        return trimmed;
    }

    /// <summary>The status as the clock sees it: a Pending or Approved row past its expiry is Expired even before anything records that.</summary>
    public BreakGlassStatus EffectiveStatus(DateTimeOffset now) =>
        Status is BreakGlassStatus.Pending or BreakGlassStatus.Approved && now >= ExpiresAt ? BreakGlassStatus.Expired : Status;

    /// <summary>Approves. The requester can never approve (dual control); null means approved.</summary>
    public BreakGlassDenial? Approve(UserId by, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(by);
        if (by == RequestedBy) return BreakGlassDenial.SelfApproval;
        var denial = EvaluateDecision(now);
        if (denial is not null) return denial;
        Status = BreakGlassStatus.Approved;
        DecidedBy = by;
        DecidedAt = BackupGuard.Timestamp(now);
        return null;
    }

    /// <summary>Denies a Pending request, stating why. The requester cannot deny their own request (they simply do not use it).</summary>
    public BreakGlassDenial? Deny(UserId by, string? reason, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(by);
        var trimmed = ValidateReason(reason);
        if (by == RequestedBy) return BreakGlassDenial.SelfApproval;
        var denial = EvaluateDecision(now);
        if (denial is not null) return denial;
        Status = BreakGlassStatus.Denied;
        DecidedBy = by;
        DecidedAt = BackupGuard.Timestamp(now);
        DecisionReason = trimmed;
        return null;
    }

    /// <summary>Spends the single use for the requester's download. Null means consumed.</summary>
    public BreakGlassDenial? Consume(UserId by, Guid downloadSessionId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(by);
        BackupGuard.Id(downloadSessionId);
        if (by != RequestedBy) return BreakGlassDenial.NotRequester;
        switch (EffectiveStatus(now))
        {
            case BreakGlassStatus.Consumed: return BreakGlassDenial.AlreadyConsumed;
            case BreakGlassStatus.Expired: return BreakGlassDenial.Expired;
            case not BreakGlassStatus.Approved: return BreakGlassDenial.NotApproved;
        }
        Status = BreakGlassStatus.Consumed;
        ConsumedAt = BackupGuard.Timestamp(now);
        DownloadSessionId = downloadSessionId;
        return null;
    }

    /// <summary>Records the expiry of a Pending or Approved request whose window ran out. False (no change) otherwise.</summary>
    public bool Expire(DateTimeOffset now)
    {
        if (Status is not (BreakGlassStatus.Pending or BreakGlassStatus.Approved) || now < ExpiresAt) return false;
        Status = BreakGlassStatus.Expired;
        return true;
    }

    private BreakGlassDenial? EvaluateDecision(DateTimeOffset now) => EffectiveStatus(now) switch
    {
        BreakGlassStatus.Pending => null,
        BreakGlassStatus.Expired => BreakGlassDenial.Expired,
        _ => BreakGlassDenial.NotPending
    };
}
