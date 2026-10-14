using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;

namespace BackupPlatform.Domain.Notifications;

/// <summary>
/// The delivery of one event to one channel, and its history. Unique per (event, channel): that is the idempotency guarantee, however many
/// workers fan the event out. The channel name and a masked target are snapshotted so the history survives deleting the channel and never holds a
/// full address. State changes happen in the store through fenced updates (owner + attempt), never by tracked entity writes, so a worker whose lease was
/// taken over cannot overwrite the new owner's result.
/// </summary>
public sealed class NotificationDelivery : ITenantOwned
{
    private NotificationDelivery()
    {
        TenantId = null!;
        ChannelName = null!;
        TargetDisplay = null!;
    }

    public NotificationDelivery(
        Guid id, TenantId tenantId, Guid eventId, Guid channelId, NotificationChannelKind kind, string channelName, string targetDisplay,
        DateTimeOffset now, string? suppressedCode = null)
    {
        BackupGuard.Id(id);
        ArgumentNullException.ThrowIfNull(tenantId);
        BackupGuard.Id(eventId);
        BackupGuard.Id(channelId);
        BackupGuard.Text(channelName, NotificationChannel.MaximumNameLength);
        BackupGuard.Text(targetDisplay, 128);
        Id = id;
        TenantId = tenantId;
        EventId = eventId;
        ChannelId = channelId;
        ChannelKind = kind;
        ChannelName = channelName;
        TargetDisplay = targetDisplay;
        CreatedAt = BackupGuard.Timestamp(now);
        UpdatedAt = CreatedAt;
        if (suppressedCode is null)
        {
            Status = NotificationDeliveryStatus.Pending;
            NextAttemptAt = CreatedAt;
        }
        else
        {
            Status = NotificationDeliveryStatus.Suppressed;
            ErrorCode = suppressedCode;
            CompletedAt = CreatedAt;
        }
    }

    public Guid Id { get; private set; }
    public TenantId TenantId { get; private set; }
    public Guid EventId { get; private set; }
    /// <summary>The channel at the time of the fan-out; deliberately not a foreign key so deleting a channel keeps its history.</summary>
    public Guid ChannelId { get; private set; }
    public NotificationChannelKind ChannelKind { get; private set; }
    public string ChannelName { get; private set; }
    public string TargetDisplay { get; private set; }
    public NotificationDeliveryStatus Status { get; private set; }
    public int AttemptCount { get; private set; }
    public DateTimeOffset? NextAttemptAt { get; private set; }
    public string? LeaseOwner { get; private set; }
    public DateTimeOffset? LeaseExpiresAt { get; private set; }
    public DateTimeOffset? LastAttemptAt { get; private set; }
    public DateTimeOffset? SentAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    /// <summary>A stable code (<see cref="NotificationDeliveryCodes"/> or a sender's), never provider output.</summary>
    public string? ErrorCode { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
}
