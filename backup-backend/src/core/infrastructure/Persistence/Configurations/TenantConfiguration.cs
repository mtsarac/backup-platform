using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Infrastructure.Persistence.Conversions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BackupPlatform.Infrastructure.Persistence.Configurations;

internal sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.ToTable("tenants", table =>
        {
            table.HasCheckConstraint("ck_tenants_id_nonempty", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("ck_tenants_name_nonblank", "\"Name\" ~ '[^[:space:]]'");
        });
        builder.HasKey(tenant => tenant.Id);
        builder.Property(tenant => tenant.Id)
            .HasConversion<TenantIdConverter>()
            .HasColumnType("uuid")
            .ValueGeneratedNever()
            .IsRequired();
        builder.Property(tenant => tenant.Name).IsRequired();
        builder.Property<uint>("xmin").IsRowVersion();
    }
}
