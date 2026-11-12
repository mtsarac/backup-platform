using BackupPlatform.Identity;
using BackupPlatform.Infrastructure.Persistence.Conversions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BackupPlatform.Infrastructure.Persistence.Configurations;

internal sealed class AccountEmailTokenConfiguration : IEntityTypeConfiguration<AccountEmailToken>
{
    public void Configure(EntityTypeBuilder<AccountEmailToken> builder)
    {
        builder.ToTable("account_email_tokens");
        builder.HasKey(token => token.Id);
        builder.Property(token => token.Id).ValueGeneratedNever();
        builder.Property(token => token.UserId).HasConversion<UserIdConverter>().HasColumnType("uuid").IsRequired();
        builder.Property(token => token.Purpose).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(token => token.TokenHash).HasMaxLength(64).IsRequired();
        builder.Property(token => token.NormalizedEmail).HasMaxLength(254).IsRequired();
        builder.Property(token => token.CreatedAt).IsRequired();
        builder.Property(token => token.ExpiresAt).IsRequired();
        builder.Property(token => token.ConsumedAt);
        builder.HasIndex(token => token.TokenHash).IsUnique();
        builder.HasIndex(token => new { token.UserId, token.Purpose });
        builder.HasOne<PasswordCredential>().WithMany().HasForeignKey(token => token.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
