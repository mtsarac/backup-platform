using BackupPlatform.Infrastructure.Persistence.Conversions;
using BackupPlatform.Domain.Backups;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BackupPlatform.Infrastructure.Persistence.Configurations;

internal sealed class UploadBandwidthLimitConfiguration : IEntityTypeConfiguration<UploadBandwidthLimit>
{
    public void Configure(EntityTypeBuilder<UploadBandwidthLimit> builder)
    {
        builder.ToTable("upload_bandwidth_limits", table => table.HasCheckConstraint("ck_upload_bandwidth_limits_range",
            $"\"BytesPerSecond\" BETWEEN {UploadBandwidth.MinimumBytesPerSecond} AND {UploadBandwidth.MaximumBytesPerSecond}"));
        builder.Property(limit => limit.TenantId).HasConversion<TenantIdConverter>().IsRequired();
        builder.Property(limit => limit.Scope).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.HasKey(limit => new { limit.TenantId, limit.Scope, limit.ScopeId });
    }
}

internal sealed class UploadBandwidthGrantConfiguration : IEntityTypeConfiguration<UploadBandwidthGrant>
{
    public void Configure(EntityTypeBuilder<UploadBandwidthGrant> builder)
    {
        builder.ToTable("upload_bandwidth_grants", table => table.HasCheckConstraint("ck_upload_bandwidth_grants_rate", "\"BytesPerSecond\" >= 0"));
        builder.Property(grant => grant.TenantId).HasConversion<TenantIdConverter>().IsRequired();
        builder.HasKey(grant => grant.AttemptId);
        // The tenant's live grants are read, summed and pruned together on every renewal.
        builder.HasIndex(grant => new { grant.TenantId, grant.ExpiresAt });
    }
}
