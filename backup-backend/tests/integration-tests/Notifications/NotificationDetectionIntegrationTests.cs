using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Notifications;
using BackupPlatform.IntegrationTests.Quota;
using BackupPlatform.Notifications;
using Microsoft.EntityFrameworkCore;
using static BackupPlatform.IntegrationTests.BackupStoreHarness;

namespace BackupPlatform.IntegrationTests.Notifications;

/// <summary>
/// The detectors against real platform state, through the real engine and store: what the platform's own facts turn into, and that the same fact is
/// reported once, resolved once and never re-announced by a repeated or concurrent sweep.
/// </summary>
public sealed class NotificationDetectionIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlFixture _database = new();
    private NotificationWorld _world = null!;

    public async Task InitializeAsync()
    {
        await _database.InitializeAsync();
        _world = new NotificationWorld(_database);
    }

    public Task DisposeAsync() => _database.DisposeAsync();

    private BackupStoreHarness Kit => _world.Quota.Harness;
    private DateTimeOffset Now => _world.Clock.GetUtcNow();

    private async Task<(StoreAgent Agent, BackupJob Job)> AgentWithJobAsync(string[]? types = null)
    {
        var agent = await Kit.AddAgentAsync();
        var job = await Kit.AddJobAsync(agent, retry: new BackupRetryPolicy { MaxAttempts = 1 });
        await _world.AddEmailAsync(agent.TenantId);
        await _world.EnableAsync(agent.TenantId, types);
        return (agent, job);
    }

    private async Task FailRunAsync(StoreAgent agent, BackupJob job, DateTimeOffset at)
    {
        var store = Kit.Store();
        await Kit.StartRunAsync(store, job, $"fail-{Guid.NewGuid():N}", at);
        var claim = await ClaimAsync(store, agent, at);
        await StartAttemptAsync(store, claim!, at);
        await CompleteAsync(store, claim!, Failed(BackupFailureCodes.InvalidConfiguration), at);
    }

    private Task<int> SweepAsync() => _world.Engine().SweepAsync(default);

    [Fact]
    public async Task A_failed_run_is_announced_once_with_only_a_stable_code_and_older_failures_stay_history()
    {
        var (agent, job) = await AgentWithJobAsync([NotificationEventTypes.BackupFailed]);
        var tenant = agent.TenantId;
        await FailRunAsync(agent, job, Now.AddHours(-2));
        await FailRunAsync(agent, job, Now.AddMinutes(5));
        _world.Clock.Advance(TimeSpan.FromMinutes(10));

        Assert.Equal(1, await SweepAsync());
        Assert.Equal(0, await SweepAsync());

        var e = Assert.Single(await _world.EventsAsync(tenant));
        Assert.Equal((NotificationEventTypes.BackupFailed, NotificationPhase.Raised, NotificationSeverity.Critical, "Database"), (e.Type, e.Phase, e.Severity, e.SubjectName));
        Assert.Equal(BackupFailureCodes.InvalidConfiguration, e.Facts["code"]);
        Assert.Single(await _world.DeliveriesAsync(tenant));
    }

    [Fact]
    public async Task Concurrent_sweeps_on_several_nodes_announce_each_fact_exactly_once()
    {
        var (agent, job) = await AgentWithJobAsync([NotificationEventTypes.BackupFailed, NotificationEventTypes.AgentOffline]);
        await _world.HeartbeatAsync(agent, Now.AddHours(-3));
        await FailRunAsync(agent, job, Now.AddMinutes(1));
        await FailRunAsync(agent, job, Now.AddMinutes(2));
        _world.Clock.Advance(TimeSpan.FromMinutes(10));

        var written = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => _world.Engine().SweepAsync(default))));
        await SweepAsync();

        var events = await _world.EventsAsync(agent.TenantId);
        Assert.Equal(3, events.Count); // two failures and one offline agent
        Assert.Equal(3, events.Select(e => e.DedupKey).Distinct().Count());
        Assert.Equal(3, (await _world.DeliveriesAsync(agent.TenantId)).Count);
        Assert.Equal(3, written.Sum());
    }

    [Fact]
    public async Task An_agent_that_goes_silent_is_announced_after_the_threshold_and_its_return_is_announced_once()
    {
        var (agent, _) = await AgentWithJobAsync([NotificationEventTypes.AgentOffline]);
        var start = Now;
        await _world.HeartbeatAsync(agent, start);

        _world.Clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(0, await SweepAsync());
        _world.Clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(1, await SweepAsync());
        _world.Clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(0, await SweepAsync());

        await _world.HeartbeatAsync(agent, Now);
        Assert.Equal(1, await SweepAsync());
        Assert.Equal(0, await SweepAsync());

        var events = await _world.EventsAsync(agent.TenantId);
        Assert.Equal([NotificationPhase.Raised, NotificationPhase.Recovered], events.Select(e => e.Phase));
        Assert.Equal("minutes", Assert.Single(events[0].Facts.Keys));
        Assert.Equal(events[0].SubjectId, events[1].SubjectId);
    }

    [Fact]
    public async Task A_revoked_or_never_seen_agent_is_not_reported_offline()
    {
        var (agent, _) = await AgentWithJobAsync([NotificationEventTypes.AgentOffline]);
        await Kit.AddAgentToAsync(agent.TenantId); // enrolled but never seen: there is no sign of life to be missing
        await _world.HeartbeatAsync(agent, Now.AddHours(-5));
        await using (var context = _database.CreatePlatformContext())
            await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE agents SET \"Status\" = 4 WHERE \"Id\" = {agent.AgentId.Value}");

        Assert.Equal(0, await SweepAsync());
        Assert.Empty(await _world.EventsAsync(agent.TenantId));
    }

    [Fact]
    public async Task A_job_without_a_recent_success_is_reported_stale_and_a_new_success_resolves_it()
    {
        var (agent, job) = await AgentWithJobAsync([NotificationEventTypes.BackupStale]);
        // The job was created long ago (the harness' T0) and has never succeeded.
        Assert.Equal(1, await SweepAsync());
        Assert.Equal(0, await SweepAsync());

        var store = Kit.Store();
        await Kit.StartRunAsync(store, job, "ok", Now);
        var claim = await ClaimAsync(store, agent, Now);
        await StartAttemptAsync(store, claim!, Now);
        await CompleteAsync(store, claim!, Success(Now), Now);
        _world.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, await SweepAsync());

        var events = await _world.EventsAsync(agent.TenantId);
        Assert.Equal([NotificationPhase.Raised, NotificationPhase.Recovered], events.Select(e => e.Phase));
        Assert.True(long.Parse(events[0].Facts["hours"]) >= 48);
    }

    [Fact]
    public async Task A_disabled_job_is_never_called_stale()
    {
        var (agent, job) = await AgentWithJobAsync([NotificationEventTypes.BackupStale]);
        await using (var context = _database.CreatePlatformContext())
            await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE backup_jobs SET \"Enabled\" = false WHERE \"Id\" = {job.Id}");

        Assert.Equal(0, await SweepAsync());
    }

    [Fact]
    public async Task A_latest_backup_that_lost_its_verified_copies_is_reported_until_they_are_back()
    {
        var (agent, job) = await AgentWithJobAsync([NotificationEventTypes.ReplicaDegraded]);
        var store = Kit.Store();
        await Kit.StartRunAsync(store, job, "ok", Now);
        var claim = await ClaimAsync(store, agent, Now);
        await StartAttemptAsync(store, claim!, Now);
        await CompleteAsync(store, claim!, Success(Now), Now);
        Assert.Equal(0, await SweepAsync());

        await SetReplicaStatusAsync(agent, "Quarantined");
        Assert.Equal(1, await SweepAsync());
        Assert.Equal(0, await SweepAsync());
        await SetReplicaStatusAsync(agent, "Available");
        Assert.Equal(1, await SweepAsync());

        var events = await _world.EventsAsync(agent.TenantId);
        Assert.Equal([NotificationPhase.Raised, NotificationPhase.Recovered], events.Select(e => e.Phase));
        Assert.Equal((NotificationSeverity.Critical, "insufficient"), (events[0].Severity, events[0].Facts["status"]));
    }

    private async Task SetReplicaStatusAsync(StoreAgent agent, string status)
    {
        await using var context = _database.CreatePlatformContext();
        await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE backup_replicas SET \"Status\" = {status} WHERE \"TenantId\" = {agent.TenantId.Value}");
    }

    [Fact]
    public async Task A_tenant_close_to_its_quota_is_reported_and_one_with_headroom_is_not()
    {
        var quota = _world.Quota;
        var tight = await quota.AgentAsync(limitBytes: 10 * QuotaWorld.Gb);
        var roomy = await quota.AgentAsync(limitBytes: 10 * QuotaWorld.Gb);
        foreach (var agent in new[] { tight, roomy })
        {
            await _world.AddEmailAsync(agent.TenantId);
            await _world.EnableAsync(agent.TenantId, [NotificationEventTypes.QuotaCritical]);
        }
        var store = quota.RunStore();
        await quota.PublishAsync(store, tight, await quota.JobAsync(tight), 9_500_000_000, [new ReplicaSpec("central", "default")], Now);
        await quota.PublishAsync(store, roomy, await quota.JobAsync(roomy), 1_000_000_000, [new ReplicaSpec("central", "default")], Now);

        Assert.Equal(1, await SweepAsync());

        var e = Assert.Single(await _world.EventsAsync(tight.TenantId));
        Assert.Equal((NotificationEventTypes.QuotaCritical, NotificationSeverity.Critical, "critical", "95"), (e.Type, e.Severity, e.Facts["state"], e.Facts["percent"]));
        Assert.Empty(await _world.EventsAsync(roomy.TenantId));
    }

    [Fact]
    public async Task Only_the_event_types_the_policy_lists_are_looked_for()
    {
        var (agent, job) = await AgentWithJobAsync([NotificationEventTypes.AgentOffline]);
        await FailRunAsync(agent, job, Now.AddMinutes(1));
        _world.Clock.Advance(TimeSpan.FromMinutes(10));

        Assert.Equal(0, await SweepAsync());
        Assert.Empty(await _world.EventsAsync(agent.TenantId));
    }

    [Fact]
    public async Task Another_tenants_facts_never_become_this_tenants_notifications()
    {
        var (agentA, jobA) = await AgentWithJobAsync([NotificationEventTypes.BackupFailed]);
        var (agentB, _) = await AgentWithJobAsync([NotificationEventTypes.BackupFailed]);
        await FailRunAsync(agentA, jobA, Now.AddMinutes(1));
        _world.Clock.Advance(TimeSpan.FromMinutes(10));

        await SweepAsync();

        Assert.Single(await _world.EventsAsync(agentA.TenantId));
        Assert.Empty(await _world.EventsAsync(agentB.TenantId));
        Assert.Empty(await _world.DeliveriesAsync(agentB.TenantId));
    }

    [Fact]
    public async Task A_policy_that_is_switched_off_stops_detection_and_a_disabled_channel_receives_nothing_new()
    {
        var (agent, job) = await AgentWithJobAsync([NotificationEventTypes.BackupFailed]);
        var channel = (await _world.Admin().ListChannelsAsync(agent.TenantId, default))!.Single();
        await _world.Admin().SetEnabledAsync(agent.TenantId, channel.Id, false, _world.Actor, default);
        await FailRunAsync(agent, job, Now.AddMinutes(1));
        _world.Clock.Advance(TimeSpan.FromMinutes(10));

        // No enabled channel exists: the fact is not turned into an event nobody could receive.
        Assert.Equal(0, await SweepAsync());
        await _world.Admin().SetEnabledAsync(agent.TenantId, channel.Id, true, _world.Actor, default);
        Assert.Equal(1, await SweepAsync());
        await _world.Admin().UpdatePolicyAsync(agent.TenantId, new PolicyCommand(false, ["backup_failed"], "warning", ["email"], true, 48, 15, 24, 30), _world.Actor, default);
        await FailRunAsync(agent, job, Now.AddMinutes(1));
        Assert.Equal(0, await SweepAsync());
    }
}
