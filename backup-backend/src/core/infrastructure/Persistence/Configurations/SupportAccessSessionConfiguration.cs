using BackupPlatform.Domain.Support;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Infrastructure.Persistence.Conversions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BackupPlatform.Infrastructure.Persistence.Configurations;

/// <summary>
/// Support access sessions. The database is the backstop of the domain's rules: a window is at most four hours, permissions are a known
/// non-empty set that includes ViewBackupMetadata, an active row has no end and an ended row has one, an admin has at most one Active session
/// per tenant, and (by trigger, see the migration) tenant, admin, permissions, reason and window can never change and an ended session
/// can never change at all.
/// </summary>
internal sealed class SupportAccessSessionConfiguration : IEntityTypeConfiguration<SupportAccessSession>
{
    public void Configure(EntityTypeBuilder<SupportAccessSession> builder)
    {
        BackupMapping.Base(builder, "support_access_sessions");
        builder.ToTable("support_access_sessions", table =>
        {
            table.HasCheckConstraint("ck_support_access_sessions_status", "\"Status\" IN ('Active','Expired','Closed','Revoked')");
            table.HasCheckConstraint("ck_support_access_sessions_permissions", "\"Permissions\" BETWEEN 1 AND 7 AND (\"Permissions\" & 1) = 1");
            table.HasCheckConstraint("ck_support_access_sessions_window", "\"ExpiresAt\" > \"StartedAt\" AND \"ExpiresAt\" <= \"StartedAt\" + interval '4 hours'");
            table.HasCheckConstraint("ck_support_access_sessions_reason", "\"Reason\" ~ '[^[:space:]]' AND length(\"Reason\") <= 500");
            table.HasCheckConstraint("ck_support_access_sessions_end", "(\"Status\" = 'Active') = (\"ClosedAt\" IS NULL)");
            table.HasCheckConstraint("ck_support_access_sessions_closer",
                "(\"Status\" IN ('Closed','Revoked')) = (\"ClosedBy\" IS NOT NULL) AND (\"CloseReason\" IS NULL OR \"Status\" = 'Revoked')");
            table.HasCheckConstraint("ck_support_access_sessions_key", "length(\"IdempotencyKey\") BETWEEN 1 AND 128 AND length(\"RequestFingerprint\") BETWEEN 1 AND 128");
        });
        builder.Property(session => session.PlatformAdminId).HasConversion<UserIdConverter>().HasColumnType("uuid").IsRequired();
        builder.Property(session => session.ClosedBy).HasConversion<UserIdConverter>().HasColumnType("uuid");
        builder.Property(session => session.Reason).HasMaxLength(SupportAccessSession.MaximumReasonLength).IsRequired();
        builder.Property(session => session.CloseReason).HasMaxLength(SupportAccessSession.MaximumReasonLength);
        builder.Property(session => session.Permissions).HasConversion<int>().IsRequired();
        builder.Property(session => session.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(session => session.StartedAt).HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(session => session.ExpiresAt).HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(session => session.ClosedAt).HasColumnType("timestamp with time zone");
        builder.Property(session => session.CreatedAt).HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(session => session.IdempotencyKey).HasMaxLength(128).IsRequired();
        builder.Property(session => session.RequestFingerprint).HasMaxLength(128).IsRequired();
        builder.HasOne<User>().WithMany().HasForeignKey(session => session.PlatformAdminId).OnDelete(DeleteBehavior.Restrict);
        // The same request key answers with the same session; and one admin holds at most one live window per tenant, whichever node asks.
        builder.HasIndex(session => new { session.TenantId, session.PlatformAdminId, session.IdempotencyKey }).IsUnique().HasDatabaseName("ux_support_access_sessions_key");
        builder.HasIndex(session => new { session.TenantId, session.PlatformAdminId }).IsUnique().HasDatabaseName("ux_support_access_sessions_active").HasFilter("\"Status\" = 'Active'");
        builder.HasIndex(session => new { session.TenantId, session.StartedAt }).HasDatabaseName("ix_support_access_sessions_tenant");
        builder.HasIndex(session => new { session.PlatformAdminId, session.StartedAt }).HasDatabaseName("ix_support_access_sessions_admin");
        builder.HasIndex(session => session.ExpiresAt).HasDatabaseName("ix_support_access_sessions_expiry").HasFilter("\"Status\" = 'Active'");
    }
}
