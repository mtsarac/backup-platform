namespace BackupPlatform.Domain.Support;

/// <summary>
/// Audit vocabulary of support access. Every event carries the real PlatformAdmin as actor, the tenant, the support session id and the
/// resource; none carries a secret, a credential, a storage path or an access token.
/// </summary>
public static class SupportAuditActions
{
    public const string SessionOpened = "support.session.opened";
    public const string SessionClosed = "support.session.closed";
    public const string SessionExpired = "support.session.expired";
    public const string SessionRevoked = "support.session.revoked";

    /// <summary>One artifact's detail was read.</summary>
    public const string ArtifactViewed = "support.artifact.viewed";
    public const string ArtifactListed = "support.artifact.listed";
    /// <summary>Backup jobs and runs; artifacts have their own action.</summary>
    public const string BackupViewed = "support.backup.viewed";
    public const string DownloadRequested = "support.artifact.download_requested";
    public const string DownloadStarted = "support.artifact.download_started";
    public const string DownloadCompleted = "support.artifact.download_completed";
    public const string DownloadFailed = "support.artifact.download_failed";

    public const string RestorePrecheckRequested = "support.restore.precheck_requested";
    public const string RestoreRequested = "support.restore.requested";
    public const string RestoreCancelRequested = "support.restore.cancel_requested";
    /// <summary>A support restore reached a terminal state; written even when its session has long ended.</summary>
    public const string RestoreCompleted = "support.restore.completed";
    public const string RestoreFailed = "support.restore.failed";

    public const string AccessDenied = "support.access_denied";

    public const string SessionTargetType = "support_session";
    public const string ArtifactTargetType = "backup_artifact";
    public const string RestoreTargetType = "restore";
}

/// <summary>A write that carries its own support-session check was refused by the database's view of the session (it ended or expired, or the permission is missing).</summary>
public sealed class SupportAccessDeniedException(SupportAccessDenial denial) : Exception("Support access was denied.")
{
    public SupportAccessDenial Denial { get; } = denial;
}
