using BackupPlatform.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BackupPlatform.Infrastructure.Persistence.Configurations;

internal sealed class LoginThrottleEntryConfiguration : IEntityTypeConfiguration<LoginThrottleEntry>
{
    public void Configure(EntityTypeBuilder<LoginThrottleEntry> builder)
    {
        builder.ToTable("login_throttle_entries");
        builder.HasKey(entry => entry.Identifier);
        builder.Property(entry => entry.Identifier).HasMaxLength(256).ValueGeneratedNever();
        builder.Property(entry => entry.Failures).IsRequired();
        builder.Property(entry => entry.WindowStart).HasColumnType("timestamp with time zone").IsRequired();
        builder.HasIndex(entry => entry.WindowStart);
    }
}
