using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Infrastructure.Persistence.Conversions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BackupPlatform.Infrastructure.Persistence.Configurations;

internal sealed class TenantRecoveryKeyConfiguration : IEntityTypeConfiguration<TenantRecoveryKey>
{
    public void Configure(EntityTypeBuilder<TenantRecoveryKey> builder)
    {
        BackupMapping.Base(builder, "tenant_recovery_keys");
        builder.ToTable("tenant_recovery_keys", table => table.HasCheckConstraint("ck_tenant_recovery_keys_version", "\"Version\" >= 1"));
        builder.Property(key => key.KeyId).HasMaxLength(32).IsRequired();
        builder.Property(key => key.PublicKeySpki).IsRequired();
        builder.Property(key => key.CreatedBy).HasConversion<UserIdConverter>().HasColumnType("uuid").IsRequired();
        builder.Property(key => key.CreatedAt).HasColumnType("timestamp with time zone").IsRequired();
        builder.HasOne<User>().WithMany().HasForeignKey(key => key.CreatedBy).OnDelete(DeleteBehavior.Restrict);
        // One optional recovery recipient per tenant.
        builder.HasIndex(key => key.TenantId).IsUnique().HasDatabaseName("ux_tenant_recovery_keys_tenant");
    }
}

/// <summary>
/// Break-glass requests. The database is the backstop of dual control: whoever decided a request is never its requester, a decided or spent request names its decision,
/// and the window is at most four hours.
/// </summary>
internal sealed class ArtifactBreakGlassRequestConfiguration : IEntityTypeConfiguration<ArtifactBreakGlassRequest>
{
    public void Configure(EntityTypeBuilder<ArtifactBreakGlassRequest> builder)
    {
        BackupMapping.Base(builder, "artifact_break_glass_requests");
        builder.ToTable("artifact_break_glass_requests", table =>
        {
            table.HasCheckConstraint("ck_artifact_break_glass_status", "\"Status\" IN ('Pending','Approved','Denied','Consumed','Expired')");
            table.HasCheckConstraint("ck_artifact_break_glass_window", "\"ExpiresAt\" > \"CreatedAt\" AND \"ExpiresAt\" <= \"CreatedAt\" + interval '4 hours'");
            table.HasCheckConstraint("ck_artifact_break_glass_reason", "\"Reason\" ~ '[^[:space:]]' AND length(\"Reason\") <= 500");
            table.HasCheckConstraint("ck_artifact_break_glass_dual_control", "\"DecidedBy\" IS NULL OR \"DecidedBy\" <> \"RequestedBy\"");
            table.HasCheckConstraint("ck_artifact_break_glass_decision",
                "(\"Status\" IN ('Approved','Denied','Consumed')) = (\"DecidedBy\" IS NOT NULL AND \"DecidedAt\" IS NOT NULL) OR \"Status\" = 'Expired'");
            table.HasCheckConstraint("ck_artifact_break_glass_consumed", "(\"Status\" = 'Consumed') = (\"ConsumedAt\" IS NOT NULL AND \"DownloadSessionId\" IS NOT NULL)");
        });
        builder.Property(request => request.RequestedBy).HasConversion<UserIdConverter>().HasColumnType("uuid").IsRequired();
        builder.Property(request => request.DecidedBy).HasConversion<UserIdConverter>().HasColumnType("uuid");
        builder.Property(request => request.Reason).HasMaxLength(ArtifactBreakGlassRequest.MaximumReasonLength).IsRequired();
        builder.Property(request => request.DecisionReason).HasMaxLength(ArtifactBreakGlassRequest.MaximumReasonLength);
        builder.Property(request => request.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(request => request.CreatedAt).HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(request => request.ExpiresAt).HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(request => request.DecidedAt).HasColumnType("timestamp with time zone");
        builder.Property(request => request.ConsumedAt).HasColumnType("timestamp with time zone");
        builder.HasOne<BackupArtifact>().WithMany().HasForeignKey(request => new { request.ArtifactId, request.TenantId })
            .HasPrincipalKey(artifact => new { artifact.Id, artifact.TenantId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(request => request.RequestedBy).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(request => request.DecidedBy).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(request => new { request.TenantId, request.CreatedAt }).HasDatabaseName("ix_artifact_break_glass_tenant");
        builder.HasIndex(request => request.ExpiresAt).HasDatabaseName("ix_artifact_break_glass_expiry").HasFilter("\"Status\" IN ('Pending','Approved')");
    }
}
