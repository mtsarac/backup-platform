using System.Net;
using BackupPlatform.Domain.Notifications;
using BackupPlatform.Notifications;
using BackupPlatform.UnitTests.Notifications.Support;
using static BackupPlatform.UnitTests.Notifications.Support.NotificationTestData;

namespace BackupPlatform.UnitTests.Notifications;

/// <summary>Who may receive what: an email address is only sent alerts after its owner confirmed it, and stops when its owner says so.</summary>
public sealed class NotificationRecipientConsentTests
{
    [Fact]
    public void A_new_email_channel_waits_for_confirmation_and_no_other_kind_ever_does()
    {
        var email = Email(confirmed: false);

        Assert.True(email.AwaitsConfirmation);
        Assert.False(email.IsDeliverable);
        Assert.False(Webhook().AwaitsConfirmation);
        Assert.True(Webhook().IsDeliverable);
        Assert.False(Webhook().ConfirmRecipient(Start));
    }

    [Fact]
    public void Confirming_makes_the_channel_deliverable_once_and_repeating_it_changes_nothing()
    {
        var email = Email(confirmed: false);

        Assert.True(email.ConfirmRecipient(Start.AddMinutes(1)));
        Assert.False(email.ConfirmRecipient(Start.AddMinutes(2)));

        Assert.True(email.IsDeliverable);
        Assert.Equal(Start.AddMinutes(1), email.RecipientVerifiedAt);
    }

    [Fact]
    public void Unsubscribing_stops_the_channel_and_forgets_the_confirmation_so_enabling_it_again_does_not_resume_mail()
    {
        var email = Email();

        Assert.True(email.Unsubscribe(Start));
        Assert.False(email.Unsubscribe(Start));

        Assert.False(email.Enabled);
        Assert.Null(email.RecipientVerifiedAt);
        email.SetEnabled(true, Start);
        Assert.False(email.IsDeliverable);
        Assert.True(email.AwaitsConfirmation);
    }

    [Fact]
    public void Re_pointing_an_email_channel_needs_the_new_address_to_confirm()
    {
        var email = Email();

        Assert.True(email.Retarget("someone-else@example.com", Start));

        Assert.False(email.IsDeliverable);
    }

    [Fact]
    public void Re_pointing_to_the_same_address_keeps_the_confirmation()
    {
        var email = Email();

        Assert.False(email.Retarget("ops@example.com", Start));

        Assert.True(email.IsDeliverable);
    }

    [Fact]
    public void A_link_fingerprint_follows_the_address_and_ignores_its_case()
    {
        Assert.Equal(RecipientLinks.Fingerprint("Ops@Example.com"), RecipientLinks.Fingerprint("ops@example.com"));
        Assert.NotEqual(RecipientLinks.Fingerprint("ops@example.com"), RecipientLinks.Fingerprint("other@example.com"));
    }

    [Fact]
    public void Outbound_https_may_use_port_443_and_only_the_other_ports_the_operator_listed()
    {
        var options = new NotificationOptions { AdditionalHttpsPorts = [8443] };

        Assert.True(options.IsHttpsPortAllowed(443));
        Assert.True(options.IsHttpsPortAllowed(8443));
        Assert.False(options.IsHttpsPortAllowed(22));
        Assert.False(options.IsHttpsPortAllowed(5432));
        Assert.False(new NotificationOptions().IsHttpsPortAllowed(8443));
    }

    [Theory]
    [InlineData("https://backup.example.com", true)]
    [InlineData("http://localhost:5000/", true)]
    [InlineData("https://backup.example.com/app", true)]
    [InlineData("ftp://backup.example.com", false)]
    [InlineData("backup.example.com", false)]
    [InlineData("https://user:pw@backup.example.com", false)]
    [InlineData("https://backup.example.com/?x=1", false)]
    public void The_public_url_must_be_a_plain_absolute_http_address(string url, bool valid) =>
        Assert.Equal(valid, new NotificationOptions { PublicBaseUrl = url }.Validate().Count == 0);

    [Theory]
    [InlineData("192.88.99.1")]
    [InlineData("64:ff9b:1::a00:1")]
    [InlineData("3fff::1")]
    [InlineData("5f00::1")]
    public void Reserved_relay_and_local_use_ranges_are_restricted(string address) =>
        Assert.True(NotificationNetworkGuard.IsRestricted(IPAddress.Parse(address)));
}

public sealed class NotificationConfirmationEngineTests
{
    [Fact]
    public async Task An_alert_is_not_lost_while_the_only_email_address_is_unconfirmed_it_goes_out_once_that_address_confirms()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(Start);
        var engine = new NotificationEngine(new FakeNotificationStore(), new StubSource(), new NotificationOptions(), time, Microsoft.Extensions.Logging.Abstractions.NullLogger<NotificationEngine>.Instance);
        var session = new InMemorySession { Policy = EnabledPolicy(Start, policy => policy.Update(true, NotificationEventTypes.Selectable, NotificationSeverity.Warning, [NotificationChannelKind.Email], true, 48, 15, 24, 30, Start)) };
        var channel = Email(confirmed: false);
        session.Channels.Add(channel);
        var offline = Set(NotificationEventTypes.AgentOffline, true, Condition(NotificationEventTypes.AgentOffline, "agent_offline:a"));

        Assert.Equal(0, await engine.ApplyAsync(session, [offline], time.GetUtcNow()));
        Assert.Empty(session.Deliveries);

        channel.ConfirmRecipient(time.GetUtcNow());
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, await engine.ApplyAsync(session, [offline], time.GetUtcNow()));
        Assert.Equal(channel.Id, Assert.Single(session.Deliveries).ChannelId);
    }

    [Fact]
    public async Task Unconfirmed_and_unsubscribed_addresses_get_no_delivery_while_confirmed_channels_still_do()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(Start);
        var engine = new NotificationEngine(new FakeNotificationStore(), new StubSource(), new NotificationOptions(), time, Microsoft.Extensions.Logging.Abstractions.NullLogger<NotificationEngine>.Instance);
        var session = new InMemorySession { Policy = EnabledPolicy(Start) };
        var unsubscribed = Email("gone@example.com", "Gone");
        unsubscribed.Unsubscribe(Start);
        session.Channels.AddRange([Email("ops@example.com", "Ops"), Email("stranger@example.com", "Stranger", confirmed: false), unsubscribed, Webhook()]);

        await engine.ApplyAsync(session, [Set(NotificationEventTypes.AgentOffline, true, Condition(NotificationEventTypes.AgentOffline, "agent_offline:a"))], time.GetUtcNow());

        Assert.Equal(["Hook", "Ops"], session.Deliveries.Select(delivery => delivery.ChannelName).Order());
    }
}
