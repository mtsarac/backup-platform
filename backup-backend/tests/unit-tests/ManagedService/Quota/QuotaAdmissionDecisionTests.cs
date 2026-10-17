using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.ManagedService.Entitlement;
using BackupPlatform.ManagedService.Plans;
using BackupPlatform.ManagedService.Quota;
using BackupPlatform.ManagedService.Subscriptions;

namespace BackupPlatform.UnitTests.ManagedService.Quota;

public sealed class QuotaAdmissionDecisionTests
{
    private const long Limit = 1_000_000_000;
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly TenantId Tenant = TenantId.New();

    private static QuotaPolicy Policy(QuotaEnforcementMode enforcement = QuotaEnforcementMode.BlockNewBackups, int warning = 80, int critical = 90) =>
        new(QuotaPolicyId.New(), "test", "Test", QuotaAccountingMode.LogicalArtifactBytes, QuotaScopeRule.All, enforcement, warning, critical, true, Now);

    private static Plan PlanOf(long? limit) => new(PlanId.New(), "Plan", limit, null, true, Now);

    private static TenantEntitlement Entitlement(string state, Plan plan, StorageLimitOverride? limitOverride = null)
    {
        if (state == "none")
        {
            return TenantEntitlement.Resolve(Tenant, null, null, Now);
        }
        var subscription = TenantSubscription.Start(Tenant, plan.Id, limitOverride ?? StorageLimitOverride.None, Now, state == "expired-by-date" ? Now.AddDays(1) : null, UserId.New());
        switch (state)
        {
            case "suspended": subscription.Suspend(Now); break;
            case "expired-stored": subscription.Expire(Now.AddDays(1)); break;
        }
        return TenantEntitlement.Resolve(Tenant, subscription, plan, Now.AddDays(2));
    }

    private static TenantEntitlement Active(long? planLimit = Limit, StorageLimitOverride? limitOverride = null) => Entitlement("active", PlanOf(planLimit), limitOverride);

    private static QuotaAdmissionDecision Decide(long used, long reserved = 0, long estimate = 0, QuotaEnforcementMode enforcement = QuotaEnforcementMode.BlockNewBackups, TenantEntitlement? entitlement = null) =>
        QuotaAdmissionDecision.Decide(entitlement ?? Active(), Policy(enforcement), used, reserved, estimate);

    [Fact]
    public void TheDenialCodesAgreeWithTheDomainAndTheEntitlement()
    {
        Assert.Equal(BackupAdmissionDenialCodes.QuotaExceeded, QuotaAdmissionDecision.QuotaExceededCode);
        Assert.Equal(BackupAdmissionDenialCodes.SubscriptionRequired, TenantEntitlement.SubscriptionRequiredCode);
        Assert.Equal("quota_exceeded", BackupAdmissionDenialCodes.QuotaExceeded);
        Assert.Equal("subscription_required", BackupAdmissionDenialCodes.SubscriptionRequired);
    }

    [Theory]
    [InlineData(0L, QuotaState.Normal)]
    [InlineData(799_999_999L, QuotaState.Normal)]
    [InlineData(850_000_000L, QuotaState.Warning)]
    [InlineData(999_999_999L, QuotaState.Critical)]
    public void UnderTheLimitANewRunIsAllowedWhateverTheWarningState(long used, QuotaState state)
    {
        var decision = Decide(used);
        Assert.True(decision.Allowed);
        Assert.Null(decision.DenialCode);
        Assert.Equal(state, decision.State);
    }

    [Theory]
    [InlineData(1_000_000_000L)]
    [InlineData(1_000_000_001L)]
    [InlineData(5_000_000_000L)]
    public void AtOrOverTheLimitANewRunIsDenied(long used)
    {
        var decision = Decide(used);
        Assert.False(decision.Allowed);
        Assert.Equal("quota_exceeded", decision.DenialCode);
        Assert.Equal(QuotaState.OverQuota, decision.State);
    }

    [Fact]
    public void ReservedBytesOfRunsInFlightCountAgainstTheLimit()
    {
        Assert.True(Decide(used: 600_000_000, reserved: 399_999_999).Allowed);
        var denied = Decide(used: 600_000_000, reserved: 400_000_000);
        Assert.False(denied.Allowed);
        Assert.Equal("quota_exceeded", denied.DenialCode);
    }

