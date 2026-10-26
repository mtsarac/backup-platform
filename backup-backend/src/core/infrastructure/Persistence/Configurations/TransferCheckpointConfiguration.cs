using BackupPlatform.Domain.Backups;
using BackupPlatform.Infrastructure.Persistence.Conversions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BackupPlatform.Infrastructure.Persistence.Configurations;

internal sealed class TransferCheckpointConfiguration : IEntityTypeConfiguration<TransferCheckpoint>
{
    public void Configure(EntityTypeBuilder<TransferCheckpoint> builder)
    {
        builder.ToTable("transfer_checkpoints", table => table.HasCheckConstraint("ck_transfer_checkpoint_offset", "\"VerifiedByteOffset\" >= 0"));
        builder.HasKey(x => new { x.TenantId, x.AttemptId, x.StorageProvider, x.DestinationId });
        builder.Property(x => x.TenantId).HasConversion<TenantIdConverter>();
        builder.Property(x => x.StorageProvider).HasMaxLength(32);
        builder.Property(x => x.ProviderState).HasMaxLength(2097152);
        builder.HasOne<BackupRunAttempt>().WithMany().HasForeignKey(x => new { x.AttemptId, x.TenantId })
            .HasPrincipalKey(x => new { x.Id, x.TenantId }).OnDelete(DeleteBehavior.Cascade);
    }
}
