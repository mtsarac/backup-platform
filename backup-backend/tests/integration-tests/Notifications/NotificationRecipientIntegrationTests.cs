using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Notifications;
using BackupPlatform.Notifications;

namespace BackupPlatform.IntegrationTests.Notifications;

/// <summary>Email recipients must consent before they are mailed, may withdraw by themselves, and a tenant cannot use the relay to spam strangers.</summary>
/// <remarks>Each test owns its database (a clone of the migrated template).</remarks>
public sealed class NotificationRecipientIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlFixture _database = new();
    private NotificationWorld _world = null!;

    public async Task InitializeAsync()
    {
        await _database.InitializeAsync();
        _world = new NotificationWorld(_database);
    }

    public Task DisposeAsync() => _database.DisposeAsync();

    private Task<TenantId> TenantAsync() => _world.Quota.Harness.AddTenantAsync();

    private async Task<NotificationChannelView> ChannelAsync(TenantId tenant, Guid id) => (await _world.Admin().GetChannelAsync(tenant, id, default))!;

    private sealed class FixedSource(Func<DateTimeOffset> now) : INotificationObservationSource
    {
        public Task<IReadOnlyList<ObservationSet>> CollectAsync(NotificationPolicy policy, DateTimeOffset at, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ObservationSet>>([new ObservationSet(NotificationEventTypes.AgentOffline, true,
                [new NotificationObservation("agent_offline:a", NotificationEventTypes.AgentOffline, NotificationSeverity.Critical, new NotificationSubject("agent", Guid.NewGuid(), "build-01"), now())])]);

        public Task<IReadOnlySet<Guid>> RetiredSubjectsAsync(TenantId tenantId, string type, IReadOnlyCollection<Guid> subjectIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
    }

    [Fact]
    public async Task A_new_email_channel_is_unconfirmed_and_its_address_is_sent_a_confirmation_request_and_nothing_else()
    {
        var tenant = await TenantAsync();
        await _world.ConfigureSmtpAsync(1);

        var channel = await _world.AddEmailAsync(tenant, confirm: false);

        Assert.False(channel.RecipientVerified);
        var request = Assert.Single(await _world.EventsAsync(tenant));
        Assert.Equal((NotificationEventTypes.RecipientVerification, NotificationPhase.Test), (request.Type, request.Phase));
        var delivery = Assert.Single(await _world.DeliveriesAsync(tenant));
        Assert.Equal((channel.Id, NotificationDeliveryStatus.Pending), (delivery.ChannelId, delivery.Status));
        Assert.Contains(NotificationAuditActions.ChannelVerificationRequested, (await _world.AuditAsync(tenant)).Select(row => row.Action));
    }

    [Fact]
    public async Task Asking_for_a_test_of_an_unconfirmed_address_sends_the_confirmation_request_never_free_text()
    {
        var tenant = await TenantAsync();
        var channel = await _world.AddEmailAsync(tenant, confirm: false);
        await _world.ConfigureSmtpAsync(1);

        Assert.Equal(NotificationOutcome.Changed, (await _world.Admin().RequestTestAsync(tenant, channel.Id, _world.Actor, default)).Outcome);

        Assert.Equal([NotificationEventTypes.RecipientVerification], (await _world.EventsAsync(tenant)).Select(e => e.Type));
    }

    [Fact]
    public async Task Without_a_public_url_an_email_channel_cannot_be_asked_to_confirm()
    {
        var world = new NotificationWorld(_database) { Options = new NotificationOptions { AllowedPrivateNetworks = ["127.0.0.0/8"] } };
        var tenant = await TenantAsync();
        await world.ConfigureSmtpAsync(1);
        var channel = await world.AddEmailAsync(tenant, confirm: false);

        var result = await world.Admin().RequestTestAsync(tenant, channel.Id, world.Actor, default);

        Assert.Equal(NotificationOutcome.TransportNotConfigured, result.Outcome);
        Assert.Empty(await world.EventsAsync(tenant));
    }

    [Fact]
    public async Task An_alert_skips_an_unconfirmed_address_and_goes_out_once_it_confirms()
    {
        var tenant = await TenantAsync();
        await _world.EnableAsync(tenant, [NotificationEventTypes.AgentOffline], kinds: ["email"]);
        var channel = await _world.AddEmailAsync(tenant, confirm: false);
        var engine = _world.Engine(new FixedSource(_world.Clock.GetUtcNow));

        Assert.Equal(0, await engine.SweepAsync(default));
        Assert.Empty(await _world.EventsAsync(tenant));

        Assert.Equal(RecipientOutcome.Confirmed, await _world.RecipientActionAsync(tenant, channel.Id, "ops@example.com", RecipientAction.Confirm));
        _world.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, await engine.SweepAsync(default));

        var delivery = Assert.Single(await _world.DeliveriesAsync(tenant));
        Assert.Equal((channel.Id, NotificationDeliveryStatus.Pending), (delivery.ChannelId, delivery.Status));
    }

    [Fact]
    public async Task Unsubscribing_stops_the_channel_by_itself_and_enabling_it_again_asks_the_address_before_any_mail_resumes()
    {
        var tenant = await TenantAsync();
        var channel = await _world.AddEmailAsync(tenant);
        await _world.ConfigureSmtpAsync(1);

        Assert.Equal(RecipientOutcome.Unsubscribed, await _world.RecipientActionAsync(tenant, channel.Id, "ops@example.com", RecipientAction.Unsubscribe));
        Assert.Equal(RecipientOutcome.Unsubscribed, await _world.RecipientActionAsync(tenant, channel.Id, "ops@example.com", RecipientAction.Unsubscribe));

        var stopped = await ChannelAsync(tenant, channel.Id);
        Assert.Equal((false, (bool?)false), (stopped.Enabled, stopped.RecipientVerified));
        Assert.Single(await _world.AuditAsync(tenant), row => row.Action == NotificationAuditActions.ChannelRecipientUnsubscribed);
        Assert.Empty(await _world.EventsAsync(tenant));

        var enabled = await _world.Admin().SetEnabledAsync(tenant, channel.Id, true, _world.Actor, default);

        Assert.Equal((true, (bool?)false), (enabled.Value!.Enabled, enabled.Value.RecipientVerified));
        Assert.Equal([NotificationEventTypes.RecipientVerification], (await _world.EventsAsync(tenant)).Select(e => e.Type));
        Assert.Equal(RecipientOutcome.Confirmed, await _world.RecipientActionAsync(tenant, channel.Id, "ops@example.com", RecipientAction.Confirm));
        Assert.Equal((true, (bool?)true), ((await ChannelAsync(tenant, channel.Id)).Enabled, (await ChannelAsync(tenant, channel.Id)).RecipientVerified));
    }

    [Fact]
    public async Task A_link_does_nothing_after_the_channel_moved_to_another_address_belongs_to_another_tenant_or_is_gone()
    {
        var tenant = await TenantAsync();
        var other = await TenantAsync();
        var channel = await _world.AddEmailAsync(tenant, "old@example.com", confirm: false);
        Assert.True((await _world.Admin().UpdateChannelAsync(tenant, channel.Id, new UpdateChannelCommand(null, "new@example.com", null), _world.Actor, default)).Succeeded);

        Assert.Equal(RecipientOutcome.Gone, await _world.RecipientActionAsync(tenant, channel.Id, "old@example.com", RecipientAction.Confirm));
        Assert.Equal(RecipientOutcome.Gone, await _world.RecipientActionAsync(other, channel.Id, "new@example.com", RecipientAction.Confirm));
        Assert.Equal((bool?)false, (await ChannelAsync(tenant, channel.Id)).RecipientVerified);

        await _world.Admin().DeleteChannelAsync(tenant, channel.Id, _world.Actor, default);
        Assert.Equal(RecipientOutcome.Gone, await _world.RecipientActionAsync(tenant, channel.Id, "new@example.com", RecipientAction.Confirm));
        Assert.Equal(RecipientOutcome.Invalid, await _world.Recipients().ApplyAsync("not-a-token", default));
    }

    [Fact]
    public async Task Re_pointing_a_confirmed_email_channel_asks_the_new_address_and_stops_alerts_to_it_until_it_confirms()
    {
        var tenant = await TenantAsync();
        var channel = await _world.AddEmailAsync(tenant, "old@example.com");
        await _world.ConfigureSmtpAsync(1);

        var moved = await _world.Admin().UpdateChannelAsync(tenant, channel.Id, new UpdateChannelCommand(null, "new@example.com", null), _world.Actor, default);

        Assert.Equal((bool?)false, moved.Value!.RecipientVerified);
        Assert.Equal([NotificationEventTypes.RecipientVerification], (await _world.EventsAsync(tenant)).Select(e => e.Type));
    }

    [Fact]
    public async Task A_tenant_cannot_dodge_the_hourly_cap_on_confirmation_requests_by_deleting_and_re_creating_channels()
    {
        var world = new NotificationWorld(_database) { Options = new NotificationOptions { AllowedPrivateNetworks = ["127.0.0.0/8"], PublicBaseUrl = NotificationWorld.Loopback.PublicBaseUrl, TestNotificationsPerTenantPerHour = 3 } };
        var tenant = await TenantAsync();
        await world.ConfigureSmtpAsync(1);

        for (var i = 0; i < 6; i++)
        {
            var channel = await world.AddEmailAsync(tenant, $"victim{i}@example.com", $"Channel {i}", confirm: false);
            await world.Admin().DeleteChannelAsync(tenant, channel.Id, world.Actor, default);
        }

        Assert.Equal(3, (await world.DeliveriesAsync(tenant)).Count);
    }

    [Fact]
    public async Task A_delivery_queued_for_an_address_that_is_no_longer_confirmed_is_held_back_but_a_confirmation_request_is_not()
    {
        var tenant = await TenantAsync();
        var channel = await _world.AddEmailAsync(tenant);
        var now = _world.Clock.GetUtcNow();
        await using (var context = _database.CreatePlatformContext())
        {
            foreach (var type in new[] { NotificationEventTypes.AgentOffline, NotificationEventTypes.RecipientVerification })
            {
                var phase = type == NotificationEventTypes.AgentOffline ? NotificationPhase.Raised : NotificationPhase.Test;
                var e = new NotificationEvent(Guid.NewGuid(), tenant, type, phase, NotificationSeverity.Critical, new NotificationSubject("agent", Guid.NewGuid(), "build-01"), null, $"seed:{Guid.NewGuid():N}", now, now);
                context.NotificationEvents.Add(e);
                context.NotificationDeliveries.Add(new NotificationDelivery(Guid.NewGuid(), tenant, e.Id, channel.Id, NotificationChannelKind.Email, channel.Name, channel.TargetDisplay, now));
            }
            await context.SaveChangesAsync();
        }
        await _world.RecipientActionAsync(tenant, channel.Id, "ops@example.com", RecipientAction.Unsubscribe);
        await _world.Admin().SetEnabledAsync(tenant, channel.Id, true, _world.Actor, default);

        var claimed = await _world.Store().ClaimDueAsync("w", now, 10, TimeSpan.FromMinutes(2), 4, default);

        var alert = Assert.Single(claimed, work => work.Event.Type == NotificationEventTypes.AgentOffline);
        Assert.Equal((NotificationDeliveryStatus.Suppressed, NotificationDeliveryCodes.RecipientUnverified), (alert.Skip!.Status, alert.Skip.Code));
        Assert.Null(alert.Target);
        Assert.Null(Assert.Single(claimed, work => work.Event.Type == NotificationEventTypes.RecipientVerification).Skip);
    }

    [Fact]
    public async Task A_webhook_may_use_port_443_and_the_operator_listed_ports_only()
    {
        var world = new NotificationWorld(_database) { Options = new NotificationOptions { AdditionalHttpsPorts = [8443] } };
        var tenant = await TenantAsync();
        var hook = await world.AddWebhookAsync(tenant, "https://hooks.example.com/a", "Default");
        await world.AddWebhookAsync(tenant, "https://hooks.example.com:8443/b", "Listed");

        var refused = await Assert.ThrowsAsync<InvalidNotificationException>(() => world.AddWebhookAsync(tenant, "https://hooks.example.com:22/c", "Ssh"));
        var moved = await Assert.ThrowsAsync<InvalidNotificationException>(() =>
            world.Admin().UpdateChannelAsync(tenant, hook.Id, new UpdateChannelCommand(null, "https://hooks.example.com:5432/a", null), world.Actor, default));

        Assert.Equal(("target", "port_not_allowed"), (refused.Field, refused.Code));
        Assert.Equal(("target", "port_not_allowed"), (moved.Field, moved.Code));
    }
}
