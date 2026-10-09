using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.ManagedService;
using BackupPlatform.ManagedService.Administration;
using BackupPlatform.ManagedService.Entitlement;
using BackupPlatform.ManagedService.Plans;
using BackupPlatform.ManagedService.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using static BackupPlatform.IntegrationTests.ManagedService.ManagedServiceTestSupport;

namespace BackupPlatform.IntegrationTests.ManagedService;

/// <summary>Real PostgreSQL, real parallel writers: the one-open-subscription invariant, atomicity and tenant isolation.</summary>
public sealed class SubscriptionInvariantIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>, IAsyncLifetime
{
    /// <summary>An existing user: subscriptions reference the assigning administrator by foreign key.</summary>
    private UserId Actor { get; set; } = UserId.New();

    public async Task InitializeAsync()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        Actor = (await world.SeedUserAsync(platformAdmin: true)).UserId;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<TenantId> NewTenantAsync()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        return (await world.AddTenantAsync()).Id;
    }

    private Task<ManagedServiceChange<SubscriptionAssignment>> AssignAsync(IManagedServiceStore store, TenantId tenant, PlanId plan) =>
        Task.Run(() => store.AssignPlanAsync(tenant, plan, StorageLimitOverride.None, null, Actor, DateTimeOffset.UtcNow, default));

    [Fact]
    public async Task ParallelAssignmentsOfDifferentPlansToOneTenantLeaveExactlyOneActiveSubscription()
    {
        var store = StoreOf(fixture);
        var tenant = await NewTenantAsync();
        var plans = new[] { await CreatePlanDirectAsync(fixture, Gb), await CreatePlanDirectAsync(fixture, 5 * Gb), await CreatePlanDirectAsync(fixture, 10 * Gb) };

        var results = await Task.WhenAll(Enumerable.Range(0, 24).Select(index => AssignAsync(store, tenant, plans[index % plans.Length])));

        Assert.All(results, result => Assert.Contains(result.Outcome, new[] { ManagedServiceOutcome.Changed, ManagedServiceOutcome.PlanAlreadyAssigned }));
        var rows = await RowsOfAsync(fixture, tenant);
        // Every successful change produced exactly one row; nothing was lost or duplicated.
        Assert.Equal(results.Count(result => result.Outcome == ManagedServiceOutcome.Changed), rows.Count);
        Assert.Equal(1, rows.Count(row => row.Status == SubscriptionStatus.Active));
        Assert.Equal(1, rows.Count(row => row.IsOpen));
        Assert.All(rows.Where(row => !row.IsOpen), row =>
        {
            Assert.Equal(SubscriptionStatus.Superseded, row.Status);
            Assert.NotNull(row.EndsAt);
        });
        // The chain is coherent: each change superseded exactly the row that was open before it, so all but one row were superseded once.
        var superseded = results.Where(result => result.Value?.SupersededSubscriptionId is not null).Select(result => result.Value!.SupersededSubscriptionId!.Value.ToString()).ToList();
        Assert.Equal(superseded.Count, superseded.Distinct().Count());
        Assert.Equal(rows.Count - 1, superseded.Count);
    }

    [Fact]
    public async Task ParallelAssignmentsOfTheSamePlanCreateExactlyOneSubscription()
    {
        var store = StoreOf(fixture);
        var tenant = await NewTenantAsync();
        var plan = await CreatePlanDirectAsync(fixture);

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => AssignAsync(store, tenant, plan)));

        Assert.Equal(1, results.Count(result => result.Outcome == ManagedServiceOutcome.Changed));
        Assert.Equal(9, results.Count(result => result.Outcome == ManagedServiceOutcome.PlanAlreadyAssigned));
        Assert.Single(await RowsOfAsync(fixture, tenant));
    }

    [Fact]
    public async Task TwoParallelAssignmentsOverHttpLeaveOneActiveSubscription()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var admin = await world.SignInAsync(await world.SeedUserAsync(platformAdmin: true));
        var tenant = await world.AddTenantAsync();
        var first = await CreatePlanAsync(admin, bytes: Gb);
        var second = await CreatePlanAsync(admin, bytes: 5 * Gb);

        var replies = await Task.WhenAll(
            CallAsync(admin, HttpMethod.Put, SubscriptionPath(tenant.Id.Value), new { planId = first }),
            CallAsync(admin, HttpMethod.Put, SubscriptionPath(tenant.Id.Value), new { planId = second }));

        Assert.All(replies, reply => Assert.Equal(System.Net.HttpStatusCode.OK, reply.Status));
        var rows = await RowsOfAsync(fixture, tenant.Id);
        Assert.Equal(2, rows.Count);
        Assert.Equal(1, rows.Count(row => row.Status == SubscriptionStatus.Active));
        Assert.Equal(1, rows.Count(row => row.Status == SubscriptionStatus.Superseded));
    }

    [Fact]
    public async Task ParallelSuspendsApplyOnceAndTheRestSeeTheNewState()
    {
        var store = StoreOf(fixture);
        var tenant = await NewTenantAsync();
        var plan = await CreatePlanDirectAsync(fixture);
        await AssignAsync(store, tenant, plan);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => store.SuspendAsync(tenant, Actor, DateTimeOffset.UtcNow, default))));

        Assert.Equal(1, results.Count(result => result.Outcome == ManagedServiceOutcome.Changed));
        Assert.All(results.Where(result => result.Outcome != ManagedServiceOutcome.Changed),
            result => Assert.Equal((ManagedServiceOutcome.InvalidTransition, SubscriptionTransitionCodes.NotActive), (result.Outcome, result.Code)));
        Assert.Equal(SubscriptionStatus.Suspended, Assert.Single(await RowsOfAsync(fixture, tenant)).Status);
    }

    [Fact]
    public async Task ParallelPlanCreationWithTheSameNameYieldsOnePlan()
    {
        var store = StoreOf(fixture);
        var name = UniqueName("Race");
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => Task.Run(() =>
            store.CreatePlanAsync(new Plan(PlanId.New(), index % 2 == 0 ? name : name.ToUpperInvariant(), Gb, null, true, DateTimeOffset.UtcNow), Actor, DateTimeOffset.UtcNow, default))));
        Assert.Equal(1, results.Count(result => result.Outcome == ManagedServiceOutcome.Changed));
        Assert.Equal(7, results.Count(result => result.Outcome == ManagedServiceOutcome.PlanNameTaken));
    }

    [Fact]
    public async Task TheDatabaseRefusesASecondOpenSubscriptionEvenWithoutTheApplicationLock()
    {
        var tenant = await NewTenantAsync();
        var plan = await CreatePlanDirectAsync(fixture);
        await using var context = fixture.CreatePlatformContext();
        context.TenantSubscriptions.Add(TenantSubscription.Start(tenant, plan, StorageLimitOverride.None, DateTimeOffset.UtcNow, null, Actor));
        await context.SaveChangesAsync();

        await using var second = fixture.CreatePlatformContext();
        second.TenantSubscriptions.Add(TenantSubscription.Start(tenant, plan, StorageLimitOverride.None, DateTimeOffset.UtcNow, null, Actor));
        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(failure.InnerException).SqlState);

        // A suspended row is open too, so it blocks a second Active row; closed history rows do not.
        await using var third = fixture.CreatePlatformContext();
        var row = await third.TenantSubscriptions.SingleAsync(item => item.TenantId == tenant);
        row.Suspend(DateTimeOffset.UtcNow);
        await third.SaveChangesAsync();
        await using var fourth = fixture.CreatePlatformContext();
        fourth.TenantSubscriptions.Add(TenantSubscription.Start(tenant, plan, StorageLimitOverride.None, DateTimeOffset.UtcNow, null, Actor));
        await Assert.ThrowsAsync<DbUpdateException>(() => fourth.SaveChangesAsync());
    }

    [Fact]
    public async Task SubscriptionRowsCannotBeBrokenAtTheDatabaseLevel()
    {
        var tenant = await NewTenantAsync();
        var plan = await CreatePlanDirectAsync(fixture);
        await using var context = fixture.CreatePlatformContext();
        const string columns = "\"Id\",\"TenantId\",\"PlanId\",\"Status\",\"StartsAt\",\"EndsAt\",\"StorageLimitOverrideKind\",\"StorageLimitOverrideBytes\",\"CreatedAt\",\"UpdatedAt\"";
        foreach (var (status, endsAt, kind, bytes) in new (int Status, string EndsAt, int Kind, string Bytes)[]
        {
            (5, "NULL", 0, "NULL"),                           // unknown status
            (3, "NULL", 0, "NULL"),                           // closed row without an end
            (4, "now() - interval '1 day'", 0, "NULL"),       // ends before it starts
            (1, "NULL", 1, "NULL"),                           // limited override without bytes
            (1, "NULL", 1, "-5"),                             // negative override
            (1, "NULL", 1, "1000000000000001"),               // override above the maximum
            (1, "NULL", 2, "5"),                              // unlimited override carrying bytes
            (1, "NULL", 0, "5"),                              // no override carrying bytes
            (1, "NULL", 9, "NULL")                            // unknown override kind
        })
        {
            var statement = $"INSERT INTO tenant_subscriptions ({columns}) VALUES (gen_random_uuid(), '{tenant.Value}', '{plan.Value}', {status}, now(), {endsAt}, {kind}, {bytes}, now(), now())";
            var failure = await Assert.ThrowsAnyAsync<Exception>(() => context.Database.ExecuteSqlRawAsync(statement));
            Assert.IsType<PostgresException>(failure is PostgresException ? failure : failure.InnerException);
        }
    }

    [Fact]
    public async Task APlanUsedByASubscriptionCannotBePhysicallyDeleted()
    {
        var store = StoreOf(fixture);
        var tenant = await NewTenantAsync();
        var plan = await CreatePlanDirectAsync(fixture);
        await AssignAsync(store, tenant, plan);
        await using var context = fixture.CreatePlatformContext();
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => context.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM plans WHERE \"Id\" = {plan.Value}"));
        Assert.Equal(PostgresErrorCodes.RestrictViolation, Assert.IsType<PostgresException>(failure is PostgresException ? failure : failure.InnerException).SqlState);
    }

    [Fact]
    public async Task AFailedAuditWriteRollsBackTheWholeChange()
    {
        var store = StoreOf(fixture);
        var tenant = await NewTenantAsync();
        var five = await CreatePlanDirectAsync(fixture, 5 * Gb);
        var ten = await CreatePlanDirectAsync(fixture, 10 * Gb);
        await AssignAsync(store, tenant, five);
        var failing = UniqueName("Audit failure");
        await using var admin = fixture.CreatePlatformContext();
        await admin.Database.ExecuteSqlRawAsync("""
            CREATE OR REPLACE FUNCTION fail_managed_service_audit() RETURNS trigger AS $$
            BEGIN RAISE EXCEPTION 'audit refused' USING ERRCODE = 'P0001'; END $$ LANGUAGE plpgsql;
            CREATE TRIGGER tr_fail_managed_service_audit BEFORE INSERT ON audit_events
            FOR EACH ROW WHEN (NEW."Action" IN ('subscription.changed', 'plan.created')) EXECUTE FUNCTION fail_managed_service_audit();
            """);
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => AssignAsync(store, tenant, ten));
            await Assert.ThrowsAnyAsync<Exception>(() => store.CreatePlanAsync(new Plan(PlanId.New(), failing, Gb, null, true, DateTimeOffset.UtcNow), Actor, DateTimeOffset.UtcNow, default));
        }
        finally
        {
            await admin.Database.ExecuteSqlRawAsync("DROP TRIGGER tr_fail_managed_service_audit ON audit_events; DROP FUNCTION fail_managed_service_audit();");
        }

        // The superseding of the old row (flushed first inside the transaction) was rolled back together with the insert and the audit.
        var rows = await RowsOfAsync(fixture, tenant);
        var only = Assert.Single(rows);
        Assert.Equal((SubscriptionStatus.Active, five), (only.Status, only.PlanId));
        Assert.DoesNotContain(await AuditOfTenantAsync(fixture, tenant), row => row.Action == ManagedServiceAuditActions.SubscriptionChanged);
        await using var check = fixture.CreatePlatformContext();
        Assert.False(await check.Plans.AnyAsync(plan => plan.Name == failing));
    }

    [Fact]
    public async Task TenantScopedContextsCannotReadOrWriteSubscriptionsOrPlans()
    {
        var store = StoreOf(fixture);
        var first = await NewTenantAsync();
        var second = await NewTenantAsync();
        var plan = await CreatePlanDirectAsync(fixture);
        await AssignAsync(store, first, plan);
        await AssignAsync(store, second, plan);

        await using (var scoped = fixture.CreateContext(first))
        {
            var visible = await scoped.TenantSubscriptions.AsNoTracking().ToListAsync();
            Assert.Equal(first, Assert.Single(visible).TenantId);
            Assert.Empty(await scoped.TenantSubscriptions.AsNoTracking().Where(row => row.TenantId == second).ToListAsync());
        }
        await using (var unscoped = fixture.CreateContext((TenantId?)null))
        {
            Assert.Empty(await unscoped.TenantSubscriptions.AsNoTracking().ToListAsync());
        }
        // Writes need the explicit platform operations context, even for the tenant's own rows.
        await using var writer = fixture.CreateContext(first);
        writer.TenantSubscriptions.Add(TenantSubscription.Start(first, plan, StorageLimitOverride.None, DateTimeOffset.UtcNow, null, Actor));
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.SaveChangesAsync());
        await using var planWriter = fixture.CreateContext(first);
        planWriter.Plans.Add(new Plan(PlanId.New(), UniqueName(), Gb, null, true, DateTimeOffset.UtcNow));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => planWriter.SaveChangesAsync());
    }

    [Fact]
    public async Task TheEntitlementReaderResolvesStatusAndLimitFromTheSharedRules()
    {
        var store = StoreOf(fixture);
        var reader = new PostgreSqlTenantEntitlementReader(OptionsOf(fixture));
        var tenant = await NewTenantAsync();
        var plan = await CreatePlanDirectAsync(fixture, 5 * Gb);
        var now = DateTimeOffset.UtcNow;

        var none = await reader.GetAsync(tenant, now, default);
        Assert.Equal((EntitlementStatus.NoSubscription, false), (none.Status, none.Permits(EntitlementOperation.StartNewBackupRun)));
        Assert.Null(none.StorageLimit);

        var assigned = await store.AssignPlanAsync(tenant, plan, StorageLimitOverride.None, now.AddDays(10), Actor, now, default);
        var active = await reader.GetAsync(tenant, now, default);
        Assert.Equal((EntitlementStatus.Active, true, (long?)5 * Gb, EffectiveStorageLimitSource.Plan),
            (active.Status, active.Permits(EntitlementOperation.StartNewBackupRun), active.StorageLimit!.Value.Bytes, active.StorageLimit.Value.Source));
        Assert.Equal(assigned.Value!.Subscription.Subscription.Id, active.SubscriptionId);

        await store.SetStorageLimitOverrideAsync(tenant, StorageLimitOverride.Limited(2 * Gb), Actor, now.AddSeconds(1), default);
        Assert.Equal(2 * Gb, (await reader.GetAsync(tenant, now, default)).StorageLimit!.Value.Bytes);

        await store.SuspendAsync(tenant, Actor, now.AddSeconds(2), default);
        var suspended = await reader.GetAsync(tenant, now, default);
        Assert.Equal(EntitlementStatus.Suspended, suspended.Status);
        Assert.False(suspended.Permits(EntitlementOperation.StartNewBackupRun));
        Assert.All(Enum.GetValues<EntitlementOperation>().Where(operation => operation != EntitlementOperation.StartNewBackupRun), operation => Assert.True(suspended.Permits(operation)));

        // Past the planned end the same row reads as Expired without any writer having touched it.
        var expired = await reader.GetAsync(tenant, now.AddDays(11), default);
        Assert.Equal((EntitlementStatus.Expired, "subscription_required"), (expired.Status, expired.DenialCode(EntitlementOperation.StartNewBackupRun)));
    }
}
