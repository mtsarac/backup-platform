using System.Net;
using System.Text.Json;
using BackupPlatform.UnitTests.Notifications.Support;

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
        using var policy = await scenario.Admin.SendAsync(HttpMethod.Put, notifications + "/policy", new
        {
            enabled = true, eventTypes = new[] { "backup_failed" }, minimumSeverity = "warning", channelKinds = new[] { "email" }, recoveryNotifications = true,
            staleAfterHours = 48, agentOfflineAfterMinutes = 15, reminderIntervalHours = 24, maxNotificationsPerHour = 30
        });
        Assert.Equal(HttpStatusCode.OK, policy.StatusCode);

        var jobId = await scenario.CreateJobAsync();
        await scenario.StartRunAsync(jobId);
        await scenario.FailNextAsync("InvalidConfiguration");

        var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
        while (smtp.Messages.Count == 0 && DateTimeOffset.UtcNow < deadline) await Task.Delay(250);

        var message = Assert.Single(smtp.Messages);
        Assert.Contains("Subject: [Backup Platform] Backup failed: Nightly full backup", message);
        Assert.Contains("InvalidConfiguration", message);
        Assert.DoesNotContain("sql-server", message); // the job's host is backup metadata a notification has no business carrying

        using var history = await scenario.Admin.GetAsync(notifications + "/deliveries");
        var item = Assert.Single(JsonDocument.Parse(await history.Content.ReadAsStringAsync()).RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(("sent", "email", "backup_failed", 1), (item.GetProperty("status").GetString(), item.GetProperty("channelKind").GetString(),
            item.GetProperty("eventType").GetString(), item.GetProperty("attempts").GetInt32()));
    }
}
