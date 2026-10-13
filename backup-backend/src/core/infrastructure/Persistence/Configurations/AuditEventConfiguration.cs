using BackupPlatform.Domain.Audit;
using BackupPlatform.Infrastructure.Persistence.Conversions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BackupPlatform.Infrastructure.Persistence.Configurations;

/// <summary>
/// Append-only administrative history. Deliberately not tenant-owned and without foreign keys: an audit event must
/// outlive and never block changes to the rows it describes.
/// </summary>
internal sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        builder.ToTable("audit_events", table =>
        {
            table.HasCheckConstraint("ck_audit_events_actor_kind", "\"ActorKind\" IN (1, 2, 3, 4, 5)");
            table.HasCheckConstraint("ck_audit_events_text", "length(\"Action\") > 0 AND length(\"TargetType\") > 0 AND length(\"TargetId\") > 0");
        });
        builder.HasKey(auditEvent => auditEvent.Id);
        builder.Property(auditEvent => auditEvent.Id).ValueGeneratedNever();
        builder.Property(auditEvent => auditEvent.At).HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(auditEvent => auditEvent.ActorUserId).HasConversion<UserIdConverter>().HasColumnType("uuid").IsRequired();
        builder.Property(auditEvent => auditEvent.ActorKind).HasConversion<int>().IsRequired();
        builder.Property(auditEvent => auditEvent.Action).HasMaxLength(64).IsRequired();
        builder.Property(auditEvent => auditEvent.TenantId).HasConversion<TenantIdConverter>().HasColumnType("uuid");
        builder.Property(auditEvent => auditEvent.TargetType).HasMaxLength(64).IsRequired();
        builder.Property(auditEvent => auditEvent.TargetId).HasMaxLength(128).IsRequired();
        builder.Property(auditEvent => auditEvent.DetailsJson).HasColumnType("jsonb").IsRequired();
        builder.Property(auditEvent => auditEvent.SupportSessionId);
        builder.HasIndex(auditEvent => new { auditEvent.SupportSessionId, auditEvent.At }).HasDatabaseName("ix_audit_events_support_session").HasFilter("\"SupportSessionId\" IS NOT NULL");
        builder.HasIndex(auditEvent => new { auditEvent.At, auditEvent.Id });
        builder.HasIndex(auditEvent => new { auditEvent.TenantId, auditEvent.At });
    }
}
