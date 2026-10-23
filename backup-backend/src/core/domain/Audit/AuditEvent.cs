using System.Text.Json;
using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Domain.Audit;

/// <summary>
/// Which kind of authority the acting user exercised. A PlatformAdmin grant is independent of tenant membership, so
/// the kind is recorded explicitly instead of being inferred from roles at read time.
/// </summary>
public enum AuditActorKind
{
    PlatformAdmin = 1,
    TenantAdministrator = 2,
    System = 3,
    User = 4,
    Anonymous = 5
}

public static class AuditActions
{
    public const string TenantCreated = "tenant.created";
    public const string TenantRenamed = "tenant.renamed";
    public const string MembershipAdded = "membership.added";
    public const string MembershipRoleChanged = "membership.role_changed";
    public const string MembershipRemoved = "membership.removed";
    public const string EnrollmentPolicyChanged = "agent_enrollment.policy_changed";
    public const string UserCreated = "user.created";

    // Authentication events
    public const string UserLoginSucceeded = "auth.login_succeeded";
    public const string UserLoginFailed = "auth.login_failed";
    public const string UserLogout = "auth.logout";

    // Agent lifecycle events
    public const string AgentEnrollmentTokenCreated = "agent.enrollment_token_created";
    public const string AgentEnrollmentTokenRevoked = "agent.enrollment_token_revoked";
    public const string AgentEnrolled = "agent.enrolled";
    public const string AgentCredentialRotationStarted = "agent.credential_rotation_started";
    public const string AgentCredentialRotationConfirmed = "agent.credential_rotation_confirmed";
    public const string AgentRevoked = "agent.revoked";

    // Backup job lifecycle events
    public const string BackupJobCreated = "backup_job.created";
    public const string BackupJobUpdated = "backup_job.updated";
    public const string BackupJobScheduleChanged = "backup_job.schedule_changed";
    public const string BackupJobScheduleRemoved = "backup_job.schedule_removed";
    public const string BackupJobRetentionChanged = "backup_job.retention_changed";
    public const string BackupJobEnabled = "backup_job.enabled";
    public const string BackupJobDisabled = "backup_job.disabled";

    // Backup run lifecycle events
    public const string BackupRunTriggered = "backup_run.triggered";
    public const string BackupRunRetryRequested = "backup_run.retry_requested";
    public const string BackupRunCancelRequested = "backup_run.cancel_requested";
    public const string BackupRunPauseRequested = "backup_run.pause_requested";
    public const string BackupRunResumed = "backup_run.resumed";
    public const string BackupRunCompleted = "backup_run.completed";
    public const string BackupRunAttemptCompleted = "backup_run.attempt_completed";
    public const string BackupRunSizeAnomaly = "backup_run.size_anomaly";
    public const string BackupScheduleOccurrenceDecided = "backup_schedule.occurrence_decided";

    // Retention / replica deletion events
    public const string BackupArtifactDeleteRequested = "backup_artifact.delete_requested";
    public const string BackupArtifactDeleteCancelled = "backup_artifact.delete_cancelled";
    public const string BackupReplicaPhysicalDeleteCompleted = "backup_replica.physical_delete_completed";
    public const string BackupArtifactRetentionApplied = "backup_artifact.retention_applied";
    public const string BackupReplicaPhysicalDeleteFailed = "backup_replica.physical_delete_failed";
    public const string BackupReplicaOrphanRecorded = "backup_replica.orphan_recorded";
    public const string BackupReplicaQuarantined = "backup_replica.quarantined";
    public const string BackupStorageOrphanDeleteCompleted = "backup_storage.orphan_delete_completed";

    // Transfer policy events
    public const string UploadBandwidthLimitChanged = "upload_bandwidth.limit_changed";
    public const string BandwidthProfileCreated = "bandwidth_profile.created";
    public const string BandwidthProfileUpdated = "bandwidth_profile.updated";
    public const string BandwidthProfileDeleted = "bandwidth_profile.deleted";
    public const string BandwidthProfileAssigned = "bandwidth_profile.assigned";

    // Database discovery events
    public const string DiscoverySettingsChanged = "discovery.settings_changed";
    public const string DiscoveryCredentialBound = "discovery.credential_bound";
    public const string DiscoveryRescanRequested = "discovery.rescan_requested";

