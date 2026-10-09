using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;

namespace BackupPlatform.Domain.Support;

/// <summary>
/// What a support session may do with the customer's backups. Deliberately small: nothing here deletes a backup, reveals an
/// agent credential or reads a storage secret, and no permission is ever implied by being a PlatformAdmin.
/// </summary>
[Flags]
public enum SupportPermissions
{
    None = 0,
    /// <summary>Backup jobs, runs, artifacts, replicas, verification and retention state, checksums.</summary>
    ViewBackupMetadata = 1,
    /// <summary>Real artifact download through the M4 secure download engine.</summary>
    DownloadBackup = 2,
    /// <summary>Restore precheck / start / cancel through the M5 restore engine (NewDatabase only).</summary>
    RestoreBackup = 4
}

public enum SupportSessionStatus
{
    Active = 1,
    Expired = 2,
    Closed = 3,
    Revoked = 4
}

/// <summary>Why a support operation was refused. Stable machine codes via <see cref="SupportAccessDenials.Code"/>.</summary>
public enum SupportAccessDenial
{
    NotSessionOwner = 1,
    SessionExpired = 2,
    SessionClosed = 3,
    SessionRevoked = 4,
    PermissionMissing = 5,
    /// <summary>A destructive Replace restore was requested; support restores are NewDatabase only.</summary>
    ReplaceForbidden = 6
}

public static class SupportAccessDenials
{
    public static string Code(SupportAccessDenial denial) => denial switch
    {
        SupportAccessDenial.NotSessionOwner => "not_session_owner",
        SupportAccessDenial.SessionExpired => "session_expired",
        SupportAccessDenial.SessionClosed => "session_closed",
        SupportAccessDenial.SessionRevoked => "session_revoked",
        SupportAccessDenial.PermissionMissing => "permission_missing",
        SupportAccessDenial.ReplaceForbidden => "replace_forbidden",
        _ => throw new ArgumentOutOfRangeException(nameof(denial))
    };
}

public static class SupportPermissionNames
{
    private static readonly (SupportPermissions Flag, string Name)[] Known =
    [
        (SupportPermissions.ViewBackupMetadata, nameof(SupportPermissions.ViewBackupMetadata)),
        (SupportPermissions.DownloadBackup, nameof(SupportPermissions.DownloadBackup)),
        (SupportPermissions.RestoreBackup, nameof(SupportPermissions.RestoreBackup))
    ];

    public static IReadOnlyList<string> ToNames(SupportPermissions permissions) =>
        [.. Known.Where(entry => permissions.HasFlag(entry.Flag)).Select(entry => entry.Name)];

    public static string Name(SupportPermissions single) => Known.Single(entry => entry.Flag == single).Name;

    /// <summary>Null when a name is unknown. Exact (case-sensitive) names only; duplicates collapse.</summary>
    public static SupportPermissions? TryParse(IEnumerable<string> names)
    {
        var result = SupportPermissions.None;
        foreach (var name in names)
        {
            var match = Known.FirstOrDefault(entry => string.Equals(entry.Name, name, StringComparison.Ordinal));
            if (match.Name is null) return null;
            result |= match.Flag;
        }
        return result;
    }
}

/// <summary>
/// A time-boxed, reasoned, permission-scoped window through which ONE PlatformAdmin may reach ONE tenant's backups. Tenant, admin,
/// permissions, reason and the time window are fixed at creation and never change; the only mutation is the one-way end of an Active
/// session (Expired by the clock, Closed by its owner, Revoked by an administrator). An ended session is never reactivated and its
/// lifetime is never extended: more time means a new session with a new reason. Validity is always judged against a server clock instant
/// passed in by the caller (<see cref="Evaluate"/>), never against the stored status alone, so a session that ran out stops working at its
/// expiry instant whether or not anything has yet recorded the expiry.
/// </summary>
public sealed class SupportAccessSession : ITenantOwned
{
    public const int MaximumReasonLength = 500;
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromHours(1);
    public static readonly TimeSpan MaximumDuration = TimeSpan.FromHours(4);

    private SupportAccessSession()
    {
        TenantId = null!; PlatformAdminId = null!; Reason = null!; IdempotencyKey = null!; RequestFingerprint = null!;
    }

    private SupportAccessSession(Guid id, TenantId tenantId, UserId platformAdminId, string reason, SupportPermissions permissions,
        DateTimeOffset now, TimeSpan duration, string idempotencyKey, string requestFingerprint)
    {
        Id = id;
        TenantId = tenantId;
        PlatformAdminId = platformAdminId;
        Reason = reason;
        Permissions = permissions;
        CreatedAt = BackupGuard.Timestamp(now);
        StartedAt = CreatedAt;
        ExpiresAt = StartedAt + duration;
        Status = SupportSessionStatus.Active;
        IdempotencyKey = idempotencyKey;
        RequestFingerprint = requestFingerprint;
    }

