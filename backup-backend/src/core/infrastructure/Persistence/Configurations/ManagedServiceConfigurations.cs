using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Infrastructure.Persistence.Conversions;
using BackupPlatform.ManagedService.Plans;
using BackupPlatform.ManagedService.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BackupPlatform.Infrastructure.Persistence.Configurations;

/// <summary>Plans are global rows. They are never deleted by the application and a foreign key protects any plan a subscription used.</summary>
internal sealed class PlanConfiguration : IEntityTypeConfiguration<Plan>
{
    public void Configure(EntityTypeBuilder<Plan> builder)
    {
        builder.ToTable("plans", table =>
        {
            table.HasCheckConstraint("ck_plans_id_nonempty", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("ck_plans_name_nonblank", "\"Name\" ~ '[^[:space:]]' AND \"NormalizedName\" ~ '[^[:space:]]'");
            table.HasCheckConstraint("ck_plans_storage_limit", "\"StorageLimitBytes\" IS NULL OR (\"StorageLimitBytes\" > 0 AND \"StorageLimitBytes\" <= 1000000000000000)");
            table.HasCheckConstraint("ck_plans_system_key", "\"SystemKey\" IS NULL OR \"IsSystem\"");
            table.HasCheckConstraint("ck_plans_policy_configuration_version", "\"PolicyConfigurationVersion\" >= 1");
        });
        builder.HasKey(plan => plan.Id);
        builder.Property(plan => plan.Id).HasConversion<PlanIdConverter>().HasColumnType("uuid").ValueGeneratedNever().IsRequired();
        builder.Property(plan => plan.Name).HasMaxLength(100).IsRequired();
        builder.Property(plan => plan.NormalizedName).HasMaxLength(100).IsRequired();
        builder.Property(plan => plan.Enabled).IsRequired();
        builder.Property(plan => plan.StorageLimitBytes).HasColumnType("bigint");
        builder.Property(plan => plan.QuotaPolicyReference).HasMaxLength(64);
        builder.Property(plan => plan.PolicyConfigurationVersion).IsRequired();
        builder.Property(plan => plan.IsSystem).IsRequired();
        builder.Property(plan => plan.SystemKey).HasMaxLength(64);
        builder.Property(plan => plan.CreatedAt).HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(plan => plan.UpdatedAt).HasColumnType("timestamp with time zone").IsRequired();
        builder.Ignore(plan => plan.IsUnlimited);
        builder.HasIndex(plan => plan.NormalizedName).IsUnique().HasDatabaseName("ux_plans_normalized_name");
        builder.HasIndex(plan => plan.SystemKey).IsUnique().HasDatabaseName("ux_plans_system_key").HasFilter("\"SystemKey\" IS NOT NULL");
        builder.Property<uint>("xmin").IsRowVersion();
    }
}

/// <summary>
/// Subscription history. One open (Active or Suspended) row per tenant is a database invariant, enforced by a partial unique
/// index, so two writers on different nodes can never both succeed even if an application lock were bypassed.
/// </summary>
internal sealed class TenantSubscriptionConfiguration : IEntityTypeConfiguration<TenantSubscription>
{
    public void Configure(EntityTypeBuilder<TenantSubscription> builder)
    {
        builder.ToTable("tenant_subscriptions", table =>
        {
            table.HasCheckConstraint("ck_tenant_subscriptions_id_nonempty", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"PlanId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("ck_tenant_subscriptions_status", "\"Status\" IN (1, 2, 3, 4)");
            table.HasCheckConstraint("ck_tenant_subscriptions_period", "\"EndsAt\" IS NULL OR \"EndsAt\" >= \"StartsAt\"");
            table.HasCheckConstraint("ck_tenant_subscriptions_closed_has_end", "\"Status\" NOT IN (3, 4) OR \"EndsAt\" IS NOT NULL");
            table.HasCheckConstraint(
                "ck_tenant_subscriptions_override",
                "(\"StorageLimitOverrideKind\" = 0 AND \"StorageLimitOverrideBytes\" IS NULL) " +
                "OR (\"StorageLimitOverrideKind\" = 1 AND \"StorageLimitOverrideBytes\" IS NOT NULL AND \"StorageLimitOverrideBytes\" > 0 AND \"StorageLimitOverrideBytes\" <= 1000000000000000) " +
                "OR (\"StorageLimitOverrideKind\" = 2 AND \"StorageLimitOverrideBytes\" IS NULL)");
        });
        builder.HasKey(subscription => subscription.Id);
        builder.Property(subscription => subscription.Id).HasConversion<SubscriptionIdConverter>().HasColumnType("uuid").ValueGeneratedNever().IsRequired();
        builder.Property(subscription => subscription.TenantId).HasConversion<TenantIdConverter>().HasColumnType("uuid").IsRequired();
        builder.Property(subscription => subscription.PlanId).HasConversion<PlanIdConverter>().HasColumnType("uuid").IsRequired();
        builder.Property(subscription => subscription.Status).HasConversion<int>().IsRequired();
        builder.Property(subscription => subscription.StartsAt).HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(subscription => subscription.EndsAt).HasColumnType("timestamp with time zone");
        builder.Property(subscription => subscription.StorageLimitOverrideKind).HasConversion<int>().IsRequired();
        builder.Property(subscription => subscription.StorageLimitOverrideBytes).HasColumnType("bigint");
        builder.Property(subscription => subscription.AssignedByUserId).HasConversion<UserIdConverter>().HasColumnType("uuid");
        builder.Property(subscription => subscription.CreatedAt).HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(subscription => subscription.UpdatedAt).HasColumnType("timestamp with time zone").IsRequired();
        builder.Ignore(subscription => subscription.Override);
        builder.Ignore(subscription => subscription.IsOpen);
        builder.HasOne<Tenant>().WithMany().HasForeignKey(subscription => subscription.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Plan>().WithMany().HasForeignKey(subscription => subscription.PlanId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(subscription => subscription.AssignedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(subscription => subscription.TenantId).IsUnique().HasDatabaseName("ux_tenant_subscriptions_one_open_per_tenant")
            .HasFilter("\"Status\" IN (1, 2)");
        builder.HasIndex(subscription => new { subscription.TenantId, subscription.CreatedAt });
        builder.HasIndex(subscription => subscription.PlanId);
        builder.Property<uint>("xmin").IsRowVersion();
    }
}
