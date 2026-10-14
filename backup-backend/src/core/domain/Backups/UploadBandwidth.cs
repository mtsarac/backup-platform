using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;

namespace BackupPlatform.Domain.Backups;

/// <summary>What a limit is attached to. Limits are independent ceilings; the most restrictive one that applies to an upload wins.</summary>
public enum UploadBandwidthScope
{
    /// <summary>The total of all the tenant's concurrent uploads, whichever agent makes them.</summary>
    Tenant = 1,

    /// <summary>The uploads made by one agent.</summary>
    Agent,

    /// <summary>One upload of a backup job (a run's transfer).</summary>
    Job
}

/// <summary>
/// Rules of an upload limit. A limit is whole bytes per second of upload transfer payload (decimal: 1 Mbit/s = 125 000 B/s); "unlimited" is the
/// absence of a limit, never a magic number. The minimum keeps a throttled transfer alive for every proxy and idle timeout on its path.
/// </summary>
public static class UploadBandwidth
{
    public const long MinimumBytesPerSecond = 16_000;
    public const long MaximumBytesPerSecond = 100_000_000_000;

    /// <summary>The smallest slice of a tenant limit that an upload is given; below it the upload waits for a slot instead of crawling.</summary>
    public const long MinimumShareBytesPerSecond = 1_000;

    public static bool IsValid(long bytesPerSecond) => bytesPerSecond is >= MinimumBytesPerSecond and <= MaximumBytesPerSecond;

    public static long Validate(long bytesPerSecond) => IsValid(bytesPerSecond)
        ? bytesPerSecond
        : throw new ArgumentOutOfRangeException(nameof(bytesPerSecond), "The upload limit is outside the supported range.");

    /// <summary>
    /// One upload's slice of the tenant limit. The slice is the equal share among the tenant's live uploads (this one included) but never more than
    /// what the others' <b>granted</b> slices leave free, so the sum of granted slices never exceeds the limit: a newcomer waits (0) until a running
    /// upload has taken its reduced slice. Unused slices of uploads capped lower by another limit are not redistributed.
    /// </summary>
    public static long Share(long tenantLimit, int otherUploads, long grantedToOthers)
    {
        var fair = tenantLimit / (otherUploads + 1);
        var share = Math.Min(fair, Math.Max(0, tenantLimit - grantedToOthers));
        return share < MinimumShareBytesPerSecond ? 0 : share;
    }
}

/// <summary>The limit of one scope; at most one row per (tenant, scope, scope id). The scope id of a tenant limit is the tenant itself.</summary>
public sealed class UploadBandwidthLimit : ITenantOwned
{
    private UploadBandwidthLimit() { TenantId = null!; }

    public UploadBandwidthLimit(TenantId tenantId, UploadBandwidthScope scope, Guid scopeId, long bytesPerSecond, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        if (!Enum.IsDefined(scope)) throw new ArgumentOutOfRangeException(nameof(scope));
        if (scopeId == Guid.Empty || scope == UploadBandwidthScope.Tenant && scopeId != tenantId.Value)
            throw new ArgumentException("The scope identity is invalid.", nameof(scopeId));
        TenantId = tenantId;
        Scope = scope;
        ScopeId = scopeId;
        BytesPerSecond = UploadBandwidth.Validate(bytesPerSecond);
        UpdatedAt = now;
    }

    public TenantId TenantId { get; private set; }
    public UploadBandwidthScope Scope { get; private set; }
    public Guid ScopeId { get; private set; }
    public long BytesPerSecond { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public void Change(long bytesPerSecond, DateTimeOffset now)
    {
        BytesPerSecond = UploadBandwidth.Validate(bytesPerSecond);
        UpdatedAt = now;
    }
}

/// <summary>
/// The slice of a tenant limit that one running attempt was last told to use. It is a lease: the agent renews it every few seconds and it simply
/// expires when the agent, its network, the backend or the attempt goes away, so a crash can never keep a slice locked.
/// </summary>
public sealed class UploadBandwidthGrant : ITenantOwned
{
    private UploadBandwidthGrant() { TenantId = null!; }

    public UploadBandwidthGrant(TenantId tenantId, Guid attemptId, long bytesPerSecond, DateTimeOffset now, DateTimeOffset expiresAt)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        if (attemptId == Guid.Empty || bytesPerSecond < 0 || expiresAt <= now) throw new ArgumentException("The grant is invalid.");
        TenantId = tenantId;
        AttemptId = attemptId;
        BytesPerSecond = bytesPerSecond;
        UpdatedAt = now;
        ExpiresAt = expiresAt;
    }

    public TenantId TenantId { get; private set; }
    public Guid AttemptId { get; private set; }

    /// <summary>0 means registered as uploading but no slice is free yet: the upload waits.</summary>
    public long BytesPerSecond { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }

    public void Renew(long bytesPerSecond, DateTimeOffset now, DateTimeOffset expiresAt)
    {
        if (bytesPerSecond < 0 || expiresAt <= now) throw new ArgumentException("The grant is invalid.");
        BytesPerSecond = bytesPerSecond;
        UpdatedAt = now;
        ExpiresAt = expiresAt;
    }
}
