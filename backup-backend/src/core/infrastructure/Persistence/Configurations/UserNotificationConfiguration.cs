using BackupPlatform.Domain.Notifications;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Infrastructure.Persistence.Conversions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BackupPlatform.Infrastructure.Persistence.Configurations;

internal sealed class UserNotificationConfiguration : IEntityTypeConfiguration<UserNotification>
{
    public void Configure(EntityTypeBuilder<UserNotification> builder)
    {
        builder.ToTable("user_notifications");
        builder.HasKey(notification => notification.Id);
        builder.Property(notification => notification.Id).ValueGeneratedNever();
        builder.Property(notification => notification.UserId).HasConversion<UserIdConverter>().IsRequired();
        builder.Property(notification => notification.ReadAt).HasColumnType("timestamp with time zone");
        builder.Property(notification => notification.EmailHandledAt).HasColumnType("timestamp with time zone");
        builder.HasOne<NotificationEvent>().WithMany().HasForeignKey(notification => notification.EventId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<User>().WithMany().HasForeignKey(notification => notification.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(notification => new { notification.EventId, notification.UserId }).IsUnique();
        builder.HasIndex(notification => notification.UserId);
    }
}
