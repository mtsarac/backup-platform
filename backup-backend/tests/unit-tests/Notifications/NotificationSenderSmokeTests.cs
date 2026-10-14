using System.Globalization;
using MimeKit;
using System.Net.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BackupPlatform.Domain.Notifications;
using BackupPlatform.Notifications;
using BackupPlatform.Notifications.Senders;
using BackupPlatform.UnitTests.Notifications.Support;
using Microsoft.Extensions.Time.Testing;
using static BackupPlatform.UnitTests.Notifications.Support.NotificationTestData;

namespace BackupPlatform.UnitTests.Notifications;

/// <summary>Real delivery over real sockets to isolated loopback servers: what actually goes on the wire, and what happens when the far side misbehaves.</summary>
public sealed class WebhookDeliverySmokeTests : IDisposable
{
    private readonly InMemorySecrets _secrets = new();
    private readonly FakeTimeProvider _time = new(Start);
    private readonly List<IDisposable> _disposables = [];

    public void Dispose() => _disposables.ForEach(disposable => disposable.Dispose());

    /// <summary>The sender under test, allowed to reach <paramref name="port"/> (a test server's random port) and, unless <paramref name="allowLoopback"/> is false, the loopback range.</summary>
    private WebhookNotificationSender Sender(int port, bool allowLoopback = true)
    {
        var options = new NotificationOptions { AllowedPrivateNetworks = allowLoopback ? ["127.0.0.0/8"] : [], AdditionalHttpsPorts = [port] };
        var client = new NotificationHttpClient(new NotificationNetworkGuard(options),
            handler => handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true);
        _disposables.Add(client);
        return new WebhookNotificationSender(client, _secrets, _time);
    }

    [Fact]
    public async Task A_delivery_posts_the_event_as_json_with_an_idempotency_key_and_nothing_sensitive()
    {
        await using var server = new TestHttpsServer();
        var work = Work(NotificationChannelKind.Webhook, server.Url(), facts: new Dictionary<string, string> { ["minutes"] = "42" });

        var outcome = await Sender(server.Port).SendAsync(work, CancellationToken.None);

        Assert.Equal(SendStatus.Sent, outcome.Status);
        var request = Assert.Single(server.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal(work.DeliveryId.ToString("D"), request.Headers["idempotency-key"]);
        Assert.Equal("application/json", request.Headers["content-type"]);
        Assert.False(request.Headers.ContainsKey("authorization"));
        using var json = JsonDocument.Parse(request.Body);
        var root = json.RootElement;
        Assert.Equal("agent_offline", root.GetProperty("type").GetString());
        Assert.Equal("raised", root.GetProperty("phase").GetString());
        Assert.Equal("critical", root.GetProperty("severity").GetString());
        Assert.Equal("build-01", root.GetProperty("subject").GetProperty("name").GetString());
        Assert.Equal("42", root.GetProperty("facts").GetProperty("minutes").GetString());
        Assert.Equal(["attempt", "deliveryId", "facts", "id", "occurredAt", "phase", "severity", "subject", "tenantId", "type"],
            root.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Bearer_authentication_sends_the_token_and_the_failure_outcome_never_contains_it()
    {
        await using var server = new TestHttpsServer(_ => new TestResponse(400));
        _secrets.Set("nc-1", "super-secret-token");
        var work = Work(NotificationChannelKind.Webhook, server.Url(), WebhookAuthentication.Bearer, "nc-1");

        var outcome = await Sender(server.Port).SendAsync(work, CancellationToken.None);

        Assert.Equal("Bearer super-secret-token", Assert.Single(server.Requests).Headers["authorization"]);
        Assert.Equal((SendStatus.Failed, "http_400"), (outcome.Status, outcome.Code));
        Assert.DoesNotContain("super-secret-token", outcome.ToString());
    }

    [Fact]
    public async Task Hmac_authentication_signs_the_timestamped_body_and_never_sends_the_secret()
    {
        await using var server = new TestHttpsServer();
        _secrets.Set("nc-2", "signing-key");
        var work = Work(NotificationChannelKind.Webhook, server.Url(), WebhookAuthentication.Hmac, "nc-2");

        await Sender(server.Port).SendAsync(work, CancellationToken.None);

        var request = Assert.Single(server.Requests);
        var timestamp = request.Headers["x-backup-timestamp"];
        Assert.Equal(_time.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), timestamp);
        var expected = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes("signing-key"), Encoding.UTF8.GetBytes(timestamp + "." + request.BodyText)));
        Assert.Equal("sha256=" + expected, request.Headers["x-backup-signature"]);
        Assert.False(request.Headers.ContainsKey("authorization"));
        Assert.DoesNotContain("signing-key", request.BodyText);
    }

