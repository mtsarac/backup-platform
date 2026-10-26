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
        builder.HasKey(grant => grant.UploadId);
        // The tenant's live grants are read, summed and pruned together on every renewal.
        builder.HasIndex(grant => new { grant.TenantId, grant.ExpiresAt });
    }
}

internal sealed class BandwidthProfileConfiguration : IEntityTypeConfiguration<BandwidthProfile>
{
    public void Configure(EntityTypeBuilder<BandwidthProfile> builder)
    {
        builder.ToTable("bandwidth_profiles");
        builder.HasKey(profile => profile.Id);
        builder.Property(profile => profile.TenantId).HasConversion<TenantIdConverter>().IsRequired();
        builder.Property(profile => profile.Name).HasMaxLength(200).IsRequired();
        builder.Property(profile => profile.TimeZoneId).HasMaxLength(64).IsRequired();
        builder.HasIndex(profile => new { profile.TenantId, profile.Name }).IsUnique();
        builder.HasMany(profile => profile.Windows).WithOne().HasForeignKey(window => window.ProfileId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class TransferScheduleWindowConfiguration : IEntityTypeConfiguration<TransferScheduleWindow>
{
    public void Configure(EntityTypeBuilder<TransferScheduleWindow> builder)
    {
        builder.ToTable("transfer_schedule_windows", table => table.HasCheckConstraint("ck_transfer_schedule_windows_rate",
            $"\"BytesPerSecond\" = 0 OR \"BytesPerSecond\" BETWEEN {UploadBandwidth.MinimumBytesPerSecond} AND {UploadBandwidth.MaximumBytesPerSecond}"));
        builder.HasKey(window => window.Id);
        builder.Property(window => window.DayOfWeek).HasConversion<int>().IsRequired();
        builder.Property(window => window.StartTime).IsRequired();
        builder.Property(window => window.EndTime).IsRequired();
        builder.HasIndex(window => window.ProfileId);
    }
}

internal sealed class BandwidthProfileAssignmentConfiguration : IEntityTypeConfiguration<BandwidthProfileAssignment>
{
    public void Configure(EntityTypeBuilder<BandwidthProfileAssignment> builder)
    {
        builder.ToTable("bandwidth_profile_assignments");
        builder.Property(assignment => assignment.TenantId).HasConversion<TenantIdConverter>().IsRequired();
        builder.Property(assignment => assignment.Scope).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.HasKey(assignment => new { assignment.TenantId, assignment.Scope, assignment.ScopeId });
        builder.HasIndex(assignment => assignment.ProfileId);
    }
}
