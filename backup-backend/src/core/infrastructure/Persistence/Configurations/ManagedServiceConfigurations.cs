using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Infrastructure.Persistence.Conversions;
using BackupPlatform.ManagedService.Plans;
using BackupPlatform.ManagedService.Quota;
using BackupPlatform.ManagedService.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

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
        // A plan may only name a policy that exists; a policy a plan names cannot be deleted.
        builder.HasOne<QuotaPolicy>().WithMany().HasForeignKey(plan => plan.QuotaPolicyReference).HasPrincipalKey(policy => policy.Key).OnDelete(DeleteBehavior.Restrict);
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

/// <summary>
/// Quota policies are global rows, never deleted. Every CHECK is written so that a NULL cannot satisfy it by accident: the columns are NOT NULL,
/// and the one JSON check uses CASE so that a non-array value is false instead of an evaluation error or an unknown.
/// </summary>
internal sealed class QuotaPolicyConfiguration : IEntityTypeConfiguration<QuotaPolicy>
{
    public void Configure(EntityTypeBuilder<QuotaPolicy> builder)
    {
        builder.ToTable("quota_policies", table =>
        {
            table.HasCheckConstraint("ck_quota_policies_id_nonempty", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("ck_quota_policies_key", "\"Key\" ~ '^[a-z0-9][a-z0-9._-]{0,63}$'");
            table.HasCheckConstraint("ck_quota_policies_name_nonblank", "\"Name\" ~ '[^[:space:]]' AND \"NormalizedName\" ~ '[^[:space:]]'");
            table.HasCheckConstraint("ck_quota_policies_version", "\"Version\" >= 1");
            table.HasCheckConstraint("ck_quota_policies_accounting_mode", "\"AccountingMode\" IN (1, 2)");
            table.HasCheckConstraint("ck_quota_policies_enforcement_mode", "\"EnforcementMode\" IN (1, 2, 3)");
            table.HasCheckConstraint("ck_quota_policies_thresholds",
                "\"WarningThresholdPercent\" BETWEEN 1 AND 99 AND \"CriticalThresholdPercent\" BETWEEN 2 AND 100 AND \"CriticalThresholdPercent\" > \"WarningThresholdPercent\"");
            table.HasCheckConstraint("ck_quota_policies_scopes",
                "CASE WHEN jsonb_typeof(\"IncludedStorageScopes\") = 'array' THEN jsonb_array_length(\"IncludedStorageScopes\") BETWEEN 1 AND 8 ELSE FALSE END");
            table.HasCheckConstraint("ck_quota_policies_default_enabled", "NOT \"IsDefault\" OR \"Enabled\"");
        });
        builder.HasKey(policy => policy.Id);
        builder.Property(policy => policy.Id).HasConversion<QuotaPolicyIdConverter>().HasColumnType("uuid").ValueGeneratedNever().IsRequired();
        builder.Property(policy => policy.Key).HasMaxLength(64).IsRequired();
        builder.Property(policy => policy.Name).HasMaxLength(100).IsRequired();
        builder.Property(policy => policy.NormalizedName).HasMaxLength(100).IsRequired();
        builder.Property(policy => policy.Version).IsRequired();
        builder.Property(policy => policy.AccountingMode).HasConversion<int>().IsRequired();
        builder.Property(policy => policy.IncludedStorageScopes)
            .HasConversion(
                new ValueConverter<QuotaScopeRule, string>(rule => rule.ToJson(), json => QuotaScopeRule.FromJson(json)),
                new ValueComparer<QuotaScopeRule>((left, right) => left!.Equals(right), rule => rule.GetHashCode(), rule => rule))
            .HasColumnType("jsonb").IsRequired();
        builder.Property(policy => policy.EnforcementMode).HasConversion<int>().IsRequired();
        builder.Property(policy => policy.WarningThresholdPercent).IsRequired();
        builder.Property(policy => policy.CriticalThresholdPercent).IsRequired();
        builder.Property(policy => policy.Enabled).IsRequired();
        builder.Property(policy => policy.IsDefault).IsRequired();
        builder.Property(policy => policy.CreatedAt).HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(policy => policy.UpdatedAt).HasColumnType("timestamp with time zone").IsRequired();
        // The key is what plans reference, so it is an alternate key (unique, and a valid foreign-key target).
        builder.HasAlternateKey(policy => policy.Key).HasName("ux_quota_policies_key");
        builder.HasIndex(policy => policy.NormalizedName).IsUnique().HasDatabaseName("ux_quota_policies_normalized_name");
        // At most one default: a unique index over a constant-true column, restricted to the default row.
        builder.HasIndex(policy => policy.IsDefault).IsUnique().HasDatabaseName("ux_quota_policies_one_default").HasFilter("\"IsDefault\"");
        builder.Property<uint>("xmin").IsRowVersion();
    }
}

/// <summary>One row per tenant (the primary key). Written by raw, idempotent statements under the tenant lock; never the source of truth.</summary>
internal sealed class TenantStorageUsageConfiguration : IEntityTypeConfiguration<TenantStorageUsage>
{
    public void Configure(EntityTypeBuilder<TenantStorageUsage> builder)
    {
        builder.ToTable("tenant_storage_usage", table =>
        {
            table.HasCheckConstraint("ck_tenant_storage_usage_tenant", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("ck_tenant_storage_usage_used", "\"UsedBytes\" >= 0");
            table.HasCheckConstraint("ck_tenant_storage_usage_reserved", "\"ReservedBytes\" >= 0");
            table.HasCheckConstraint("ck_tenant_storage_usage_accounting_mode", "\"AccountingMode\" IN (1, 2)");
            table.HasCheckConstraint("ck_tenant_storage_usage_policy_version", "\"PolicyVersion\" >= 1");
            table.HasCheckConstraint("ck_tenant_storage_usage_revision", "\"Revision\" >= 0");
        });
        builder.HasKey(usage => usage.TenantId);
        builder.Property(usage => usage.TenantId).HasConversion<TenantIdConverter>().HasColumnType("uuid").ValueGeneratedNever().IsRequired();
        builder.Property(usage => usage.UsedBytes).HasColumnType("bigint").IsRequired();
        builder.Property(usage => usage.ReservedBytes).HasColumnType("bigint").IsRequired();
        builder.Property(usage => usage.CalculatedAt).HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(usage => usage.AccountingMode).HasConversion<int>().IsRequired();
        builder.Property(usage => usage.PolicyId).HasConversion<QuotaPolicyIdConverter>().HasColumnType("uuid").IsRequired();
        builder.Property(usage => usage.PolicyVersion).IsRequired();
        builder.Property(usage => usage.Revision).HasColumnType("bigint").IsRequired();
        builder.HasOne<Tenant>().WithMany().HasForeignKey(usage => usage.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<QuotaPolicy>().WithMany().HasForeignKey(usage => usage.PolicyId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(usage => usage.PolicyId);
        builder.HasIndex(usage => usage.CalculatedAt).HasDatabaseName("ix_tenant_storage_usage_calculated");
    }
}

/// <summary>
/// Unreleased rows add up to a tenant's ReservedBytes. The run id is the key, so admitting or releasing the same run twice changes nothing.
/// There is deliberately no foreign key to the run: the reservation is made before the run row exists, in the same agent-locked flow.
/// </summary>
internal sealed class QuotaReservationConfiguration : IEntityTypeConfiguration<QuotaReservation>
{
    public void Configure(EntityTypeBuilder<QuotaReservation> builder)
    {
        builder.ToTable("quota_reservations", table =>
        {
            table.HasCheckConstraint("ck_quota_reservations_ids", "\"RunId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("ck_quota_reservations_bytes", "\"Bytes\" > 0");
            table.HasCheckConstraint("ck_quota_reservations_period", "\"ExpiresAt\" > \"CreatedAt\"");
            table.HasCheckConstraint("ck_quota_reservations_release", "(\"ReleasedAt\" IS NULL) = (\"ReleaseReason\" IS NULL)");
            table.HasCheckConstraint("ck_quota_reservations_reason", "\"ReleaseReason\" IS NULL OR \"ReleaseReason\" IN ('run_finished', 'expired', 'run_not_created', 'orphaned')");
        });
        builder.HasKey(reservation => reservation.RunId);
        builder.Property(reservation => reservation.RunId).ValueGeneratedNever().IsRequired();
        builder.Property(reservation => reservation.TenantId).HasConversion<TenantIdConverter>().HasColumnType("uuid").IsRequired();
        builder.Property(reservation => reservation.Bytes).HasColumnType("bigint").IsRequired();
        builder.Property(reservation => reservation.CreatedAt).HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(reservation => reservation.ExpiresAt).HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(reservation => reservation.ReleasedAt).HasColumnType("timestamp with time zone");
        builder.Property(reservation => reservation.ReleaseReason).HasMaxLength(32);
        builder.HasOne<Tenant>().WithMany().HasForeignKey(reservation => reservation.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(reservation => reservation.TenantId).HasDatabaseName("ix_quota_reservations_active").HasFilter("\"ReleasedAt\" IS NULL");
        builder.HasIndex(reservation => reservation.ExpiresAt).HasDatabaseName("ix_quota_reservations_expiry").HasFilter("\"ReleasedAt\" IS NULL");
    }
}
