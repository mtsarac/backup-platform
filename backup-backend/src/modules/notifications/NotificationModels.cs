using BackupPlatform.Application.Platform;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Notifications;

namespace BackupPlatform.Notifications;

/// <summary>Audit action names of notification administration. All carry the tenant (transports: none); none carries a target, a URL or a secret value.</summary>
public static class NotificationAuditActions
{
    public const string ChannelCreated = "notification_channel.created";
    public const string ChannelUpdated = "notification_channel.updated";
    public const string ChannelEnabled = "notification_channel.enabled";
    public const string ChannelDisabled = "notification_channel.disabled";
    public const string ChannelDeleted = "notification_channel.deleted";
    public const string ChannelSecretRotated = "notification_channel.secret_rotated";
    public const string ChannelSecretCleared = "notification_channel.secret_cleared";
    public const string ChannelTestRequested = "notification_channel.test_requested";
    public const string PolicyUpdated = "notification_policy.updated";
    public const string TransportUpdated = "notification_transport.updated";
    public const string TransportSecretRotated = "notification_transport.secret_rotated";
    public const string TransportSecretCleared = "notification_transport.secret_cleared";

    public const string ChannelTarget = "notification_channel";
    public const string PolicyTarget = "notification_policy";
    public const string TransportTarget = "notification_transport";
}

/// <summary>Machine-readable validation failure naming the offending field; never carries the rejected value.</summary>
public sealed class InvalidNotificationException(string field, string code) : Exception($"Invalid notification field '{field}' ({code}).")
{
    public string Field { get; } = field;
    public string Code { get; } = code;
}

public sealed record NotificationActor(UserId UserId, AuditActorKind Kind);

public enum NotificationOutcome
{
    Changed,
    Unchanged,
    NotFound,
    TenantNotFound,
    NameTaken,
    TargetTaken,
    LimitReached,
    TransportNotConfigured,
    TestRateLimited,
    ConcurrentUpdate
}

public sealed record NotificationResult<T>(NotificationOutcome Outcome, T? Value = default) where T : class
{
    public bool Succeeded => Outcome is NotificationOutcome.Changed or NotificationOutcome.Unchanged;
}

// ---- store records ---------------------------------------------------------------------------------------------------------------------

public sealed record LastDeliverySummary(NotificationDeliveryStatus Status, DateTimeOffset At, string? ErrorCode);

public sealed record NotificationChannelRecord(NotificationChannel Channel, LastDeliverySummary? Last);

public sealed record DeliveryFilter(NotificationDeliveryStatus? Status, NotificationChannelKind? Kind, string? EventType);

public sealed record NotificationDeliveryRecord(
    NotificationDelivery Delivery, string EventType, NotificationPhase Phase, NotificationSeverity Severity, string SubjectKind, string SubjectName);

/// <summary>What a mutation of a channel did, for the audit event and for the secret that became obsolete (to delete after the commit).</summary>
public sealed record ChannelChange(string AuditAction, string? ObsoleteSecret = null, IReadOnlyDictionary<string, object?>? Details = null);

public sealed record ChannelMutationResult(NotificationOutcome Outcome, NotificationChannelRecord? Record = null, string? ObsoleteSecret = null);

public sealed record TransportChange(string AuditAction, string? ObsoleteSecret = null);

public sealed record TransportMutationResult(NotificationOutcome Outcome, NotificationTransportSettings? Settings = null, string? ObsoleteSecret = null);

// ---- engine ----------------------------------------------------------------------------------------------------------------------------

/// <summary>One fact a detector found. <see cref="Key"/> identifies a condition (<c>agent_offline:{id}</c>) or, for a one-shot occurrence, the event itself (<c>backup_failed:{runId}</c>).</summary>
public sealed record NotificationObservation(
    string Key, string Type, NotificationSeverity Severity, NotificationSubject Subject, DateTimeOffset OccurredAt, IReadOnlyDictionary<string, string>? Facts = null);

/// <summary>
/// Everything a detector found for ONE event type in ONE tenant. A condition set is complete: every condition of this type that is active in the
/// database and absent here has ended. An occurrence set only adds events (duplicates are ignored by their key).
/// </summary>
public sealed record ObservationSet(string Type, bool IsCondition, IReadOnlyList<NotificationObservation> Observations);

