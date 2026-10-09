using BackupPlatform.Application.Platform;
using BackupPlatform.Domain.Identity;
using BackupPlatform.ManagedService;
using BackupPlatform.ManagedService.Administration;
using BackupPlatform.ManagedService.Plans;
using BackupPlatform.ManagedService.Subscriptions;

namespace BackupPlatform.UnitTests.ManagedService;

public sealed class ManagedServiceAdministrationServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly UserId Actor = UserId.New();

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class CapturingStore : IManagedServiceStore
    {
        public Plan? CreatedPlan { get; private set; }
        public (string Name, long? Limit, string? Reference)? UpdatedPlan { get; private set; }
        public (PlanId Plan, StorageLimitOverride Override, DateTimeOffset? EndsAt)? Assigned { get; private set; }
        public StorageLimitOverride? OverrideSet { get; private set; }

        public Task<PagedResult<PlanView>> ListPlansAsync(PlanFilter filter, int page, int pageSize, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ManagedServiceChange<PlanView>> CreatePlanAsync(Plan plan, UserId actor, DateTimeOffset now, CancellationToken cancellationToken)
        {
            CreatedPlan = plan;
            return Task.FromResult(new ManagedServiceChange<PlanView>(ManagedServiceOutcome.Changed));
        }

        public Task<ManagedServiceChange<PlanView>> UpdatePlanAsync(
            PlanId planId, string name, long? storageLimitBytes, string? quotaPolicyReference, UserId actor, DateTimeOffset now, CancellationToken cancellationToken)
        {
            UpdatedPlan = (name, storageLimitBytes, quotaPolicyReference);
            return Task.FromResult(new ManagedServiceChange<PlanView>(ManagedServiceOutcome.Changed));
        }

        public Task<ManagedServiceChange<PlanView>> SetPlanEnabledAsync(PlanId planId, bool enabled, UserId actor, DateTimeOffset now, CancellationToken cancellationToken) =>
            Task.FromResult(new ManagedServiceChange<PlanView>(ManagedServiceOutcome.Changed));

        public Task<(bool TenantExists, SubscriptionRecord? Current)> GetCurrentSubscriptionAsync(TenantId tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PagedResult<SubscriptionRecord>?> ListSubscriptionsAsync(TenantId tenantId, int page, int pageSize, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ManagedServiceChange<SubscriptionAssignment>> AssignPlanAsync(
            TenantId tenantId, PlanId planId, StorageLimitOverride limitOverride, DateTimeOffset? endsAt, UserId actor, DateTimeOffset now, CancellationToken cancellationToken)
        {
            Assigned = (planId, limitOverride, endsAt);
            return Task.FromResult(new ManagedServiceChange<SubscriptionAssignment>(ManagedServiceOutcome.PlanDisabled));
        }

        public Task<ManagedServiceChange<SubscriptionRecord>> SuspendAsync(TenantId tenantId, UserId actor, DateTimeOffset now, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ManagedServiceChange<SubscriptionRecord>> ResumeAsync(TenantId tenantId, UserId actor, DateTimeOffset now, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ManagedServiceChange<SubscriptionRecord>> SetStorageLimitOverrideAsync(
            TenantId tenantId, StorageLimitOverride limitOverride, UserId actor, DateTimeOffset now, CancellationToken cancellationToken)
        {
            OverrideSet = limitOverride;
            return Task.FromResult(new ManagedServiceChange<SubscriptionRecord>(ManagedServiceOutcome.SubscriptionNotFound));
        }
    }

    private static (ManagedServiceAdministrationService Service, CapturingStore Store) Create()
    {
        var store = new CapturingStore();
        return (new ManagedServiceAdministrationService(store, new FixedTime()), store);
    }

    private static (string Field, string Code) Rejection(Func<Task> action)
    {
        var failure = Assert.ThrowsAsync<InvalidManagedServiceRequestException>(action).GetAwaiter().GetResult();
        return (failure.Field, failure.Code);
    }

    [Fact]
    public async Task AnExplicitByteLimitCreatesALimitedPlanAndUnlimitedCreatesAnUnlimitedOne()
    {
        var (service, store) = Create();
        await service.CreatePlanAsync("5 GB", 5_000_000_000, null, null, null, Actor, default);
        Assert.Equal((5_000_000_000L, true), (store.CreatedPlan!.StorageLimitBytes, store.CreatedPlan.Enabled));
        await service.CreatePlanAsync("Unlimited custom", null, true, null, false, Actor, default);
        Assert.Equal((null, false), (store.CreatedPlan!.StorageLimitBytes, store.CreatedPlan.Enabled));
    }

    [Theory]
    [InlineData(null, null, "required")]
    [InlineData(null, false, "required")]
    [InlineData(5_000_000_000L, true, "ambiguous")]
    [InlineData(0L, null, "out_of_range")]
    [InlineData(-1L, false, "out_of_range")]
    public void AMissingOrContradictoryLimitIsRejectedSoNoPlanIsSilentlyUnlimited(long? bytes, bool? unlimited, string code)
    {
        var (service, store) = Create();
        Assert.Equal(("storageLimit", code), Rejection(() => service.CreatePlanAsync("Plan", bytes, unlimited, null, null, Actor, default)));
        Assert.Equal(("storageLimit", code), Rejection(() => service.UpdatePlanAsync(PlanId.New(), "Plan", bytes, unlimited, null, Actor, default)));
        Assert.Null(store.CreatedPlan);
        Assert.Null(store.UpdatedPlan);
    }

    [Fact]
    public async Task UpdatePassesTheNormalizedNameAndTheParsedLimit()
    {
        var (service, store) = Create();
        await service.UpdatePlanAsync(PlanId.New(), "  Five   GB ", 5_000_000_000, null, "ref-1", Actor, default);
        Assert.Equal(("Five GB", (long?)5_000_000_000, "ref-1"), store.UpdatedPlan!.Value);
        await service.UpdatePlanAsync(PlanId.New(), "Five GB", null, true, null, Actor, default);
        Assert.Null(store.UpdatedPlan!.Value.Limit);
    }

    [Fact]
    public void EnablingNeedsAnExplicitValue() =>
        Assert.Equal(("enabled", "required"), Rejection(() => Create().Service.SetPlanEnabledAsync(PlanId.New(), null, Actor, default)));

    [Fact]
    public void AssignmentRequiresAPlanAndAFutureEnd()
    {
        var (service, store) = Create();
        Assert.Equal(("planId", "required"), Rejection(() => service.AssignPlanAsync(TenantId.New(), null, null, null, null, Actor, default)));
        Assert.Equal(("planId", "required"), Rejection(() => service.AssignPlanAsync(TenantId.New(), Guid.Empty, null, null, null, Actor, default)));
        Assert.Equal(("endsAt", "invalid_value"), Rejection(() => service.AssignPlanAsync(TenantId.New(), Guid.NewGuid(), null, null, Now, Actor, default)));
        Assert.Null(store.Assigned);
    }

    [Fact]
    public async Task AssignmentWithoutAnOverrideIsNoOverrideAndNeverInherited()
    {
        var (service, store) = Create();
        var plan = Guid.NewGuid();
        var change = await service.AssignPlanAsync(TenantId.New(), plan, null, null, Now.AddDays(5), Actor, default);
        Assert.Equal(ManagedServiceOutcome.PlanDisabled, change.Outcome);
        Assert.Equal((new PlanId(plan), StorageLimitOverride.None, (DateTimeOffset?)Now.AddDays(5)), store.Assigned!.Value);
    }

    [Theory]
    [InlineData("limited", 3_000_000_000L)]
    [InlineData("LIMITED", 3_000_000_000L)]
    public async Task OverridesParseCaseInsensitively(string kind, long bytes)
    {
        var (service, store) = Create();
        await service.SetStorageLimitOverrideAsync(TenantId.New(), kind, bytes, Actor, default);
        Assert.Equal(StorageLimitOverride.Limited(bytes), store.OverrideSet);
    }

    [Fact]
    public async Task UnlimitedAndNoneOverridesCarryNoBytes()
    {
        var (service, store) = Create();
        await service.SetStorageLimitOverrideAsync(TenantId.New(), "unlimited", null, Actor, default);
        Assert.Equal(StorageLimitOverride.Unlimited, store.OverrideSet);
        await service.SetStorageLimitOverrideAsync(TenantId.New(), "none", null, Actor, default);
        Assert.Equal(StorageLimitOverride.None, store.OverrideSet);
    }

    [Theory]
    [InlineData(null, null, "invalid_kind")]
    [InlineData("", null, "invalid_kind")]
    [InlineData("forever", null, "invalid_kind")]
    [InlineData("2", null, "invalid_kind")]
    [InlineData("limited", null, "required")]
    [InlineData("limited", 0L, "out_of_range")]
    [InlineData("limited", -5L, "out_of_range")]
    [InlineData("limited", long.MaxValue, "out_of_range")]
    [InlineData("unlimited", 1L, "unexpected")]
    [InlineData("none", 1L, "unexpected")]
    public void InvalidOverridesNameTheFieldAndCode(string? kind, long? bytes, string code)
    {
        var (service, store) = Create();
        Assert.Equal(("storageLimitOverride", code), Rejection(() => service.SetStorageLimitOverrideAsync(TenantId.New(), kind, bytes, Actor, default)));
        Assert.Null(store.OverrideSet);
    }

    [Fact]
    public void ViewsCarryTheEffectiveStatusAndLimitFromTheSharedRules()
    {
        var plan = new Plan(PlanId.New(), "5 GB", 5_000_000_000, null, true, Now);
        var subscription = TenantSubscription.Start(TenantId.New(), plan.Id, StorageLimitOverride.Limited(20_000_000_000), Now, Now.AddDays(1), Actor);
        var record = new SubscriptionRecord(subscription, plan, "Admin");
        var live = ManagedServiceAdministrationService.ToView(record, Now.AddHours(1));
        Assert.Equal((SubscriptionStatus.Active, (long?)20_000_000_000, EffectiveStorageLimitSource.Override, (long?)5_000_000_000), (live.Status, live.EffectiveStorageLimit.Bytes, live.EffectiveStorageLimit.Source, live.PlanStorageLimitBytes));
        Assert.Equal(SubscriptionStatus.Expired, ManagedServiceAdministrationService.ToView(record, Now.AddDays(2)).Status);
    }
}