    [Theory]
    [InlineData(0L, 1_000_000_000L, true)]
    [InlineData(0L, 1_000_000_001L, false)]
    [InlineData(500_000_000L, 500_000_000L, true)]
    [InlineData(500_000_000L, 500_000_001L, false)]
    [InlineData(999_999_999L, 1L, true)]
    [InlineData(999_999_999L, 2L, false)]
    [InlineData(1_000_000_000L, 0L, false)]
    public void TheEstimateMustFitInWhatIsLeft(long used, long estimate, bool allowed)
    {
        var decision = Decide(used, estimate: estimate);
        Assert.Equal(allowed, decision.Allowed);
        Assert.Equal(allowed ? null : "quota_exceeded", decision.DenialCode);
    }

    [Fact]
    public void TheEstimateIsCheckedAgainstUsedPlusReserved()
    {
        Assert.True(Decide(used: 300_000_000, reserved: 300_000_000, estimate: 400_000_000).Allowed);
        Assert.False(Decide(used: 300_000_000, reserved: 300_000_000, estimate: 400_000_001).Allowed);
    }

    [Fact]
    public void WithoutHistoryAnyRoomLeftAdmitsTheRun() =>
        Assert.True(Decide(used: 999_999_999, estimate: 0).Allowed);

    [Fact]
    public void ANegativeEstimateNeverMakesRoom()
    {
        Assert.True(Decide(used: 999_999_999, estimate: -5_000).Allowed);
        Assert.False(Decide(used: Limit, estimate: -5_000).Allowed);
    }

    [Fact]
    public void AHugeEstimateIsDeniedWithoutOverflowing()
    {
        var entitlement = Active(StorageLimits.MaximumBytes);
        var decision = QuotaAdmissionDecision.Decide(entitlement, Policy(), StorageLimits.MaximumBytes - 1, 0, long.MaxValue);
        Assert.False(decision.Allowed);
        Assert.Equal("quota_exceeded", decision.DenialCode);
        Assert.False(QuotaAdmissionDecision.Decide(entitlement, Policy(), long.MaxValue, long.MaxValue, long.MaxValue).Allowed);
    }

    [Theory]
    [InlineData(QuotaEnforcementMode.WarnOnly, QuotaState.OverQuota)]
    [InlineData(QuotaEnforcementMode.None, QuotaState.Unlimited)]
    public void NonBlockingEnforcementNeverDeniesAndReportsItsOwnState(QuotaEnforcementMode enforcement, QuotaState state)
    {
        var decision = Decide(used: 3 * Limit, reserved: Limit, estimate: Limit, enforcement: enforcement);
        Assert.True(decision.Allowed);
        Assert.Null(decision.DenialCode);
        Assert.Equal(state, decision.State);
    }

    [Theory]
    [InlineData("override-unlimited")]
    [InlineData("plan-unlimited")]
    public void AnUnlimitedTenantIsAlwaysAllowedEvenWhenBlockingIsOn(string kind)
    {
        var entitlement = kind == "override-unlimited" ? Active(Limit, StorageLimitOverride.Unlimited) : Active(null);
        var decision = Decide(used: long.MaxValue, reserved: long.MaxValue, estimate: long.MaxValue, entitlement: entitlement);
        Assert.True(decision.Allowed);
        Assert.Null(decision.DenialCode);
        Assert.Equal(QuotaState.Unlimited, decision.State);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("suspended")]
    [InlineData("expired-stored")]
    [InlineData("expired-by-date")]
    public void WithoutAnActiveSubscriptionTheRunIsDeniedAsSubscriptionRequired(string state)
    {
        var entitlement = Entitlement(state, PlanOf(Limit));
        var decision = Decide(used: 0, entitlement: entitlement);
        Assert.False(decision.Allowed);
        Assert.Equal("subscription_required", decision.DenialCode);
    }