    [Fact]
    public async Task A_missing_or_unreadable_secret_is_a_permanent_configuration_failure_and_nothing_is_sent()
    {
        await using var server = new TestHttpsServer();

        var noReference = await Sender(server.Port).SendAsync(Work(NotificationChannelKind.Webhook, server.Url(), WebhookAuthentication.Bearer, null), CancellationToken.None);
        var unreadable = await Sender(server.Port).SendAsync(Work(NotificationChannelKind.Webhook, server.Url(), WebhookAuthentication.Bearer, "gone"), CancellationToken.None);

        Assert.Equal((SendStatus.Failed, "secret_missing"), (noReference.Status, noReference.Code));
        Assert.Equal((SendStatus.Failed, "secret_unavailable"), (unreadable.Status, unreadable.Code));
        Assert.Empty(server.Requests);
    }

    [Theory]
    [InlineData(500, SendStatus.Retry)]
    [InlineData(503, SendStatus.Retry)]
    [InlineData(408, SendStatus.Retry)]
    [InlineData(429, SendStatus.Retry)]
    [InlineData(400, SendStatus.Failed)]
    [InlineData(401, SendStatus.Failed)]
    [InlineData(404, SendStatus.Failed)]
    [InlineData(204, SendStatus.Sent)]
    public async Task The_endpoints_answer_decides_between_sent_retry_and_permanent_failure(int status, SendStatus expected)
    {
        await using var server = new TestHttpsServer(_ => new TestResponse(status));

        var outcome = await Sender(server.Port).SendAsync(Work(NotificationChannelKind.Webhook, server.Url()), CancellationToken.None);

        Assert.Equal(expected, outcome.Status);
    }

    [Fact]
    public async Task A_rate_limited_endpoint_dictates_when_to_come_back()
    {
        await using var server = new TestHttpsServer(_ => new TestResponse(429, new Dictionary<string, string> { ["Retry-After"] = "120" }));

        var outcome = await Sender(server.Port).SendAsync(Work(NotificationChannelKind.Webhook, server.Url()), CancellationToken.None);

        Assert.Equal((SendStatus.Retry, "http_429", (TimeSpan?)TimeSpan.FromSeconds(120)), (outcome.Status, outcome.Code, outcome.RetryAfter));
    }

    [Fact]
    public async Task A_redirect_is_never_followed_so_it_cannot_lead_to_a_host_nobody_validated()
    {
        await using var internalService = new TestHttpsServer();
        await using var server = new TestHttpsServer(_ => new TestResponse(302, new Dictionary<string, string> { ["Location"] = internalService.Url("/admin") }));

        var outcome = await Sender(server.Port).SendAsync(Work(NotificationChannelKind.Webhook, server.Url()), CancellationToken.None);

        Assert.Equal((SendStatus.Failed, "redirect_not_followed"), (outcome.Status, outcome.Code));
        Assert.Empty(internalService.Requests);
    }

