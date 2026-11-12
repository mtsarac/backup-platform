using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Identity;
using BackupPlatform.Infrastructure.Persistence.Conversions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BackupPlatform.Infrastructure.Persistence.Configurations;

internal sealed class KnownSignInConfiguration : IEntityTypeConfiguration<KnownSignIn>
{
    public void Configure(EntityTypeBuilder<KnownSignIn> builder)
    {
        builder.ToTable("known_sign_ins");
        builder.HasKey(item => new { item.UserId, item.DeviceHash, item.IpAddress });
        builder.Property(item => item.UserId).HasConversion<UserIdConverter>().HasColumnType("uuid");
        builder.Property(item => item.DeviceHash).HasMaxLength(64);
        builder.Property(item => item.IpAddress).HasMaxLength(64);
        builder.Property(item => item.FirstSeenAt).IsRequired();
        builder.Property(item => item.LastSeenAt).IsRequired();
        builder.HasOne<User>().WithMany().HasForeignKey(item => item.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