    [Theory]
    [InlineData(QuotaEnforcementMode.BlockNewBackups)]
    [InlineData(QuotaEnforcementMode.WarnOnly)]
    [InlineData(QuotaEnforcementMode.None)]
    public void TheSubscriptionIsCheckedBeforeAnyQuotaMode(QuotaEnforcementMode enforcement)
    {
        var decision = Decide(used: 5 * Limit, enforcement: enforcement, entitlement: Entitlement("suspended", PlanOf(Limit)));
        Assert.False(decision.Allowed);
        Assert.Equal("subscription_required", decision.DenialCode);
    }

    [Fact]
    public void ANoSubscriptionTenantHasNoLimitToReportAgainst()
    {
        var decision = Decide(used: 5 * Limit, entitlement: Entitlement("none", PlanOf(Limit)));
        Assert.Equal("subscription_required", decision.DenialCode);
        Assert.Equal(QuotaState.Unlimited, decision.State);
    }

    [Fact]
    public void ALimitedOverrideReplacesThePlanLimitInBothDirections()
    {
        var raised = Active(Limit, StorageLimitOverride.Limited(10 * Limit));
        Assert.True(Decide(used: 5 * Limit, entitlement: raised).Allowed);
        Assert.False(Decide(used: 10 * Limit, entitlement: raised).Allowed);

        var lowered = Active(10 * Limit, StorageLimitOverride.Limited(Limit));
        Assert.False(Decide(used: Limit, entitlement: lowered).Allowed);
        Assert.True(Decide(used: Limit - 1, entitlement: lowered).Allowed);
    }

    [Fact]
    public void AnUnlimitedOverrideLiftsAPlanLimitThatWouldDeny()
    {
        Assert.False(Decide(used: 2 * Limit, entitlement: Active(Limit)).Allowed);
        Assert.True(Decide(used: 2 * Limit, entitlement: Active(Limit, StorageLimitOverride.Unlimited)).Allowed);
    }

    [Fact]
    public void ThresholdsOfThePolicyDriveTheReportedState()
    {
        var decision = QuotaAdmissionDecision.Decide(Active(), Policy(warning: 50, critical: 60), 550_000_000, 0, 0);
        Assert.Equal(QuotaState.Warning, decision.State);
        Assert.True(decision.Allowed);
    }

    [Fact]
    public void ArgumentsAreRequired()
    {
        Assert.Throws<ArgumentNullException>(() => QuotaAdmissionDecision.Decide(null!, Policy(), 0, 0, 0));
        Assert.Throws<ArgumentNullException>(() => QuotaAdmissionDecision.Decide(Active(), null!, 0, 0, 0));
    }
}

public sealed class TenantQuotaStatusTests
{
    private const long Limit = 1_000_000_000;
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly TenantId Tenant = TenantId.New();

    private static QuotaPolicy Policy(QuotaEnforcementMode enforcement = QuotaEnforcementMode.BlockNewBackups) =>
        new(QuotaPolicyId.New(), "status-policy", "Status", QuotaAccountingMode.PhysicalReplicaBytes, QuotaScopeRule.All, enforcement, 70, 85, true, Now);

    private static TenantEntitlement Active(long? limit, StorageLimitOverride? limitOverride = null)
    {
        var plan = new Plan(PlanId.New(), "Plan", limit, null, true, Now);
        var subscription = TenantSubscription.Start(Tenant, plan.Id, limitOverride ?? StorageLimitOverride.None, Now, null, UserId.New());
        return TenantEntitlement.Resolve(Tenant, subscription, plan, Now);
    }

    [Fact]
    public void ReportsLimitUsageRemainingStateAndThePolicyThatProducedThem()
    {
        var policy = Policy();
        var status = TenantQuotaStatus.Resolve(Active(Limit), policy, 600_000_000, 150_000_000, Now);

        Assert.Equal(Tenant, status.TenantId);
        Assert.Equal(EntitlementStatus.Active, status.EntitlementStatus);
        Assert.Equal(((long?)Limit, false), (status.LimitBytes, status.IsUnlimited));
        Assert.Equal((600_000_000L, 150_000_000L, (long?)250_000_000), (status.UsedBytes, status.ReservedBytes, status.RemainingBytes));
        Assert.Equal(QuotaState.Warning, status.State);
        Assert.Equal((70, 85, QuotaAccountingMode.PhysicalReplicaBytes, QuotaEnforcementMode.BlockNewBackups), (status.WarningThresholdPercent, status.CriticalThresholdPercent, status.AccountingMode, status.EnforcementMode));
        Assert.Equal((policy.Id, "status-policy", 1, Now), (status.PolicyId, status.PolicyKey, status.PolicyVersion, status.UsageCalculatedAt));
        Assert.True(status.NewBackupsAllowed);
        Assert.Null(status.DenialCode);
    }

