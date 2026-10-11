using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.ManagedService.Entitlement;
using BackupPlatform.ManagedService.Plans;
using BackupPlatform.ManagedService.Quota;
using BackupPlatform.ManagedService.Subscriptions;
using Microsoft.EntityFrameworkCore;
using static BackupPlatform.IntegrationTests.Quota.AdmissionTestKit;

namespace BackupPlatform.IntegrationTests.Quota;

/// <summary>
/// The gate for a NEW BackupRun against real PostgreSQL with the real admission policy: subscription first, then quota, the soft limit of a
/// running backup, the retry invariant, atomicity of run creation, and many nodes asking at once.
/// </summary>
public sealed class AdmissionIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static readonly IReadOnlyList<ReplicaSpec> Central = [ReplicaSpec.Central];
    private readonly QuotaWorld _world = new(fixture);

    private async Task<TenantQuotaStatus> StatusAsync(TenantId tenantId, DateTimeOffset? at = null) =>
        (await _world.Usage().GetQuotaStatusAsync(tenantId, at ?? T0, default))!;

    // ---- the quota gate ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(4_500_000_000, BackupAdmissionDenialCodes.QuotaSizeUnknown)]
    [InlineData(4_999_999_999, BackupAdmissionDenialCodes.QuotaSizeUnknown)]
    [InlineData(5_000_000_000, BackupAdmissionDenialCodes.QuotaExceeded)]
    [InlineData(5_500_000_000, BackupAdmissionDenialCodes.QuotaExceeded)]
    public async Task AFirstCountedRunNeverBypassesHardQuota(long usedBytes, string denialCode)
    {
        var agent = await _world.AgentAsync();
        var history = await _world.JobAsync(agent);
        var fresh = await _world.JobAsync(agent);
        var store = _world.RunStore();
        await _world.PublishAsync(store, agent, history, usedBytes, Central, T0);

        var result = await store.StartRunAsync(agent.TenantId, fresh.Id, "first-key", T0.AddMinutes(1), default);

        Assert.Equal(usedBytes, (await _world.StoredUsageAsync(agent.TenantId))!.UsedBytes);
        Assert.Empty(await ReservationsAsync(fixture, agent.TenantId));
        Assert.Equal(BackupStartOutcome.AdmissionDenied, result.Outcome);
        Assert.Equal(denialCode, result.DenialCode);
        Assert.Null(result.Run);
        Assert.Equal(0, await _world.Harness.CountRunsAsync(fresh.Id));
    }

    [Theory]
    [InlineData(2_500_000_000, true)]
    [InlineData(2_500_000_001, false)]
    public async Task TheSizeOfTheJobsPreviousArtifactDecidesTheLastBytes(long previousBytes, bool admitted)
    {
        var agent = await _world.AgentAsync();
        var job = await _world.JobAsync(agent);
        var store = _world.RunStore();
        await _world.PublishAsync(store, agent, job, previousBytes, Central, T0);

        // used + the same job's last artifact must still fit: 2.5 GB + 2.5 GB is exactly the 5 GB limit, one byte more is not.
        var result = await store.StartRunAsync(agent.TenantId, job.Id, "next", T0.AddMinutes(1), default);

        var usage = (await _world.StoredUsageAsync(agent.TenantId))!;
        Assert.True(usage.UsedBytes < 5 * Gb);
        if (admitted)
        {
            Assert.Equal(BackupStartOutcome.Started, result.Outcome);
            var reservation = Assert.Single(await ReservationsAsync(fixture, agent.TenantId));
            Assert.Equal(result.Run!.Id, reservation.RunId);
            Assert.Equal(previousBytes, reservation.Bytes);
            Assert.Null(reservation.ReleasedAt);
            Assert.Equal(previousBytes, usage.ReservedBytes);
            return;
        }
        Assert.Equal(BackupStartOutcome.AdmissionDenied, result.Outcome);
        Assert.Equal(BackupAdmissionDenialCodes.QuotaExceeded, result.DenialCode);
        Assert.Empty(await ReservationsAsync(fixture, agent.TenantId));
        Assert.Equal(1, await _world.Harness.CountRunsAsync(job.Id));
    }

    [Fact]
    public async Task ADeniedStartLeavesNoTraceSoTheSameKeyStartsARunOnceCapacityIsBack()
    {
        var agent = await _world.AgentAsync();
        var history = await _world.JobAsync(agent);
        var store = _world.RunStore();
        await _world.PublishAsync(store, agent, history, 5_500_000_000, Central, T0);

        var denied = await store.StartRunAsync(agent.TenantId, history.Id, "retry-me", T0.AddMinutes(1), default);
        Assert.Equal(BackupStartOutcome.AdmissionDenied, denied.Outcome);
        Assert.Equal(1, await _world.Harness.CountRunsAsync(history.Id));

        await SetOverrideAsync(_world, agent.TenantId, StorageLimitOverride.Limited(12 * Gb));
        var again = await store.StartRunAsync(agent.TenantId, history.Id, "retry-me", T0.AddMinutes(2), default);

        // Started, not Replayed: the refusal did not consume the idempotency key.
        Assert.Equal(BackupStartOutcome.Started, again.Outcome);
        Assert.Equal("retry-me", again.Run!.IdempotencyKey);
        Assert.Equal(2, await _world.Harness.CountRunsAsync(history.Id));
    }

    [Fact]
    public async Task WarnOnlyPolicyAdmitsEvenThoughTheTenantIsOverQuota()
    {
        var key = await CreatePolicyAsync(_world, QuotaEnforcementMode.WarnOnly);
        var agent = await _world.AgentAsync(5 * Gb, key);
        var history = await _world.JobAsync(agent);
        var fresh = await _world.JobAsync(agent);
        var store = _world.RunStore();
        await _world.PublishAsync(store, agent, history, 5_500_000_000, Central, T0);

        var status = await StatusAsync(agent.TenantId);
        Assert.Equal((QuotaState.OverQuota, QuotaEnforcementMode.WarnOnly, key), (status.State, status.EnforcementMode, status.PolicyKey));
        Assert.True(status.NewBackupsAllowed);
        Assert.Null(status.DenialCode);

        var viaFreshJob = await StartAsync(store, fresh, "fresh", T0.AddMinutes(1));
        // The job whose last artifact alone would overflow is admitted too: nothing is ever refused, it is only shown.
        var viaHistoryJob = await StartAsync(store, history, "history", T0.AddMinutes(2));

        Assert.NotEqual(viaFreshJob.Id, viaHistoryJob.Id);
        Assert.Equal(QuotaState.OverQuota, (await StatusAsync(agent.TenantId)).State);
    }

    [Fact]
    public async Task NonePolicyDecidesNothingAndReservesNothing()
    {
        var key = await CreatePolicyAsync(_world, QuotaEnforcementMode.None);
        var agent = await _world.AgentAsync(5 * Gb, key);
        var history = await _world.JobAsync(agent);
        var store = _world.RunStore();
        await _world.PublishAsync(store, agent, history, 5_500_000_000, Central, T0);

        await StartAsync(store, history, "again", T0.AddMinutes(1));

        Assert.Empty(await ReservationsAsync(fixture, agent.TenantId));
        var status = await StatusAsync(agent.TenantId);
        Assert.Equal(QuotaState.Unlimited, status.State);
        Assert.Equal(5_500_000_000, status.UsedBytes);
    }

    [Fact]
    public async Task AnUnlimitedPlanAdmitsWithoutReservingAnything()
    {
        var agent = await _world.AgentAsync(limitBytes: null);
        var history = await _world.JobAsync(agent);
        var store = _world.RunStore();
        await _world.PublishAsync(store, agent, history, 50 * Gb, Central, T0);

        await StartAsync(store, history, "again", T0.AddMinutes(1));

        Assert.Empty(await ReservationsAsync(fixture, agent.TenantId));
        var status = await StatusAsync(agent.TenantId);
        Assert.True(status.IsUnlimited);
        Assert.Equal(QuotaState.Unlimited, status.State);
        Assert.Null(status.LimitBytes);
    }

    [Fact]
    public async Task TheLegacyUnlimitedSystemPlanAdmitsEveryRun()
    {
        var agent = await _world.AgentAsync(subscribe: false);
        await using (var context = fixture.CreatePlatformContext())
        {
            var legacy = await context.Plans.AsNoTracking().SingleAsync(plan => plan.SystemKey == Plan.LegacyUnlimitedSystemKey);
            await _world.AssignAsync(agent.TenantId, legacy.Id);
        }
        var history = await _world.JobAsync(agent);
        var store = _world.RunStore();
        await _world.PublishAsync(store, agent, history, 20 * Gb, Central, T0);

        await StartAsync(store, history, "again", T0.AddMinutes(1));

        Assert.True((await StatusAsync(agent.TenantId)).IsUnlimited);
        Assert.Empty(await ReservationsAsync(fixture, agent.TenantId));
    }

    // ---- limit overrides ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ALimitedOverrideReplacesThePlanLimitInBothDirections()
    {
        var agent = await _world.AgentAsync();
        var history = await _world.JobAsync(agent);
        var store = _world.RunStore();
        await _world.PublishAsync(store, agent, history, Gb, Central, T0);
        var one = await _world.JobAsync(agent);
        var two = history;
        var three = history;

        // The 5 GB plan has room; a 1 GB override has none (used == limit).
        await SetOverrideAsync(_world, agent.TenantId, StorageLimitOverride.Limited(Gb));
        var status = await StatusAsync(agent.TenantId);
        Assert.Equal((Gb, QuotaState.OverQuota), (status.LimitBytes, status.State));
        var denied = await store.StartRunAsync(agent.TenantId, one.Id, "k1", T0.AddMinutes(1), default);
        Assert.Equal((BackupStartOutcome.AdmissionDenied, BackupAdmissionDenialCodes.QuotaExceeded), (denied.Outcome, denied.DenialCode));

        // The known 1 GB artifact fits exactly alongside the stored 1 GB.
        await SetOverrideAsync(_world, agent.TenantId, StorageLimitOverride.Limited(2 * Gb));
        await StartAsync(store, two, "k2", T0.AddMinutes(2));

        // A larger override than the plan limit opens the gate on a tenant that was over its plan.
        var publisher = await AddAgentAsync(_world, agent.TenantId);
        await _world.PublishAsync(_world.RunStore(), publisher, await CreateJobAsync(_world, publisher), 5 * Gb, Central, T0);
        await SetOverrideAsync(_world, agent.TenantId, StorageLimitOverride.None);
        var overPlan = await store.StartRunAsync(agent.TenantId, three.Id, "k3", T0.AddMinutes(3), default);
        Assert.Equal(BackupStartOutcome.AdmissionDenied, overPlan.Outcome);
        await SetOverrideAsync(_world, agent.TenantId, StorageLimitOverride.Limited(20 * Gb));
        await StartAsync(store, three, "k3", T0.AddMinutes(4));
    }

    [Fact]
    public async Task AnUnlimitedOverrideAdmitsAnOverQuotaTenantAndClearingItRestoresTheDenial()
    {
        var agent = await _world.AgentAsync();
        var history = await _world.JobAsync(agent);
        var store = _world.RunStore();
        await _world.PublishAsync(store, agent, history, 5_500_000_000, Central, T0);
        var one = await _world.JobAsync(agent);
        var two = await _world.JobAsync(agent);
        Assert.Equal(BackupStartOutcome.AdmissionDenied, (await store.StartRunAsync(agent.TenantId, one.Id, "k0", T0.AddMinutes(1), default)).Outcome);

        await SetOverrideAsync(_world, agent.TenantId, StorageLimitOverride.Unlimited);
        await StartAsync(store, one, "k1", T0.AddMinutes(2));
        var unlimited = await StatusAsync(agent.TenantId);
        Assert.True(unlimited.IsUnlimited);
        Assert.Equal(QuotaState.Unlimited, unlimited.State);

        await SetOverrideAsync(_world, agent.TenantId, StorageLimitOverride.None);
        var denied = await store.StartRunAsync(agent.TenantId, two.Id, "k2", T0.AddMinutes(3), default);
        Assert.Equal((BackupStartOutcome.AdmissionDenied, BackupAdmissionDenialCodes.QuotaExceeded), (denied.Outcome, denied.DenialCode));
    }

    [Fact]
    public async Task ADowngradeBelowCurrentUsageDeniesNewRunsButTouchesNoExistingBackup()
    {
        var agent = await _world.AgentAsync(10 * Gb);
        var history = await _world.JobAsync(agent);
        var fresh = await _world.JobAsync(agent);
        var store = _world.RunStore();
        var (publishedRun, _) = await _world.PublishAsync(store, agent, history, 4_500_000_000, Central, T0);
        Assert.Equal(QuotaState.Normal, (await StatusAsync(agent.TenantId)).State);

        await _world.AssignAsync(agent.TenantId, await _world.PlanAsync(Gb));

        var status = await StatusAsync(agent.TenantId);
        Assert.Equal((QuotaState.OverQuota, Gb, 0L, 4_500_000_000), (status.State, status.LimitBytes, status.RemainingBytes, status.UsedBytes));
        var denied = await store.StartRunAsync(agent.TenantId, fresh.Id, "after-downgrade", T0.AddMinutes(1), default);
        Assert.Equal((BackupStartOutcome.AdmissionDenied, BackupAdmissionDenialCodes.QuotaExceeded), (denied.Outcome, denied.DenialCode));
        Assert.Equal(1, await _world.Harness.CountArtifactsAsync(publishedRun.Id));
        Assert.Equal((4_500_000_000, 4_500_000_000), await _world.SourceUsageAsync(agent.TenantId));
        Assert.Equal(4_500_000_000, (await _world.StoredUsageAsync(agent.TenantId))!.UsedBytes);

        await _world.AssignAsync(agent.TenantId, await _world.PlanAsync(10 * Gb));
        await StartAsync(store, history, "after-upgrade", T0.AddMinutes(2));
    }

    // ---- the subscription gate ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task ATenantWithoutASubscriptionIsDeniedWithSubscriptionRequired()
    {
        var agent = await _world.AgentAsync(subscribe: false);
        var job = await _world.JobAsync(agent);

        var result = await _world.RunStore().StartRunAsync(agent.TenantId, job.Id, "k", T0, default);

        Assert.Equal((BackupStartOutcome.AdmissionDenied, BackupAdmissionDenialCodes.SubscriptionRequired), (result.Outcome, result.DenialCode));
        Assert.Equal(0, await _world.Harness.CountRunsAsync(job.Id));
        Assert.Equal(EntitlementStatus.NoSubscription, (await StatusAsync(agent.TenantId)).EntitlementStatus);
    }

    [Fact]
    public async Task SuspendingDeniesNewRunsAndResumingAdmitsAgainWithTheSameKey()
    {
        var agent = await _world.AgentAsync(limitBytes: null);
        var job = await _world.JobAsync(agent);
        var store = _world.RunStore();

        await SuspendAsync(_world, agent.TenantId);
        var denied = await store.StartRunAsync(agent.TenantId, job.Id, "k", T0, default);
        Assert.Equal((BackupStartOutcome.AdmissionDenied, BackupAdmissionDenialCodes.SubscriptionRequired), (denied.Outcome, denied.DenialCode));
        Assert.Equal(0, await _world.Harness.CountRunsAsync(job.Id));
        var suspended = await StatusAsync(agent.TenantId);
        Assert.Equal((EntitlementStatus.Suspended, false, BackupAdmissionDenialCodes.SubscriptionRequired), (suspended.EntitlementStatus, suspended.NewBackupsAllowed, suspended.DenialCode));

        await ResumeAsync(_world, agent.TenantId);
        var resumed = await store.StartRunAsync(agent.TenantId, job.Id, "k", T0.AddMinutes(1), default);
        Assert.Equal(BackupStartOutcome.Started, resumed.Outcome);
    }

    [Fact]
    public async Task ASubscriptionThatEndedDeniesFromItsEndWithoutAnySweeper()
    {
        var agent = await _world.AgentAsync(subscribe: false);
        var endsAt = DateTimeOffset.UtcNow.AddHours(1);
        await _world.AssignAsync(agent.TenantId, await _world.PlanAsync(null), endsAt: endsAt);
        var before = await _world.JobAsync(agent);
        var after = await _world.JobAsync(agent);
        var store = _world.RunStore();

        await StartAsync(store, before, "before-end", T0);
        var denied = await store.StartRunAsync(agent.TenantId, after.Id, "after-end", endsAt.AddMinutes(1), default);

        Assert.Equal((BackupStartOutcome.AdmissionDenied, BackupAdmissionDenialCodes.SubscriptionRequired), (denied.Outcome, denied.DenialCode));
        Assert.Equal(0, await _world.Harness.CountRunsAsync(after.Id));
        Assert.Equal(EntitlementStatus.Active, (await StatusAsync(agent.TenantId, T0)).EntitlementStatus);
        Assert.Equal(EntitlementStatus.Expired, (await StatusAsync(agent.TenantId, endsAt)).EntitlementStatus);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheSubscriptionIsDecidedBeforeTheQuota(bool noSubscription)
    {
        var agent = await _world.AgentAsync(subscribe: !noSubscription);
        var history = await _world.JobAsync(agent);
        var fresh = await _world.JobAsync(agent);
        // Usage beyond any 5 GB limit, published while nobody asked admission.
        await _world.PublishAsync(_world.RunStore(AdmitAllBackupAdmission.Instance), agent, history, 6 * Gb, Central, T0);
        if (!noSubscription)
        {
            await SuspendAsync(_world, agent.TenantId);
        }

        var result = await _world.RunStore().StartRunAsync(agent.TenantId, fresh.Id, "k", T0.AddMinutes(1), default);

        Assert.Equal((BackupStartOutcome.AdmissionDenied, BackupAdmissionDenialCodes.SubscriptionRequired), (result.Outcome, result.DenialCode));
    }

    // ---- reservations bound transfer, not persistence of already-written artifacts -------------------------------------------

    [Fact]
    public async Task AHistoricalReservationIsExposedAsAnArtifactByteBound()
    {
        var agent = await _world.AgentAsync();
        var job = await _world.JobAsync(agent);
        var store = _world.RunStore();
        await _world.PublishAsync(store, agent, job, Gb, Central, T0);
        var run = await StartAsync(store, job, "bounded", T0.AddMinutes(1));
        var claim = await ClaimStartedAsync(store, agent, T0.AddMinutes(1));
        Assert.Equal(run.Id, claim.Run.Id);
        Assert.Equal(Gb, claim.MaxArtifactBytes);
        Assert.Equal(Gb, (await store.GetAttemptAsync(agent.TenantId, agent.AgentId, claim.Attempt.Id, default))!.MaxArtifactBytes);
    }

    // ---- replay and retry never ask again -----------------------------------------------------------------------------------

    [Fact]
    public async Task AReplayedIdempotencyKeyReturnsTheOriginalRunEvenWhenTheTenantCannotStartNewOnesAnymore()
    {
        var agent = await _world.AgentAsync();
        var job = await _world.JobAsync(agent);
        await _world.PublishAsync(_world.RunStore(), agent, job, 100, Central, T0.AddSeconds(-1));
        var publisher = await AddAgentAsync(_world, agent.TenantId);
        var counting = new CountingAdmission(_world.Admission());
        var store = _world.RunStore(counting);
        var original = await StartAsync(store, job, "same-key", T0);
        Assert.Equal(1, counting.Calls);
        await _world.PublishAsync(_world.RunStore(), publisher, await CreateJobAsync(_world, publisher), 5_500_000_000, Central, T0);
        await SuspendAsync(_world, agent.TenantId);

        var replay = await store.StartRunAsync(agent.TenantId, job.Id, "same-key", T0.AddMinutes(1), default);

        Assert.Equal(BackupStartOutcome.Replayed, replay.Outcome);
        Assert.Equal(original.Id, replay.Run!.Id);
        Assert.Equal(1, counting.Calls);
        Assert.Equal(2, await _world.Harness.CountRunsAsync(job.Id));
        var fresh = await store.StartRunAsync(agent.TenantId, job.Id, "other-key", T0.AddMinutes(2), default);
        Assert.Equal(BackupStartOutcome.AdmissionDenied, fresh.Outcome);
        Assert.Equal(2, counting.Calls);
    }

    [Fact]
    public async Task AdmissionIsAskedOncePerNewRunWithItsIdentityAndTheManualTrigger()
    {
        var agent = await _world.AgentAsync(limitBytes: null);
        var job = await _world.JobAsync(agent);
        var counting = new CountingAdmission(_world.Admission());
        var store = _world.RunStore(counting);

        var first = await StartAsync(store, job, "k1", T0);
        var second = await StartAsync(store, job, "k2", T0.AddMinutes(1));

        Assert.Equal([first.Id, second.Id], counting.Requests.Select(request => request.RunId).ToArray());
        Assert.All(counting.Requests, request =>
        {
            Assert.Equal(BackupTriggerTypes.Manual, request.TriggerType);
            Assert.Equal((agent.TenantId, job.Id), (request.TenantId, request.JobId));
        });
        Assert.Empty(counting.Released);
    }

    public enum Blocked { OverQuota, Suspended }

    [Theory]
    [InlineData(Blocked.OverQuota)]
    [InlineData(Blocked.Suspended)]
    public async Task RetriesOfAnAdmittedRunNeverAskAdmissionAgainButANewRunAlwaysDoes(Blocked blocked)
    {
        var agent = await _world.AgentAsync();
        var publisher = await AddAgentAsync(_world, agent.TenantId);
        var job = await _world.JobAsync(agent);
        await _world.PublishAsync(_world.RunStore(), agent, job, Gb / 10, Central, T0.AddSeconds(-1));
        var counting = new CountingAdmission(_world.Admission());
        var store = _world.RunStore(counting);
        var run = await StartAsync(store, job, "first", T0);
        await FailNextAsync(store, agent, BackupFailureCodes.DatabaseUnavailable, T0);
        Assert.Equal(BackupStatus.RetryPending, (await RunAsync(_world, agent.TenantId, run.Id)).Status);

        // From here on the tenant may not start anything new.
        if (blocked == Blocked.Suspended)
        {
            await SuspendAsync(_world, agent.TenantId);
        }
        else
        {
            await _world.PublishAsync(_world.RunStore(), publisher, await CreateJobAsync(_world, publisher), 5_500_000_000, Central, T0);
        }

        // Automatic retry.
        var started = await store.StartDueRetriesAsync(T0.AddMinutes(1), 500, default);
        Assert.Contains(started, item => item.RunId == run.Id);
        await FailNextAsync(store, agent, BackupFailureCodes.OperationTimedOut, T0.AddMinutes(1));
        Assert.Equal(BackupStatus.RetryPending, (await RunAsync(_world, agent.TenantId, run.Id)).Status);

        // Manual retry of the run that is waiting for its backoff.
        var manual = await store.RetryRunAsync(agent.TenantId, run.Id, T0.AddMinutes(2), default);
        Assert.Equal(BackupRetryOutcome.Started, manual.Outcome);
        var claim = await ClaimStartedAsync(store, agent, T0.AddMinutes(2));
        Assert.Equal(run.Id, claim.Run.Id);
        await BackupStoreHarness.CompleteAsync(store, claim, QuotaWorld.Success(Gb / 10, Central, T0.AddMinutes(3)), T0.AddMinutes(3));

        Assert.Equal(BackupStatus.Succeeded, (await RunAsync(_world, agent.TenantId, run.Id)).Status);
        Assert.Equal(3, await _world.Harness.CountAttemptsAsync(run.Id));
        Assert.Equal(1, counting.Calls);

        // A new run is a new question, and the answer is no.
        var denied = await store.StartRunAsync(agent.TenantId, job.Id, "second", T0.AddMinutes(4), default);
        Assert.Equal(BackupStartOutcome.AdmissionDenied, denied.Outcome);
        Assert.Equal(blocked == Blocked.Suspended ? BackupAdmissionDenialCodes.SubscriptionRequired : BackupAdmissionDenialCodes.QuotaExceeded, denied.DenialCode);
        Assert.Equal(2, counting.Calls);
        Assert.NotEqual(counting.Requests[0].RunId, counting.Requests[1].RunId);
    }

    [Fact]
    public async Task ReopeningAFailedRunByHandIsNotANewRunAndHoldsNoReservation()
    {
        var agent = await _world.AgentAsync();
        var publisher = await AddAgentAsync(_world, agent.TenantId);
        var job = await _world.JobAsync(agent);
        await _world.PublishAsync(_world.RunStore(), agent, job, 100, Central, T0.AddSeconds(-1));
        var counting = new CountingAdmission(_world.Admission());
        var store = _world.RunStore(counting);
        var run = await StartAsync(store, job, "first", T0);
        await FailNextAsync(store, agent, BackupFailureCodes.PreCheckFailed, T0);
        Assert.Equal(BackupStatus.Failed, (await RunAsync(_world, agent.TenantId, run.Id)).Status);
        await _world.PublishAsync(_world.RunStore(), publisher, await CreateJobAsync(_world, publisher), 5_500_000_000, Central, T0);

        var reopened = await store.RetryRunAsync(agent.TenantId, run.Id, T0.AddMinutes(1), default);

        Assert.Equal(BackupRetryOutcome.Started, reopened.Outcome);
        Assert.Equal(1, counting.Calls);
        Assert.Empty(await UnreleasedAsync(fixture, agent.TenantId));
        Assert.Equal(2, await _world.Harness.CountAttemptsAsync(run.Id));
    }

    // ---- atomicity of run creation ------------------------------------------------------------------------------------------

    [Fact]
    public async Task AReservationIsGivenBackWhenCreatingTheAdmittedRunFails()
    {
        var agent = await _world.AgentAsync();
        var job = await _world.JobAsync(agent);
        await _world.PublishAsync(_world.RunStore(), agent, job, Gb, Central, T0);
        var counting = new CountingAdmission(_world.Admission());
        var store = _world.RunStore(counting);

        // A key beyond the column limit passes admission (and takes a reservation) but cannot become a run.
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.StartRunAsync(agent.TenantId, job.Id, new string('k', 200), T0.AddMinutes(1), default));

        var request = Assert.Single(counting.Requests);
        Assert.Equal(request.RunId, Assert.Single(counting.Released));
        var reservation = await ReservationOfAsync(fixture, agent.TenantId, request.RunId);
        Assert.Equal((Gb, QuotaReservation.ReasonNotCreated), (reservation.Bytes, reservation.ReleaseReason));
        Assert.NotNull(reservation.ReleasedAt);
        Assert.Empty(await UnreleasedAsync(fixture, agent.TenantId));
        Assert.Equal(0, (await _world.StoredUsageAsync(agent.TenantId))!.ReservedBytes);
        Assert.Equal(1, await _world.Harness.CountRunsAsync(job.Id));
    }

    [Fact]
    public async Task AReservationIsGivenBackWhenTheSkipIndexRefusesTheAdmittedRun()
    {
        var agent = await _world.AgentAsync();
        var rivalAgent = await AddAgentAsync(_world, agent.TenantId);
        var job = await CreateJobAsync(_world, agent, BackupOverlapPolicy.Skip);
        await _world.PublishAsync(_world.RunStore(), agent, job, Gb, Central, T0);
        var rivalRunId = Guid.Empty;
        // Between the overlap check and the insert another writer (one that does not take this agent's lock) activates a run of the same job.
        var counting = new CountingAdmission(_world.Admission())
        {
            AfterAdmitted = async _ => rivalRunId = await InsertActiveRunAsync(job, rivalAgent)
        };
        var store = _world.RunStore(counting);

        var result = await store.StartRunAsync(agent.TenantId, job.Id, "mine", T0.AddMinutes(2), default);

        Assert.Equal(BackupStartOutcome.SkippedOverlap, result.Outcome);
        Assert.Equal(rivalRunId, result.ActiveRunId);
        Assert.Null(result.Run);
        var request = Assert.Single(counting.Requests);
        Assert.Equal(request.RunId, Assert.Single(counting.Released));
        var reservation = await ReservationOfAsync(fixture, agent.TenantId, request.RunId);
        Assert.Equal(QuotaReservation.ReasonNotCreated, reservation.ReleaseReason);
        Assert.Empty(await UnreleasedAsync(fixture, agent.TenantId));
        Assert.Equal(0, (await _world.StoredUsageAsync(agent.TenantId))!.ReservedBytes);
        // The history run and the rival: the admitted run was never created.
        Assert.Equal(2, await _world.Harness.CountRunsAsync(job.Id));
    }

    private async Task<Guid> InsertActiveRunAsync(BackupJob job, StoreAgent otherAgent)
    {
        var run = new BackupRun(Guid.NewGuid(), job.TenantId, job.Id, otherAgent.AgentId, job.Configuration, "rival-key", T0.AddMinutes(1), job.RetryPolicy, BackupOverlapPolicy.Skip);
        run.QueueFirstAttempt(Guid.NewGuid(), T0.AddMinutes(1));
        await using var context = fixture.CreatePlatformContext();
        context.BackupRuns.Add(run);
        await context.SaveChangesAsync();
        return run.Id;
    }

    // ---- many nodes asking at once -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(5, 800_000_000)]
    [InlineData(8, 550_000_000)]
    public async Task ParallelAdmissionsNeverBookMoreThanTheRemainingSpace(int racers, long previousBytes)
    {
        // Every job's last artifact is previousBytes, so the tenant uses racers * previousBytes (4 GB / 4.4 GB of 5 GB) and each new run
        // estimates previousBytes: one fits (4.8 / 4.95 GB), a second would project 5.6 / 5.5 GB.
        var first = await _world.AgentAsync();
        var agents = new List<StoreAgent> { first };
        for (var index = 1; index < racers; index++)
        {
            agents.Add(await AddAgentAsync(_world, first.TenantId));
        }
        var jobs = new List<BackupJob>();
        var setup = _world.RunStore(AdmitAllBackupAdmission.Instance);
        foreach (var agent in agents)
        {
            var job = await _world.JobAsync(agent);
            await _world.PublishAsync(setup, agent, job, previousBytes, Central, T0);
            jobs.Add(job);
        }
        Assert.Equal(racers * previousBytes, (await _world.StoredUsageAsync(first.TenantId))!.UsedBytes);

        var results = await Task.WhenAll(jobs.Select((job, index) =>
            _world.RunStore().StartRunAsync(first.TenantId, job.Id, $"race-{index}", T0.AddMinutes(1), default)));

        var admitted = Assert.Single(results, result => result.Outcome == BackupStartOutcome.Started);
        var denied = results.Where(result => result.Outcome != BackupStartOutcome.Started).ToList();
        Assert.Equal(racers - 1, denied.Count);
        Assert.All(denied, result => Assert.Equal((BackupStartOutcome.AdmissionDenied, BackupAdmissionDenialCodes.QuotaExceeded), (result.Outcome, result.DenialCode)));
        var reservation = Assert.Single(await UnreleasedAsync(fixture, first.TenantId));
        Assert.Equal((admitted.Run!.Id, previousBytes), (reservation.RunId, reservation.Bytes));
        var usage = (await _world.StoredUsageAsync(first.TenantId))!;
        Assert.Equal((racers * previousBytes, previousBytes), (usage.UsedBytes, usage.ReservedBytes));
        // racers history runs plus the single admitted one.
        Assert.Equal(racers + 1, (await _world.RunStore().ListRunsAsync(first.TenantId, null, 200, default)).Count);
    }

    [Fact]
    public async Task ParallelHardAdmissionsWithoutAnEstimateAreAllDenied()
    {
        const int racers = 8;
        var first = await _world.AgentAsync();
        var jobs = new List<BackupJob> { await _world.JobAsync(first) };
        for (var index = 1; index < racers; index++)
        {
            jobs.Add(await _world.JobAsync(await AddAgentAsync(_world, first.TenantId)));
        }

        var results = await Task.WhenAll(jobs.Select((job, index) =>
            _world.RunStore().StartRunAsync(first.TenantId, job.Id, $"race-{index}", T0, default)));

        Assert.All(results, result => Assert.Equal((BackupStartOutcome.AdmissionDenied, BackupAdmissionDenialCodes.QuotaSizeUnknown), (result.Outcome, result.DenialCode)));
        Assert.Empty(await ReservationsAsync(fixture, first.TenantId));
        Assert.Empty(await _world.RunStore().ListRunsAsync(first.TenantId, null, 200, default));
        Assert.Equal(0, (await _world.StoredUsageAsync(first.TenantId))?.ReservedBytes ?? 0);
    }
}
