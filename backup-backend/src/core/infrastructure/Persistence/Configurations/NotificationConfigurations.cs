using System.Text.Json;
using BackupPlatform.Domain.Notifications;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Infrastructure.Persistence.Conversions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BackupPlatform.Infrastructure.Persistence.Configurations;

internal static class NotificationMapping
{
    internal const string Statuses = "'Pending','InProgress','Sent','Failed','Suppressed','Cancelled'";
    internal const string Severities = "'Info','Warning','Critical'";

    internal static PropertyBuilder<IReadOnlyList<T>> JsonList<T>(PropertyBuilder<IReadOnlyList<T>> property, string column) =>
        property.HasConversion(
                value => JsonSerializer.Serialize(value, (JsonSerializerOptions?)null),
                value => JsonSerializer.Deserialize<List<T>>(value, (JsonSerializerOptions?)null)!,
                new ValueComparer<IReadOnlyList<T>>((left, right) => left!.SequenceEqual(right!), value => value.Aggregate(0, (hash, item) => HashCode.Combine(hash, item)), value => value.ToList()))
            .HasColumnName(column).HasColumnType("jsonb").IsRequired();

    internal static PropertyBuilder<IReadOnlyDictionary<string, string>> JsonMap(PropertyBuilder<IReadOnlyDictionary<string, string>> property, string column) =>
        property.HasConversion(
                value => JsonSerializer.Serialize(value, (JsonSerializerOptions?)null),
                value => JsonSerializer.Deserialize<Dictionary<string, string>>(value, (JsonSerializerOptions?)null)!,
                new ValueComparer<IReadOnlyDictionary<string, string>>(
                    (left, right) => left!.Count == right!.Count && left.All(pair => right.ContainsKey(pair.Key) && right[pair.Key] == pair.Value),
                    value => value.Aggregate(0, (hash, pair) => HashCode.Combine(hash, pair.Key, pair.Value)),
                    value => new Dictionary<string, string>(value)))
            .HasColumnName(column).HasColumnType("jsonb").IsRequired();
}

internal sealed class NotificationChannelConfiguration : IEntityTypeConfiguration<NotificationChannel>
{
    public void Configure(EntityTypeBuilder<NotificationChannel> builder)
    {
        BackupMapping.Base(builder, "notification_channels");
        builder.ToTable("notification_channels", table =>
        {
            table.HasCheckConstraint("ck_notification_channels_kind", "\"Kind\" IN ('Email','Webhook','Sms')");
            table.HasCheckConstraint("ck_notification_channels_auth", "\"Authentication\" IN ('None','Bearer','Hmac') AND (\"Kind\" = 'Webhook' OR \"Authentication\" = 'None')");
            table.HasCheckConstraint("ck_notification_channels_name", "length(btrim(\"Name\")) BETWEEN 1 AND 100 AND \"NormalizedName\" = lower(btrim(\"Name\"))");
            table.HasCheckConstraint("ck_notification_channels_secret", "(\"SecretReference\" IS NULL AND \"SecretUpdatedAt\" IS NULL) OR (\"Authentication\" <> 'None' AND length(\"SecretReference\") BETWEEN 1 AND 128 AND \"SecretUpdatedAt\" IS NOT NULL)");
            table.HasCheckConstraint("ck_notification_channels_recipient_verified", "\"RecipientVerifiedAt\" IS NULL OR \"Kind\" = 'Email'");
        });
        builder.Property(channel => channel.Kind).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(channel => channel.Name).HasMaxLength(NotificationChannel.MaximumNameLength).IsRequired();
        builder.Property(channel => channel.NormalizedName).HasMaxLength(NotificationChannel.MaximumNameLength).IsRequired();
        builder.Property(channel => channel.Target).HasMaxLength(NotificationChannel.MaximumTargetLength).IsRequired();
        builder.Property(channel => channel.NormalizedTarget).HasMaxLength(NotificationChannel.MaximumTargetLength).IsRequired();
        builder.Property(channel => channel.Authentication).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(channel => channel.Enabled).IsRequired();
        builder.Property(channel => channel.SecretReference).HasMaxLength(128);
        builder.Property(channel => channel.SecretUpdatedAt).HasColumnType("timestamp with time zone");
        builder.Property(channel => channel.CreatedAt).HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(channel => channel.UpdatedAt).HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(channel => channel.RecipientVerifiedAt).HasColumnType("timestamp with time zone");
        builder.Property(channel => channel.SecurityOwnerUserId).HasConversion<UserIdConverter>().HasColumnType("uuid");
        builder.HasOne<User>().WithMany().HasForeignKey(channel => channel.SecurityOwnerUserId).OnDelete(DeleteBehavior.Restrict);
        builder.Ignore(channel => channel.HasSecret);
        builder.Ignore(channel => channel.IsDeliverable);
        builder.Ignore(channel => channel.AwaitsConfirmation);
        builder.HasIndex(channel => new { channel.TenantId, channel.NormalizedName }).IsUnique().HasDatabaseName("ux_notification_channels_name");
        builder.HasIndex(channel => new { channel.TenantId, channel.Kind, channel.NormalizedTarget }).IsUnique().HasDatabaseName("ux_notification_channels_target");
    }
}

