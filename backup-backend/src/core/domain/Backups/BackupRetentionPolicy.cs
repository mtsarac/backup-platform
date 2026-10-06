namespace BackupPlatform.Domain.Backups;

/// <summary>
/// How long a successful backup is kept and how long it stays recoverable after it was found expired. It is a property of the
/// <see cref="BackupJob"/>, snapshotted on every <see cref="BackupRun"/> when the run is created, and turned into an absolute
/// expiration on the artifact when the artifact is accepted. A later edit of the job therefore never moves the expiration of a
/// backup that already exists.
/// </summary>
/// <remarks>
/// <see cref="Enabled"/> false means "keep forever": no expiration is computed and nothing is ever deleted automatically. The
/// bounds here are only what is representable and sane; the platform's operational minimums (for example "never less than an hour") are
/// backend options enforced where a policy enters through the API.
/// </remarks>
public sealed record BackupRetentionPolicy
{
    public static readonly TimeSpan MinimumKeepForLimit = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaximumKeepForLimit = TimeSpan.FromDays(3650);
    public static readonly TimeSpan MinimumDeleteGraceLimit = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaximumDeleteGraceLimit = TimeSpan.FromDays(365);

    public static BackupRetentionPolicy Default { get; } = new();

    public bool Enabled { get; init; } = true;

    /// <summary>How long after the backup completed it expires.</summary>
    public TimeSpan KeepFor { get; init; } = TimeSpan.FromDays(30);

    /// <summary>How long an expired (or manually deleted) backup stays physically present and cancellable before its replicas are removed.</summary>
    public TimeSpan DeleteGracePeriod { get; init; } = TimeSpan.FromHours(24);

    public BackupRetentionPolicy Validate()
    {
        if (KeepFor < MinimumKeepForLimit || KeepFor > MaximumKeepForLimit) throw new ArgumentException("KeepFor is out of range.");
        if (DeleteGracePeriod < MinimumDeleteGraceLimit || DeleteGracePeriod > MaximumDeleteGraceLimit) throw new ArgumentException("DeleteGracePeriod is out of range.");
        return this;
    }

    /// <summary>The absolute expiration of a backup that completed at <paramref name="completedAt"/>; null when retention is disabled.</summary>
    public DateTimeOffset? ExpiresAt(DateTimeOffset completedAt) => Enabled ? BackupGuard.Timestamp(completedAt + KeepFor) : null;
}

/// <summary>Why a replica entered the delete lifecycle.</summary>
public enum BackupDeleteReason
{
    /// <summary>The artifact's <see cref="BackupArtifact.RetentionExpiresAt"/> passed.</summary>
    RetentionExpired = 1,
    /// <summary>An administrator asked for it.</summary>
    Manual
}

/// <summary>
/// Stable machine-readable reasons a physical delete did not complete. Persisted and shown to operators; never contains a path or a
/// provider message.
/// </summary>
public static class BackupDeleteFailureCodes
{
    /// <summary>The backend has no storage configured that can reach the replica (for example an agent-local disk the backend cannot see).</summary>
    public const string StorageNotConfigured = "storage_not_configured";
    public const string DeleteNotSupported = "delete_not_supported";
    /// <summary>The persisted location is not a valid storage key.</summary>
    public const string InvalidLocation = "invalid_location";
    public const string AccessDenied = "access_denied";
    public const string StorageUnavailable = "storage_unavailable";
    public const string IoError = "io_error";
    /// <summary>The provider reported the delete as done but the object is still present.</summary>
    public const string DeleteNotVerified = "delete_not_verified";
    public const string Unexpected = "unexpected";
}
