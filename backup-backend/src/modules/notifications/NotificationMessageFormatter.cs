using BackupPlatform.Domain.Notifications;

namespace BackupPlatform.Notifications;

/// <summary>The text of one notification. <see cref="Short"/> fits one SMS-sized line.</summary>
public sealed record RenderedMessage(string Subject, string Text, string Short);

/// <summary>
/// Turns an event into text. Only the event's own message-safe values are used (type, severity, subject display name, a few scalar facts): never a
/// path, a connection detail, a database or server name, or provider output. Free text (the subject name) is flattened to a single line first, so
/// it cannot break a header or forge a second paragraph.
/// </summary>
public static class NotificationMessageFormatter
{
    public const string Product = "Backup Platform";
    private const int ShortLimit = 160;

    public static RenderedMessage Render(NotificationEventSnapshot e)
    {
        var name = Flatten(e.SubjectName);
        var (title, detail) = e.Phase switch
        {
            NotificationPhase.Test => ("Test notification", $"This is a test notification for the channel \"{name}\". If you can read it, delivery works."),
            NotificationPhase.Recovered => Recovered(e.Type, name),
            _ => Raised(e, name)
        };
        var severity = e.Phase == NotificationPhase.Raised ? e.Severity.ToString().ToUpperInvariant() : e.Phase.ToString().ToUpperInvariant();
        var text = $"{detail}\n\nSeverity: {e.Severity}\nTime (UTC): {e.OccurredAt.UtcDateTime:yyyy-MM-dd HH:mm:ss}\n\nThis is an automated message from {Product}. " +
                   "Manage notifications under Notifications in the web console.";
        var shortText = $"[{Product}] {severity}: {title}";
        return new RenderedMessage($"[{Product}] {title}", text, shortText.Length <= ShortLimit ? shortText : shortText[..(ShortLimit - 1)] + "…");
    }

    private static (string Title, string Detail) Raised(NotificationEventSnapshot e, string name)
    {
        string Fact(string key, string fallback = "unknown") => e.Facts.TryGetValue(key, out var value) ? Flatten(value) : fallback;
        return e.Type switch
        {
            NotificationEventTypes.BackupFailed => ($"Backup failed: {name}", $"A backup run of job \"{name}\" failed (reason code: {Fact("code")})."),
            NotificationEventTypes.BackupStale => ($"No recent successful backup: {name}", $"Job \"{name}\" has not completed a successful backup for about {Fact("hours")} hours."),
            NotificationEventTypes.AgentOffline => ($"Agent offline: {name}", $"Agent \"{name}\" has not been seen for about {Fact("minutes")} minutes."),
            NotificationEventTypes.QuotaCritical => ($"Storage quota {Fact("state", "critical")}: {Fact("percent")}% used",
                $"The tenant's backup storage is at {Fact("percent")}% of its limit ({Fact("state", "critical")})."),
            NotificationEventTypes.ReplicaDegraded => ($"Backup replicas {Fact("status", "degraded")}: {name}",
                $"The latest backup of job \"{name}\" has {Fact("verified")} of {Fact("required")} required verified replicas."),
            _ => ($"Notification: {name}", $"An event of type {e.Type} occurred for \"{name}\".")
        };
    }

    private static (string Title, string Detail) Recovered(string type, string name) => type switch
    {
        NotificationEventTypes.BackupStale => ($"Backups succeeding again: {name}", $"Job \"{name}\" completed a successful backup again."),
        NotificationEventTypes.AgentOffline => ($"Agent back online: {name}", $"Agent \"{name}\" is connected again."),
        NotificationEventTypes.QuotaCritical => ("Storage quota back to normal", "The tenant's backup storage is below the critical threshold again."),
        NotificationEventTypes.ReplicaDegraded => ($"Backup replicas healthy again: {name}", $"The latest backup of job \"{name}\" meets its replica requirement again."),
        _ => ($"Resolved: {name}", $"The condition for \"{name}\" has ended.")
    };

    /// <summary>One line, no control characters, bounded; applied to every free-text value that reaches a message.</summary>
    public static string Flatten(string value)
    {
        var chars = value.Select(character => char.IsControl(character) ? ' ' : character).ToArray();
        var text = new string(chars).Trim();
        return text.Length <= 100 ? text : text[..100];
    }
}
