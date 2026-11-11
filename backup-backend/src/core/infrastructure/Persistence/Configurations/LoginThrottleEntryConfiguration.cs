using BackupPlatform.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BackupPlatform.Infrastructure.Persistence.Configurations;

internal sealed class LoginThrottleEntryConfiguration : IEntityTypeConfiguration<LoginThrottleEntry>
{
    public void Configure(EntityTypeBuilder<LoginThrottleEntry> builder)
    {
        builder.ToTable("login_throttle_entries");
        builder.HasKey(entry => new { entry.Kind, entry.Scope, entry.Identifier });
        builder.Property(entry => entry.Kind).HasMaxLength(64);
        builder.Property(entry => entry.Scope).HasMaxLength(7);
        builder.Property(entry => entry.Identifier).HasMaxLength(256);
        builder.Property(entry => entry.Failures).IsRequired();
        builder.Property(entry => entry.LastFailureAt).HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(entry => entry.LockedUntil).HasColumnType("timestamp with time zone").IsRequired();
        builder.HasIndex(entry => entry.LastFailureAt);
    }
}