    // Artifact encryption events (ids only, never key material)
    public const string TenantWrapKeyCreated = "tenant_wrap_key.created";
    public const string BackupArtifactKeyUnwrapped = "backup_artifact.key_unwrapped";
    public const string TenantWrapKeyRotated = "tenant_wrap_key.rotated";
    public const string TenantWrapKeyRetired = "tenant_wrap_key.retired";
    public const string TenantRecoveryKeySet = "tenant_recovery_key.set";
    public const string TenantRecoveryKeyRemoved = "tenant_recovery_key.removed";

    // Break-glass decrypt of one tenant artifact by a PlatformAdmin (dual control). Ids and reasons only, never key material.
    public const string BreakGlassRequested = "break_glass.requested";
    public const string BreakGlassApproved = "break_glass.approved";
    public const string BreakGlassDenied = "break_glass.denied";
    public const string BreakGlassConsumed = "break_glass.consumed";
    public const string BreakGlassExpired = "break_glass.expired";
    public const string BreakGlassDownloadStarted = "break_glass.download_started";
    public const string BreakGlassDownloadCompleted = "break_glass.download_completed";
    public const string BreakGlassDownloadFailed = "break_glass.download_failed";
    /// <summary>An approve, deny or use was refused (self-approval, already decided, expired, not the requester); the attempt stays on record.</summary>
    public const string BreakGlassRefused = "break_glass.refused";
    public const string BreakGlassOwnerNotificationQueued = "break_glass.owner_notification_queued";
    public const string BreakGlassOwnerNotificationUnavailable = "break_glass.owner_notification_unavailable";
}

/// <summary>
/// An immutable, durable record of an administrative change. Written in the same transaction as the change it
/// describes; never updated or deleted by the application. Details carry identifiers and values, never secrets.
/// </summary>
public sealed class AuditEvent
{
    public const int MaximumDetailsLength = 4096;

    private AuditEvent()
    {
        ActorUserId = null!;
        Action = null!;
        TargetType = null!;
        TargetId = null!;
        DetailsJson = null!;
    }

    private AuditEvent(
        Guid id, DateTimeOffset at, UserId actorUserId, AuditActorKind actorKind, string action,
        TenantId? tenantId, string targetType, string targetId, string detailsJson, Guid? supportSessionId)
    {
        Id = id;
        At = at.ToUniversalTime();
        ActorUserId = actorUserId;
        ActorKind = actorKind;
        Action = action;
        TenantId = tenantId;
        TargetType = targetType;
        TargetId = targetId;
        DetailsJson = detailsJson;
        SupportSessionId = supportSessionId;
    }

    public Guid Id { get; private set; }
    public DateTimeOffset At { get; private set; }
    public UserId ActorUserId { get; private set; }
    public AuditActorKind ActorKind { get; private set; }
    public string Action { get; private set; }
    /// <summary>The tenant the change concerns; null for changes that belong to no tenant (for example user creation).</summary>
    public TenantId? TenantId { get; private set; }
    public string TargetType { get; private set; }
    public string TargetId { get; private set; }
    /// <summary>A JSON object of scalar values.</summary>
    public string DetailsJson { get; private set; }
    /// <summary>The support access session the event belongs to; null for everything that is not support access. Indexed, so a session's history is one query.</summary>
    public Guid? SupportSessionId { get; private set; }

    public static AuditEvent Record(
        DateTimeOffset at, UserId actor, AuditActorKind actorKind, string action, TenantId? tenantId,
        string targetType, string targetId, IReadOnlyDictionary<string, object?>? details = null, Guid? supportSessionId = null)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetType);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        if (!Enum.IsDefined(actorKind))
        {
            throw new ArgumentOutOfRangeException(nameof(actorKind));
        }
        var json = JsonSerializer.Serialize(details ?? new Dictionary<string, object?>());
        if (json.Length > MaximumDetailsLength)
        {
            throw new ArgumentException("Audit details are too large.", nameof(details));
        }
        if (supportSessionId == Guid.Empty) throw new ArgumentException("A support session id must not be empty.", nameof(supportSessionId));
        return new AuditEvent(Guid.NewGuid(), at, actor, actorKind, action, tenantId, targetType, targetId, json, supportSessionId);
    }
}
