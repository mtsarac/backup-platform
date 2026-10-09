using BackupPlatform.Domain.Identity;
using BackupPlatform.ManagedService.Entitlement;
using BackupPlatform.ManagedService.Plans;
using BackupPlatform.ManagedService.Subscriptions;

namespace BackupPlatform.UnitTests.ManagedService;

/// <summary>
/// The contract the later admission slice builds on. Nothing here changes backup behavior; it pins what a subscription state means.
/// </summary>
public sealed class TenantEntitlementTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly TenantId Tenant = TenantId.New();

    private static Plan PlanOf(long? limit) => new(PlanId.New(), "Plan", limit, null, true, Now);

    private static TenantEntitlement Entitlement(string state, Plan plan, StorageLimitOverride? limitOverride = null)
    {
        var subscription = TenantSubscription.Start(Tenant, plan.Id, limitOverride ?? StorageLimitOverride.None, Now, state == "expired-by-date" ? Now.AddDays(1) : null, UserId.New());
        switch (state)
        {
            case "suspended": subscription.Suspend(Now); break;
            case "expired-stored": subscription.Expire(Now.AddDays(1)); break;
        }
        return TenantEntitlement.Resolve(Tenant, subscription, plan, Now.AddDays(2));
    }

    [Fact]
    public void WithoutASubscriptionTheTenantIsNoSubscriptionWithNoLimit()
    {
        var entitlement = TenantEntitlement.Resolve(Tenant, null, null, Now);
        Assert.Equal(EntitlementStatus.NoSubscription, entitlement.Status);
        Assert.Null(entitlement.StorageLimit);
        Assert.Null(entitlement.SubscriptionId);
    }

    [Theory]
    [InlineData("active", EntitlementStatus.Active)]
    [InlineData("suspended", EntitlementStatus.Suspended)]
    [InlineData("expired-stored", EntitlementStatus.Expired)]
    [InlineData("expired-by-date", EntitlementStatus.Expired)]
    public void StatusFollowsTheEffectiveSubscriptionState(string state, EntitlementStatus expected) =>
        Assert.Equal(expected, Entitlement(state, PlanOf(5_000_000_000)).Status);

    public static TheoryData<string> NonActiveStates => ["none", "suspended", "expired-stored", "expired-by-date"];

    [Theory]
    [MemberData(nameof(NonActiveStates))]
    public void WithoutAnActiveSubscriptionOnlyNewBackupRunsAreDenied(string state)
    {
        var entitlement = state == "none" ? TenantEntitlement.Resolve(Tenant, null, null, Now) : Entitlement(state, PlanOf(5_000_000_000));
        Assert.False(entitlement.Permits(EntitlementOperation.StartNewBackupRun));
        Assert.Equal("subscription_required", entitlement.DenialCode(EntitlementOperation.StartNewBackupRun));
        Assert.Equal(TenantEntitlement.SubscriptionRequiredCode, entitlement.DenialCode(EntitlementOperation.StartNewBackupRun));
        // Existing customer data and operations stay available; a retry of an already admitted run is not cut off.
        foreach (var operation in Enum.GetValues<EntitlementOperation>().Where(operation => operation != EntitlementOperation.StartNewBackupRun))
        {
            Assert.True(entitlement.Permits(operation), $"{operation} must stay permitted without an active subscription.");
            Assert.Null(entitlement.DenialCode(operation));
        }
    }

    [Fact]
    public void AnActiveSubscriptionPermitsEverything()
    {
        var entitlement = Entitlement("active", PlanOf(5_000_000_000));
        foreach (var operation in Enum.GetValues<EntitlementOperation>())
        {
            Assert.True(entitlement.Permits(operation));
        }
    }

    [Fact]
    public void TheLimitComesFromTheSharedResolverWithTheOverrideWinning()
    {
        var plan = PlanOf(5_000_000_000);
        Assert.Equal(5_000_000_000L, Entitlement("active", plan).StorageLimit!.Value.Bytes);
        var overridden = Entitlement("active", plan, StorageLimitOverride.Limited(20_000_000_000));
        Assert.Equal((20_000_000_000L, EffectiveStorageLimitSource.Override), (overridden.StorageLimit!.Value.Bytes!.Value, overridden.StorageLimit.Value.Source));
        Assert.True(Entitlement("active", plan, StorageLimitOverride.Unlimited).StorageLimit!.Value.IsUnlimited);
        Assert.True(Entitlement("active", PlanOf(null)).StorageLimit!.Value.IsUnlimited);
    }

    [Fact]
    public void ThePlanMustBeTheSubscriptionsPlan()
    {
        var subscription = TenantSubscription.Start(Tenant, PlanId.New(), StorageLimitOverride.None, Now, null, UserId.New());
        Assert.Throws<ArgumentException>(() => TenantEntitlement.Resolve(Tenant, subscription, PlanOf(1), Now));
        Assert.Throws<ArgumentException>(() => TenantEntitlement.Resolve(Tenant, subscription, null, Now));
    }
}
