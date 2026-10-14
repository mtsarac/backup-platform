using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Notifications;
using BackupPlatform.Notifications;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.IntegrationTests.Notifications;

/// <remarks>Each test owns its database (a clone of the migrated template): claiming deliveries is global by design, so tests must not see each other's.</remarks>
public sealed class NotificationStoreIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlFixture fixture = new();
    private NotificationWorld _world = null!;

    public async Task InitializeAsync()
    {
        await fixture.InitializeAsync();
        _world = new NotificationWorld(fixture);
    }

    public Task DisposeAsync() => fixture.DisposeAsync();

    private Task<TenantId> TenantAsync() => _world.Quota.Harness.AddTenantAsync();

    [Fact]
    public async Task A_tenant_never_sees_or_changes_another_tenants_channels_or_history()
    {
        var a = await TenantAsync();
        var b = await TenantAsync();
        var channel = await _world.AddEmailAsync(a, "ops@example.com");
        var admin = _world.Admin();

        Assert.Empty(await admin.ListChannelsAsync(b, default) ?? throw new Xunit.Sdk.XunitException("tenant b exists"));
        Assert.Null(await admin.GetChannelAsync(b, channel.Id, default));
        Assert.Equal(NotificationOutcome.NotFound, (await admin.SetEnabledAsync(b, channel.Id, false, _world.Actor, default)).Outcome);
        Assert.Equal(NotificationOutcome.NotFound, (await admin.UpdateChannelAsync(b, channel.Id, new UpdateChannelCommand("stolen", null, null), _world.Actor, default)).Outcome);
        Assert.Equal(NotificationOutcome.NotFound, (await admin.RequestTestAsync(b, channel.Id, _world.Actor, default)).Outcome);
        Assert.Equal(NotificationOutcome.NotFound, await admin.DeleteChannelAsync(b, channel.Id, _world.Actor, default));
        var untouched = await admin.GetChannelAsync(a, channel.Id, default);
        Assert.Equal(("Ops", true), (untouched!.Name, untouched.Enabled));
    }

    [Fact]
    public async Task History_is_scoped_to_the_tenant_that_owns_it()
    {
        var a = await TenantAsync();
        var b = await TenantAsync();
        var channel = await _world.AddEmailAsync(a);
        await _world.AddEmailAsync(b, "other@example.com");
        await _world.ConfigureSmtpAsync(1);
        Assert.Equal(NotificationOutcome.Changed, (await _world.Admin().RequestTestAsync(a, channel.Id, _world.Actor, default)).Outcome);

        var own = await _world.Admin().ListDeliveriesAsync(a, null, null, null, 1, 25, default);
        var foreign = await _world.Admin().ListDeliveriesAsync(b, null, null, null, 1, 25, default);

        Assert.Equal(1, own.TotalCount);
        Assert.Equal(0, foreign.TotalCount);
    }

    [Fact]
    public async Task Names_and_targets_are_unique_within_a_tenant_only_and_the_number_of_channels_is_bounded()
    {
        var a = await TenantAsync();
        var b = await TenantAsync();
        await _world.AddEmailAsync(a, "ops@example.com", "Ops");
        var admin = _world.Admin();

        Assert.Equal(NotificationOutcome.NameTaken, (await admin.CreateChannelAsync(a, new CreateChannelCommand("email", " OPS ", "other@example.com", null, null), _world.Actor, default)).Outcome);
        Assert.Equal(NotificationOutcome.TargetTaken, (await admin.CreateChannelAsync(a, new CreateChannelCommand("email", "Second", "OPS@example.com", null, null), _world.Actor, default)).Outcome);
        Assert.Equal(NotificationOutcome.Changed, (await admin.CreateChannelAsync(b, new CreateChannelCommand("email", "Ops", "ops@example.com", null, null), _world.Actor, default)).Outcome);

        var small = new NotificationWorld(fixture) { Options = new NotificationOptions { MaxChannelsPerTenant = 2, AllowedPrivateNetworks = ["127.0.0.0/8"] } };
        var c = await TenantAsync();
        await small.AddEmailAsync(c, "1@example.com", "One");
        await small.AddEmailAsync(c, "2@example.com", "Two");
        Assert.Equal(NotificationOutcome.LimitReached, (await small.Admin().CreateChannelAsync(c, new CreateChannelCommand("email", "Three", "3@example.com", null, null), small.Actor, default)).Outcome);
    }

    [Fact]
    public async Task A_secret_is_stored_only_in_the_secret_store_and_appears_in_no_view_row_or_audit_event()
    {
        var tenant = await TenantAsync();
        const string url = "https://hooks.example.com/services/T000/B000/PATHTOKEN";
        const string token = "bearer-token-123456";
        var created = await _world.AddWebhookAsync(tenant, url, authentication: "bearer", secret: token);
        var rotated = await _world.Admin().SetSecretAsync(tenant, created.Id, "rotated-token-654321", _world.Actor, default);
        await _world.Admin().ClearSecretAsync(tenant, created.Id, _world.Actor, default);
        await _world.Admin().SetSecretAsync(tenant, created.Id, "final-token-000111", _world.Actor, default);

        Assert.True(created.HasSecret);
        Assert.Equal("https://hooks.example.com/…", created.TargetDisplay);
        // Replaced and cleared secrets are deleted: exactly the current one remains.
        Assert.Equal(["final-token-000111"], _world.Secrets.Values);
        var rows = System.Text.Json.JsonSerializer.Serialize(new object[] { created, rotated.Value!, await _world.Admin().GetChannelAsync(tenant, created.Id, default) ?? throw new InvalidOperationException() });
        var audit = string.Join("\n", (await _world.AuditAsync(tenant)).Select(row => row.DetailsJson + row.TargetId));
        await using var context = fixture.CreatePlatformContext();
        var channelRow = await context.NotificationChannels.AsNoTracking().SingleAsync(row => row.TenantId == tenant);
        foreach (var leaked in new[] { token, "rotated-token-654321", "final-token-000111", "PATHTOKEN" })
        {
            Assert.DoesNotContain(leaked, rows);
            Assert.DoesNotContain(leaked, audit);
            Assert.DoesNotContain(leaked, channelRow.SecretReference);
        }
        Assert.Contains(NotificationAuditActions.ChannelSecretRotated, (await _world.AuditAsync(tenant)).Select(row => row.Action));
        Assert.Contains(NotificationAuditActions.ChannelSecretCleared, (await _world.AuditAsync(tenant)).Select(row => row.Action));
    }

    [Fact]
    public async Task Every_change_is_audited_with_its_actor_and_without_the_address()
    {
        var tenant = await TenantAsync();
        var channel = await _world.AddEmailAsync(tenant, "private.person@example.com", "Boss");
        await _world.Admin().SetEnabledAsync(tenant, channel.Id, false, _world.Actor, default);
        await _world.Admin().SetEnabledAsync(tenant, channel.Id, false, _world.Actor, default);
        await _world.EnableAsync(tenant);
        await _world.Admin().DeleteChannelAsync(tenant, channel.Id, _world.Actor, default);

        var audit = await _world.AuditAsync(tenant);

        Assert.Equal([NotificationAuditActions.ChannelCreated, NotificationAuditActions.ChannelRecipientConfirmed, NotificationAuditActions.ChannelDisabled, NotificationAuditActions.PolicyUpdated,
            NotificationAuditActions.ChannelDeleted], audit.Select(row => row.Action));
        var confirmation = Assert.Single(audit, row => row.Action == NotificationAuditActions.ChannelRecipientConfirmed);
        Assert.Equal((UserId.System, BackupPlatform.Domain.Audit.AuditActorKind.Anonymous), (confirmation.ActorUserId, confirmation.ActorKind));
        Assert.All(audit, row =>
        {
            if (row != confirmation) Assert.Equal(_world.Actor.UserId, row.ActorUserId);
            Assert.Equal(tenant, row.TenantId);
            Assert.DoesNotContain("private.person", row.DetailsJson);
        });
    }

    [Fact]
    public async Task The_policy_is_created_on_the_first_change_remembers_when_it_was_switched_on_and_ignores_a_repeat()
    {
        var tenant = await TenantAsync();
        var before = await _world.Admin().GetPolicyAsync(tenant, default);
        Assert.False(before!.Enabled);
        Assert.Null(before.UpdatedAt);

        var switchedOn = _world.Clock.GetUtcNow();
        await _world.EnableAsync(tenant, ["backup_failed"], "critical", ["email"], recovery: false, staleHours: 12, offlineMinutes: 30, reminderHours: 0, perHour: 5);
        _world.Clock.Advance(TimeSpan.FromHours(1));
        var again = await _world.Admin().UpdatePolicyAsync(tenant, new PolicyCommand(true, ["backup_failed"], "critical", ["email"], false, 12, 30, 0, 5), _world.Actor, default);

        Assert.Equal(NotificationOutcome.Unchanged, again.Outcome);
        await using var context = fixture.CreatePlatformContext();
        var row = await context.NotificationPolicies.AsNoTracking().SingleAsync(policy => policy.TenantId == tenant);
        Assert.Equal(switchedOn, row.ActiveSince);
        Assert.Equal((NotificationSeverity.Critical, 12, 30, 0, 5), (row.MinimumSeverity, row.StaleAfterHours, row.AgentOfflineAfterMinutes, row.ReminderIntervalHours, row.MaxNotificationsPerHour));
        Assert.Equal(["backup_failed"], row.EventTypes);
        Assert.Equal([NotificationChannelKind.Email], row.ChannelKinds);
    }

    [Theory]
    [InlineData("staleAfterHours", 0)]
    [InlineData("agentOfflineAfterMinutes", 4)]
    [InlineData("reminderIntervalHours", 169)]
    [InlineData("maxNotificationsPerHour", 201)]
    public async Task A_threshold_outside_its_range_is_rejected(string field, int value)
    {
        var tenant = await TenantAsync();
        var command = new PolicyCommand(true, ["backup_failed"], "warning", ["email"], true, 48, 15, 24, 30);
        command = field switch
        {
            "staleAfterHours" => command with { StaleAfterHours = value },
            "agentOfflineAfterMinutes" => command with { AgentOfflineAfterMinutes = value },
            "reminderIntervalHours" => command with { ReminderIntervalHours = value },
            _ => command with { MaxNotificationsPerHour = value }
        };

        var failure = await Assert.ThrowsAsync<InvalidNotificationException>(() => _world.Admin().UpdatePolicyAsync(tenant, command, _world.Actor, default));

        Assert.Equal(field, failure.Field);
    }

    [Fact]
    public async Task A_test_notification_needs_a_usable_transport_is_rate_limited_and_does_not_depend_on_the_policy()
    {
        var tenant = await TenantAsync();
        var email = await _world.AddEmailAsync(tenant);
        var admin = _world.Admin();

        Assert.Equal(NotificationOutcome.TransportNotConfigured, (await admin.RequestTestAsync(tenant, email.Id, _world.Actor, default)).Outcome);
        await _world.ConfigureSmtpAsync(1);
        for (var i = 0; i < _world.Options.TestNotificationsPerChannelPerHour; i++)
            Assert.Equal(NotificationOutcome.Changed, (await admin.RequestTestAsync(tenant, email.Id, _world.Actor, default)).Outcome);
        Assert.Equal(NotificationOutcome.TestRateLimited, (await admin.RequestTestAsync(tenant, email.Id, _world.Actor, default)).Outcome);
        _world.Clock.Advance(TimeSpan.FromMinutes(61));
        Assert.Equal(NotificationOutcome.Changed, (await admin.RequestTestAsync(tenant, email.Id, _world.Actor, default)).Outcome);

        Assert.All(await _world.EventsAsync(tenant), e => Assert.Equal((NotificationEventTypes.Test, NotificationPhase.Test), (e.Type, e.Phase)));
    }

    [Fact]
    public async Task The_enabled_transport_cannot_be_left_half_configured()
    {
        var transports = _world.Transports();
        var credentials = new Dictionary<string, string?> { ["host"] = "smtp.example.com", ["port"] = "587", ["security"] = "starttls", ["username"] = "bob", ["fromAddress"] = "a@example.com" };

        var incomplete = await Assert.ThrowsAsync<InvalidNotificationException>(() => transports.UpdateAsync(NotificationTransports.Email, new TransportCommand(true, credentials), _world.PlatformActor, default));
        Assert.Equal("incomplete_configuration", incomplete.Code);

        Assert.True((await transports.UpdateAsync(NotificationTransports.Email, new TransportCommand(false, credentials), _world.PlatformActor, default)).Succeeded);
        Assert.True((await transports.SetSecretAsync(NotificationTransports.Email, "smtp-password", _world.PlatformActor, default)).Succeeded);
        Assert.True((await transports.UpdateAsync(NotificationTransports.Email, new TransportCommand(true, credentials), _world.PlatformActor, default)).Value!.Enabled);
        // Taking the credential away switches the transport off instead of leaving it enabled and unable to authenticate.
        var cleared = await transports.ClearSecretAsync(NotificationTransports.Email, _world.PlatformActor, default);
        Assert.False(cleared.Value!.Enabled);
        Assert.Empty(_world.Secrets.Values);
        Assert.DoesNotContain("smtp-password", string.Join("\n", (await _world.AuditAsync(null)).Select(row => row.DetailsJson)));
    }

    // ---- delivery claims ----------------------------------------------------------------------------------------------------------------

    private async Task<(TenantId Tenant, NotificationChannelView Channel)> SeedDueAsync(int count)
    {
        var tenant = await TenantAsync();
        await _world.EnableAsync(tenant);
        var channel = await _world.AddEmailAsync(tenant);
        var now = _world.Clock.GetUtcNow();
        await using var context = fixture.CreatePlatformContext();
        for (var i = 0; i < count; i++)
        {
            var e = new NotificationEvent(Guid.NewGuid(), tenant, NotificationEventTypes.AgentOffline, NotificationPhase.Raised, NotificationSeverity.Critical,
                new NotificationSubject("agent", Guid.NewGuid(), $"agent-{i}"), null, $"seed:{Guid.NewGuid():N}", now, now);
            context.NotificationEvents.Add(e);
            context.NotificationDeliveries.Add(new NotificationDelivery(Guid.NewGuid(), tenant, e.Id, channel.Id, NotificationChannelKind.Email, channel.Name, channel.TargetDisplay, now));
        }
        await context.SaveChangesAsync();
        return (tenant, channel);
    }

    [Fact]
    public async Task Concurrent_workers_never_claim_the_same_delivery()
    {
        await SeedDueAsync(60);
        var claimed = new System.Collections.Concurrent.ConcurrentBag<(string Owner, Guid Id)>();

        await Task.WhenAll(Enumerable.Range(0, 4).Select(i => Task.Run(async () =>
        {
            var store = _world.Store();
            while (true)
            {
                var batch = await store.ClaimDueAsync($"worker-{i}", _world.Clock.GetUtcNow(), 7, TimeSpan.FromMinutes(2), 4, default);
                if (batch.Count == 0) return;
                foreach (var work in batch) claimed.Add(($"worker-{i}", work.DeliveryId));
            }
        })));

        Assert.Equal(60, claimed.Count);
        Assert.Equal(60, claimed.Select(c => c.Id).Distinct().Count());
    }

    [Fact]
    public async Task A_dead_workers_claim_expires_and_is_taken_over_and_the_old_owner_cannot_overwrite_the_new_result()
    {
        var (tenant, _) = await SeedDueAsync(1);
        var store = _world.Store();
        var now = _world.Clock.GetUtcNow();
        var first = Assert.Single(await store.ClaimDueAsync("w1", now, 10, TimeSpan.FromMinutes(2), 4, default));
        Assert.Empty(await store.ClaimDueAsync("w2", now.AddMinutes(1), 10, TimeSpan.FromMinutes(2), 4, default));

        var second = Assert.Single(await store.ClaimDueAsync("w2", now.AddMinutes(3), 10, TimeSpan.FromMinutes(2), 4, default));

        Assert.Equal((first.DeliveryId, 1, 2), (second.DeliveryId, first.Attempt, second.Attempt));
        Assert.False(await store.CompleteAsync(first.DeliveryId, "w1", first.Attempt, new DeliveryResult(NotificationDeliveryStatus.Sent), now.AddMinutes(4), default));
        Assert.True(await store.CompleteAsync(second.DeliveryId, "w2", second.Attempt, new DeliveryResult(NotificationDeliveryStatus.Failed, "http_400"), now.AddMinutes(4), default));
        Assert.False(await store.CompleteAsync(second.DeliveryId, "w2", second.Attempt, new DeliveryResult(NotificationDeliveryStatus.Sent), now.AddMinutes(5), default));
        var row = Assert.Single(await _world.DeliveriesAsync(tenant));
        Assert.Equal((NotificationDeliveryStatus.Failed, "http_400", 2), (row.Status, row.ErrorCode, row.AttemptCount));
    }

    [Fact]
    public async Task A_delivery_that_keeps_killing_its_worker_ends_as_failed_instead_of_being_retried_forever()
    {
        var (tenant, _) = await SeedDueAsync(1);
        var store = _world.Store();
        var at = _world.Clock.GetUtcNow();
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            Assert.Single(await store.ClaimDueAsync("crashy", at, 10, TimeSpan.FromMinutes(1), maxAttempts: 2, default));
            at = at.AddMinutes(2);
        }

        Assert.Empty(await store.ClaimDueAsync("crashy", at, 10, TimeSpan.FromMinutes(1), maxAttempts: 2, default));

        var row = Assert.Single(await _world.DeliveriesAsync(tenant));
        Assert.Equal((NotificationDeliveryStatus.Failed, NotificationDeliveryCodes.AttemptsExhausted), (row.Status, row.ErrorCode));
    }

    [Fact]
    public async Task A_retry_is_not_claimed_before_its_time()
    {
        await SeedDueAsync(1);
        var store = _world.Store();
        var now = _world.Clock.GetUtcNow();
        var claim = Assert.Single(await store.ClaimDueAsync("w1", now, 10, TimeSpan.FromMinutes(2), 4, default));
        await store.CompleteAsync(claim.DeliveryId, "w1", claim.Attempt, new DeliveryResult(NotificationDeliveryStatus.Pending, "http_503", now.AddSeconds(60)), now, default);

        Assert.Empty(await store.ClaimDueAsync("w1", now.AddSeconds(59), 10, TimeSpan.FromMinutes(2), 4, default));
        Assert.Equal(2, Assert.Single(await store.ClaimDueAsync("w1", now.AddSeconds(60), 10, TimeSpan.FromMinutes(2), 4, default)).Attempt);
    }

    [Fact]
    public async Task What_the_owner_switched_off_after_the_fan_out_is_not_sent()
    {
        var (tenant, channel) = await SeedDueAsync(3);
        await _world.Admin().SetEnabledAsync(tenant, channel.Id, false, _world.Actor, default);
        var store = _world.Store();
        var now = _world.Clock.GetUtcNow();

        var disabled = await store.ClaimDueAsync("w1", now, 10, TimeSpan.FromMinutes(2), 4, default);
        Assert.All(disabled, work => Assert.Equal(new DeliverySkip(NotificationDeliveryStatus.Suppressed, NotificationDeliveryCodes.ChannelDisabled), work.Skip));
        Assert.All(disabled, work => Assert.Null(work.Target));

        var (tenant2, _) = await SeedDueAsync(1);
        await _world.Admin().UpdatePolicyAsync(tenant2, new PolicyCommand(false, ["agent_offline"], "warning", ["email"], true, 48, 15, 24, 30), _world.Actor, default);
        Assert.Equal(NotificationDeliveryCodes.PolicyDisabled, Assert.Single(await store.ClaimDueAsync("w1", now, 10, TimeSpan.FromMinutes(2), 4, default)).Skip!.Code);

        var (tenant3, channel3) = await SeedDueAsync(1);
        await _world.Admin().DeleteChannelAsync(tenant3, channel3.Id, _world.Actor, default);
        Assert.Equal(NotificationDeliveryCodes.ChannelRemoved, Assert.Single(await store.ClaimDueAsync("w1", now, 10, TimeSpan.FromMinutes(2), 4, default)).Skip!.Code);
    }

    [Fact]
    public async Task Pruning_removes_old_finished_history_with_its_deliveries_and_keeps_anything_in_flight()
    {
        var (tenant, _) = await SeedDueAsync(2);
        var store = _world.Store();
        var now = _world.Clock.GetUtcNow();
        var claim = (await store.ClaimDueAsync("w1", now, 1, TimeSpan.FromMinutes(2), 4, default)).Single();
        await store.CompleteAsync(claim.DeliveryId, "w1", claim.Attempt, new DeliveryResult(NotificationDeliveryStatus.Sent), now, default);

        var removed = await store.PruneAsync(now.AddDays(1), default);

        Assert.Equal(1, removed);
        var remaining = Assert.Single(await _world.DeliveriesAsync(tenant));
        Assert.Equal(NotificationDeliveryStatus.Pending, remaining.Status);
        Assert.Single(await _world.EventsAsync(tenant));
    }
}
