using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;

namespace BackupPlatform.Domain.Backups;

public enum BackupShareStatus
{
    Active = 1,
    Expired = 2,
    Revoked = 3
}

/// <summary>
/// A time-limited grant created by a UserAdmin of the owning tenant that allows PlatformAdmins
/// to download exactly one backup artifact.
/// </summary>
public sealed class BackupShare : ITenantOwned
{
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromHours(24);
    public static readonly TimeSpan MaximumDuration = TimeSpan.FromHours(72);

    private BackupShare()
    {
        TenantId = null!;
        CreatedByUserId = null!;
    }

    public BackupShare(
        Guid id,
        TenantId tenantId,
        Guid artifactId,
        UserId createdByUserId,
        UserRole createdByRole,
        DateTimeOffset createdAtUtc,
        DateTimeOffset expiresAtUtc,
        string? reason = null,
        DateTimeOffset? revokedAtUtc = null,
        UserId? revokedByUserId = null)
    {
        BackupGuard.Id(id);
        ArgumentNullException.ThrowIfNull(tenantId);
        BackupGuard.Id(artifactId);
        ArgumentNullException.ThrowIfNull(createdByUserId);
        if (!Enum.IsDefined(createdByRole))
        {
            throw new ArgumentOutOfRangeException(nameof(createdByRole));
        }

        var createdUtc = createdAtUtc.ToUniversalTime();
        var expiresUtc = expiresAtUtc.ToUniversalTime();

        if (expiresUtc <= createdUtc)
        {
            throw new ArgumentException("ExpiresAtUtc must be greater than CreatedAtUtc.", nameof(expiresAtUtc));
        }

        var duration = expiresUtc - createdUtc;
        if (duration > MaximumDuration)
        {
            throw new ArgumentException("Share duration cannot exceed 72 hours.", nameof(expiresAtUtc));
        }

        if (revokedAtUtc.HasValue != (revokedByUserId != null))
        {
            throw new ArgumentException("Revocation timestamp and revoking user id must be provided together.");
        }

        Id = id;
        TenantId = tenantId;
        ArtifactId = artifactId;
        CreatedByUserId = createdByUserId;
        CreatedByRole = createdByRole;
        CreatedAtUtc = createdUtc;
        ExpiresAtUtc = expiresUtc;
        Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        RevokedAtUtc = revokedAtUtc?.ToUniversalTime();
        RevokedByUserId = revokedByUserId;
    }

    public Guid Id { get; private set; }
    public TenantId TenantId { get; private set; }
    public Guid ArtifactId { get; private set; }
    public UserId CreatedByUserId { get; private set; }
    public UserRole CreatedByRole { get; private set; }
    public string? Reason { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset ExpiresAtUtc { get; private set; }
    public DateTimeOffset? RevokedAtUtc { get; private set; }
    public UserId? RevokedByUserId { get; private set; }

    public BackupShareStatus GetStatus(DateTimeOffset now)
    {
        if (RevokedAtUtc is not null && RevokedAtUtc.Value <= now)
        {
            return BackupShareStatus.Revoked;
        }

        if (ExpiresAtUtc <= now)
        {
            return BackupShareStatus.Expired;
        }

        return BackupShareStatus.Active;
    }

    public bool IsActive(DateTimeOffset now) => GetStatus(now) == BackupShareStatus.Active;

    public bool CanBeManagedBy(UserRole role)
    {
        if (CreatedByRole == UserRole.PlatformAdmin)
        {
            return role == UserRole.PlatformAdmin;
        }
        return true;
    }

    public void Revoke(UserId actor, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (RevokedAtUtc is not null)
        {
            return;
        }

        RevokedAtUtc = now.ToUniversalTime();
        RevokedByUserId = actor;
    }

    public void Revoke(UserId actor, UserRole actorRole, DateTimeOffset now)
    {
        if (!CanBeManagedBy(actorRole))
        {
            throw new InvalidOperationException("A tenant UserAdmin cannot revoke a share created by a PlatformAdmin.");
        }

        Revoke(actor, now);
    }

    public static BackupShare Create(
        Guid id,
        BackupArtifact artifact,
        UserId createdByUserId,
        UserRole createdByRole,
        DateTimeOffset now,
        TimeSpan? duration = null,
        string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(createdByUserId);

        var requestedDuration = duration ?? DefaultDuration;
        if (requestedDuration <= TimeSpan.Zero || requestedDuration > MaximumDuration)
        {
            throw new ArgumentException("Share duration must be positive and not exceed 72 hours.", nameof(duration));
        }

        if (!artifact.Replicas.Any(r => r.VerificationState == "Verified" && r.Status == BackupReplicaStatus.Available))
        {
            throw new InvalidOperationException("Artifact must have at least one verified and available downloadable replica.");
        }

        var createdAtUtc = now.ToUniversalTime();
        var expiresAtUtc = createdAtUtc + requestedDuration;

        return new BackupShare(
            id,
            artifact.TenantId,
            artifact.Id,
            createdByUserId,
            createdByRole,
            createdAtUtc,
            expiresAtUtc,
            reason);
    }
}
