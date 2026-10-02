using BackupPlatform.Agents.Enrollment;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Infrastructure.Persistence.Conversions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BackupPlatform.Infrastructure.Persistence.Configurations;

internal sealed class TenantAgentEnrollmentPolicyConfiguration : IEntityTypeConfiguration<TenantAgentEnrollmentPolicy>
{
    public void Configure(EntityTypeBuilder<TenantAgentEnrollmentPolicy> builder)
    {
        builder.ToTable("tenant_agent_enrollment_policies", table =>
        {
            table.HasCheckConstraint("ck_tenant_agent_enrollment_policies_tenant_id", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
        });
        builder.HasKey(policy => policy.TenantId);
        builder.Property(policy => policy.TenantId)
            .HasConversion<TenantIdConverter>().HasColumnType("uuid").ValueGeneratedNever().IsRequired();
        builder.Property(policy => policy.IsEnabled).IsRequired();
        builder.Property(policy => policy.ChangedBy).HasConversion<UserIdConverter>().HasColumnType("uuid").IsRequired();
        builder.Property(policy => policy.ChangedAt).HasColumnType("timestamp with time zone").IsRequired();
        builder.HasOne<Tenant>().WithMany().HasForeignKey(policy => policy.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(policy => policy.ChangedBy).OnDelete(DeleteBehavior.Restrict);
        builder.Property<uint>("xmin").IsRowVersion();
    }
}
