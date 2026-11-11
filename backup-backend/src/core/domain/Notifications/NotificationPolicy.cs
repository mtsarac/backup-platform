using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;

namespace BackupPlatform.Domain.Notifications;

/// <summary>
/// A tenant's notification policy: whether notifications are on, which event types and severities qualify, which channel kinds may be used and
/// the thresholds the detectors apply. At most one per tenant; a tenant without a row behaves as <see cref="Defaults"/> (off).
/// </summary>
public sealed class NotificationPolicy : ITenantOwned
{
    public const int MinimumStaleAfterHours = 1, MaximumStaleAfterHours = 720;
    public const int MinimumAgentOfflineMinutes = 5, MaximumAgentOfflineMinutes = 1440;
    public const int MaximumReminderIntervalHours = 168;
    public const int MinimumPerHour = 1, MaximumPerHour = 200;

    private NotificationPolicy()
    {
        TenantId = null!;
        EventTypes = [];
        ChannelKinds = [];
    }

    private NotificationPolicy(Guid id, TenantId tenantId, DateTimeOffset now)
    {
        BackupGuard.Id(id);
        ArgumentNullException.ThrowIfNull(tenantId);
        Id = id;
        TenantId = tenantId;
        EventTypes = [.. NotificationEventTypes.Selectable];
        ChannelKinds = [NotificationChannelKind.Email, NotificationChannelKind.Webhook, NotificationChannelKind.Sms];
        MinimumSeverity = NotificationSeverity.Warning;
        RecoveryNotifications = true;
        StaleAfterHours = 48;
        AgentOfflineAfterMinutes = 15;
        ReminderIntervalHours = 24;
        MaxNotificationsPerHour = 30;
        CreatedAt = BackupGuard.Timestamp(now);
        UpdatedAt = CreatedAt;
    }

    public Guid Id { get; private set; }
    public TenantId TenantId { get; private set; }
    public bool Enabled { get; private set; }
    /// <summary>
    /// When notifications were last switched on. One-shot facts (a failed run) older than this are history, not news, so enabling a policy
    /// never floods a tenant with notifications for things that happened before it asked.
    /// </summary>
    public DateTimeOffset? ActiveSince { get; private set; }
    public IReadOnlyList<string> EventTypes { get; private set; }
    public NotificationSeverity MinimumSeverity { get; private set; }
    public IReadOnlyList<NotificationChannelKind> ChannelKinds { get; private set; }
    public bool RecoveryNotifications { get; private set; }
    public int StaleAfterHours { get; private set; }
    public int AgentOfflineAfterMinutes { get; private set; }
    /// <summary>Hours between repeats of a still-active condition; 0 sends none.</summary>
    public int ReminderIntervalHours { get; private set; }
    public int MaxNotificationsPerHour { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static NotificationPolicy Defaults(TenantId tenantId, DateTimeOffset now) => new(Guid.NewGuid(), tenantId, now);

    public bool Admits(string eventType, NotificationSeverity severity) =>
        Enabled && ChannelKinds.Count > 0 && EventTypes.Contains(eventType) && severity >= MinimumSeverity;

    /// <summary>Applies a complete new policy. False, changing nothing, when it equals the current one.</summary>
    public bool Update(
        bool enabled, IEnumerable<string> eventTypes, NotificationSeverity minimumSeverity, IEnumerable<NotificationChannelKind> channelKinds,
        bool recoveryNotifications, int staleAfterHours, int agentOfflineAfterMinutes, int reminderIntervalHours, int maxNotificationsPerHour, DateTimeOffset now)
    {
        var types = eventTypes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var kinds = channelKinds.Distinct().Order().ToList();
        if (types.Any(type => !NotificationEventTypes.IsSelectable(type))) throw new ArgumentException("Unknown event type.", nameof(eventTypes));
        if (kinds.Any(kind => !Enum.IsDefined(kind))) throw new ArgumentException("Unknown channel kind.", nameof(channelKinds));
        if (!Enum.IsDefined(minimumSeverity)) throw new ArgumentException("Unknown severity.", nameof(minimumSeverity));
        if (staleAfterHours is < MinimumStaleAfterHours or > MaximumStaleAfterHours) throw new ArgumentOutOfRangeException(nameof(staleAfterHours));
        if (agentOfflineAfterMinutes is < MinimumAgentOfflineMinutes or > MaximumAgentOfflineMinutes) throw new ArgumentOutOfRangeException(nameof(agentOfflineAfterMinutes));
        if (reminderIntervalHours is < 0 or > MaximumReminderIntervalHours) throw new ArgumentOutOfRangeException(nameof(reminderIntervalHours));
        if (maxNotificationsPerHour is < MinimumPerHour or > MaximumPerHour) throw new ArgumentOutOfRangeException(nameof(maxNotificationsPerHour));
        var unchanged = Enabled == enabled && EventTypes.SequenceEqual(types) && MinimumSeverity == minimumSeverity && ChannelKinds.SequenceEqual(kinds)
            && RecoveryNotifications == recoveryNotifications && StaleAfterHours == staleAfterHours && AgentOfflineAfterMinutes == agentOfflineAfterMinutes
            && ReminderIntervalHours == reminderIntervalHours && MaxNotificationsPerHour == maxNotificationsPerHour;
        if (unchanged) return false;
        if (enabled && !Enabled) ActiveSince = BackupGuard.Timestamp(now);
        Enabled = enabled;
        EventTypes = types;
        MinimumSeverity = minimumSeverity;
        ChannelKinds = kinds;
        RecoveryNotifications = recoveryNotifications;
        StaleAfterHours = staleAfterHours;
        AgentOfflineAfterMinutes = agentOfflineAfterMinutes;
        ReminderIntervalHours = reminderIntervalHours;
        MaxNotificationsPerHour = maxNotificationsPerHour;
        UpdatedAt = BackupGuard.Timestamp(now);
        return true;
    }
}
