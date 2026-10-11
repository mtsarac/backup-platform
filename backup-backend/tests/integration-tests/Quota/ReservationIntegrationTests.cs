using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Infrastructure.Backups;
using BackupPlatform.ManagedService.Administration;
using BackupPlatform.ManagedService.Quota;
using Microsoft.EntityFrameworkCore;
using static BackupPlatform.IntegrationTests.Quota.AdmissionTestKit;

namespace BackupPlatform.IntegrationTests.Quota;

/// <summary>
/// The reservation an admitted run holds: created with the admission, keyed by the run, given back by every way a run can end, by expiry, as an
/// orphan and when a callback was missed, and always reconcilable from the table.
/// </summary>
public sealed class ReservationIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static readonly IReadOnlyList<ReplicaSpec> Central = [ReplicaSpec.Central];
    private static readonly DateTimeOffset At = T0.AddMinutes(10);
    private readonly QuotaWorld _world = new(fixture);

    /// <summary>A tenant (5 GB unless told) whose job already has one finished backup, so the job's next run estimates that artifact's size.</summary>
    private sealed record Seed(StoreAgent Agent, BackupJob Job, PostgreSqlBackupRunStore Store, BackupRun HistoryRun);

    private async Task<Seed> SeedAsync(QuotaWorld? world = null, long previousBytes = Gb, long limitBytes = 5 * Gb, BackupRetryPolicy? retry = null)
    {
        world ??= _world;
        var agent = await world.AgentAsync(limitBytes);
        var job = await CreateJobAsync(world, agent, retry: retry);
        var store = world.RunStore();
        var (history, _) = await world.PublishAsync(store, agent, job, previousBytes, Central, T0);
        return new Seed(agent, job, store, history);
    }

    private static async Task<TenantStorageUsage> UsageAsync(QuotaWorld world, Seed seed) => (await world.StoredUsageAsync(seed.Agent.TenantId))!;

    // ---- creation ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnAdmittedRunHoldsAReservationKeyedByItsRunIdAndTheStateCountsIt()
    {
        var seed = await SeedAsync(previousBytes: 2 * Gb);

        var run = await StartAsync(seed.Store, seed.Job, "r", At);

        var reservation = Assert.Single(await ReservationsAsync(fixture, seed.Agent.TenantId));
        Assert.Equal(run.Id, reservation.RunId);
        Assert.Equal(2 * Gb, reservation.Bytes);
        Assert.Null(reservation.ReleasedAt);
        Assert.Null(reservation.ReleaseReason);
        Assert.Equal(At, reservation.CreatedAt);
        Assert.Equal(At + _world.QuotaOptions.ReservationTimeToLive, reservation.ExpiresAt);
        var usage = await UsageAsync(_world, seed);
        Assert.Equal((2 * Gb, 2 * Gb), (usage.UsedBytes, usage.ReservedBytes));
        // 2 GB used + 2 GB reserved of 5 GB is exactly 80%: used alone would be Normal.
        var status = (await _world.Usage().GetQuotaStatusAsync(seed.Agent.TenantId, T0, default))!;
        Assert.Equal((QuotaState.Warning, 2 * Gb, 2 * Gb, Gb), (status.State, status.UsedBytes, status.ReservedBytes, status.RemainingBytes));

        await FailNextAsync(seed.Store, seed.Agent, BackupFailureCodes.PreCheckFailed, At);

        var after = (await _world.Usage().GetQuotaStatusAsync(seed.Agent.TenantId, T0, default))!;
        Assert.Equal((QuotaState.Normal, 2 * Gb, 0L, 3 * Gb), (after.State, after.UsedBytes, after.ReservedBytes, after.RemainingBytes));
    }

    // ---- release by every way a run ends --------------------------------------------------------------------------------------

    public enum Ending { Succeeded, FailedWithoutRetry, CancelledWhilePending, CancelledWhileRetryPending, AgentLost }

    [Theory]
    [InlineData(Ending.Succeeded, BackupStatus.Succeeded, 2_000_000_000)]
    [InlineData(Ending.FailedWithoutRetry, BackupStatus.Failed, 1_000_000_000)]
    [InlineData(Ending.CancelledWhilePending, BackupStatus.Cancelled, 1_000_000_000)]
    [InlineData(Ending.CancelledWhileRetryPending, BackupStatus.Cancelled, 1_000_000_000)]
    [InlineData(Ending.AgentLost, BackupStatus.AgentLost, 1_000_000_000)]
    public async Task EveryWayARunEndsGivesItsReservationBack(Ending ending, BackupStatus expectedStatus, long usedAfter)
    {
        var seed = await SeedAsync(retry: ending == Ending.AgentLost ? new BackupRetryPolicy { MaxAttempts = 1 } : null);
        var tenant = seed.Agent.TenantId;
        var run = await StartAsync(seed.Store, seed.Job, "r", At);
        Assert.Equal(Gb, Assert.Single(await UnreleasedAsync(fixture, tenant)).Bytes);
        Assert.Equal(Gb, (await UsageAsync(_world, seed)).ReservedBytes);

        switch (ending)
        {
            case Ending.Succeeded:
                var claim = await ClaimStartedAsync(seed.Store, seed.Agent, At);
                await BackupStoreHarness.CompleteAsync(seed.Store, claim, QuotaWorld.Success(Gb, Central, At.AddMinutes(1)), At.AddMinutes(1));
                break;
            case Ending.FailedWithoutRetry:
                await FailNextAsync(seed.Store, seed.Agent, BackupFailureCodes.PreCheckFailed, At);
                break;
            case Ending.CancelledWhilePending:
                await seed.Store.RequestCancellationAsync(tenant, run.Id, At.AddMinutes(1), default);
                break;
            case Ending.CancelledWhileRetryPending:
                await FailNextAsync(seed.Store, seed.Agent, BackupFailureCodes.DatabaseUnavailable, At);
                Assert.Equal(BackupStatus.RetryPending, (await RunAsync(_world, tenant, run.Id)).Status);
                // Waiting for its backoff is not the end: the reservation stays until the run really ends.
                Assert.Null((await ReservationOfAsync(fixture, tenant, run.Id)).ReleasedAt);
                Assert.Equal(Gb, (await UsageAsync(_world, seed)).ReservedBytes);
                await seed.Store.RequestCancellationAsync(tenant, run.Id, At.AddMinutes(2), default);
                break;
            case Ending.AgentLost:
                await ClaimStartedAsync(seed.Store, seed.Agent, At);
                Assert.True(await seed.Store.ExpireStaleAsync(At.AddMinutes(3), default) >= 1);
                break;
        }

        Assert.Equal(expectedStatus, (await RunAsync(_world, tenant, run.Id)).Status);
        var reservation = await ReservationOfAsync(fixture, tenant, run.Id);
        Assert.NotNull(reservation.ReleasedAt);
        Assert.Equal(QuotaReservation.ReasonRunFinished, reservation.ReleaseReason);
        Assert.Empty(await UnreleasedAsync(fixture, tenant));
        var usage = await UsageAsync(_world, seed);
        Assert.Equal((usedAfter, 0L), (usage.UsedBytes, usage.ReservedBytes));
        Assert.Equal(usedAfter, (await _world.SourceUsageAsync(tenant)).Logical);
    }

    [Fact]
    public async Task ARetryableFailureKeepsTheReservationAndTheRetryThatSucceedsReleasesItWhileUsageGrows()
    {
        var seed = await SeedAsync();
        var tenant = seed.Agent.TenantId;
        var run = await StartAsync(seed.Store, seed.Job, "r", At);
        await FailNextAsync(seed.Store, seed.Agent, BackupFailureCodes.DatabaseUnavailable, At);
        Assert.Equal(BackupStatus.RetryPending, (await RunAsync(_world, tenant, run.Id)).Status);
        Assert.Null((await ReservationOfAsync(fixture, tenant, run.Id)).ReleasedAt);

        Assert.Contains(await seed.Store.StartDueRetriesAsync(At.AddMinutes(1), 500, default), item => item.RunId == run.Id);
        var claim = await ClaimStartedAsync(seed.Store, seed.Agent, At.AddMinutes(1));
        Assert.Null((await ReservationOfAsync(fixture, tenant, run.Id)).ReleasedAt);
        await BackupStoreHarness.CompleteAsync(seed.Store, claim, QuotaWorld.Success(Gb, Central, At.AddMinutes(2)), At.AddMinutes(2));

        Assert.Equal(BackupStatus.Succeeded, (await RunAsync(_world, tenant, run.Id)).Status);
        Assert.Equal(QuotaReservation.ReasonRunFinished, (await ReservationOfAsync(fixture, tenant, run.Id)).ReleaseReason);
        var usage = await UsageAsync(_world, seed);
        Assert.Equal((2 * Gb, 0L), (usage.UsedBytes, usage.ReservedBytes));
    }

    [Fact]
    public async Task ADuplicateCompletionCallbackChangesNeitherUsageNorTheReleasedReservation()
    {
        var seed = await SeedAsync();
        var tenant = seed.Agent.TenantId;
        var run = await StartAsync(seed.Store, seed.Job, "r", At);
        var claim = await ClaimStartedAsync(seed.Store, seed.Agent, At);
        var completion = QuotaWorld.Success(Gb, Central, At.AddMinutes(1));
        await BackupStoreHarness.CompleteAsync(seed.Store, claim, completion, At.AddMinutes(1));
        var releasedAt = (await ReservationOfAsync(fixture, tenant, run.Id)).ReleasedAt;

        // The agent did not see the answer and sends the same result again.
        await seed.Store.CompleteAttemptAsync(BackupStoreHarness.Command(claim, At.AddMinutes(2)), completion, default);

        var usage = await UsageAsync(_world, seed);
        Assert.Equal((2 * Gb, 0L), (usage.UsedBytes, usage.ReservedBytes));
        Assert.Equal(1, await _world.Harness.CountArtifactsAsync(run.Id));
        Assert.Equal(releasedAt, (await ReservationOfAsync(fixture, tenant, run.Id)).ReleasedAt);
        Assert.Equal(2 * Gb, (await _world.SourceUsageAsync(tenant)).Logical);
    }

    // ---- duplicates --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AdmittingTheSameRunTwiceReservesOnceAndReleasingTwiceReleasesOnce()
    {
        var seed = await SeedAsync();
        var tenant = seed.Agent.TenantId;
        var admission = _world.Admission();
        var request = new BackupAdmissionRequest(tenant, seed.Job.Id, Guid.NewGuid(), BackupTriggerTypes.Manual, At);

        Assert.True((await admission.AdmitNewRunAsync(request, default)).Allowed);
        Assert.True((await admission.AdmitNewRunAsync(request, default)).Allowed);

        var reservation = await ReservationOfAsync(fixture, tenant, request.RunId);
        Assert.Equal(Gb, reservation.Bytes);
        Assert.Equal(Gb, (await UsageAsync(_world, seed)).ReservedBytes);

        await admission.ReleaseAsync(tenant, request.RunId, default);
        var released = await ReservationOfAsync(fixture, tenant, request.RunId);
        Assert.Equal(QuotaReservation.ReasonNotCreated, released.ReleaseReason);
        Assert.NotNull(released.ReleasedAt);
        await admission.ReleaseAsync(tenant, request.RunId, default);
        await admission.ReleaseAsync(tenant, Guid.NewGuid(), default);

        var again = await ReservationOfAsync(fixture, tenant, request.RunId);
        Assert.Equal(released.ReleasedAt, again.ReleasedAt);
        Assert.Equal(0, (await UsageAsync(_world, seed)).ReservedBytes);
    }

    [Fact]
    public async Task AReservationCanOnlyBeReleasedByItsOwnTenant()
    {
        var seed = await SeedAsync();
        var stranger = await _world.AgentAsync();
        var admission = _world.Admission();
        var request = new BackupAdmissionRequest(seed.Agent.TenantId, seed.Job.Id, Guid.NewGuid(), BackupTriggerTypes.Manual, At);
        Assert.True((await admission.AdmitNewRunAsync(request, default)).Allowed);

        await admission.ReleaseAsync(stranger.TenantId, request.RunId, default);

        Assert.Null((await ReservationOfAsync(fixture, seed.Agent.TenantId, request.RunId)).ReleasedAt);
        Assert.Equal(Gb, (await UsageAsync(_world, seed)).ReservedBytes);
    }

    // ---- expiry ------------------------------------------------------------------------------------------------------------

    private QuotaWorld ShortLived() => new(fixture) { QuotaOptions = new QuotaOptions { ReservationTimeToLive = TimeSpan.FromMinutes(30) } };

    [Fact]
    public async Task MaintenanceKeepsTheReservationOfAQueuedRunAtItsExpiryInstant()
    {
        var world = ShortLived();
        var seed = await SeedAsync(world);
        var tenant = seed.Agent.TenantId;
        var run = await StartAsync(seed.Store, seed.Job, "r", T0.AddMinutes(1));
        var expiresAt = T0.AddMinutes(31);
        Assert.Equal(expiresAt, (await ReservationOfAsync(fixture, tenant, run.Id)).ExpiresAt);

        var early = await world.Usage().RunMaintenanceAsync(expiresAt.AddSeconds(-1), default);
        Assert.Empty(early.Errors);
        Assert.Null((await ReservationOfAsync(fixture, tenant, run.Id)).ReleasedAt);

        var onTime = await world.Usage().RunMaintenanceAsync(expiresAt, default);

        Assert.Empty(onTime.Errors);
        var reservation = await ReservationOfAsync(fixture, tenant, run.Id);
        Assert.Null(reservation.ReleasedAt);
        Assert.Equal(Gb, (await UsageAsync(world, seed)).ReservedBytes);
        Assert.Equal(BackupStatus.Pending, (await RunAsync(world, tenant, run.Id)).Status);
    }

    [Fact]
    public async Task TheNextAdmissionCannotSpendAnExpiredQueuedRunsReservation()
    {
        var world = ShortLived();
        var agent = await world.AgentAsync(2_500_000_000);
        var big = await CreateJobAsync(world, agent);
        var small = await CreateJobAsync(world, agent);
        var store = world.RunStore();
        await world.PublishAsync(store, agent, big, Gb, Central, T0);
        await world.PublishAsync(store, agent, small, 400_000_000, Central, T0);
        var tenant = agent.TenantId;

        // used 1.4 GB + the big job's 1 GB estimate = 2.4 GB fits; the small job's 0.4 GB on top (2.8 GB) does not.
        var holder = await StartAsync(store, big, "holder", T0.AddMinutes(1));
        var blocked = await store.StartRunAsync(tenant, small.Id, "blocked", T0.AddMinutes(2), default);
        Assert.Equal((BackupStartOutcome.AdmissionDenied, BackupAdmissionDenialCodes.QuotaExceeded), (blocked.Outcome, blocked.DenialCode));

        // Expiry alone cannot free the holder's budget: it can still be claimed without asking admission.
        var later = await store.StartRunAsync(tenant, small.Id, "blocked", T0.AddMinutes(32), default);
        Assert.Equal((BackupStartOutcome.AdmissionDenied, BackupAdmissionDenialCodes.QuotaExceeded), (later.Outcome, later.DenialCode));
        Assert.Null((await ReservationOfAsync(fixture, tenant, holder.Id)).ReleasedAt);
        Assert.Equal(Gb, (await world.StoredUsageAsync(tenant))!.ReservedBytes);
    }

    // ---- orphans and missed callbacks ----------------------------------------------------------------------------------------

    [Fact]
    public async Task AReservationWhoseRunWasNeverCreatedIsReleasedAsOrphanedAfterTenMinutesAndOnlyForItsTenant()
    {
        var first = await _world.AgentAsync();
        var second = await _world.AgentAsync();
        var orphan = Guid.NewGuid();
        var bystander = Guid.NewGuid();
        await InsertReservationAsync(fixture, first.TenantId, orphan, 700_000_000, T0, T0.AddHours(6));
        await InsertReservationAsync(fixture, second.TenantId, bystander, 700_000_000, T0, T0.AddHours(6));
        var usage = _world.Usage();

        var early = (await usage.ReconcileAsync(first.TenantId, null, T0.AddMinutes(10).AddSeconds(-1), default))!;
        Assert.Equal((0, 700_000_000), (early.ReservationsReleased, early.ReservedBytes));

        var due = (await usage.ReconcileAsync(first.TenantId, null, T0.AddMinutes(10), default))!;

        Assert.Equal((1, 0L), (due.ReservationsReleased, due.ReservedBytes));
        var reservation = await ReservationOfAsync(fixture, first.TenantId, orphan);
        Assert.Equal((QuotaReservation.ReasonOrphaned, T0.AddMinutes(10)), (reservation.ReleaseReason, reservation.ReleasedAt));
        Assert.Equal(0, (await _world.StoredUsageAsync(first.TenantId))!.ReservedBytes);
        Assert.Null((await ReservationOfAsync(fixture, second.TenantId, bystander)).ReleasedAt);
    }

    [Fact]
    public async Task AReservationOfARunThatAlreadyFinishedIsReleasedByReconcile()
    {
        var seed = await SeedAsync();
        var tenant = seed.Agent.TenantId;
        // The history run is Succeeded; its release callback "was missed".
        await InsertReservationAsync(fixture, tenant, seed.HistoryRun.Id, 300_000_000, T0, T0.AddHours(6));
        Assert.Equal(BackupStatus.Succeeded, (await RunAsync(_world, tenant, seed.HistoryRun.Id)).Status);

        var result = (await _world.Usage().ReconcileAsync(tenant, null, T0.AddMinutes(1), default))!;

        Assert.Equal((1, 0L), (result.ReservationsReleased, result.ReservedBytes));
        var reservation = await ReservationOfAsync(fixture, tenant, seed.HistoryRun.Id);
        Assert.Equal((QuotaReservation.ReasonRunFinished, T0.AddMinutes(1)), (reservation.ReleaseReason, reservation.ReleasedAt));
        Assert.Equal(0, (await UsageAsync(_world, seed)).ReservedBytes);
    }

    [Fact]
    public async Task ReconcileKeepsTheReservationOfARunThatIsStillActive()
    {
        var seed = await SeedAsync();
        var tenant = seed.Agent.TenantId;
        var run = await StartAsync(seed.Store, seed.Job, "r", At);

        var result = (await _world.Usage().ReconcileAsync(tenant, null, At.AddMinutes(5), default))!;

        Assert.False(result.Drifted);
        Assert.Equal((0, Gb), (result.ReservationsReleased, result.ReservedBytes));
        Assert.Null((await ReservationOfAsync(fixture, tenant, run.Id)).ReleasedAt);
    }

    [Fact]
    public async Task ARunThatStartedAndRunsPastTheOrphanGraceKeepsItsReservationUnderReconcileAndMaintenance()
    {
        var seed = await SeedAsync();
        var tenant = seed.Agent.TenantId;
        var run = await StartAsync(seed.Store, seed.Job, "r", At);
        await ClaimStartedAsync(seed.Store, seed.Agent, At);
        Assert.Equal(BackupStatus.Running, (await RunAsync(_world, tenant, run.Id)).Status);
        var usage = _world.Usage();

        // The orphan rule is about a run that does not exist; a Running run is older than OrphanGrace here but is still very much there.
        var afterGrace = (await usage.ReconcileAsync(tenant, null, At.AddMinutes(11), default))!;
        await usage.RunMaintenanceAsync(At.AddHours(1), default);

        Assert.Equal((0, Gb), (afterGrace.ReservationsReleased, afterGrace.ReservedBytes));
        Assert.Null((await ReservationOfAsync(fixture, tenant, run.Id)).ReleasedAt);
        Assert.Equal(Gb, (await UsageAsync(_world, seed)).ReservedBytes);
    }

    // ---- live runs keep reservations until lifecycle termination --------------------------------------------------------------

    private Task<BackupLeaseResult?> RenewAsync(Seed seed, BackupClaim claim, DateTimeOffset at, TimeSpan lease) =>
        seed.Store.RenewLeaseAsync(BackupStoreHarness.Command(claim, at), lease, default);

    [Fact]
    public async Task ARunRunningUnderAValidLeaseKeepsItsReservationSevenHoursInDespiteTheSixHourTimeToLive()
    {
        var seed = await SeedAsync();
        var tenant = seed.Agent.TenantId;
        var run = await StartAsync(seed.Store, seed.Job, "r", At);
        var claim = await ClaimStartedAsync(seed.Store, seed.Agent, At);
        Assert.Equal(At + _world.QuotaOptions.ReservationTimeToLive, (await ReservationOfAsync(fixture, tenant, run.Id)).ExpiresAt);
        Assert.NotNull(await RenewAsync(seed, claim, At.AddMinutes(1), TimeSpan.FromHours(8)));
        var usage = _world.Usage();

        var reconciled = (await usage.ReconcileAsync(tenant, null, At.AddHours(7), default))!;
        var maintained = await usage.RunMaintenanceAsync(At.AddHours(7), default);

        Assert.Empty(maintained.Errors);
        Assert.Equal((0, Gb), (reconciled.ReservationsReleased, reconciled.ReservedBytes));
        Assert.Null((await ReservationOfAsync(fixture, tenant, run.Id)).ReleasedAt);
        Assert.Equal(Gb, (await UsageAsync(_world, seed)).ReservedBytes);
    }

    [Fact]
    public async Task AReservationIsKeptAfterLeaseRenewalStopsUntilTheRunTerminates()
    {
        var seed = await SeedAsync();
        var tenant = seed.Agent.TenantId;
        var run = await StartAsync(seed.Store, seed.Job, "r", At);
        var claim = await ClaimStartedAsync(seed.Store, seed.Agent, At);
        var usage = _world.Usage();
        var lease = TimeSpan.FromMinutes(90);
        var renewedAt = At.AddMinutes(1);
        Assert.NotNull(await RenewAsync(seed, claim, renewedAt, lease));

        // An agent renewing hourly (each renewal arrives before the previous lease lapses): every pass past the six-hour expiry sees a lease that is still valid.
        for (var hour = 1; hour <= 8; hour++)
        {
            renewedAt = At.AddMinutes(1).AddHours(hour);
            Assert.NotNull(await RenewAsync(seed, claim, renewedAt, lease));
            await usage.RunMaintenanceAsync(renewedAt, default);
            Assert.Null((await ReservationOfAsync(fixture, tenant, run.Id)).ReleasedAt);
        }
        Assert.Equal(Gb, (await UsageAsync(_world, seed)).ReservedBytes);

        // A lapsed lease is not terminal: the lifecycle can schedule a retry without admission.
        var lapsed = renewedAt + lease;
        await usage.RunMaintenanceAsync(lapsed.AddSeconds(-1), default);
        Assert.Null((await ReservationOfAsync(fixture, tenant, run.Id)).ReleasedAt);
        var after = await usage.RunMaintenanceAsync(lapsed, default);

        Assert.Empty(after.Errors);
        var reservation = await ReservationOfAsync(fixture, tenant, run.Id);
        Assert.Null(reservation.ReleasedAt);
        Assert.Equal(Gb, (await UsageAsync(_world, seed)).ReservedBytes);
    }

    [Fact]
    public async Task AnAbandonedRunReleasesItsReservationOnlyWhenMarkedAgentLost()
    {
        var seed = await SeedAsync(retry: new BackupRetryPolicy { MaxAttempts = 1 });
        var tenant = seed.Agent.TenantId;
        var run = await StartAsync(seed.Store, seed.Job, "r", At);
        await ClaimStartedAsync(seed.Store, seed.Agent, At);
        var expiresAt = At + _world.QuotaOptions.ReservationTimeToLive;

        // The lease lapsed, but until the lifecycle ends the run it could still retry with this reservation.
        await _world.Usage().RunMaintenanceAsync(expiresAt, default);

        var reservation = await ReservationOfAsync(fixture, tenant, run.Id);
        Assert.Null(reservation.ReleasedAt);
        Assert.Equal(Gb, (await UsageAsync(_world, seed)).ReservedBytes);
        Assert.True(await seed.Store.ExpireStaleAsync(expiresAt.AddMinutes(1), default) >= 1);
        Assert.Equal(BackupStatus.AgentLost, (await RunAsync(_world, tenant, run.Id)).Status);
        Assert.Equal(QuotaReservation.ReasonRunFinished, (await ReservationOfAsync(fixture, tenant, run.Id)).ReleaseReason);
        Assert.Equal(0, (await UsageAsync(_world, seed)).ReservedBytes);
    }

    [Fact]
    public async Task AFinishedRunIsNotShieldedByTheLeaseItLeftBehind()
    {
        var seed = await SeedAsync();
        var tenant = seed.Agent.TenantId;
        var run = await StartAsync(seed.Store, seed.Job, "r", At);
        var claim = await ClaimStartedAsync(seed.Store, seed.Agent, At);
        Assert.NotNull(await RenewAsync(seed, claim, At.AddMinutes(1), TimeSpan.FromHours(8)));
        await BackupStoreHarness.CompleteAsync(seed.Store, claim, QuotaWorld.Success(Gb, Central, At.AddMinutes(2)), At.AddMinutes(2));
        Assert.Equal(BackupStatus.Succeeded, (await RunAsync(_world, tenant, run.Id)).Status);
        // The release callback "was missed": a past-expiry reservation of the finished run is back, while its lease timestamp still lies in the future.
        await using (var context = fixture.CreatePlatformContext())
        {
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE quota_reservations SET "ReleasedAt" = NULL, "ReleaseReason" = NULL, "ExpiresAt" = {At.AddMinutes(30)} WHERE "RunId" = {run.Id}
                """);
        }

        var result = (await _world.Usage().ReconcileAsync(tenant, null, At.AddHours(1), default))!;

        Assert.Equal((1, 0L), (result.ReservationsReleased, result.ReservedBytes));
        Assert.NotNull((await ReservationOfAsync(fixture, tenant, run.Id)).ReleasedAt);
    }

    [Fact]
    public async Task AnOrphanedReservationWithoutAnyRunRecordIsStillReleasedElevenMinutesIn()
    {
        var agent = await _world.AgentAsync();
        var orphan = Guid.NewGuid();
        await InsertReservationAsync(fixture, agent.TenantId, orphan, 700_000_000, T0, T0.AddHours(6));

        var result = (await _world.Usage().ReconcileAsync(agent.TenantId, null, T0.AddMinutes(11), default))!;

        Assert.Equal((1, 0L), (result.ReservationsReleased, result.ReservedBytes));
        Assert.Equal(QuotaReservation.ReasonOrphaned, (await ReservationOfAsync(fixture, agent.TenantId, orphan)).ReleaseReason);
    }

    [Fact]
    public async Task ReconcileRecomputesReservedBytesFromTheTableAndAuditsOnlyWhenAnAdministratorAsks()
    {
        var seed = await SeedAsync();
        var tenant = seed.Agent.TenantId;
        var actor = await _world.ActorAsync();
        var run = await StartAsync(seed.Store, seed.Job, "r", At);
        var usage = _world.Usage();

        await _world.CorruptUsageAsync(tenant, 123, 777);
        var silent = (await usage.ReconcileAsync(tenant, null, At.AddMinutes(1), default))!;

        Assert.True(silent.Drifted);
        Assert.Equal((123L, 777L, Gb, Gb), (silent.PreviousUsedBytes, silent.PreviousReservedBytes, silent.UsedBytes, silent.ReservedBytes));
        var healed = await UsageAsync(_world, seed);
        Assert.Equal((Gb, Gb), (healed.UsedBytes, healed.ReservedBytes));
        Assert.Empty(await AuditAsync(fixture, tenant, ManagedServiceAuditActions.StorageUsageCorrected));

        await _world.CorruptUsageAsync(tenant, 5, 0);
        var audited = (await usage.ReconcileAsync(tenant, actor, At.AddMinutes(2), default))!;

        Assert.True(audited.Drifted);
        Assert.Equal((5L, 0L), (audited.PreviousUsedBytes, audited.PreviousReservedBytes));
        var audit = Assert.Single(await AuditAsync(fixture, tenant, ManagedServiceAuditActions.StorageUsageCorrected));
        Assert.Equal((AuditActorKind.PlatformAdmin, actor, ManagedServiceAuditActions.StorageUsageTargetType), (audit.ActorKind, audit.ActorUserId, audit.TargetType));

        var clean = (await usage.ReconcileAsync(tenant, actor, At.AddMinutes(3), default))!;

        Assert.False(clean.Drifted);
        Assert.Single(await AuditAsync(fixture, tenant, ManagedServiceAuditActions.StorageUsageCorrected));
        // The reservation itself was never touched: it belongs to a live run.
        Assert.Null((await ReservationOfAsync(fixture, tenant, run.Id)).ReleasedAt);
    }

    [Fact]
    public async Task RetryPendingRunKeepsItsReservationPastExpiryAndItsNextAttemptRemainsBounded()
    {
        var world = ShortLived();
        var seed = await SeedAsync(world);
        var run = await StartAsync(seed.Store, seed.Job, "waiting-retry", At);
        await FailNextAsync(seed.Store, seed.Agent, BackupFailureCodes.DatabaseUnavailable, At);
        var later = At.AddHours(2);
        var maintained = await world.Usage().RunMaintenanceAsync(later, default);
        Assert.Empty(maintained.Errors);
        Assert.Null((await ReservationOfAsync(fixture, seed.Agent.TenantId, run.Id)).ReleasedAt);
        Assert.Equal(Gb, (await UsageAsync(world, seed)).ReservedBytes);
        await seed.Store.StartDueRetriesAsync(later, 100, default);
        var claim = await ClaimStartedAsync(seed.Store, seed.Agent, later);
        Assert.Equal(Gb, claim.MaxArtifactBytes);
    }
}
