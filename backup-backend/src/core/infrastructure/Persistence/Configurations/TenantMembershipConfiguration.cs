using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Infrastructure.Persistence.Conversions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BackupPlatform.Infrastructure.Persistence.Configurations;

internal sealed class TenantMembershipConfiguration : IEntityTypeConfiguration<TenantMembership>
{
    public void Configure(EntityTypeBuilder<TenantMembership> builder)
    {
        builder.ToTable("tenant_memberships", table =>
        {
            table.HasCheckConstraint("ck_tenant_memberships_tenant_id_nonempty", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("ck_tenant_memberships_user_id_nonempty", "\"UserId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("ck_tenant_memberships_role", "\"Role\" BETWEEN 1 AND 4");
        });
        builder.HasKey(membership => new { membership.TenantId, membership.UserId });
        builder.Property(membership => membership.TenantId)
            .HasConversion<TenantIdConverter>()
            .HasColumnType("uuid")
            .ValueGeneratedNever()
            .IsConcurrencyToken()
            .IsRequired();
        builder.Property(membership => membership.UserId)
            .HasConversion<UserIdConverter>()
            .HasColumnType("uuid")
            .ValueGeneratedNever()
            .IsRequired();
        builder.Property(membership => membership.Role).HasConversion<int>().IsRequired();
        builder.HasOne<Tenant>().WithMany()
            .HasForeignKey(membership => membership.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany()
            .HasForeignKey(membership => membership.UserId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.Property<uint>("xmin").IsRowVersion();
    }
}