    [Theory]
    [InlineData(1_000_000_000L, 0L)]
    [InlineData(1_200_000_000L, 0L)]
    [InlineData(850_000_000L, 150_000_000L)]
    [InlineData(400_000_000L, 700_000_000L)]
    public void RemainingIsNeverNegativeAndAtTheLimitNewBackupsAreNotAllowed(long used, long reserved)
    {
        var status = TenantQuotaStatus.Resolve(Active(Limit), Policy(), used, reserved, Now);
        Assert.Equal(0L, status.RemainingBytes);
        Assert.Equal(QuotaState.OverQuota, status.State);
        Assert.False(status.NewBackupsAllowed);
        Assert.Equal("quota_exceeded", status.DenialCode);
    }

    [Fact]
    public void ReservedBytesReduceTheRemainingSpace()
    {
        var status = TenantQuotaStatus.Resolve(Active(Limit), Policy(), 100, 200, Now);
        Assert.Equal(Limit - 300, status.RemainingBytes);
    }

    [Fact]
    public void AnUnlimitedTenantHasNoLimitAndNoRemaining()
    {
        var status = TenantQuotaStatus.Resolve(Active(Limit, StorageLimitOverride.Unlimited), Policy(), 9 * Limit, 0, Now);
        Assert.True(status.IsUnlimited);
        Assert.Null(status.LimitBytes);
        Assert.Null(status.RemainingBytes);
        Assert.Equal(QuotaState.Unlimited, status.State);
        Assert.True(status.NewBackupsAllowed);
    }

    [Fact]
    public void WarnOnlyShowsOverQuotaButStillAllowsNewBackups()
    {
        var status = TenantQuotaStatus.Resolve(Active(Limit), Policy(QuotaEnforcementMode.WarnOnly), 2 * Limit, 0, Now);
        Assert.Equal(QuotaState.OverQuota, status.State);
        Assert.Equal(0L, status.RemainingBytes);
        Assert.True(status.NewBackupsAllowed);
        Assert.Null(status.DenialCode);
    }

    [Fact]
    public void EnforcementNoneReportsUnlimitedStateButKeepsTheLimitAndRemaining()
    {
        var status = TenantQuotaStatus.Resolve(Active(Limit), Policy(QuotaEnforcementMode.None), 400_000_000, 0, Now);
        Assert.Equal(QuotaState.Unlimited, status.State);
        Assert.Equal((long?)Limit, status.LimitBytes);
        Assert.Equal((long?)600_000_000, status.RemainingBytes);
        Assert.True(status.NewBackupsAllowed);
    }

    [Fact]
    public void ATenantWithoutASubscriptionIsNotAllowedToStartBackupsAndHasNoLimit()
    {
        var status = TenantQuotaStatus.Resolve(TenantEntitlement.Resolve(Tenant, null, null, Now), Policy(), 123, 0, Now);
        Assert.Equal(EntitlementStatus.NoSubscription, status.EntitlementStatus);
        Assert.False(status.NewBackupsAllowed);
        Assert.Equal("subscription_required", status.DenialCode);
        Assert.Null(status.LimitBytes);
        Assert.False(status.IsUnlimited);
        Assert.Null(status.RemainingBytes);
        Assert.Equal(123L, status.UsedBytes);
    }

    [Fact]
    public void RemainingDoesNotOverflowOnSaturatedUsage()
    {
        var status = TenantQuotaStatus.Resolve(Active(Limit), Policy(), long.MaxValue, long.MaxValue, Now);
        Assert.Equal(0L, status.RemainingBytes);
        Assert.Equal(QuotaState.OverQuota, status.State);
    }
}