internal sealed class NotificationPolicyConfiguration : IEntityTypeConfiguration<NotificationPolicy>
{
    public void Configure(EntityTypeBuilder<NotificationPolicy> builder)
    {
        BackupMapping.Base(builder, "notification_policies");
        builder.ToTable("notification_policies", table =>
        {
            table.HasCheckConstraint("ck_notification_policies_severity", $"\"MinimumSeverity\" IN ({NotificationMapping.Severities})");
            table.HasCheckConstraint("ck_notification_policies_thresholds",
                $"\"StaleAfterHours\" BETWEEN {NotificationPolicy.MinimumStaleAfterHours} AND {NotificationPolicy.MaximumStaleAfterHours} AND " +
                $"\"AgentOfflineAfterMinutes\" BETWEEN {NotificationPolicy.MinimumAgentOfflineMinutes} AND {NotificationPolicy.MaximumAgentOfflineMinutes} AND " +
                $"\"ReminderIntervalHours\" BETWEEN 0 AND {NotificationPolicy.MaximumReminderIntervalHours} AND " +
                $"\"MaxNotificationsPerHour\" BETWEEN {NotificationPolicy.MinimumPerHour} AND {NotificationPolicy.MaximumPerHour}");
        });
        builder.Property(policy => policy.Enabled).IsRequired();
        builder.Property(policy => policy.ActiveSince).HasColumnType("timestamp with time zone");
        NotificationMapping.JsonList(builder.Property(policy => policy.EventTypes), "EventTypesJson");
        NotificationMapping.JsonList(builder.Property(policy => policy.ChannelKinds), "ChannelKindsJson");
        builder.Property(policy => policy.MinimumSeverity).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(policy => policy.RecoveryNotifications).IsRequired();
        builder.Property(policy => policy.CreatedAt).HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(policy => policy.UpdatedAt).HasColumnType("timestamp with time zone").IsRequired();
        builder.HasIndex(policy => policy.TenantId).IsUnique().HasDatabaseName("ux_notification_policies_tenant");
        builder.HasIndex(policy => policy.Enabled).HasDatabaseName("ix_notification_policies_enabled").HasFilter("\"Enabled\"");
    }
}

