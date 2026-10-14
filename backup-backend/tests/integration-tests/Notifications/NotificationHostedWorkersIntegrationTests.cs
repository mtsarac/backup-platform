using System.Net;
using System.Text;
using System.Text.Json;
using BackupPlatform.UnitTests.Notifications.Support;
using MimeKit;

namespace BackupPlatform.IntegrationTests.Notifications;

/// <summary>The production composition: the API host starts the detection and delivery loops by itself, on real timers, and a failed backup becomes a real email.</summary>
public sealed class NotificationHostedWorkersIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlFixture _database = new();

    public Task InitializeAsync() => _database.InitializeAsync();

    public Task DisposeAsync() => _database.DisposeAsync();

    [Fact]
    public async Task A_failed_backup_reaches_the_mailbox_through_the_hosts_own_background_workers()
    {
        await using var smtp = new TestSmtpServer();
        var configuration = new Dictionary<string, string?>
        {
            ["Notifications:Enabled"] = "true",
            ["Notifications:SweepInterval"] = "00:00:05",
            ["Notifications:DeliveryPollInterval"] = "00:00:01",
            ["Notifications:AllowedPrivateNetworks:0"] = "127.0.0.0/8"
        };
        await using var scenario = await BackupApiScenario.CreateAsync(_database, configuration, new MutableClock(live: true));
        var platform = await scenario.World.SignInAsync(await scenario.World.SeedUserAsync(platformAdmin: true));
        var notifications = $"{scenario.Root}/notifications";

        using var transport = await platform.SendAsync(HttpMethod.Put, "/api/platform/notification-transports/email",
            new { enabled = true, host = "127.0.0.1", port = smtp.Port, security = "none", fromAddress = "alerts@example.com", fromName = "Alerts" });
        Assert.Equal(HttpStatusCode.OK, transport.StatusCode);
        using var channel = await scenario.Admin.SendAsync(HttpMethod.Post, notifications + "/channels", new { kind = "email", name = "Ops", target = "ops@example.com" });
        Assert.Equal(HttpStatusCode.Created, channel.StatusCode);
        var channelPath = $"{notifications}/channels/{JsonDocument.Parse(await channel.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid()}";
        using var policy = await scenario.Admin.SendAsync(HttpMethod.Put, notifications + "/policy", new
        {
            enabled = true, eventTypes = new[] { "backup_failed" }, minimumSeverity = "warning", channelKinds = new[] { "email" }, recoveryNotifications = true,
            staleAfterHours = 48, agentOfflineAfterMinutes = 15, reminderIntervalHours = 24, maxNotificationsPerHour = 30
        });
        Assert.Equal(HttpStatusCode.OK, policy.StatusCode);

        // A new address first gets a confirmation request and nothing else; opening its link only shows what it would do.
        await WaitForMessagesAsync(smtp, 1);
        var confirmation = ParseMessage(smtp.Messages[0]);
        Assert.Equal("[Backup Platform] Confirm your email address", confirmation.Subject);
        var confirmLink = LinkPath(confirmation);
        using (var preview = await scenario.World.Anonymous.GetAsync(confirmLink))
        {
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
            Assert.Contains("<button", await preview.Content.ReadAsStringAsync());
        }
        Assert.False(await RecipientVerifiedAsync(scenario, channelPath));

        // Confirming is a POST without any session; a replay of the same link is harmless.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var confirmed = await scenario.World.Anonymous.PostAsync(confirmLink, null);
            Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        }
        Assert.True(await RecipientVerifiedAsync(scenario, channelPath));

        var jobId = await scenario.CreateJobAsync();
        await scenario.StartRunAsync(jobId);
        await scenario.FailNextAsync("InvalidConfiguration");
        await WaitForMessagesAsync(smtp, 2);

        Assert.Equal(2, smtp.Messages.Count);
        var alert = ParseMessage(smtp.Messages[1]);
        Assert.Equal("[Backup Platform] Backup failed: Nightly full backup", alert.Subject);
        Assert.Contains("InvalidConfiguration", alert.TextBody);
        Assert.DoesNotContain("sql-server", smtp.Messages[1]); // the job's host is backup metadata a notification has no business carrying

        using var history = await scenario.Admin.GetAsync(notifications + "/deliveries?eventType=backup_failed");
        var item = Assert.Single(JsonDocument.Parse(await history.Content.ReadAsStringAsync()).RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(("sent", "email", "backup_failed", 1), (item.GetProperty("status").GetString(), item.GetProperty("channelKind").GetString(),
            item.GetProperty("eventType").GetString(), item.GetProperty("attempts").GetInt32()));

        // The recipient can walk away without any login: the link of the alert itself (or the one-click header) unsubscribes and stops the channel.
        var unsubscribe = alert.Headers["List-Unsubscribe"]!.Trim('<', '>');
        using var oneClick = await scenario.World.Anonymous.PostAsync(unsubscribe[Root.Length..], new StringContent("List-Unsubscribe=One-Click"));
        Assert.Equal(HttpStatusCode.OK, oneClick.StatusCode);
        using var after = await scenario.Admin.GetAsync(channelPath);
        var state = JsonDocument.Parse(await after.Content.ReadAsStringAsync()).RootElement;
        Assert.False(state.GetProperty("enabled").GetBoolean());
        Assert.False(state.GetProperty("recipientVerified").GetBoolean());

        using var forged = await scenario.World.Anonymous.PostAsync("/api/notifications/recipient?token=not-a-token", null);
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
    }

    private const string Root = "https://backup.example.com";

    private static MimeMessage ParseMessage(string raw) => MimeMessage.Load(new MemoryStream(Encoding.UTF8.GetBytes(raw)));

    /// <summary>The first recipient link of a mail (the confirmation link comes before the unsubscribe link), as a path the test client can call.</summary>
    private static string LinkPath(MimeMessage message) =>
        message.TextBody!.Split('\n').Select(line => line.Trim()).First(line => line.StartsWith(Root + "/api/notifications/recipient?token=", StringComparison.Ordinal))[Root.Length..];

    private static async Task WaitForMessagesAsync(TestSmtpServer smtp, int count)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
        while (smtp.Messages.Count < count && DateTimeOffset.UtcNow < deadline) await Task.Delay(250);
    }

    private static async Task<bool> RecipientVerifiedAsync(BackupApiScenario scenario, string channelPath)
    {
        using var response = await scenario.Admin.GetAsync(channelPath);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("recipientVerified").GetBoolean();
    }
}
