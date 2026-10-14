using BackupPlatform.Domain.Backups;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BackupPlatform.Infrastructure.Persistence.Configurations;

internal sealed class BackupStorageCleanupConfiguration : IEntityTypeConfiguration<BackupStorageCleanup>
{
    public void Configure(EntityTypeBuilder<BackupStorageCleanup> builder)
    {
        BackupMapping.Base(builder, "backup_storage_cleanups");
        builder.Property(cleanup => cleanup.JobId).IsRequired();
        builder.Property(cleanup => cleanup.RunId).IsRequired();
        builder.Property(cleanup => cleanup.AttemptId).IsRequired();
        builder.Property(cleanup => cleanup.StorageProvider).HasMaxLength(64).IsRequired();
        builder.Property(cleanup => cleanup.StorageName).HasMaxLength(128).IsRequired();
        builder.Property(cleanup => cleanup.TargetIdentity).HasMaxLength(64).IsRequired();
        builder.Property(cleanup => cleanup.Key).HasMaxLength(1024).IsRequired();
        builder.Property(cleanup => cleanup.IsPartial).IsRequired();
        builder.Property(cleanup => cleanup.Status).HasConversion<int>().IsRequired();
        builder.Property(cleanup => cleanup.CreatedAt).IsRequired();
        builder.Property(cleanup => cleanup.LeaseToken);
        builder.Property(cleanup => cleanup.LeaseExpiresAt);
        builder.Property(cleanup => cleanup.CompletedAt);

        builder.ToTable("backup_storage_cleanups", table =>
        {
            table.HasCheckConstraint("ck_backup_storage_cleanups_status", "\"Status\" IN (1, 2)");
            table.HasCheckConstraint("ck_backup_storage_cleanups_lease", "(\"Status\" = 2 AND \"CompletedAt\" IS NOT NULL) OR (\"Status\" = 1 AND (\"LeaseToken\" IS NULL) = (\"LeaseExpiresAt\" IS NULL))");
        });

        builder.HasIndex(cleanup => new { cleanup.StorageProvider, cleanup.StorageName, cleanup.TargetIdentity }).IsUnique();
        builder.HasIndex(cleanup => new { cleanup.StorageProvider, cleanup.StorageName, cleanup.Status, cleanup.LeaseExpiresAt })
            .HasFilter("\"Status\" = 1");
    }
}
