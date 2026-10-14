using System.Net;
using BackupPlatform.Domain.Notifications;
using BackupPlatform.Notifications;
using static BackupPlatform.UnitTests.Notifications.Support.NotificationTestData;

namespace BackupPlatform.UnitTests.Notifications;

public sealed class NotificationValidationTests
{
    [Theory]
    [InlineData("ops@example.com", "ops@example.com")]
    [InlineData("  Ops@Example.com ", "Ops@Example.com")]
    public void A_plain_email_address_is_accepted(string input, string expected) =>
        Assert.Equal(expected, NotificationTargets.Normalize(NotificationChannelKind.Email, input));

    [Theory]
    [InlineData("")]
    [InlineData("no-at-sign")]
    [InlineData("a@b")]
    [InlineData("Ops <ops@example.com>")]
    [InlineData("a@example.com, b@example.com")]
    [InlineData("a@example.com\r\nBcc: x@example.com")]
    public void Display_names_lists_and_header_injection_are_refused_as_recipients(string input) =>
        Assert.Null(NotificationTargets.Normalize(NotificationChannelKind.Email, input));

    [Theory]
    [InlineData("https://hooks.example.com/notify", "https://hooks.example.com/notify")]
    [InlineData("https://HOOKS.example.com:8443/a/b", "https://hooks.example.com:8443/a/b")]
    public void A_webhook_must_be_a_clean_https_url(string input, string expected) =>
        Assert.Equal(expected, NotificationTargets.Normalize(NotificationChannelKind.Webhook, input));

    [Theory]
    [InlineData("http://hooks.example.com/n")]
    [InlineData("https://user:pass@hooks.example.com/n")]
    [InlineData("https://hooks.example.com/n?token=abc")]
    [InlineData("https://hooks.example.com/n#frag")]
    [InlineData("ftp://hooks.example.com/n")]
    [InlineData("hooks.example.com/n")]
    [InlineData("https:///n")]
    public void Plain_http_credentials_in_the_url_query_strings_and_other_schemes_are_refused(string input) =>
        Assert.Null(NotificationTargets.Normalize(NotificationChannelKind.Webhook, input));

    [Fact]
    public void A_webhook_is_only_ever_displayed_without_its_path_because_the_path_may_be_a_token()
    {
        var display = NotificationTargets.Display(NotificationChannelKind.Webhook, "https://hooks.example.com:8443/services/T000/B000/SECRETTOKEN");

        Assert.Equal("https://hooks.example.com:8443/…", display);
        Assert.DoesNotContain("SECRETTOKEN", display);
    }

    [Fact]
    public void Smtp_credentials_are_refused_over_an_unencrypted_connection()
    {
        var values = new Dictionary<string, string?> { ["host"] = "smtp.example.com", ["port"] = "25", ["security"] = "none", ["username"] = "bob", ["fromAddress"] = "alerts@example.com" };

        var failure = Assert.Throws<InvalidNotificationException>(() => NotificationTransportRules.ValidateEmail(values, enabled: true));

        Assert.Equal(("security", "insecure_authentication"), (failure.Field, failure.Code));
    }

    [Theory]
    [InlineData("host", "")]
    [InlineData("port", "0")]
    [InlineData("port", "70000")]
    [InlineData("port", "abc")]
    [InlineData("security", "ssl3")]
    [InlineData("security", "1")]
    [InlineData("fromAddress", "not-an-address")]
    public void An_enabled_smtp_transport_needs_a_valid_host_port_security_and_sender(string field, string value)
    {
        var values = new Dictionary<string, string?> { ["host"] = "smtp.example.com", ["port"] = "587", ["security"] = "starttls", ["fromAddress"] = "alerts@example.com" };
        values[field] = value;

        var failure = Assert.Throws<InvalidNotificationException>(() => NotificationTransportRules.ValidateEmail(values, enabled: true));

        Assert.Equal(field, failure.Field);
    }

    [Fact]
    public void A_transport_with_credentials_but_no_stored_password_is_not_available()
    {
        var settings = new NotificationTransportSettings(NotificationTransports.Email, Start);
        settings.Apply(true, new Dictionary<string, string> { ["host"] = "smtp.example.com", ["port"] = "587", ["security"] = "starttls", ["username"] = "bob", ["fromAddress"] = "a@example.com" }, Start);

        Assert.Null(NotificationTransportRules.ReadEmail(settings));
        settings.AttachSecret("nt-email-1", Start);
        Assert.NotNull(NotificationTransportRules.ReadEmail(settings));
    }

    [Fact]
    public void A_message_is_one_line_per_field_and_carries_no_more_than_the_event_states()
    {
        var work = Work(NotificationChannelKind.Email, "a@example.com", subjectName: "nightly\r\nBcc: evil@example.com", type: NotificationEventTypes.BackupFailed,
            facts: new Dictionary<string, string> { ["code"] = "disk_full" });

        var message = NotificationMessageFormatter.Render(work.Event);

        Assert.DoesNotContain('\r', message.Subject);
        Assert.DoesNotContain('\n', message.Subject);
        Assert.Contains("disk_full", message.Text);
        Assert.True(message.Short.Length <= 160);
        Assert.DoesNotContain("evil@example.com\n", message.Text);
    }
}

public sealed class NotificationNetworkGuardTests
{
    private static readonly NotificationNetworkGuard Guard = new(new NotificationOptions());

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.5")]
    [InlineData("172.16.9.9")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fd00::1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("64:ff9b::a00:1")]
    [InlineData("2002:c0a8:0101::1")]
    public void Private_loopback_link_local_and_disguised_addresses_are_restricted(string address) =>
        Assert.True(NotificationNetworkGuard.IsRestricted(IPAddress.Parse(address)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("93.184.216.34")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("::ffff:8.8.8.8")]
    public void Public_addresses_are_not_restricted(string address) =>
        Assert.False(NotificationNetworkGuard.IsRestricted(IPAddress.Parse(address)));

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("[::1]")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("localhost")]
    public async Task A_restricted_target_is_refused_before_any_connection_is_attempted(string host)
    {
        var failure = await Assert.ThrowsAsync<NotificationNetworkException>(() => Guard.ResolveAsync(host, CancellationToken.None));

        Assert.Equal(NotificationNetworkException.Denied, failure.Code);
    }

    [Fact]
    public async Task An_operator_may_allow_a_private_range_and_only_that_range()
    {
        var guard = new NotificationNetworkGuard(new NotificationOptions { AllowedPrivateNetworks = ["127.0.0.0/8"] });

        Assert.Equal([IPAddress.Loopback], await guard.ResolveAsync("127.0.0.1", CancellationToken.None));
        await Assert.ThrowsAsync<NotificationNetworkException>(() => guard.ResolveAsync("10.1.2.3", CancellationToken.None));
    }
}