/// <summary>Reads the platform's state and reports conditions. Implemented against persistence by the infrastructure; the notifications module knows no other module.</summary>
public interface INotificationObservationSource
{
    /// <summary>
    /// The sets for the types <paramref name="policy"/> enables. A type whose collection failed is omitted (never reported as empty), so a
    /// transient read error can never be mistaken for "everything recovered".
    /// </summary>
    Task<IReadOnlyList<ObservationSet>> CollectAsync(NotificationPolicy policy, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>One tenant's notification state inside ONE transaction that is serialized per tenant on every node. Nothing is saved unless the unit of work completes.</summary>
public interface INotificationSession
{
    Task<NotificationPolicy?> GetPolicyAsync();
    Task<IReadOnlyList<NotificationChannel>> ListChannelsAsync();
    Task<NotificationChannel?> GetChannelAsync(Guid id);
    Task<NotificationCondition?> GetConditionAsync(string key);
    Task<IReadOnlyList<NotificationCondition>> ListActiveConditionsAsync(string type);
    void AddCondition(NotificationCondition condition);
    Task<bool> EventExistsAsync(string dedupKey);
    /// <summary>Events (tests excluded) created since <paramref name="since"/>, not counting those added in this session.</summary>
    Task<int> CountEventsSinceAsync(DateTimeOffset since);
    Task<int> CountTestsSinceAsync(Guid channelId, DateTimeOffset since);
    void AddEvent(NotificationEvent notificationEvent);
    void AddDelivery(NotificationDelivery delivery);
    void Audit(NotificationActor actor, string action, string targetType, string targetId, IReadOnlyDictionary<string, object?> details);
}

public interface INotificationStore
{
    // ---- administration
    Task<IReadOnlyList<NotificationChannelRecord>?> ListChannelsAsync(TenantId tenantId, CancellationToken cancellationToken);
    Task<NotificationChannelRecord?> GetChannelAsync(TenantId tenantId, Guid id, CancellationToken cancellationToken);
    Task<ChannelMutationResult> CreateChannelAsync(NotificationChannel channel, NotificationActor actor, int maximumChannels, CancellationToken cancellationToken);
    /// <summary>Loads the channel of the tenant, lets <paramref name="mutate"/> change it (null = nothing changed) and saves the change with its audit event.</summary>
    Task<ChannelMutationResult> MutateChannelAsync(TenantId tenantId, Guid id, NotificationActor actor, Func<NotificationChannel, ChannelChange?> mutate, CancellationToken cancellationToken);
    /// <summary>The secret reference the deleted channel held (null: none); NotFound is reported as <c>Outcome</c>.</summary>
    Task<ChannelMutationResult> DeleteChannelAsync(TenantId tenantId, Guid id, NotificationActor actor, CancellationToken cancellationToken);
    Task<NotificationPolicy?> GetPolicyAsync(TenantId tenantId, CancellationToken cancellationToken);
    /// <summary>Null when the tenant does not exist. The row is created on the first change.</summary>
    Task<(NotificationPolicy Policy, bool Changed)?> UpdatePolicyAsync(TenantId tenantId, NotificationActor actor, Func<NotificationPolicy, bool> apply, CancellationToken cancellationToken);
    Task<PagedResult<NotificationDeliveryRecord>> ListDeliveriesAsync(TenantId tenantId, DeliveryFilter filter, int page, int pageSize, CancellationToken cancellationToken);
    Task<NotificationTransportSettings?> GetTransportAsync(string key, CancellationToken cancellationToken);
    Task<TransportMutationResult> MutateTransportAsync(string key, NotificationActor actor, Func<NotificationTransportSettings, TransportChange?> mutate, CancellationToken cancellationToken);

    // ---- engine
    Task<IReadOnlyList<NotificationPolicy>> ListEnabledPoliciesAsync(CancellationToken cancellationToken);
    /// <summary>Runs <paramref name="work"/> in one transaction holding the tenant's notification lock, and commits it when the work returns. False when the tenant does not exist.</summary>
    Task<bool> InTenantSessionAsync(TenantId tenantId, Func<INotificationSession, Task> work, CancellationToken cancellationToken);

    // ---- delivery
    Task<IReadOnlyList<DeliveryWork>> ClaimDueAsync(string owner, DateTimeOffset now, int batchSize, TimeSpan lease, int maxAttempts, CancellationToken cancellationToken);
    /// <summary>Records the attempt's outcome. False when the lease was lost (another worker owns the delivery now): the caller's result is discarded.</summary>
    Task<bool> CompleteAsync(Guid deliveryId, string owner, int attempt, DeliveryResult result, DateTimeOffset now, CancellationToken cancellationToken);
    Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken cancellationToken);
}

/// <summary>The event as a sender sees it: only message-safe values.</summary>
public sealed record NotificationEventSnapshot(
    Guid Id, string Type, NotificationPhase Phase, NotificationSeverity Severity, string SubjectKind, Guid? SubjectId, string SubjectName,
    IReadOnlyDictionary<string, string> Facts, DateTimeOffset OccurredAt);

/// <summary>A claimed delivery with everything needed to send it. <see cref="Skip"/> is set when the delivery must not be sent (channel gone/disabled, policy off).</summary>
public sealed record DeliveryWork(
    Guid DeliveryId, TenantId TenantId, int Attempt, NotificationChannelKind Kind, Guid ChannelId, string? Target, WebhookAuthentication Authentication,
    string? SecretReference, NotificationEventSnapshot Event, DeliverySkip? Skip);

public sealed record DeliverySkip(NotificationDeliveryStatus Status, string Code);

/// <summary>The outcome of one attempt: Sent, Failed (permanent), Suppressed/Cancelled (not sent on purpose), or Pending with a <see cref="NextAttemptAt"/> (retry).</summary>
public sealed record DeliveryResult(NotificationDeliveryStatus Status, string? Code = null, DateTimeOffset? NextAttemptAt = null);

// ---- views -----------------------------------------------------------------------------------------------------------------------------

public sealed record NotificationChannelView(
    Guid Id, string Kind, string Name, string TargetDisplay, bool Enabled, string Authentication, bool HasSecret, DateTimeOffset? SecretUpdatedAt,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, LastDeliveryView? LastDelivery);

public sealed record LastDeliveryView(string Status, DateTimeOffset At, string? ErrorCode);

public sealed record NotificationPolicyView(
    bool Enabled, IReadOnlyList<string> EventTypes, string MinimumSeverity, IReadOnlyList<string> ChannelKinds, bool RecoveryNotifications,
    int StaleAfterHours, int AgentOfflineAfterMinutes, int ReminderIntervalHours, int MaxNotificationsPerHour, DateTimeOffset? UpdatedAt,
    bool EmailAvailable, bool SmsAvailable);

public sealed record NotificationDeliveryView(
    Guid Id, Guid EventId, string EventType, string Phase, string Severity, string SubjectKind, string SubjectName, string ChannelKind, string ChannelName,
    string TargetDisplay, string Status, int Attempts, string? ErrorCode, DateTimeOffset? NextAttemptAt, DateTimeOffset? SentAt, DateTimeOffset CreatedAt);

/// <summary>A platform transport as an administrator sees it. <see cref="Configuration"/> is the non-secret dictionary; the credential is only ever "set or not".</summary>
public sealed record NotificationTransportView(
    string Key, bool Enabled, bool Configured, IReadOnlyDictionary<string, string> Configuration, bool HasSecret, DateTimeOffset? SecretUpdatedAt, DateTimeOffset? UpdatedAt);

public sealed record PolicyCommand(
    bool Enabled, IReadOnlyList<string>? EventTypes, string? MinimumSeverity, IReadOnlyList<string>? ChannelKinds, bool RecoveryNotifications,
    int StaleAfterHours, int AgentOfflineAfterMinutes, int ReminderIntervalHours, int MaxNotificationsPerHour);

public sealed record CreateChannelCommand(string? Kind, string? Name, string? Target, string? Authentication, string? Secret);

public sealed record UpdateChannelCommand(string? Name, string? Target, string? Authentication);

public sealed record TransportCommand(bool Enabled, IReadOnlyDictionary<string, string?> Values);
