using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Notifications;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Infrastructure.Notifications;

public static class UserNotificationWriter
{
    public static async Task<Guid> RecordAsync(PlatformDbContext context, NotificationEvent notificationEvent, IReadOnlyCollection<UserId> users,
        bool platformAdmins, bool tenantAdmins, UserId? except, CancellationToken cancellationToken)
    {
        if (!context.IsPlatformOperations) throw new InvalidOperationException("Notification routing requires platform operations.");
        if (context.Database.CurrentTransaction is null) throw new InvalidOperationException("A notification requires the business transaction.");
        if (tenantAdmins && notificationEvent.TenantId is null) throw new ArgumentException("Tenant recipients require a tenant.");
        var key = $"user-notification:{notificationEvent.TenantId}:{notificationEvent.DedupKey}";
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", cancellationToken);
        var existing = context.ChangeTracker.Entries<NotificationEvent>().Select(entry => entry.Entity)
            .FirstOrDefault(item => item.TenantId == notificationEvent.TenantId && item.DedupKey == notificationEvent.DedupKey)
            ?? await context.NotificationEvents.SingleOrDefaultAsync(item => item.TenantId == notificationEvent.TenantId && item.DedupKey == notificationEvent.DedupKey, cancellationToken);
        if (existing is null)
        {
            existing = notificationEvent;
            context.NotificationEvents.Add(existing);
        }
        var recipients = users.ToHashSet();
        if (platformAdmins)
            recipients.UnionWith(await context.Users.Where(user => user.Role == UserRole.PlatformAdmin).Select(user => user.Id).ToListAsync(cancellationToken));
        if (tenantAdmins)
            recipients.UnionWith(await (from membership in context.TenantMemberships
                join user in context.Users on membership.UserId equals user.Id
                where membership.TenantId == notificationEvent.TenantId && user.Role == UserRole.UserAdmin
                select user.Id).ToListAsync(cancellationToken));
        if (except is not null) recipients.Remove(except);
        var present = await context.UserNotifications.Where(item => item.EventId == existing.Id).Select(item => item.UserId).ToListAsync(cancellationToken);
        recipients.ExceptWith(present);
        recipients.ExceptWith(context.ChangeTracker.Entries<UserNotification>().Where(entry => entry.Entity.EventId == existing.Id).Select(entry => entry.Entity.UserId));
        foreach (var recipient in recipients) context.UserNotifications.Add(new UserNotification(Guid.NewGuid(), existing.Id, recipient));
        if (recipients.Count > 0)
            await context.Database.ExecuteSqlRawAsync("SELECT pg_notify('user_notifications', '')", cancellationToken);
        return existing.Id;
    }
}