    public Guid Id { get; private set; }
    public TenantId TenantId { get; private set; }
    public UserId PlatformAdminId { get; private set; }
    public string Reason { get; private set; }
    public SupportPermissions Permissions { get; private set; }
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public SupportSessionStatus Status { get; private set; }
    public DateTimeOffset? ClosedAt { get; private set; }
    /// <summary>Who ended it: the owner for Closed, the revoking administrator for Revoked, null for Expired.</summary>
    public UserId? ClosedBy { get; private set; }
    /// <summary>The stated reason of a revocation; null otherwise.</summary>
    public string? CloseReason { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public string IdempotencyKey { get; private set; }
    /// <summary>Digest of the request, so a replayed key with a different request is a conflict instead of a silent replay.</summary>
    public string RequestFingerprint { get; private set; }

    public static SupportAccessSession Open(Guid id, TenantId tenantId, UserId platformAdminId, string? reason, SupportPermissions permissions,
        DateTimeOffset now, TimeSpan duration, string idempotencyKey, string requestFingerprint)
    {
        BackupGuard.Id(id);
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(platformAdminId);
        BackupGuard.Text(idempotencyKey, 128);
        BackupGuard.Text(requestFingerprint, 128);
        var trimmed = ValidateReason(reason);
        ValidatePermissions(permissions);
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration), "A support session needs a positive duration.");
        if (duration > MaximumDuration) throw new ArgumentOutOfRangeException(nameof(duration), "A support session cannot exceed four hours.");
        return new SupportAccessSession(id, tenantId, platformAdminId, trimmed, permissions, now, duration, idempotencyKey, requestFingerprint);
    }

    /// <summary>The trimmed reason; throws <see cref="ArgumentException"/> when it is blank, too long or contains control characters.</summary>
    public static string ValidateReason(string? reason)
    {
        var trimmed = reason?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > MaximumReasonLength ||
            trimmed.Any(character => char.IsControl(character) && character is not ('\n' or '\r' or '\t')))
            throw new ArgumentException("A support session needs a reason of at most 500 characters.", nameof(reason));
        return trimmed;
    }

    public static void ValidatePermissions(SupportPermissions permissions)
    {
        const SupportPermissions all = SupportPermissions.ViewBackupMetadata | SupportPermissions.DownloadBackup | SupportPermissions.RestoreBackup;
        if (permissions == SupportPermissions.None || (permissions & ~all) != 0)
            throw new ArgumentException("A support session needs at least one known permission.", nameof(permissions));
        if (permissions != SupportPermissions.ViewBackupMetadata && !permissions.HasFlag(SupportPermissions.ViewBackupMetadata))
            throw new ArgumentException("Downloading or restoring requires ViewBackupMetadata.", nameof(permissions));
    }

    /// <summary>Active in the database and not past its expiry at <paramref name="now"/>.</summary>
    public bool IsUsable(DateTimeOffset now) => Status == SupportSessionStatus.Active && now < ExpiresAt;

    /// <summary>The status as the clock sees it: an Active row past its expiry is Expired even before anything records that.</summary>
    public SupportSessionStatus EffectiveStatus(DateTimeOffset now) =>
        Status == SupportSessionStatus.Active && now >= ExpiresAt ? SupportSessionStatus.Expired : Status;

    /// <summary>
    /// The single authorization decision for a support operation: the caller must be the owning admin, the session must still be usable
    /// at <paramref name="now"/>, and <paramref name="required"/> must have been granted. Null means allowed.
    /// </summary>
    public SupportAccessDenial? Evaluate(UserId actor, SupportPermissions required, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (actor != PlatformAdminId) return SupportAccessDenial.NotSessionOwner;
        switch (EffectiveStatus(now))
        {
            case SupportSessionStatus.Expired: return SupportAccessDenial.SessionExpired;
            case SupportSessionStatus.Closed: return SupportAccessDenial.SessionClosed;
            case SupportSessionStatus.Revoked: return SupportAccessDenial.SessionRevoked;
        }
        return required != SupportPermissions.None && (Permissions & required) != required ? SupportAccessDenial.PermissionMissing : null;
    }

    /// <summary>Records the expiry of an Active session whose time ran out. False (no change) otherwise. The session ends at its expiry instant.</summary>
    public bool Expire(DateTimeOffset now)
    {
        if (Status != SupportSessionStatus.Active || now < ExpiresAt) return false;
        Status = SupportSessionStatus.Expired;
        ClosedAt = ExpiresAt;
        return true;
    }

    /// <summary>The owner ends an Active, unexpired session early.</summary>
    public void Close(UserId by, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(by);
        if (by != PlatformAdminId) throw new InvalidOperationException("Only the owning administrator can close a support session.");
        End(SupportSessionStatus.Closed, by, now, null);
    }

    /// <summary>An administrator withdraws an Active, unexpired session, stating why.</summary>
    public void Revoke(UserId by, string reason, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(by);
        End(SupportSessionStatus.Revoked, by, now, ValidateReason(reason));
    }

    private void End(SupportSessionStatus status, UserId by, DateTimeOffset now, string? closeReason)
    {
        if (!IsUsable(now)) throw new InvalidOperationException("Only an active support session can be ended; an ended session is never reactivated.");
        Status = status;
        ClosedAt = BackupGuard.Timestamp(now);
        ClosedBy = by;
        CloseReason = closeReason;
    }
}
