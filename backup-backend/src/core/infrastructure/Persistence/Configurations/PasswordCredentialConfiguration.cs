using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Identity;
using BackupPlatform.Infrastructure.Persistence.Conversions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BackupPlatform.Infrastructure.Persistence.Configurations;

internal sealed class PasswordCredentialConfiguration : IEntityTypeConfiguration<PasswordCredential>
{
    public void Configure(EntityTypeBuilder<PasswordCredential> builder)
    {
        builder.ToTable("password_credentials", table =>
        {
            table.HasCheckConstraint("ck_password_credentials_user_id", "\"UserId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("ck_password_credentials_login", "\"LoginIdentifier\" ~ '^[A-Za-z0-9._-]{3,128}$'");
            table.HasCheckConstraint("ck_password_credentials_normalized_login", "\"NormalizedLoginIdentifier\" = upper(\"LoginIdentifier\" COLLATE \"C\")");
            table.HasCheckConstraint("ck_password_credentials_hash", "\"PasswordHash\" ~ '[^[:space:]]'");
        });
        builder.HasKey(credential => credential.UserId);
        builder.Property(credential => credential.UserId).HasConversion<UserIdConverter>().HasColumnType("uuid").ValueGeneratedNever();
        builder.Property(credential => credential.LoginIdentifier).HasMaxLength(128).IsRequired();
        builder.Property(credential => credential.NormalizedLoginIdentifier).HasMaxLength(128).IsRequired();
        builder.Property(credential => credential.PasswordHash).IsRequired();
        builder.HasIndex(credential => credential.NormalizedLoginIdentifier).IsUnique();
        builder.HasOne<User>().WithMany().HasForeignKey(credential => credential.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.Property<uint>("xmin").IsRowVersion();
    }
}
