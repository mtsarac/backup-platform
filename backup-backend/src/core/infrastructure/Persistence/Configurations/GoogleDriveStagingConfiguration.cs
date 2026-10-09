using BackupPlatform.Domain.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BackupPlatform.Infrastructure.Persistence.Configurations;

internal sealed class GoogleDriveStagingConfiguration : IEntityTypeConfiguration<GoogleDriveStagingReservation>
{
    public void Configure(EntityTypeBuilder<GoogleDriveStagingReservation> builder)
    {
        builder.ToTable("google_drive_staging_reservations", table =>
            table.HasCheckConstraint("ck_google_drive_staging_bytes", "\"ExpectedBytes\" > 0 AND \"WrittenBytes\" >= 0 AND \"WrittenBytes\" <= \"ExpectedBytes\""));
        builder.HasKey(row => row.Id);
        builder.Property(row => row.VolumeId).HasMaxLength(128).IsRequired();
        builder.Property(row => row.NodeId).HasMaxLength(128).IsRequired();
        builder.Property(row => row.DirectoryPath).HasMaxLength(4096).IsRequired();
        builder.Property(row => row.FileName).HasMaxLength(64).IsRequired();
        builder.HasIndex(row => new { row.VolumeId, row.NodeId, row.LeaseExpiresAt })
            .HasDatabaseName("ix_google_drive_staging_cleanup");
    }
}
