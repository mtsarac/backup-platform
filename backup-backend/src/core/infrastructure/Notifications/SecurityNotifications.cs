using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Notifications;
using BackupPlatform.Infrastructure.Persistence;

namespace BackupPlatform.Infrastructure.Notifications;

/// <summary>The mandatory account security category: one in-app notification for the affected user, written in the caller's business transaction.</summary>
public static class SecurityNotifications
{
    public static Task RecordAsync(PlatformDbContext context, UserId user, string type, DateTimeOffset now, CancellationToken cancellationToken, string? dedupKey = null)
    {
        if (!NotificationEventTypes.IsSecurity(type)) throw new ArgumentException("Not a security event type.", nameof(type));
        var notificationEvent = new NotificationEvent(
            Guid.NewGuid(), null, type, NotificationPhase.Raised,
            type == NotificationEventTypes.SecuritySuspiciousLogin ? NotificationSeverity.Warning : NotificationSeverity.Info,
            new NotificationSubject("user_security", user.Value, "Account security"), null,
            dedupKey ?? $"{type}:{user.Value:N}:{Guid.NewGuid():N}", now, now);
        return UserNotificationWriter.RecordAsync(context, notificationEvent, [user], platformAdmins: false, tenantAdmins: false, except: null, cancellationToken);
    }
}
