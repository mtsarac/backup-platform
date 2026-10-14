using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Notifications;
using BackupPlatform.Notifications;
using Microsoft.EntityFrameworkCore;
using BackupPlatform.UnitTests.Notifications.Support;
using static BackupPlatform.IntegrationTests.BackupStoreHarness;

namespace BackupPlatform.IntegrationTests.Notifications;

/// <summary>
/// The whole path on real infrastructure: PostgreSQL, the detectors, the engine, the delivery processor and the real SMTP and HTTPS clients talking to
/// isolated loopback servers. Time is under the test's control, so outages, backoff, restarts and recovery are exercised for real and in order.
/// </summary>
public sealed class NotificationDeliverySmokeIntegrationTests : IAsyncLifetime
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

    private async Task<StoreAgent> OfflineAgentTenantAsync(TestSmtpServer smtp, TestHttpsServer hook, string[]? types = null, int perHour = 30)
    {
        var agent = await Kit.AddAgentAsync();
        await _world.ConfigureSmtpAsync(smtp.Port);
        await _world.AddEmailAsync(agent.TenantId, "ops@example.com");
        await _world.AddWebhookAsync(agent.TenantId, hook.Url(), authentication: "bearer", secret: "hook-token");
        await _world.EnableAsync(agent.TenantId, types ?? [NotificationEventTypes.AgentOffline], perHour: perHour);
        await _world.HeartbeatAsync(agent, Now);
        return agent;
    }

    private async Task SweepAfterAsync(TimeSpan wait)
    {
        _world.Clock.Advance(wait);
        await _world.Engine().SweepAsync(default);
    }

    [Fact]
    public async Task An_offline_agent_reaches_the_inbox_and_the_endpoint_through_an_outage_a_restart_and_repeated_sweeps_and_its_return_is_announced()
    {
        await using var smtp = new TestSmtpServer();
        var calls = 0;
        await using var hook = new TestHttpsServer(_ => new TestResponse(Interlocked.Increment(ref calls) <= 2 ? 503 : 200));
        var agent = await OfflineAgentTenantAsync(smtp, hook);

        await SweepAfterAsync(TimeSpan.FromMinutes(20));
        await _world.Engine().SweepAsync(default);
        await _world.Engine().SweepAsync(default);
        Assert.Equal(2, await _world.DeliverAsync());

        // The mailbox has its message; the endpoint was down: one failed attempt, scheduled for later, not hammered.
        Assert.Single(smtp.Messages);
        Assert.Contains("Subject: [Backup Platform] Agent offline", smtp.Messages[0]);
        Assert.Single(hook.Requests);
        var pending = (await _world.DeliveriesAsync(agent.TenantId)).Single(delivery => delivery.ChannelKind == NotificationChannelKind.Webhook);
        Assert.Equal((NotificationDeliveryStatus.Pending, 1, "http_503"), (pending.Status, pending.AttemptCount, pending.ErrorCode));
        Assert.Equal(0, await _world.DeliverAsync());
        Assert.Single(hook.Requests);

        // A restarted node (new processor, new owner) picks the retry up when it is due, fails again, and finally succeeds.
        _world.Clock.Advance(TimeSpan.FromMinutes(2));
        await _world.DeliverAsync(_world.Processor(), "worker-after-restart");
        _world.Clock.Advance(TimeSpan.FromMinutes(5));
        await _world.DeliverAsync(_world.Processor(), "worker-after-restart");

        var done = (await _world.DeliveriesAsync(agent.TenantId)).Single(delivery => delivery.ChannelKind == NotificationChannelKind.Webhook);
        Assert.Equal((NotificationDeliveryStatus.Sent, 3, (string?)null), (done.Status, done.AttemptCount, done.ErrorCode));
        Assert.Equal(3, hook.Requests.Count);
        // Every attempt carries the same idempotency key, and the secret travelled only as the bearer credential.
        Assert.Single(hook.Requests.Select(request => request.Headers["idempotency-key"]).Distinct());
        Assert.All(hook.Requests, request => Assert.Equal("Bearer hook-token", request.Headers["authorization"]));
        Assert.DoesNotContain("hook-token", hook.Requests[2].BodyText);
        Assert.Single(smtp.Messages);

        // The agent comes back: one recovery message to each channel.
        await _world.HeartbeatAsync(agent, Now);
        await SweepAfterAsync(TimeSpan.FromMinutes(1));
        await _world.Engine().SweepAsync(default);
        await _world.DeliverAsync();

        Assert.Equal(2, smtp.Messages.Count);
        Assert.Contains("Agent back online", smtp.Messages[1]);
        Assert.Equal("recovered", System.Text.Json.JsonDocument.Parse(hook.Requests[^1].Body).RootElement.GetProperty("phase").GetString());
        var history = await _world.Admin().ListDeliveriesAsync(agent.TenantId, null, null, null, 1, 25, default);
        Assert.Equal(4, history.TotalCount);
        Assert.All(history.Items, item => Assert.Equal("sent", item.Status));
    }

    [Fact]
    public async Task Providers_that_are_down_end_as_failed_history_after_a_bounded_number_of_attempts_and_never_disturb_a_backup()
    {
        var smtp = new TestSmtpServer();
        await using var hook = new TestHttpsServer(_ => new TestResponse(500));
        var agent = await OfflineAgentTenantAsync(smtp, hook, [NotificationEventTypes.AgentOffline, NotificationEventTypes.BackupFailed]);
        await smtp.DisposeAsync();
        await SweepAfterAsync(TimeSpan.FromMinutes(20));

        // A backup runs to completion while the notification stack is failing around it.
        var job = await Kit.AddJobAsync(agent);
        var store = Kit.Store();
        await Kit.StartRunAsync(store, job, "while-notifications-fail", Now);
        var claim = await ClaimAsync(store, agent, Now);
        await StartAttemptAsync(store, claim!, Now);
        await CompleteAsync(store, claim!, Success(Now), Now);

        for (var pass = 0; pass < 12; pass++)
        {
            await _world.DeliverAsync();
            await _world.DeliverAsync();
            _world.Clock.Advance(TimeSpan.FromMinutes(10));
        }

        Assert.Equal(_world.Options.MaxAttempts, hook.Requests.Count);
        var deliveries = await _world.DeliveriesAsync(agent.TenantId);
        Assert.All(deliveries, delivery => Assert.Equal((NotificationDeliveryStatus.Failed, _world.Options.MaxAttempts), (delivery.Status, delivery.AttemptCount)));
        Assert.Equal(["connect_failed", "http_500"], deliveries.Select(delivery => delivery.ErrorCode!).Order(StringComparer.Ordinal));
        await using var context = _database.CreatePlatformContext();
        Assert.Equal(BackupStatus.Succeeded, (await context.BackupRuns.SingleAsync(run => run.JobId == job.Id)).Status);
    }

    [Fact]
    public async Task A_crashed_workers_claim_is_taken_over_and_the_message_is_sent_once()
    {
        await using var smtp = new TestSmtpServer();
        await using var hook = new TestHttpsServer();
        var agent = await OfflineAgentTenantAsync(smtp, hook);
        await SweepAfterAsync(TimeSpan.FromMinutes(20));
        var crashed = await _world.Store().ClaimDueAsync("crashed-worker", Now, 10, _world.Options.LeaseDuration, _world.Options.MaxAttempts, default);
        Assert.Equal(2, crashed.Count);

        Assert.Equal(0, await _world.DeliverAsync(owner: "healthy-worker"));
        _world.Clock.Advance(_world.Options.LeaseDuration + TimeSpan.FromSeconds(1));
        Assert.Equal(2, await _world.DeliverAsync(owner: "healthy-worker"));

        Assert.Single(smtp.Messages);
        Assert.Single(hook.Requests);
        Assert.All(await _world.DeliveriesAsync(agent.TenantId), delivery => Assert.Equal((NotificationDeliveryStatus.Sent, 2), (delivery.Status, delivery.AttemptCount)));
    }

    [Fact]
    public async Task A_burst_is_capped_per_tenant_and_the_held_back_notifications_are_visible_in_the_history()
    {
        await using var smtp = new TestSmtpServer();
        await using var hook = new TestHttpsServer();
        var first = await OfflineAgentTenantAsync(smtp, hook, perHour: 2);
        for (var i = 0; i < 4; i++)
        {
            var extra = new StoreAgent(first.TenantId, await Kit.AddAgentToAsync(first.TenantId));
            await _world.HeartbeatAsync(extra, Now);
        }
        await SweepAfterAsync(TimeSpan.FromMinutes(20));

        await _world.DeliverAsync();

        Assert.Equal(2, smtp.Messages.Count);
        Assert.Equal(2, hook.Requests.Count);
        var deliveries = await _world.DeliveriesAsync(first.TenantId);
        Assert.Equal(10, deliveries.Count);
        Assert.Equal(6, deliveries.Count(delivery => delivery is { Status: NotificationDeliveryStatus.Suppressed, ErrorCode: NotificationDeliveryCodes.BurstLimited }));
    }

    [Fact]
    public async Task Switching_the_policy_off_before_sending_cancels_what_is_still_waiting()
    {
        await using var smtp = new TestSmtpServer();
        await using var hook = new TestHttpsServer();
        var agent = await OfflineAgentTenantAsync(smtp, hook);
        await SweepAfterAsync(TimeSpan.FromMinutes(20));

        await _world.Admin().UpdatePolicyAsync(agent.TenantId, new PolicyCommand(false, ["agent_offline"], "warning", ["email", "webhook"], true, 48, 15, 24, 30), _world.Actor, default);
        await _world.DeliverAsync();

        Assert.Empty(smtp.Messages);
        Assert.Empty(hook.Requests);
        Assert.All(await _world.DeliveriesAsync(agent.TenantId), delivery => Assert.Equal((NotificationDeliveryStatus.Cancelled, NotificationDeliveryCodes.PolicyDisabled), (delivery.Status, delivery.ErrorCode)));
    }

    [Fact]
    public async Task One_tenants_events_never_reach_another_tenants_endpoint()
    {
        await using var smtp = new TestSmtpServer();
        await using var hookA = new TestHttpsServer();
        await using var hookB = new TestHttpsServer();
        var a = await OfflineAgentTenantAsync(smtp, hookA);
        var b = await Kit.AddAgentAsync();
        await _world.AddWebhookAsync(b.TenantId, hookB.Url());
        await _world.EnableAsync(b.TenantId, [NotificationEventTypes.AgentOffline]);
        await _world.HeartbeatAsync(b, Now);
        // Only tenant A's agent goes silent: B's agent keeps reporting in.
        _world.Clock.Advance(TimeSpan.FromMinutes(10));
        await _world.HeartbeatAsync(b, Now);
        await SweepAfterAsync(TimeSpan.FromMinutes(10));
        await _world.DeliverAsync();

        Assert.Single(hookA.Requests);
        Assert.Empty(hookB.Requests);
        Assert.Empty(await _world.DeliveriesAsync(b.TenantId));
        Assert.Equal(a.TenantId.Value, System.Text.Json.JsonDocument.Parse(hookA.Requests[0].Body).RootElement.GetProperty("tenantId").GetGuid());
    }
}
