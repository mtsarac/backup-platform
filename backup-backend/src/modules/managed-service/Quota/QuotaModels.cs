using BackupPlatform.Application.Platform;
using BackupPlatform.Domain.Identity;
using BackupPlatform.ManagedService.Entitlement;

namespace BackupPlatform.ManagedService.Quota;

public sealed record QuotaPolicyView(
    QuotaPolicyId Id, string Key, string Name, int Version, QuotaAccountingMode AccountingMode, QuotaScopeRule IncludedStorageScopes,
    QuotaEnforcementMode EnforcementMode, int WarningThresholdPercent, int CriticalThresholdPercent, bool Enabled, bool IsDefault,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static QuotaPolicyView From(QuotaPolicy policy) => new(
        policy.Id, policy.Key, policy.Name, policy.Version, policy.AccountingMode, policy.IncludedStorageScopes, policy.EnforcementMode,
        policy.WarningThresholdPercent, policy.CriticalThresholdPercent, policy.Enabled, policy.IsDefault, policy.CreatedAt, policy.UpdatedAt);
}

/// <summary>The projection as stored, with the policy it was computed under.</summary>
public sealed record TenantStorageUsageView(
    TenantId TenantId, long UsedBytes, long ReservedBytes, DateTimeOffset CalculatedAt, QuotaAccountingMode AccountingMode,
    QuotaPolicyId PolicyId, string PolicyKey, int PolicyVersion, long Revision);

/// <summary>
/// What a reconcile or rebuild found. <see cref="Drifted"/> is true when the stored projection disagreed with the sources (or was missing or
/// stale), in which case it was corrected; the Previous* members describe what was stored before (null when there was no projection).
/// </summary>
public sealed record UsageReconciliation(
    TenantId TenantId, bool Drifted, long? PreviousUsedBytes, long? PreviousReservedBytes, int? PreviousPolicyVersion,
    long UsedBytes, long ReservedBytes, QuotaPolicyId PolicyId, int PolicyVersion, int ReservationsReleased);

/// <summary>Filters of the cross-tenant usage list. Each is optional; all given ones must match.</summary>
public sealed record StorageUsageFilter(string? Search, QuotaState? State, EntitlementStatus? SubscriptionStatus);

/// <summary>
/// One tenant of the cross-tenant usage list. A tenant without a stored projection has <see cref="HasUsage"/> false and its usage figures are
/// not meaningful (they are zero, never a computed answer). <see cref="UsageCurrent"/> is false when the stored projection was computed under
/// another definition of usage than the tenant's current policy; the figures are then the last known ones and the next read, a policy-change
/// recalculation or the maintenance pass replaces them.
/// </summary>
public sealed record TenantStorageUsageListItem(
    TenantId TenantId, string TenantName, TenantQuotaStatus Quota, bool HasUsage, bool UsageCurrent, DateTimeOffset? CalculatedAt);

public sealed record StorageUsageMaintenanceReport(
    int ReservationsExpired, int ProjectionsInitialized, int ProjectionsReconciled, int DriftCorrected, IReadOnlyList<Exception> Errors);

/// <summary>
/// Tenant storage usage: reads, and the operations that recompute it from the sources. Callers authorize first; implementations read and
/// write by explicit tenant id. A null result means the tenant does not exist.
/// </summary>
public interface IStorageUsageService
{
    /// <summary>The projection; a tenant that has none yet gets one computed from the sources first, so a reader never sees a missing or stale number.</summary>
    Task<TenantStorageUsageView?> GetUsageAsync(TenantId tenantId, CancellationToken cancellationToken);

