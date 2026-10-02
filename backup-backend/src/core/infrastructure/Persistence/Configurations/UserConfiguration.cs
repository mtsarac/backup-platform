using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Infrastructure.Persistence.Conversions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BackupPlatform.Infrastructure.Persistence.Configurations;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users", table =>
        {
            table.HasCheckConstraint("ck_users_id_nonempty", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("ck_users_display_name_nonblank", "\"DisplayName\" ~ '[^[:space:]]'");
        });
        builder.HasKey(user => user.Id);
        builder.Property(user => user.Id)
            .HasConversion<UserIdConverter>()
            .HasColumnType("uuid")
            .ValueGeneratedNever()
            .IsRequired();
        builder.Property(user => user.DisplayName).IsRequired();
        builder.Property<uint>("xmin").IsRowVersion();
    }
}
