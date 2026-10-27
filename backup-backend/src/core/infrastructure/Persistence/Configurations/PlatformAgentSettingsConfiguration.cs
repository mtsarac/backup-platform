using BackupPlatform.Agents.Enrollment;
using BackupPlatform.Infrastructure.Persistence.Conversions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BackupPlatform.Infrastructure.Persistence.Configurations;

internal sealed class PlatformAgentSettingsConfiguration : IEntityTypeConfiguration<PlatformAgentSettings>
{
    public void Configure(EntityTypeBuilder<PlatformAgentSettings> builder)
    {
        builder.ToTable("platform_agent_settings", table =>
        {
            table.HasCheckConstraint("ck_platform_agent_settings_singleton", "\"Id\" = 1");
        });
        builder.HasKey(settings => settings.Id);
        builder.Property(settings => settings.Id).ValueGeneratedNever();
        builder.Property(settings => settings.ServerUrl).HasMaxLength(PlatformAgentSettings.MaximumServerUrlLength);
        builder.Property(settings => settings.EnrollmentTokenLifetimeMinutes).IsRequired();
        builder.Property(settings => settings.UpdatedBy).HasConversion<UserIdConverter>().HasColumnType("uuid").IsRequired();
        builder.Property(settings => settings.UpdatedAt).HasColumnType("timestamp with time zone").IsRequired();
    }
}
