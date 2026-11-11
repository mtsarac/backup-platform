using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Identity;
using BackupPlatform.Infrastructure.Persistence.Conversions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BackupPlatform.Infrastructure.Persistence.Configurations;

internal sealed class TotpCredentialConfiguration : IEntityTypeConfiguration<TotpCredential>
{
    public void Configure(EntityTypeBuilder<TotpCredential> builder)
    {
        builder.ToTable("totp_credentials", table =>
        {
            table.HasCheckConstraint("ck_totp_credentials_user_id", "\"UserId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
        });
        builder.HasKey(credential => credential.UserId);
        builder.Property(credential => credential.UserId).HasConversion<UserIdConverter>().HasColumnType("uuid").ValueGeneratedNever();
        builder.Property(credential => credential.EncryptedSecret).HasMaxLength(1024).IsRequired();
        builder.Property(credential => credential.EnrolledAt).HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(credential => credential.LastUsedEpochStep).HasColumnType("bigint");
        builder.HasOne<User>().WithMany().HasForeignKey(credential => credential.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