internal sealed class NotificationConditionConfiguration : IEntityTypeConfiguration<NotificationCondition>
{
    public void Configure(EntityTypeBuilder<NotificationCondition> builder)
    {
        BackupMapping.Base(builder, "notification_conditions");
        builder.ToTable("notification_conditions", table =>
        {
            table.HasCheckConstraint("ck_notification_conditions_key", $"length(\"Key\") BETWEEN 1 AND {NotificationCondition.MaximumKeyLength}");
            table.HasCheckConstraint("ck_notification_conditions_severity", $"\"Severity\" IN ({NotificationMapping.Severities}) AND \"NotifiedSeverity\" IN ({NotificationMapping.Severities})");
        });
        builder.Property(condition => condition.Key).HasMaxLength(NotificationCondition.MaximumKeyLength).IsRequired();
        builder.Property(condition => condition.Type).HasMaxLength(32).IsRequired();
        builder.Property(condition => condition.SubjectKind).HasMaxLength(32).IsRequired();
        builder.Property(condition => condition.SubjectName).HasMaxLength(100).IsRequired();
        builder.Property(condition => condition.Severity).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(condition => condition.NotifiedSeverity).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(condition => condition.ActiveSince).HasColumnType("timestamp with time zone");
        builder.Property(condition => condition.ResolvedAt).HasColumnType("timestamp with time zone");
        builder.Property(condition => condition.LastObservedAt).HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(condition => condition.LastNotifiedAt).HasColumnType("timestamp with time zone");
        builder.HasIndex(condition => new { condition.TenantId, condition.Key }).IsUnique().HasDatabaseName("ux_notification_conditions_key");
        builder.HasIndex(condition => new { condition.TenantId, condition.Type }).HasDatabaseName("ix_notification_conditions_active").HasFilter("\"IsActive\"");
    }
}

internal sealed class NotificationEventConfiguration : IEntityTypeConfiguration<NotificationEvent>
{
    public void Configure(EntityTypeBuilder<NotificationEvent> builder)
    {
        BackupMapping.Base(builder, "notification_events");
        builder.ToTable("notification_events", table =>
        {
            table.HasCheckConstraint("ck_notification_events_phase", "\"Phase\" IN ('Raised','Recovered','Test')");
            table.HasCheckConstraint("ck_notification_events_severity", $"\"Severity\" IN ({NotificationMapping.Severities})");
        });
        builder.Property(notificationEvent => notificationEvent.Type).HasMaxLength(32).IsRequired();
        builder.Property(notificationEvent => notificationEvent.Phase).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(notificationEvent => notificationEvent.Severity).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(notificationEvent => notificationEvent.SubjectKind).HasMaxLength(32).IsRequired();
        builder.Property(notificationEvent => notificationEvent.SubjectName).HasMaxLength(100).IsRequired();
        NotificationMapping.JsonMap(builder.Property(notificationEvent => notificationEvent.Facts), "FactsJson");
        builder.Property(notificationEvent => notificationEvent.DedupKey).HasMaxLength(NotificationCondition.MaximumKeyLength + 32).IsRequired();
        builder.Property(notificationEvent => notificationEvent.OccurredAt).HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(notificationEvent => notificationEvent.CreatedAt).HasColumnType("timestamp with time zone").IsRequired();
        builder.HasIndex(notificationEvent => new { notificationEvent.TenantId, notificationEvent.DedupKey }).IsUnique().HasDatabaseName("ux_notification_events_dedup");
        builder.HasIndex(notificationEvent => new { notificationEvent.TenantId, notificationEvent.CreatedAt }).HasDatabaseName("ix_notification_events_recent");
        builder.HasIndex(notificationEvent => notificationEvent.CreatedAt).HasDatabaseName("ix_notification_events_created");
    }
}

