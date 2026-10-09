using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Infrastructure.Backups;
using BackupPlatform.IntegrationTests.Scheduling;
using BackupPlatform.ManagedService.Quota;
using BackupPlatform.ManagedService.Subscriptions;
using static BackupPlatform.IntegrationTests.Quota.AdmissionTestKit;
using static BackupPlatform.IntegrationTests.Scheduling.ScheduleKit;

namespace BackupPlatform.IntegrationTests.Quota;

/// <summary>
/// A scheduled occurrence passes the same gate as a manual start. A refusal is a decided occurrence (AdmissionDenied, reason = denial code, no run),
/// recorded once for its (job, instant) and never re-decided; the next occurrence is decided afresh.
/// </summary>
public sealed class ScheduledAdmissionIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static readonly IReadOnlyList<ReplicaSpec> Central = [ReplicaSpec.Central];
    private readonly QuotaWorld _world = new(fixture);
    private readonly ScheduleKit _kit = new(fixture);

    private sealed record Scheduled(StoreAgent Agent, BackupJob Job, CountingAdmission Admission, PostgreSqlBackupScheduleStore Store);

    /// <summary>A tenant with a hourly scheduled job (30 minute grace) whose evaluations go through a counting wrapper of the real admission.</summary>
    private async Task<Scheduled> ScheduleAsync(BackupOverlapPolicy overlap = BackupOverlapPolicy.Queue, bool subscribe = true)
    {
        var agent = await _world.AgentAsync(subscribe: subscribe);
        var job = await _kit.AddJobAsync(agent, overlap: overlap);
        var admission = new CountingAdmission(_world.Admission());
        return new Scheduled(agent, job, admission, _world.ScheduleStore(admission));
    }

    private static Task<BackupScheduleEvaluation> EvaluateAsync(Scheduled scheduled, DateTimeOffset now) =>
        scheduled.Store.EvaluateAsync(scheduled.Job.TenantId, scheduled.Job.Id, now, default);

    private async Task MakeOverQuotaAsync(StoreAgent agent)
    {
        var publisher = await AddAgentAsync(_world, agent.TenantId);
        await _world.PublishAsync(_world.RunStore(), publisher, await CreateJobAsync(_world, publisher), 5_500_000_000, Central, T0);
    }

    public enum Cause { OverQuota, Suspended, NoSubscription }

    [Theory]
    [InlineData(Cause.OverQuota)]
    [InlineData(Cause.Suspended)]
    [InlineData(Cause.NoSubscription)]
    public async Task ADueOccurrenceOfATenantThatCannotStartRunsIsRecordedAsDeniedAndCreatesNoRun(Cause cause)
    {
        var scheduled = await ScheduleAsync(subscribe: cause != Cause.NoSubscription);
        var tenant = scheduled.Agent.TenantId;
        switch (cause)
        {
            case Cause.OverQuota:
                await MakeOverQuotaAsync(scheduled.Agent);
                break;
            case Cause.Suspended:
                await SuspendAsync(_world, tenant);
                break;
        }
        var code = cause == Cause.OverQuota ? BackupAdmissionDenialCodes.QuotaExceeded : BackupAdmissionDenialCodes.SubscriptionRequired;
        await _kit.SeenAsync(scheduled.Agent, H(1, 19));

        var evaluation = await EvaluateAsync(scheduled, H(1, 20));

        Assert.Null(evaluation.CreatedRunId);
        Assert.Null(evaluation.AgentToNotify);
        Assert.Equal(BackupOccurrenceOutcome.AdmissionDenied, Assert.Single(evaluation.Recorded).Outcome);
        Assert.Equal(0, await _world.Harness.CountRunsAsync(scheduled.Job.Id));
        var occurrence = Assert.Single(await _kit.OccurrencesAsync(scheduled.Job));
        Assert.Equal((H(1), BackupOccurrenceOutcome.AdmissionDenied, code, H(1, 20)), (occurrence.ScheduledAt, occurrence.Outcome, occurrence.Reason, occurrence.DecidedAt));
        Assert.Null(occurrence.RunId);
        var view = BackupScheduleService.ToView(occurrence);
        Assert.Equal(("AdmissionDenied", code, 1200d), (view.Outcome, view.Reason, view.DelaySeconds));
        Assert.Null(view.RunId);
        var request = Assert.Single(scheduled.Admission.Requests);
        Assert.Equal((BackupTriggerTypes.Scheduled, tenant, scheduled.Job.Id, H(1, 20)), (request.TriggerType, request.TenantId, request.JobId, request.Now));
        Assert.Equal((false, code), (scheduled.Admission.Decisions.Single().Allowed, scheduled.Admission.Decisions.Single().DenialCode));
        Assert.Empty(await ReservationsAsync(fixture, tenant));

        // The decision is final for that instant: evaluating again, now or later, neither re-asks nor records anything.
        var sameInstant = await EvaluateAsync(scheduled, H(1, 20));
        var later = await EvaluateAsync(scheduled, H(1, 25));
        Assert.All(new[] { sameInstant, later }, result =>
        {
            Assert.Null(result.CreatedRunId);
            Assert.Empty(result.Recorded);
        });
        Assert.Single(await _kit.OccurrencesAsync(scheduled.Job));
        Assert.Equal(1, scheduled.Admission.Calls);

        // Capacity is back: the NEXT instant is decided on its own and creates a normal scheduled run.
        switch (cause)
        {
            case Cause.OverQuota:
                await SetOverrideAsync(_world, tenant, StorageLimitOverride.Limited(10 * Gb));
                break;
            case Cause.Suspended:
                await ResumeAsync(_world, tenant);
                break;
            case Cause.NoSubscription:
                await _world.AssignAsync(tenant, await _world.PlanAsync(5 * Gb));
                break;
        }
        await _kit.SeenAsync(scheduled.Agent, H(2, 9));
        var created = await EvaluateAsync(scheduled, H(2, 10));

        Assert.NotNull(created.CreatedRunId);
        Assert.Equal(scheduled.Agent.AgentId, created.AgentToNotify);
        var run = await _kit.RunAsync(scheduled.Job, created.CreatedRunId!.Value);
        Assert.Equal((BackupTriggerTypes.Scheduled, H(2), BackupStatus.Pending), (run.TriggerType, run.ScheduledOccurrence, run.Status));
        var occurrences = await _kit.OccurrencesAsync(scheduled.Job);
        Assert.Equal([BackupOccurrenceOutcome.AdmissionDenied, BackupOccurrenceOutcome.RunCreated], occurrences.Select(item => item.Outcome).ToArray());
        Assert.Equal((BackupOccurrenceReasons.RunCreated, run.Id), (occurrences[1].Reason, occurrences[1].RunId));
        Assert.Equal(2, scheduled.Admission.Calls);
        Assert.Equal((BackupTriggerTypes.Scheduled, run.Id, true), (scheduled.Admission.Requests[1].TriggerType, scheduled.Admission.Requests[1].RunId, scheduled.Admission.Decisions[1].Allowed));
    }

    [Theory]
    [InlineData(BackupOverlapPolicy.Skip)]
    [InlineData(BackupOverlapPolicy.Queue)]
    public async Task OverlapIsAppliedAfterARecoveryExactlyAsBefore(BackupOverlapPolicy overlap)
    {
        var scheduled = await ScheduleAsync(overlap);
        var tenant = scheduled.Agent.TenantId;
        await SuspendAsync(_world, tenant);
        await _kit.SeenAsync(scheduled.Agent, H(1, 19));
        await EvaluateAsync(scheduled, H(1, 20));
        await ResumeAsync(_world, tenant);
        await _kit.SeenAsync(scheduled.Agent, H(2, 9));
        var first = await EvaluateAsync(scheduled, H(2, 10));
        Assert.NotNull(first.CreatedRunId);
        await _kit.SeenAsync(scheduled.Agent, H(3, 9));

        var third = await EvaluateAsync(scheduled, H(3, 10));

        var occurrences = await _kit.OccurrencesAsync(scheduled.Job);
        Assert.Equal(3, occurrences.Count);
        if (overlap == BackupOverlapPolicy.Skip)
        {
            // The first run is still queued: the instant is skipped, and the gate is not even asked.
            Assert.Null(third.CreatedRunId);
            Assert.Equal((BackupOccurrenceOutcome.SkippedOverlap, BackupOccurrenceReasons.ActiveRun, first.CreatedRunId), (occurrences[2].Outcome, occurrences[2].Reason, occurrences[2].RunId));
            Assert.Equal(1, await _world.Harness.CountRunsAsync(scheduled.Job.Id));
            Assert.Equal(2, scheduled.Admission.Calls);
            return;
        }
        Assert.NotNull(third.CreatedRunId);
        Assert.NotEqual(first.CreatedRunId, third.CreatedRunId);
        Assert.Equal((BackupOccurrenceOutcome.RunCreated, third.CreatedRunId), (occurrences[2].Outcome, occurrences[2].RunId));
        Assert.Equal(2, await _world.Harness.CountRunsAsync(scheduled.Job.Id));
        Assert.Equal(3, scheduled.Admission.Calls);
    }

    [Fact]
    public async Task AScheduledRunHoldsItsReservationUntilItEndsAndTheUnclaimedSweepGivesItBack()
    {
        var scheduled = await ScheduleAsync(BackupOverlapPolicy.Skip);
        var tenant = scheduled.Agent.TenantId;
        // One finished backup of the job gives the next run an estimate of 1 GB.
        await _world.PublishAsync(_world.RunStore(), scheduled.Agent, scheduled.Job, Gb, Central, T0);
        await _kit.SeenAsync(scheduled.Agent, H(1, 19));

        var evaluation = await EvaluateAsync(scheduled, H(1, 20));

        var runId = evaluation.CreatedRunId!.Value;
        var request = Assert.Single(scheduled.Admission.Requests);
        Assert.Equal((runId, BackupTriggerTypes.Scheduled), (request.RunId, request.TriggerType));
        var reservation = Assert.Single(await ReservationsAsync(fixture, tenant));
        Assert.Equal((runId, Gb), (reservation.RunId, reservation.Bytes));
        Assert.Null(reservation.ReleasedAt);
        Assert.Equal(Gb, (await _world.StoredUsageAsync(tenant))!.ReservedBytes);

        // Nobody claims it before the grace (30 minutes after 01:00) ends.
        Assert.True(await scheduled.Store.ExpireUnclaimedRunsAsync(H(1, 31), 100, default) >= 1);

        var run = await _kit.RunAsync(scheduled.Job, runId);
        Assert.Equal((BackupStatus.Cancelled, BackupFailureCodes.ScheduleGraceExpired), (run.Status, run.TerminalReason));
        var released = await ReservationOfAsync(fixture, tenant, runId);
        Assert.Equal(QuotaReservation.ReasonRunFinished, released.ReleaseReason);
        Assert.NotNull(released.ReleasedAt);
        var usage = (await _world.StoredUsageAsync(tenant))!;
        Assert.Equal((Gb, 0L), (usage.UsedBytes, usage.ReservedBytes));
    }

    [Fact]
    public async Task TwoNodesDecidingTheSameDeniedInstantRecordItOnlyOnce()
    {
        var scheduled = await ScheduleAsync();
        await SuspendAsync(_world, scheduled.Agent.TenantId);
        await _kit.SeenAsync(scheduled.Agent, H(1, 19));
        var otherNode = _world.ScheduleStore(new CountingAdmission(_world.Admission()));

        var results = await Task.WhenAll(
            EvaluateAsync(scheduled, H(1, 20)),
            otherNode.EvaluateAsync(scheduled.Job.TenantId, scheduled.Job.Id, H(1, 20), default));

        Assert.All(results, result => Assert.Null(result.CreatedRunId));
        Assert.Single(results.SelectMany(result => result.Recorded));
        var occurrence = Assert.Single(await _kit.OccurrencesAsync(scheduled.Job));
        Assert.Equal((BackupOccurrenceOutcome.AdmissionDenied, BackupAdmissionDenialCodes.SubscriptionRequired), (occurrence.Outcome, occurrence.Reason));
        Assert.Equal(0, await _world.Harness.CountRunsAsync(scheduled.Job.Id));
    }
}
