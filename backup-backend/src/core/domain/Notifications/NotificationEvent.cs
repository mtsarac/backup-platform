using System.Collections.ObjectModel;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;

namespace BackupPlatform.Domain.Notifications;

/// <summary>
/// One thing worth telling a tenant about. The event is the durable outbox record: it is written in the same transaction that decides to
/// notify and its deliveries, so nothing is lost across a crash and nothing is sent twice for the same <see cref="DedupKey"/>.
/// It carries only what a message needs: a type, a severity, the subject's display name and a few scalar facts, never paths, connection details or
/// provider output.
/// </summary>
public sealed class NotificationEvent : ITenantOwned
{
    public const int MaximumFacts = 8;
    public const int MaximumFactLength = 100;

    private static readonly IReadOnlyDictionary<string, string> NoFacts = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());

    private NotificationEvent()
    {
        TenantId = null!;
        Type = null!;
        SubjectKind = null!;
        SubjectName = null!;
        DedupKey = null!;
        Facts = NoFacts;
    }

    public NotificationEvent(
        Guid id, TenantId tenantId, string type, NotificationPhase phase, NotificationSeverity severity, NotificationSubject subject,
        IReadOnlyDictionary<string, string>? facts, string dedupKey, DateTimeOffset occurredAt, DateTimeOffset now)
    {
        BackupGuard.Id(id);
        ArgumentNullException.ThrowIfNull(tenantId);
        if (type != NotificationEventTypes.Test && !NotificationEventTypes.IsSelectable(type)) throw new ArgumentException("Unknown event type.", nameof(type));
        if (!Enum.IsDefined(phase) || !Enum.IsDefined(severity)) throw new ArgumentException("Unknown phase or severity.");
        BackupGuard.Text(dedupKey, NotificationCondition.MaximumKeyLength + 32);
        Id = id;
        TenantId = tenantId;
        Type = type;
        Phase = phase;
        Severity = severity;
        SubjectKind = subject.Kind;
        SubjectId = subject.Id;
        SubjectName = Truncate(subject.Name, 100);
        Facts = Snapshot(facts);
        DedupKey = dedupKey;
        OccurredAt = BackupGuard.Timestamp(occurredAt);
        CreatedAt = BackupGuard.Timestamp(now);
    }

    public Guid Id { get; private set; }
    public TenantId TenantId { get; private set; }
    public string Type { get; private set; }
    public NotificationPhase Phase { get; private set; }
    public NotificationSeverity Severity { get; private set; }
    public string SubjectKind { get; private set; }
    public Guid? SubjectId { get; private set; }
    public string SubjectName { get; private set; }
    /// <summary>Scalar, message-safe values (a percentage, an hour count, a stable failure code).</summary>
    public IReadOnlyDictionary<string, string> Facts { get; private set; }
    /// <summary>Unique per tenant: the same fact observed twice, by any number of workers, produces one event.</summary>
    public string DedupKey { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];

    private static IReadOnlyDictionary<string, string> Snapshot(IReadOnlyDictionary<string, string>? facts)
    {
        if (facts is null || facts.Count == 0) return NoFacts;
        if (facts.Count > MaximumFacts) throw new ArgumentException("Too many facts.", nameof(facts));
        var copy = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in facts)
        {
            if (string.IsNullOrEmpty(key) || key.Length > 32 || value is null || value.Length > MaximumFactLength || value.Any(char.IsControl))
                throw new ArgumentException("Invalid fact.", nameof(facts));
            copy[key] = value;
        }
        return new ReadOnlyDictionary<string, string>(copy);
    }
}