internal sealed class NotificationDeliveryConfiguration : IEntityTypeConfiguration<NotificationDelivery>
{
    public void Configure(EntityTypeBuilder<NotificationDelivery> builder)
    {
        BackupMapping.Base(builder, "notification_deliveries");
        builder.ToTable("notification_deliveries", table =>
        {
            table.HasCheckConstraint("ck_notification_deliveries_kind", "\"ChannelKind\" IN ('Email','Webhook','Sms')");
            table.HasCheckConstraint("ck_notification_deliveries_status", $"\"Status\" IN ({NotificationMapping.Statuses})");
            table.HasCheckConstraint("ck_notification_deliveries_attempts", "\"AttemptCount\" >= 0");
            // A delivery is either waiting (due time, no lease), being worked (lease), or finished (completion time, no due time and no lease).
            table.HasCheckConstraint("ck_notification_deliveries_state",
                "(\"Status\" = 'Pending' AND \"NextAttemptAt\" IS NOT NULL AND \"LeaseOwner\" IS NULL AND \"CompletedAt\" IS NULL) OR " +
                "(\"Status\" = 'InProgress' AND \"LeaseOwner\" IS NOT NULL AND \"LeaseExpiresAt\" IS NOT NULL AND \"CompletedAt\" IS NULL) OR " +
                "(\"Status\" IN ('Sent','Failed','Suppressed','Cancelled') AND \"NextAttemptAt\" IS NULL AND \"LeaseOwner\" IS NULL AND \"CompletedAt\" IS NOT NULL)");
        });
        builder.Property(delivery => delivery.EventId).IsRequired();
        builder.Property(delivery => delivery.ChannelId).IsRequired();
        builder.Property(delivery => delivery.ChannelKind).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(delivery => delivery.ChannelName).HasMaxLength(NotificationChannel.MaximumNameLength).IsRequired();
        builder.Property(delivery => delivery.TargetDisplay).HasMaxLength(128).IsRequired();
        builder.Property(delivery => delivery.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(delivery => delivery.LeaseOwner).HasMaxLength(128);
        builder.Property(delivery => delivery.ErrorCode).HasMaxLength(64);
        foreach (var name in new[] { nameof(NotificationDelivery.NextAttemptAt), nameof(NotificationDelivery.LeaseExpiresAt), nameof(NotificationDelivery.LastAttemptAt),
                     nameof(NotificationDelivery.SentAt), nameof(NotificationDelivery.CompletedAt) })
            builder.Property(name).HasColumnType("timestamp with time zone");
        builder.Property(delivery => delivery.CreatedAt).HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(delivery => delivery.UpdatedAt).HasColumnType("timestamp with time zone").IsRequired();
        // The event and its deliveries always belong to one tenant (composite key), and pruning an event takes its deliveries with it.
        builder.HasOne<NotificationEvent>().WithMany().HasForeignKey(delivery => new { delivery.EventId, delivery.TenantId })
            .HasPrincipalKey(notificationEvent => new { notificationEvent.Id, notificationEvent.TenantId }).OnDelete(DeleteBehavior.Cascade);
        // Idempotency: one delivery per (event, channel), however many workers fan an event out.
        builder.HasIndex(delivery => new { delivery.EventId, delivery.ChannelId }).IsUnique().HasDatabaseName("ux_notification_deliveries_event_channel");
        builder.HasIndex(delivery => delivery.NextAttemptAt).HasDatabaseName("ix_notification_deliveries_due").HasFilter("\"Status\" = 'Pending'");
        builder.HasIndex(delivery => delivery.LeaseExpiresAt).HasDatabaseName("ix_notification_deliveries_lease").HasFilter("\"Status\" = 'InProgress'");
        builder.HasIndex(delivery => new { delivery.TenantId, delivery.CreatedAt }).HasDatabaseName("ix_notification_deliveries_history");
        builder.HasIndex(delivery => new { delivery.TenantId, delivery.ChannelId, delivery.CreatedAt }).HasDatabaseName("ix_notification_deliveries_channel");
    }
}

internal sealed class NotificationTransportSettingsConfiguration : IEntityTypeConfiguration<NotificationTransportSettings>
{
    public void Configure(EntityTypeBuilder<NotificationTransportSettings> builder)
    {
        builder.ToTable("notification_transport_settings", table =>
        {
            table.HasCheckConstraint("ck_notification_transport_settings_key", "\"Key\" IN ('email','sms')");
            table.HasCheckConstraint("ck_notification_transport_settings_secret", "(\"SecretReference\" IS NULL) = (\"SecretUpdatedAt\" IS NULL)");
        });
        builder.HasKey(settings => settings.Key);
        builder.Property(settings => settings.Key).HasMaxLength(16).ValueGeneratedNever();
        builder.Property(settings => settings.Enabled).IsRequired();
        NotificationMapping.JsonMap(builder.Property(settings => settings.Configuration), "ConfigurationJson");
        builder.Property(settings => settings.SecretReference).HasMaxLength(128);
        builder.Property(settings => settings.SecretUpdatedAt).HasColumnType("timestamp with time zone");
        builder.Property(settings => settings.UpdatedAt).HasColumnType("timestamp with time zone").IsRequired();
        builder.Property<uint>("xmin").IsRowVersion();
        builder.Ignore(settings => settings.HasSecret);
    }
}
