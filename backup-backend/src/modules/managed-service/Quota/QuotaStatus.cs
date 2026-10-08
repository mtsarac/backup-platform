using BackupPlatform.Domain.Identity;
using BackupPlatform.ManagedService.Entitlement;
using BackupPlatform.ManagedService.Subscriptions;

namespace BackupPlatform.ManagedService.Quota;

/// <summary>The single place used + reserved bytes become a <see cref="QuotaState"/>.</summary>
public static class QuotaStateResolver
{
    public static QuotaState Resolve(
        EffectiveStorageLimit? limit, QuotaEnforcementMode enforcement, int warningPercent, int criticalPercent, long usedBytes, long reservedBytes)
    {
        if (limit is not { IsUnlimited: false, Bytes: { } limitBytes } || enforcement == QuotaEnforcementMode.None)
        {
            return QuotaState.Unlimited;
        }
        var committed = Committed(usedBytes, reservedBytes);
        if (limitBytes <= 0 || committed >= limitBytes)
        {
            return QuotaState.OverQuota;
        }
        // 128-bit products: neither committed * 100 nor limit * percent can overflow.
        var scaled = (UInt128)(ulong)committed * 100;
        if (scaled >= (UInt128)(ulong)limitBytes * (ulong)criticalPercent)
        {
            return QuotaState.Critical;
        }
        return scaled >= (UInt128)(ulong)limitBytes * (ulong)warningPercent ? QuotaState.Warning : QuotaState.Normal;
    }

    /// <summary>Used plus reserved, clamped at <see cref="long.MaxValue"/> and never negative.</summary>
    public static long Committed(long usedBytes, long reservedBytes)
    {
        var used = Math.Max(0, usedBytes);
        var reserved = Math.Max(0, reservedBytes);
        return used > long.MaxValue - reserved ? long.MaxValue : used + reserved;
    }
}

/// <summary>Whether a NEW BackupRun may be created, by tenant standing and quota. Retry attempts of an admitted run never reach this.</summary>
public sealed record QuotaAdmissionDecision(bool Allowed, string? DenialCode, QuotaState State)
{
    public const string QuotaExceededCode = "quota_exceeded";

    public static QuotaAdmissionDecision Decide(
        TenantEntitlement entitlement, QuotaPolicy policy, long usedBytes, long reservedBytes, long estimatedBytes)
    {
        ArgumentNullException.ThrowIfNull(entitlement);
        ArgumentNullException.ThrowIfNull(policy);
        var state = QuotaStateResolver.Resolve(entitlement.StorageLimit, policy.EnforcementMode, policy.WarningThresholdPercent, policy.CriticalThresholdPercent, usedBytes, reservedBytes);
        if (entitlement.DenialCode(EntitlementOperation.StartNewBackupRun) is { } subscriptionCode)
        {
            return new QuotaAdmissionDecision(false, subscriptionCode, state);
        }
        if (policy.EnforcementMode != QuotaEnforcementMode.BlockNewBackups || state == QuotaState.Unlimited)
        {
            return new QuotaAdmissionDecision(true, null, state);
        }
        // Already at or over the limit is refused outright. Beyond that, the run's expected size is checked against what is left, so two
        // runs started together cannot both be admitted into an overshoot that either one alone would have avoided. The estimate is only
        // what the same job produced last time (0 without history); the real artifact may still turn out bigger, and is never cut off.
        var limit = entitlement.StorageLimit!.Value.Bytes!.Value;
        var committed = QuotaStateResolver.Committed(usedBytes, reservedBytes);
        var projected = committed > long.MaxValue - Math.Max(0, estimatedBytes) ? long.MaxValue : committed + Math.Max(0, estimatedBytes);
        return state == QuotaState.OverQuota || projected > limit
            ? new QuotaAdmissionDecision(false, QuotaExceededCode, state)
            : new QuotaAdmissionDecision(true, null, state);
    }
}

/// <summary>What the tenant is subscribed to, for display next to its quota. Resolved by the reader that has the plan row; never part of a quota decision.</summary>
public sealed record TenantPlanSummary(string Name, bool IsSystem, string? SystemKey, DateTimeOffset? SubscriptionEndsAt);

/// <summary>
/// The tenant's storage standing, ready for an API: the effective limit (M1), the policy (how to count), the projected usage and the derived
/// state. <see cref="RemainingBytes"/> is the limit less used and reserved bytes, never below zero; null when there is no limit.
/// </summary>
public sealed record TenantQuotaStatus(
    TenantId TenantId, EntitlementStatus EntitlementStatus, long? LimitBytes, bool IsUnlimited, long UsedBytes, long ReservedBytes, long? RemainingBytes,
    QuotaState State, int WarningThresholdPercent, int CriticalThresholdPercent, QuotaAccountingMode AccountingMode, QuotaEnforcementMode EnforcementMode,
    QuotaPolicyId PolicyId, string PolicyKey, int PolicyVersion, DateTimeOffset UsageCalculatedAt, bool NewBackupsAllowed, string? DenialCode,
    QuotaScopeRule IncludedStorageScopes)
{
    /// <summary>Null when the tenant has no subscription.</summary>
    public TenantPlanSummary? Plan { get; init; }

    public static TenantQuotaStatus Resolve(TenantEntitlement entitlement, QuotaPolicy policy, long usedBytes, long reservedBytes, DateTimeOffset calculatedAt)
    {
        var limit = entitlement.StorageLimit;
        var state = QuotaStateResolver.Resolve(limit, policy.EnforcementMode, policy.WarningThresholdPercent, policy.CriticalThresholdPercent, usedBytes, reservedBytes);
        var admission = QuotaAdmissionDecision.Decide(entitlement, policy, usedBytes, reservedBytes, 0);
        var limitBytes = limit is { IsUnlimited: false } ? limit.Value.Bytes : null;
        long? remaining = limitBytes is { } bytes ? Math.Max(0, bytes - QuotaStateResolver.Committed(usedBytes, reservedBytes)) : null;
        return new TenantQuotaStatus(
            entitlement.TenantId, entitlement.Status, limitBytes, limit is { IsUnlimited: true }, usedBytes, reservedBytes, remaining, state,
            policy.WarningThresholdPercent, policy.CriticalThresholdPercent, policy.AccountingMode, policy.EnforcementMode,
            policy.Id, policy.Key, policy.Version, calculatedAt, admission.Allowed, admission.DenialCode, policy.IncludedStorageScopes);
    }
}
