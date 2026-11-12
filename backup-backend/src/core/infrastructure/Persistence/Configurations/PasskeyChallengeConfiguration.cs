using BackupPlatform.Identity;
using BackupPlatform.Infrastructure.Persistence.Conversions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BackupPlatform.Infrastructure.Persistence.Configurations;

internal sealed class PasskeyChallengeConfiguration : IEntityTypeConfiguration<PasskeyChallenge>
{
    public void Configure(EntityTypeBuilder<PasskeyChallenge> builder)
    {
        builder.ToTable("passkey_challenges", table =>
        {
            table.HasCheckConstraint("ck_passkey_challenges_id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("ck_passkey_challenges_purpose", "\"Purpose\" ~ '^[a-z_]{1,32}$'");
        });
        builder.HasKey(challenge => challenge.Id);
        builder.Property(challenge => challenge.Id).ValueGeneratedNever();
        builder.Property(challenge => challenge.UserId).HasConversion<UserIdConverter>().HasColumnType("uuid");
        builder.Property(challenge => challenge.SessionId);
        builder.Property(challenge => challenge.Purpose).HasMaxLength(32).IsRequired();
        builder.Property(challenge => challenge.OptionsJson).IsRequired();
        builder.Property(challenge => challenge.CreatedAt).HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(challenge => challenge.ExpiresAt).HasColumnType("timestamp with time zone").IsRequired();
        builder.HasIndex(challenge => challenge.ExpiresAt);
    }
}
