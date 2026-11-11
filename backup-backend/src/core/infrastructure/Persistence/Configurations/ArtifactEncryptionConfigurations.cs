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

