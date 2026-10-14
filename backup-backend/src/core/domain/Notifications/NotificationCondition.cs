using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;

namespace BackupPlatform.Domain.Notifications;

public enum NotificationConditionDecision
{
    /// <summary>Nothing to say: still the same condition, a flap inside the cooldown, or the policy does not admit it.</summary>
    None = 0,
    /// <summary>The condition started or got worse: notify.</summary>
    Raise = 1,
    /// <summary>Still active after the reminder interval: notify again.</summary>
    Remind = 2,
    /// <summary>It ended after having been notified: say so.</summary>
    Recover = 3
}

/// <summary>What a detector sees about one condition right now.</summary>
public sealed record NotificationSubject(string Kind, Guid? Id, string Name);

/// <summary>
/// The durable state of one ongoing condition (an agent that is offline, a job that has not succeeded for too long): the thing that turns a
/// stream of identical observations into one notification, a reminder per interval and one recovery. All transitions are decided here, so every
/// worker that observes the same fact reaches the same decision, and a restart resumes from the row.
/// </summary>
public sealed class NotificationCondition : ITenantOwned
{
    public const int MaximumKeyLength = 200;

    private NotificationCondition()
    {
        TenantId = null!;
        Key = null!;
        Type = null!;
        SubjectKind = null!;
        SubjectName = null!;
    }

    public NotificationCondition(Guid id, TenantId tenantId, string key, string type, NotificationSubject subject, DateTimeOffset now)
    {
        BackupGuard.Id(id);
        ArgumentNullException.ThrowIfNull(tenantId);
        BackupGuard.Text(key, MaximumKeyLength);
        if (!NotificationEventTypes.IsSelectable(type)) throw new ArgumentException("Unknown event type.", nameof(type));
        Id = id;
        TenantId = tenantId;
        Key = key;
        Type = type;
        SubjectKind = subject.Kind;
        SubjectId = subject.Id;
        SubjectName = subject.Name;
        Severity = NotifiedSeverity = NotificationSeverity.Info;
        LastObservedAt = BackupGuard.Timestamp(now);
    }

    public Guid Id { get; private set; }
    public TenantId TenantId { get; private set; }
    /// <summary>Unique per tenant, for example <c>agent_offline:{agentId}</c>.</summary>
    public string Key { get; private set; }
    public string Type { get; private set; }
    public string SubjectKind { get; private set; }
    public Guid? SubjectId { get; private set; }
    public string SubjectName { get; private set; }
    public NotificationSeverity Severity { get; private set; }
    public bool IsActive { get; private set; }
    /// <summary>How many times the condition has started; part of every event's dedup key, so a second occurrence is a new event and a replay of one is not.</summary>
    public int Activations { get; private set; }
    /// <summary>Events produced in the current activation (raise, escalations, reminders).</summary>
    public int Sequence { get; private set; }
    /// <summary>The current activation has produced a Raise; only then does its end produce a Recover.</summary>
    public bool Notified { get; private set; }
    public NotificationSeverity NotifiedSeverity { get; private set; }
    public DateTimeOffset? ActiveSince { get; private set; }
    public DateTimeOffset? ResolvedAt { get; private set; }
    public DateTimeOffset LastObservedAt { get; private set; }
    public DateTimeOffset? LastNotifiedAt { get; private set; }
    /// <summary>Notifications withheld because the condition flapped inside the cooldown.</summary>
    public int SuppressedCount { get; private set; }

    /// <summary>The dedup key of the event the last decision produced.</summary>
    public string EventKey(NotificationConditionDecision decision) =>
        decision == NotificationConditionDecision.Recover ? $"{Key}#{Activations}.recovered" : $"{Key}#{Activations}.{Sequence}";

    /// <summary>The condition holds. <paramref name="admitted"/>: the policy lets this type/severity through and a channel exists.</summary>
    public NotificationConditionDecision ObserveActive(
        NotificationSubject subject, NotificationSeverity severity, bool admitted, TimeSpan reminderInterval, TimeSpan flapCooldown, DateTimeOffset now)
    {
        var at = BackupGuard.Timestamp(now);
        SubjectName = subject.Name;
        Severity = severity;
        // Coarse touch: a steady condition must not cost a write per sweep.
        if (!IsActive || at - LastObservedAt >= TimeSpan.FromMinutes(10)) LastObservedAt = at;
        if (!IsActive)
        {
            IsActive = true;
            Activations++;
            Sequence = 0;
            Notified = false;
            ActiveSince = at;
            ResolvedAt = null;
        }
        if (!admitted) return NotificationConditionDecision.None;
        if (!Notified)
        {
            if (LastNotifiedAt is { } last && at - last < flapCooldown)
            {
                SuppressedCount++;
                return NotificationConditionDecision.None;
            }
            return Notify(severity, at);
        }
        if (severity > NotifiedSeverity) return Notify(severity, at);
        if (reminderInterval > TimeSpan.Zero && at - LastNotifiedAt!.Value >= reminderInterval)
        {
            LastNotifiedAt = at;
            Sequence++;
            return NotificationConditionDecision.Remind;
        }
        return NotificationConditionDecision.None;
    }

    /// <summary>The condition no longer holds. <paramref name="recoveryAdmitted"/>: the policy sends recoveries for this type.</summary>
    public NotificationConditionDecision ObserveResolved(bool recoveryAdmitted, DateTimeOffset now)
    {
        if (!IsActive) return NotificationConditionDecision.None;
        var at = BackupGuard.Timestamp(now);
        IsActive = false;
        ResolvedAt = at;
        LastObservedAt = at;
        var wasNotified = Notified;
        Notified = false;
        if (!wasNotified || !recoveryAdmitted) return NotificationConditionDecision.None;
        return NotificationConditionDecision.Recover;
    }

    private NotificationConditionDecision Notify(NotificationSeverity severity, DateTimeOffset at)
    {
        Notified = true;
        NotifiedSeverity = severity;
        LastNotifiedAt = at;
        Sequence++;
        return NotificationConditionDecision.Raise;
    }
}
