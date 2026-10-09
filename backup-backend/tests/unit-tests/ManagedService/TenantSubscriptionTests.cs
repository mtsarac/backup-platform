using BackupPlatform.Domain.Identity;
using BackupPlatform.ManagedService;
using BackupPlatform.ManagedService.Plans;
using BackupPlatform.ManagedService.Subscriptions;

namespace BackupPlatform.UnitTests.ManagedService;

public sealed class TenantSubscriptionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    private static TenantSubscription Start(DateTimeOffset? endsAt = null, StorageLimitOverride? limitOverride = null) =>
        TenantSubscription.Start(TenantId.New(), PlanId.New(), limitOverride ?? StorageLimitOverride.None, Now, endsAt, UserId.New());

    private static string CodeOf(Action action) => Assert.Throws<SubscriptionTransitionException>(action).Code;

    [Fact]
    public void ANewSubscriptionIsActiveAndOpenWithTheActorRecorded()
    {
        var actor = UserId.New();
        var subscription = TenantSubscription.Start(TenantId.New(), PlanId.New(), StorageLimitOverride.Limited(2_000_000_000), Now, null, actor);
        Assert.Equal(SubscriptionStatus.Active, subscription.Status);
        Assert.True(subscription.IsOpen);
        Assert.Equal(actor, subscription.AssignedByUserId);
        Assert.Equal(Now, subscription.StartsAt);
        Assert.Null(subscription.EndsAt);
        Assert.Equal(StorageLimitOverride.Limited(2_000_000_000), subscription.Override);
    }

    [Fact]
    public void AnEndInThePastOrNowIsRejected()
    {
        Assert.Equal("endsAt", Assert.Throws<InvalidManagedServiceRequestException>(() => Start(Now)).Field);
        Assert.Throws<InvalidManagedServiceRequestException>(() => Start(Now.AddSeconds(-1)));
        Assert.Equal(Now.AddDays(30), Start(Now.AddDays(30)).EndsAt);
    }

    [Fact]
    public void SuspendAndResumeRoundTrip()
    {
        var subscription = Start();
        subscription.Suspend(Now.AddMinutes(1));
        Assert.Equal(SubscriptionStatus.Suspended, subscription.Status);
        Assert.True(subscription.IsOpen);
        subscription.Resume(Now.AddMinutes(2));
        Assert.Equal(SubscriptionStatus.Active, subscription.Status);
        Assert.Equal(Now.AddMinutes(2), subscription.UpdatedAt);
    }

    [Fact]
    public void TransitionsOutOfTheWrongStateAreRefusedWithAStableCode()
    {
        var active = Start();
        Assert.Equal(SubscriptionTransitionCodes.NotSuspended, CodeOf(() => active.Resume(Now)));
        active.Suspend(Now);
        Assert.Equal(SubscriptionTransitionCodes.NotActive, CodeOf(() => active.Suspend(Now)));
    }

    [Fact]
    public void SupersedingClosesTheRowAsHistoryAndItCannotChangeAgain()
    {
        var subscription = Start();
        subscription.Supersede(Now.AddDays(1));
        Assert.Equal(SubscriptionStatus.Superseded, subscription.Status);
        Assert.False(subscription.IsOpen);
        Assert.Equal(Now.AddDays(1), subscription.EndsAt);
        Assert.Equal(SubscriptionTransitionCodes.NotOpen, CodeOf(() => subscription.Supersede(Now.AddDays(2))));
        Assert.Equal(SubscriptionTransitionCodes.NotActive, CodeOf(() => subscription.Suspend(Now.AddDays(2))));
        Assert.Equal(SubscriptionTransitionCodes.NotSuspended, CodeOf(() => subscription.Resume(Now.AddDays(2))));
        Assert.Equal(SubscriptionTransitionCodes.NotOpen, CodeOf(() => subscription.ChangeOverride(StorageLimitOverride.Unlimited, Now.AddDays(2))));
    }

    [Fact]
    public void ASuspendedRowCanBeSuperseded()
    {
        var subscription = Start();
        subscription.Suspend(Now);
        subscription.Supersede(Now.AddHours(1));
        Assert.Equal(SubscriptionStatus.Superseded, subscription.Status);
    }

    [Fact]
    public void ASupersededEndNeverPrecedesTheStartEvenWithClockSkew()
    {
        var subscription = Start();
        subscription.Supersede(Now.AddSeconds(-5));
        Assert.Equal(Now, subscription.EndsAt);
    }

    [Fact]
    public void ExpiryIsDerivedAtReadTimeAndNeedsNoSweeper()
    {
        var subscription = Start(Now.AddDays(10));
        Assert.Equal(SubscriptionStatus.Active, subscription.EffectiveStatusAt(Now.AddDays(9)));
        Assert.Equal(SubscriptionStatus.Expired, subscription.EffectiveStatusAt(Now.AddDays(10)));
        Assert.Equal(SubscriptionStatus.Active, subscription.Status);
        subscription.Suspend(Now.AddDays(1));
        Assert.Equal(SubscriptionStatus.Expired, subscription.EffectiveStatusAt(Now.AddDays(11)));
    }

    [Fact]
    public void APastEndRowRefusesEveryChangeExceptBeingClosed()
    {
        var late = Now.AddDays(11);
        var active = Start(Now.AddDays(10));
        Assert.Equal(SubscriptionTransitionCodes.Expired, CodeOf(() => active.Suspend(late)));
        Assert.Equal(SubscriptionTransitionCodes.Expired, CodeOf(() => active.Supersede(late)));
        Assert.Equal(SubscriptionTransitionCodes.Expired, CodeOf(() => active.ChangeOverride(StorageLimitOverride.Unlimited, late)));
        var suspended = Start(Now.AddDays(10));
        suspended.Suspend(Now);
        Assert.Equal(SubscriptionTransitionCodes.Expired, CodeOf(() => suspended.Resume(late)));
        active.Expire(late);
        Assert.Equal(SubscriptionStatus.Expired, active.Status);
        Assert.Equal(Now.AddDays(10), active.EndsAt);
    }

    [Fact]
    public void ExpiringAnOpenEndlessRowStoresTheClosingMoment()
    {
        var subscription = Start();
        subscription.Expire(Now.AddDays(3));
        Assert.Equal((SubscriptionStatus.Expired, (DateTimeOffset?)Now.AddDays(3)), (subscription.Status, subscription.EndsAt));
        Assert.Equal(SubscriptionTransitionCodes.NotOpen, CodeOf(() => subscription.Expire(Now.AddDays(4))));
        Assert.Equal(SubscriptionTransitionCodes.Expired, CodeOf(() => subscription.Suspend(Now.AddDays(4))));
        Assert.Equal(SubscriptionTransitionCodes.Expired, CodeOf(() => subscription.Resume(Now.AddDays(4))));
    }

    [Fact]
    public void ChangingTheOverrideIsInPlaceAndIdempotent()
    {
        var subscription = Start();
        Assert.False(subscription.ChangeOverride(StorageLimitOverride.None, Now.AddMinutes(1)));
        Assert.True(subscription.ChangeOverride(StorageLimitOverride.Limited(7_000_000_000), Now.AddMinutes(2)));
        Assert.Equal(7_000_000_000L, subscription.StorageLimitOverrideBytes);
        Assert.False(subscription.ChangeOverride(StorageLimitOverride.Limited(7_000_000_000), Now.AddMinutes(3)));
        Assert.True(subscription.ChangeOverride(StorageLimitOverride.Unlimited, Now.AddMinutes(4)));
        Assert.Null(subscription.StorageLimitOverrideBytes);
        Assert.Equal(StorageLimitOverrideKind.Unlimited, subscription.StorageLimitOverrideKind);
        Assert.True(subscription.ChangeOverride(StorageLimitOverride.None, Now.AddMinutes(5)));
        Assert.Equal(Now.AddMinutes(5), subscription.UpdatedAt);
    }

    [Fact]
    public void ASuspendedRowKeepsItsOverrideEditable()
    {
        var subscription = Start();
        subscription.Suspend(Now);
        Assert.True(subscription.ChangeOverride(StorageLimitOverride.Limited(1), Now.AddMinutes(1)));
        Assert.Equal(SubscriptionStatus.Suspended, subscription.Status);
    }
}
