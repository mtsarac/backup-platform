using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Identity;
using BackupPlatform.Infrastructure.Persistence.Conversions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BackupPlatform.Infrastructure.Persistence.Configurations;

internal sealed class RecoveryCodeConfiguration : IEntityTypeConfiguration<RecoveryCode>
{
    public void Configure(EntityTypeBuilder<RecoveryCode> builder)
    {
        builder.ToTable("recovery_codes", table =>
        {
            table.HasCheckConstraint("ck_recovery_codes_id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("ck_recovery_codes_user_id", "\"UserId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
        });
        builder.HasKey(code => code.Id);
        builder.Property(code => code.Id).ValueGeneratedNever();
        builder.Property(code => code.UserId).HasConversion<UserIdConverter>().HasColumnType("uuid").ValueGeneratedNever();
        builder.Property(code => code.CodeHash).HasMaxLength(128).IsRequired();
        builder.Property(code => code.CreatedAt).HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(code => code.RedeemedAt).HasColumnType("timestamp with time zone");
        builder.HasOne<User>().WithMany().HasForeignKey(code => code.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(code => code.UserId);
        builder.HasIndex(code => new { code.UserId, code.CodeHash });
    }
}
