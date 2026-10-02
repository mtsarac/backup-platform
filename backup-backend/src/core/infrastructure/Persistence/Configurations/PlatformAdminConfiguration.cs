using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Infrastructure.Persistence.Conversions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BackupPlatform.Infrastructure.Persistence.Configurations;

internal sealed class PlatformAdminConfiguration : IEntityTypeConfiguration<PlatformAdmin>
{
    public void Configure(EntityTypeBuilder<PlatformAdmin> builder)
    {
        builder.ToTable("platform_admins", table =>
        {
            table.HasCheckConstraint("ck_platform_admins_user_id_nonempty", "\"UserId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
        });
        builder.HasKey(admin => admin.UserId);
        builder.Property(admin => admin.UserId)
            .HasConversion<UserIdConverter>()
            .HasColumnType("uuid")
            .ValueGeneratedNever()
            .IsRequired();
        builder.HasOne<User>().WithMany()
            .HasForeignKey(admin => admin.UserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
