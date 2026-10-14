namespace BackupPlatform.Domain.Notifications;

public enum NotificationChannelKind
{
    Email = 1,
    Webhook = 2,
    Sms = 3
}

/// <summary>Ordered: a policy's minimum severity admits itself and everything above it.</summary>
public enum NotificationSeverity
{
    Info = 1,
    Warning = 2,
    Critical = 3
}

public enum NotificationPhase
{
    /// <summary>The condition started (or escalated, or is being reminded about).</summary>
    Raised = 1,
    /// <summary>The condition ended after it had been notified.</summary>
    Recovered = 2,
    /// <summary>An operator-requested test of one channel.</summary>
    Test = 3
}

public enum NotificationDeliveryStatus
{
    Pending = 1,
    InProgress = 2,
    Sent = 3,
    /// <summary>Terminal: a permanent error or the attempts ran out. Never retried again.</summary>
    Failed = 4,
    /// <summary>Terminal: deliberately not sent (burst limit, channel or policy switched off).</summary>
    Suppressed = 5,
    /// <summary>Terminal: the channel disappeared or the owner's configuration no longer allows sending.</summary>
    Cancelled = 6
}

public enum WebhookAuthentication
{
    None = 1,
    Bearer = 2,
    /// <summary>The body is signed with the secret (HMAC-SHA256); the secret itself is never sent.</summary>
    Hmac = 3
}

/// <summary>Event types are lowercase keys (stored as text) so a new type adds a key, not a schema change.</summary>
public static class NotificationEventTypes
{
    public const string BackupFailed = "backup_failed";
    public const string BackupStale = "backup_stale";
    public const string AgentOffline = "agent_offline";
    public const string QuotaCritical = "quota_critical";
    public const string ReplicaDegraded = "replica_degraded";
    /// <summary>Not selectable in a policy; used only by the channel test.</summary>
    public const string Test = "test";
    /// <summary>Not selectable in a policy; the one message an unconfirmed email recipient may receive (a confirmation request with a link, nothing else).</summary>
    public const string RecipientVerification = "recipient_verification";

    public static IReadOnlyList<string> Selectable { get; } = [BackupFailed, BackupStale, AgentOffline, QuotaCritical, ReplicaDegraded];

    public static bool IsSelectable(string? value) => value is not null && Selectable.Contains(value);
}

/// <summary>Delivery suppression/cancellation codes that are decided by the platform, not by a provider.</summary>
public static class NotificationDeliveryCodes
{
    public const string BurstLimited = "burst_limited";
    public const string ChannelDisabled = "channel_disabled";
    public const string ChannelRemoved = "channel_removed";
    public const string PolicyDisabled = "policy_disabled";
    public const string AttemptsExhausted = "attempts_exhausted";
    public const string RecipientUnverified = "recipient_unverified";
}
