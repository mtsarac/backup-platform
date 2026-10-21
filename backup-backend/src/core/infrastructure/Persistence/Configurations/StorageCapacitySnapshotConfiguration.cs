using BackupPlatform.Domain.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BackupPlatform.Infrastructure.Persistence.Configurations;

internal sealed class StorageCapacitySnapshotConfiguration : IEntityTypeConfiguration<StorageCapacitySnapshot>
{
    public void Configure(EntityTypeBuilder<StorageCapacitySnapshot> builder)
    {
        BackupMapping.Base(builder, "storage_capacity_snapshots");
        builder.ToTable("storage_capacity_snapshots", table =>
        {
            table.HasCheckConstraint("ck_storage_capacity_snapshots_status", "\"Status\" IN ('Available','Warning','Critical','CapacityUnavailable')");
        });
        builder.Property(snapshot => snapshot.ConnectionId).IsRequired();
        builder.Property(snapshot => snapshot.TotalBytes);
        builder.Property(snapshot => snapshot.UsedBytes);
        builder.Property(snapshot => snapshot.FreeBytes);
        builder.Property(snapshot => snapshot.Status).HasMaxLength(32).IsRequired();
        builder.Property(snapshot => snapshot.ReportedAt).HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(snapshot => snapshot.ErrorMessage).HasMaxLength(1024);
        builder.HasOne<StorageConnection>().WithMany().HasForeignKey(snapshot => snapshot.ConnectionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(snapshot => new { snapshot.TenantId, snapshot.ConnectionId, snapshot.ReportedAt })
            .HasDatabaseName("ix_storage_capacity_snapshots_connection");
    }
}