    [Fact]
    public async Task By_default_a_loopback_or_private_endpoint_is_refused_and_no_connection_is_made()
    {
        await using var server = new TestHttpsServer();

        var outcome = await Sender(server.Port, allowLoopback: false).SendAsync(Work(NotificationChannelKind.Webhook, server.Url()), CancellationToken.None);

        Assert.Equal((SendStatus.Failed, NotificationNetworkException.Denied), (outcome.Status, outcome.Code));
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task A_name_that_resolves_to_a_private_address_is_refused_like_the_address_itself()
    {
        await using var server = new TestHttpsServer();

        var outcome = await Sender(server.Port, allowLoopback: false).SendAsync(Work(NotificationChannelKind.Webhook, $"https://localhost:{server.Port}/hook"), CancellationToken.None);

        Assert.Equal((SendStatus.Failed, NotificationNetworkException.Denied), (outcome.Status, outcome.Code));
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task A_proxy_from_the_environment_cannot_be_used_to_bypass_the_guard()
    {
        // The client ignores HTTPS_PROXY: if it did not, this request would be sent to the "proxy" instead of being refused.
        await using var proxy = new TestHttpsServer();
        var previous = Environment.GetEnvironmentVariable("HTTPS_PROXY");
        Environment.SetEnvironmentVariable("HTTPS_PROXY", proxy.Url("/").TrimEnd('/'));
        try
        {
            var outcome = await Sender(proxy.Port, allowLoopback: false).SendAsync(Work(NotificationChannelKind.Webhook, "https://10.255.255.1/hook"), CancellationToken.None);

            Assert.Equal((SendStatus.Failed, NotificationNetworkException.Denied), (outcome.Status, outcome.Code));
            Assert.Empty(proxy.Requests);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HTTPS_PROXY", previous);
        }
    }

    [Fact]
    public async Task An_endpoint_that_is_down_is_a_retryable_failure_not_an_exception()
    {
        var server = new TestHttpsServer();
        var url = server.Url();
        var port = server.Port;
        await server.DisposeAsync();

        var outcome = await Sender(port).SendAsync(Work(NotificationChannelKind.Webhook, url), CancellationToken.None);

        Assert.Equal(SendStatus.Retry, outcome.Status);
    }

    [Fact]
    public async Task A_port_other_than_443_that_the_operator_did_not_allow_is_refused_without_connecting()
    {
        await using var server = new TestHttpsServer();

        var outcome = await Sender(port: 443).SendAsync(Work(NotificationChannelKind.Webhook, server.Url()), CancellationToken.None);

        Assert.Equal((SendStatus.Failed, NotificationNetworkException.Denied), (outcome.Status, outcome.Code));
        Assert.Empty(server.Requests);
    }
}

public sealed class SmtpDeliverySmokeTests
{
    private static readonly NotificationOptions Loopback = new() { AllowedPrivateNetworks = ["127.0.0.0/8"], PublicBaseUrl = "https://backup.example.com" };

    private static (EmailNotificationSender Sender, InMemorySecrets Secrets) Create(TestSmtpServer server, SmtpSecurity security, string? username = null, string? password = null, NotificationOptions? options = null)
    {
        var secrets = new InMemorySecrets();
        var store = new FakeNotificationStore();
        var settings = new NotificationTransportSettings(NotificationTransports.Email, Start);
        var configuration = new Dictionary<string, string>
        {
            ["host"] = "127.0.0.1", ["port"] = server.Port.ToString(), ["security"] = security.ToString().ToLowerInvariant(), ["fromAddress"] = "alerts@example.com", ["fromName"] = "Alerts"
        };
        if (username is not null) configuration["username"] = username;
        settings.Apply(true, configuration, Start);
        if (password is not null)
        {
            settings.AttachSecret("nt-email-1", Start);
            secrets.Set("nt-email-1", password);
        }
        store.Transports[NotificationTransports.Email] = settings;
        var effective = options ?? Loopback;
        return (new EmailNotificationSender(store, secrets, new NotificationNetworkGuard(effective), new FakeRecipientTokens(), effective, client => client.ServerCertificateValidationCallback = (_, _, _, _) => true), secrets);
    }

    private static DeliveryWork Mail(string to = "ops@example.com") => Work(NotificationChannelKind.Email, to, type: NotificationEventTypes.BackupFailed, subjectName: "nightly",
        facts: new Dictionary<string, string> { ["code"] = "disk_full" });

    [Fact]
    public async Task A_message_reaches_the_server_with_the_recipient_subject_and_only_the_event_text()
    {
        await using var server = new TestSmtpServer();
        var (sender, _) = Create(server, SmtpSecurity.None);
        var work = Mail();

        var outcome = await sender.SendAsync(work, CancellationToken.None);

        Assert.Equal(SendStatus.Sent, outcome.Status);
        var message = Assert.Single(server.Messages);
        Assert.Contains("To: ops@example.com", message);
        Assert.Contains("Subject: [Backup Platform] Backup failed: nightly", message);
        Assert.Contains($"Message-Id: <{work.DeliveryId:N}@example.com>", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("disk_full", message);
        Assert.Contains("Auto-Submitted: auto-generated", message);
    }

    private static MimeMessage Parse(string raw) => MimeMessage.Load(new MemoryStream(Encoding.UTF8.GetBytes(raw)));

    private static RecipientClaim ClaimOf(string url) => new FakeRecipientTokens().Read(Uri.UnescapeDataString(url[(url.IndexOf("token=", StringComparison.Ordinal) + 6)..].TrimEnd('>')))!;

    [Fact]
    public async Task Every_message_carries_an_unsubscribe_link_bound_to_its_channel_and_address_and_a_one_click_header()
    {
        await using var server = new TestSmtpServer();
        var (sender, _) = Create(server, SmtpSecurity.None);
        var work = Mail();

        await sender.SendAsync(work, CancellationToken.None);

        var message = Parse(Assert.Single(server.Messages));
        var header = message.Headers["List-Unsubscribe"]!;
        Assert.StartsWith("<https://backup.example.com/api/notifications/recipient?token=", header);
        Assert.Equal("List-Unsubscribe=One-Click", message.Headers["List-Unsubscribe-Post"]);
        Assert.Contains(header.Trim('<', '>'), message.TextBody);
        Assert.Equal(new RecipientClaim(work.TenantId, work.ChannelId, RecipientAction.Unsubscribe, RecipientLinks.Fingerprint("ops@example.com")), ClaimOf(header));
    }

    [Fact]
    public async Task A_confirmation_request_is_fixed_text_with_a_confirm_link_and_never_the_tenants_wording()
    {
        await using var server = new TestSmtpServer();
        var (sender, _) = Create(server, SmtpSecurity.None);
        var work = Work(NotificationChannelKind.Email, "ops@example.com", type: NotificationEventTypes.RecipientVerification, phase: NotificationPhase.Test, subjectName: "Your bank: verify your card here");

        var outcome = await sender.SendAsync(work, CancellationToken.None);

        Assert.Equal(SendStatus.Sent, outcome.Status);
        var message = Parse(Assert.Single(server.Messages));
        Assert.Equal("[Backup Platform] Confirm your email address", message.Subject);
        Assert.DoesNotContain("bank", message.TextBody);
        var confirm = message.TextBody!.Split('\n').Select(line => line.Trim()).Single(line => line.Contains("token=") && ClaimOf(line).Action == RecipientAction.Confirm);
        Assert.Equal(new RecipientClaim(work.TenantId, work.ChannelId, RecipientAction.Confirm, RecipientLinks.Fingerprint("ops@example.com")), ClaimOf(confirm));
    }

    [Fact]
    public async Task Without_a_public_url_there_is_no_unsubscribe_link_and_so_no_email_is_sent()
    {
        await using var server = new TestSmtpServer();
        var (sender, _) = Create(server, SmtpSecurity.None, options: new NotificationOptions { AllowedPrivateNetworks = Loopback.AllowedPrivateNetworks });

        var outcome = await sender.SendAsync(Mail(), CancellationToken.None);

        Assert.Equal((SendStatus.Failed, "public_url_not_configured"), (outcome.Status, outcome.Code));
        Assert.Equal(0, server.Connections);
    }

    [Theory]
    [InlineData(TestSmtpTls.Implicit, SmtpSecurity.Implicit)]
    [InlineData(TestSmtpTls.StartTls, SmtpSecurity.StartTls)]
    public async Task An_encrypted_authenticated_session_delivers_and_presents_the_stored_password(TestSmtpTls serverTls, SmtpSecurity security)
    {
        await using var server = new TestSmtpServer(serverTls, ("bob", "p4ss"));
        var (sender, _) = Create(server, security, "bob", "p4ss");

        var outcome = await sender.SendAsync(Mail(), CancellationToken.None);

        Assert.Equal(SendStatus.Sent, outcome.Status);
        Assert.Equal(["bob"], server.AuthAttempts);
        Assert.Single(server.Messages);
    }

    [Fact]
    public async Task Wrong_credentials_are_a_permanent_failure_that_never_names_the_password()
    {
        await using var server = new TestSmtpServer(TestSmtpTls.Implicit, ("bob", "p4ss"));
        var (sender, _) = Create(server, SmtpSecurity.Implicit, "bob", "wrong-password");

        var outcome = await sender.SendAsync(Mail(), CancellationToken.None);

        Assert.Equal((SendStatus.Failed, "smtp_auth_failed"), (outcome.Status, outcome.Code));
        Assert.DoesNotContain("wrong-password", outcome.ToString());
        Assert.Empty(server.Messages);
    }

    [Fact]
    public async Task Required_encryption_is_never_silently_dropped()
    {
        // The server offers no STARTTLS: a client that falls back to plaintext would deliver (and send credentials) in the clear.
        await using var server = new TestSmtpServer(TestSmtpTls.None);
        var (sender, _) = Create(server, SmtpSecurity.StartTls);

        var outcome = await sender.SendAsync(Mail(), CancellationToken.None);

        Assert.Equal((SendStatus.Retry, "smtp_tls_unavailable"), (outcome.Status, outcome.Code));
        Assert.Empty(server.Messages);
    }

    [Theory]
    [InlineData("451 try again later", SendStatus.Retry, "smtp_temporary")]
    [InlineData("452 mailbox full", SendStatus.Retry, "smtp_temporary")]
    [InlineData("550 no such user", SendStatus.Failed, "smtp_rejected")]
    [InlineData("554 rejected", SendStatus.Failed, "smtp_rejected")]
    public async Task The_servers_verdict_decides_between_retry_and_permanent_failure(string reply, SendStatus status, string code)
    {
        await using var server = new TestSmtpServer { DataReply = reply };
        var (sender, _) = Create(server, SmtpSecurity.None);

        var outcome = await sender.SendAsync(Mail(), CancellationToken.None);

        Assert.Equal((status, code), (outcome.Status, outcome.Code));
    }

    [Fact]
    public async Task A_relay_that_is_down_is_a_retryable_failure()
    {
        var server = new TestSmtpServer();
        var (sender, _) = Create(server, SmtpSecurity.None);
        await server.DisposeAsync();

        var outcome = await sender.SendAsync(Mail(), CancellationToken.None);

        Assert.Equal((SendStatus.Retry, NotificationNetworkException.ConnectFailed), (outcome.Status, outcome.Code));
    }

    [Fact]
    public async Task A_relay_on_a_private_address_is_refused_unless_the_operator_allowed_that_range()
    {
        await using var server = new TestSmtpServer();
        var (sender, _) = Create(server, SmtpSecurity.None, options: new NotificationOptions { PublicBaseUrl = Loopback.PublicBaseUrl });

        var outcome = await sender.SendAsync(Mail(), CancellationToken.None);

        Assert.Equal((SendStatus.Failed, NotificationNetworkException.Denied), (outcome.Status, outcome.Code));
        Assert.Equal(0, server.Connections);
    }

    [Fact]
    public async Task A_disabled_or_incomplete_transport_fails_permanently_without_connecting()
    {
        await using var server = new TestSmtpServer();
        var (_, _) = Create(server, SmtpSecurity.None);
        var unconfigured = new EmailNotificationSender(new FakeNotificationStore(), new InMemorySecrets(), new NotificationNetworkGuard(Loopback), new FakeRecipientTokens(), Loopback);

        var outcome = await unconfigured.SendAsync(Mail(), CancellationToken.None);

        Assert.Equal((SendStatus.Failed, "transport_unavailable"), (outcome.Status, outcome.Code));
        Assert.Equal(0, server.Connections);
    }
}
