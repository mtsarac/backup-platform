using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Identity;
using BackupPlatform.Infrastructure.Persistence.Conversions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BackupPlatform.Infrastructure.Persistence.Configurations;

internal sealed class PasskeyCredentialConfiguration : IEntityTypeConfiguration<PasskeyCredential>
{
    public void Configure(EntityTypeBuilder<PasskeyCredential> builder)
    {
        builder.ToTable("passkey_credentials", table =>
        {
            table.HasCheckConstraint("ck_passkey_credentials_id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("ck_passkey_credentials_user_id", "\"UserId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("ck_passkey_credentials_name", "\"Name\" ~ '[^[:space:]]'");
        });
        builder.HasKey(credential => credential.Id);
        builder.Property(credential => credential.Id).ValueGeneratedNever();
        builder.Property(credential => credential.UserId).HasConversion<UserIdConverter>().HasColumnType("uuid").ValueGeneratedNever();
        builder.Property(credential => credential.Name).HasMaxLength(128).IsRequired();
        builder.Property(credential => credential.CredentialId).IsRequired();
        builder.Property(credential => credential.PublicKey).IsRequired();
        builder.Property(credential => credential.SignatureCounter).IsRequired();
        builder.Property(credential => credential.AaGuid);
        builder.Property(credential => credential.CreatedAt).HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(credential => credential.LastUsedAt).HasColumnType("timestamp with time zone");
        builder.HasIndex(credential => credential.UserId);
        builder.HasIndex(credential => credential.CredentialId).IsUnique();
        builder.HasOne<User>().WithMany().HasForeignKey(credential => credential.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}
