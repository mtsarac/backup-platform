using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Domain.Notifications;

public sealed class UserNotification
{
    private UserNotification() { UserId = null!; }

    public UserNotification(Guid id, Guid eventId, UserId userId)
    {
        if (id == Guid.Empty || eventId == Guid.Empty) throw new ArgumentException("Notification and event ids must not be empty.");
        ArgumentNullException.ThrowIfNull(userId);
        Id = id;
        EventId = eventId;
        UserId = userId;
    }

    public Guid Id { get; private set; }
    public Guid EventId { get; private set; }
    public UserId UserId { get; private set; }
    public DateTimeOffset? ReadAt { get; private set; }
}