    /// <summary>
    /// One page of every tenant's standing, read in bulk from the stored projections (no per-tenant query, and nothing is recomputed or written by
    /// a list). Filtering by quota state or subscription status is applied to the derived values before paging.
    /// </summary>
    Task<PagedResult<TenantStorageUsageListItem>> ListStorageUsageAsync(
        StorageUsageFilter filter, int page, int pageSize, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Effective limit (M1 entitlement) + policy + usage. Same freshness guarantee as <see cref="GetUsageAsync"/>.</summary>
    Task<TenantQuotaStatus?> GetQuotaStatusAsync(TenantId tenantId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Recomputes from the sources, compares with the projection and corrects a difference. Safe to repeat. With an <paramref name="actor"/> a
    /// correction is audited; without one (the maintenance loop) it is logged.
    /// </summary>
    Task<UsageReconciliation?> ReconcileAsync(TenantId tenantId, UserId? actor, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>A PlatformAdmin's complete recomputation: the projection is replaced from the sources whether or not it differed, and the operation is audited.</summary>
    Task<UsageReconciliation?> RebuildAsync(TenantId tenantId, UserId actor, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>One bounded maintenance pass: expire reservations, compute missing projections, reconcile the oldest ones.</summary>
    Task<StorageUsageMaintenanceReport> RunMaintenanceAsync(DateTimeOffset now, CancellationToken cancellationToken);
}

public sealed record QuotaPolicyChange(QuotaPolicyView Policy, int TenantsRecalculated);

public interface IQuotaPolicyStore
{
    Task<IReadOnlyList<QuotaPolicyView>> ListAsync(CancellationToken cancellationToken);

    Task<Administration.ManagedServiceChange<QuotaPolicyChange>> CreateAsync(QuotaPolicy policy, UserId actor, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Applies the validated values to the stored policy, audits the change, and recomputes the projections of the tenants that used it.</summary>
    Task<Administration.ManagedServiceChange<QuotaPolicyChange>> UpdateAsync(
        QuotaPolicyId id, QuotaPolicyValues values, UserId actor, DateTimeOffset now, CancellationToken cancellationToken);
}

public sealed record QuotaPolicyValues(
    string Name, QuotaAccountingMode AccountingMode, QuotaScopeRule IncludedStorageScopes, QuotaEnforcementMode EnforcementMode,
    int WarningThresholdPercent, int CriticalThresholdPercent, bool Enabled);

public sealed record QuotaScopeSelectorRequest(string? Kind, IReadOnlyList<string>? Values);

/// <summary>Validated quota policy administration for PlatformAdmin. Authorization is the caller's responsibility.</summary>
public sealed class QuotaPolicyAdministrationService(IQuotaPolicyStore store, TimeProvider timeProvider)
{
    public Task<IReadOnlyList<QuotaPolicyView>> ListAsync(CancellationToken cancellationToken) => store.ListAsync(cancellationToken);

    public Task<Administration.ManagedServiceChange<QuotaPolicyChange>> CreateAsync(
        string? key, string? name, string? accountingMode, IReadOnlyList<QuotaScopeSelectorRequest>? scopes, string? enforcementMode,
        int? warningThresholdPercent, int? criticalThresholdPercent, bool? enabled, UserId actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var now = timeProvider.GetUtcNow();
        var policy = new QuotaPolicy(
            QuotaPolicyId.New(), key ?? string.Empty, name ?? string.Empty, ParseEnum<QuotaAccountingMode>(accountingMode, "accountingMode"),
            ParseScopes(scopes), ParseEnum<QuotaEnforcementMode>(enforcementMode, "enforcementMode"),
            Required(warningThresholdPercent, "warningThresholdPercent"), Required(criticalThresholdPercent, "criticalThresholdPercent"), enabled ?? true, now);
        return store.CreateAsync(policy, actor, now, cancellationToken);
    }

    public Task<Administration.ManagedServiceChange<QuotaPolicyChange>> UpdateAsync(
        QuotaPolicyId id, string? name, string? accountingMode, IReadOnlyList<QuotaScopeSelectorRequest>? scopes, string? enforcementMode,
        int? warningThresholdPercent, int? criticalThresholdPercent, bool? enabled, UserId actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(actor);
        var (display, _) = ManagedServiceValidation.PolicyName(name);
        var (warning, critical) = QuotaPolicy.Thresholds(Required(warningThresholdPercent, "warningThresholdPercent"), Required(criticalThresholdPercent, "criticalThresholdPercent"));
        var values = new QuotaPolicyValues(
            display, ParseEnum<QuotaAccountingMode>(accountingMode, "accountingMode"), ParseScopes(scopes),
            ParseEnum<QuotaEnforcementMode>(enforcementMode, "enforcementMode"), warning, critical, Required(enabled, "enabled"));
        return store.UpdateAsync(id, values, actor, timeProvider.GetUtcNow(), cancellationToken);
    }

    private static QuotaScopeRule ParseScopes(IReadOnlyList<QuotaScopeSelectorRequest>? scopes) =>
        QuotaScopeRule.Create((scopes ?? []).Select(scope => new QuotaScopeSelector(QuotaScopeRule.ParseKind(scope?.Kind), scope?.Values ?? [])));

    private static T Required<T>(T? value, string field) where T : struct =>
        value ?? throw new InvalidManagedServiceRequestException(field, "required");

    /// <summary>Matches a defined member by name only; numeric text such as "1" is not a name.</summary>
    private static T ParseEnum<T>(string? value, string field) where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidManagedServiceRequestException(field, "required");
        }
        foreach (var name in Enum.GetNames<T>())
        {
            if (string.Equals(name, value.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return Enum.Parse<T>(name);
            }
        }
        throw new InvalidManagedServiceRequestException(field, "invalid_value");
    }
}
