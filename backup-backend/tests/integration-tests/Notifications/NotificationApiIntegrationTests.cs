using System.Net;
using System.Text.Json;
using BackupPlatform.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.IntegrationTests.Notifications;

/// <summary>
/// The management API through real HTTP: who may call it, what it returns (never a secret or a full target), and what it refuses. Each test owns its
/// database: the SMTP relay is platform-wide state, so tests that configure it must not leak into the next.
/// </summary>
public sealed class NotificationApiIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlFixture fixture = new();

    public Task InitializeAsync() => fixture.InitializeAsync();

    public Task DisposeAsync() => fixture.DisposeAsync();
    private const string WebhookUrl = "https://hooks.example.com/services/T000/B000/PATHTOKEN";

    private static string Notifications(TenantSetup setup) => $"/api/tenants/{setup.Tenant.Id.Value}/notifications";

    private async Task<(AgentWorld World, TenantSetup Setup)> CreateAsync(TenantRole role = TenantRole.Admin)
    {
        var world = await AgentWorld.CreateAsync(fixture);
        return (world, await world.CreateTenantAsync(role));
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"Expected {expected} but got {(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    [Theory]
    [InlineData(TenantRole.Viewer, HttpStatusCode.Forbidden)]
    [InlineData(TenantRole.Operator, HttpStatusCode.Forbidden)]
    [InlineData(TenantRole.Admin, HttpStatusCode.OK)]
    [InlineData(TenantRole.Owner, HttpStatusCode.OK)]
    public async Task Only_tenant_administrators_can_read_or_change_notification_settings(TenantRole role, HttpStatusCode expected)
    {
        var (world, setup) = await CreateAsync(role);
        await using var _ = world;
        var admin = setup.Admin;

        using var policy = await admin.GetAsync(Notifications(setup) + "/policy");
        using var channels = await admin.GetAsync(Notifications(setup) + "/channels");
        using var deliveries = await admin.GetAsync(Notifications(setup) + "/deliveries");
        using var create = await admin.SendAsync(HttpMethod.Post, Notifications(setup) + "/channels", new { kind = "email", name = "Ops", target = "ops@example.com" });
        using var put = await admin.SendAsync(HttpMethod.Put, Notifications(setup) + "/policy", new { enabled = false, eventTypes = new[] { "backup_failed" }, minimumSeverity = "warning", channelKinds = new[] { "email" }, recoveryNotifications = true, staleAfterHours = 48, agentOfflineAfterMinutes = 15, reminderIntervalHours = 24, maxNotificationsPerHour = 30 });

        Assert.Equal(expected, policy.StatusCode);
        Assert.Equal(expected, channels.StatusCode);
        Assert.Equal(expected, deliveries.StatusCode);
        Assert.Equal(expected == HttpStatusCode.OK ? HttpStatusCode.Created : expected, create.StatusCode);
        Assert.Equal(expected, put.StatusCode);
    }

    [Fact]
    public async Task Platform_transport_settings_are_for_platform_administrators_only_and_anonymous_callers_get_nothing()
    {
        var (world, setup) = await CreateAsync();
        await using var _ = world;

        using var tenantAdmin = await setup.Admin.GetAsync("/api/platform/notification-transports");
        using var anonymous = await world.Anonymous.GetAsync("/api/platform/notification-transports");
        using var anonymousTenant = await world.Anonymous.GetAsync(Notifications(setup) + "/policy");

        Assert.Equal(HttpStatusCode.Forbidden, tenantAdmin.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousTenant.StatusCode);
    }

    [Fact]
    public async Task Another_tenants_administrator_cannot_reach_or_find_the_channels_of_this_tenant()
    {
        var (world, a) = await CreateAsync();
        await using var _ = world;
        var b = await world.CreateTenantAsync();
        var channel = await ReadAsync(await a.Admin.SendAsync(HttpMethod.Post, Notifications(a) + "/channels", new { kind = "email", name = "Ops", target = "ops@example.com" }), HttpStatusCode.Created);
        var id = channel.GetProperty("id").GetGuid();

        // By guessing the tenant in the URL: not a member, no access.
        using var foreign = await b.Admin.GetAsync(Notifications(a) + "/channels");
        // By using their own tenant with the other tenant's channel id: simply not found.
        using var guessed = await b.Admin.GetAsync(Notifications(b) + $"/channels/{id}");
        using var guessedTest = await b.Admin.SendAsync(HttpMethod.Post, Notifications(b) + $"/channels/{id}/test");
        using var guessedDelete = await b.Admin.SendAsync(HttpMethod.Delete, Notifications(b) + $"/channels/{id}");
        var own = await ReadAsync(await b.Admin.GetAsync(Notifications(b) + "/channels"), HttpStatusCode.OK);

        Assert.Contains(foreign.StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });
        Assert.Equal(HttpStatusCode.NotFound, guessed.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, guessedTest.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, guessedDelete.StatusCode);
        Assert.Empty(own.GetProperty("items").EnumerateArray());
        Assert.Single((await ReadAsync(await a.Admin.GetAsync(Notifications(a) + "/channels"), HttpStatusCode.OK)).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task No_response_and_no_audit_event_ever_contains_a_secret_or_the_full_webhook_url()
    {
        var (world, setup) = await CreateAsync();
        await using var _ = world;
        var admin = setup.Admin;
        var responses = new List<string>();
        async Task<JsonElement> Record(Task<HttpResponseMessage> request, HttpStatusCode expected)
        {
            using var response = await request;
            var text = await response.Content.ReadAsStringAsync();
            responses.Add(text);
            Assert.True(response.StatusCode == expected, $"Expected {expected} but got {(int)response.StatusCode}: {text}");
            return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
        }

        var created = await Record(admin.SendAsync(HttpMethod.Post, Notifications(setup) + "/channels",
            new { kind = "webhook", name = "Hook", target = WebhookUrl, authentication = "bearer", secret = "TOP-SECRET-TOKEN-1" }), HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();
        await Record(admin.SendAsync(HttpMethod.Put, Notifications(setup) + $"/channels/{id}/secret", new { value = "TOP-SECRET-TOKEN-2" }), HttpStatusCode.OK);
        await Record(admin.GetAsync(Notifications(setup) + "/channels"), HttpStatusCode.OK);
        await Record(admin.GetAsync(Notifications(setup) + $"/channels/{id}"), HttpStatusCode.OK);
        await Record(admin.SendAsync(HttpMethod.Put, Notifications(setup) + $"/channels/{id}", new { authentication = "hmac" }), HttpStatusCode.OK);
        await Record(admin.SendAsync(HttpMethod.Delete, Notifications(setup) + $"/channels/{id}/secret"), HttpStatusCode.OK);
        await Record(admin.SendAsync(HttpMethod.Post, Notifications(setup) + $"/channels/{id}/test"), HttpStatusCode.Accepted);
        await Record(admin.GetAsync(Notifications(setup) + "/deliveries"), HttpStatusCode.OK);

        Assert.Equal("https://hooks.example.com/…", created.GetProperty("targetDisplay").GetString());
        Assert.True(created.GetProperty("hasSecret").GetBoolean());
        Assert.False(created.TryGetProperty("target", out var rawTarget) || created.TryGetProperty("secret", out var rawSecret), "a view never carries the target or a secret");
        await using var context = fixture.CreatePlatformContext();
        var audit = string.Join("\n", await context.AuditEvents.AsNoTracking().Where(row => row.TenantId == setup.Tenant.Id).Select(row => row.DetailsJson + row.TargetId).ToListAsync());
        Assert.Contains("notification_channel", string.Join(",", await context.AuditEvents.AsNoTracking().Where(row => row.TenantId == setup.Tenant.Id).Select(row => row.Action).ToListAsync()));
        foreach (var leaked in new[] { "TOP-SECRET-TOKEN-1", "TOP-SECRET-TOKEN-2", "PATHTOKEN", "T000/B000" })
        {
            Assert.DoesNotContain(leaked, string.Join("\n", responses));
            Assert.DoesNotContain(leaked, audit);
        }
    }

    [Theory]
    [InlineData("webhook", "http://hooks.example.com/n", "target")]
    [InlineData("webhook", "https://user:pw@hooks.example.com/n", "target")]
    [InlineData("webhook", "https://hooks.example.com/n?token=1", "target")]
    [InlineData("email", "not-an-address", "target")]
    [InlineData("email", "Ops <ops@example.com>", "target")]
    [InlineData("pager", "x", "kind")]
    public async Task A_channel_with_an_invalid_target_or_kind_is_rejected_and_names_the_field_not_the_value(string kind, string target, string field)
    {
        var (world, setup) = await CreateAsync();
        await using var _ = world;

        using var response = await setup.Admin.SendAsync(HttpMethod.Post, Notifications(setup) + "/channels", new { kind, name = "Bad", target });
        var body = await ReadAsync(response, HttpStatusCode.BadRequest);

        Assert.Equal("invalid_request", body.GetProperty("error").GetString());
        Assert.Equal(field, body.GetProperty("field").GetString());
        Assert.DoesNotContain(target, body.GetRawText());
    }

    [Fact]
    public async Task A_secret_is_refused_for_a_channel_that_has_no_authentication()
    {
        var (world, setup) = await CreateAsync();
        await using var _ = world;

        using var withSecret = await setup.Admin.SendAsync(HttpMethod.Post, Notifications(setup) + "/channels", new { kind = "webhook", name = "Open", target = WebhookUrl, authentication = "none", secret = "x" });
        using var email = await setup.Admin.SendAsync(HttpMethod.Post, Notifications(setup) + "/channels", new { kind = "email", name = "Mail", target = "a@example.com", secret = "x" });

        Assert.Equal(HttpStatusCode.BadRequest, withSecret.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, email.StatusCode);
    }

    [Fact]
    public async Task The_policy_defaults_to_off_round_trips_and_rejects_values_outside_the_contract()
    {
        var (world, setup) = await CreateAsync();
        await using var _ = world;
        var path = Notifications(setup) + "/policy";

        var initial = await ReadAsync(await setup.Admin.GetAsync(path), HttpStatusCode.OK);
        Assert.False(initial.GetProperty("enabled").GetBoolean());
        Assert.Equal(5, initial.GetProperty("eventTypes").GetArrayLength());
        Assert.Equal((48, 15, 24, 30), (initial.GetProperty("staleAfterHours").GetInt32(), initial.GetProperty("agentOfflineAfterMinutes").GetInt32(),
            initial.GetProperty("reminderIntervalHours").GetInt32(), initial.GetProperty("maxNotificationsPerHour").GetInt32()));
        Assert.False(initial.GetProperty("transports").GetProperty("email").GetProperty("available").GetBoolean());
        Assert.Equal(JsonValueKind.Null, initial.GetProperty("updatedAt").ValueKind);

        var body = new { enabled = true, eventTypes = new[] { "agent_offline", "quota_critical" }, minimumSeverity = "critical", channelKinds = new[] { "email" }, recoveryNotifications = false, staleAfterHours = 24, agentOfflineAfterMinutes = 30, reminderIntervalHours = 0, maxNotificationsPerHour = 10 };
        var saved = await ReadAsync(await setup.Admin.SendAsync(HttpMethod.Put, path, body), HttpStatusCode.OK);
        Assert.True(saved.GetProperty("enabled").GetBoolean());
        Assert.Equal("critical", saved.GetProperty("minimumSeverity").GetString());
        Assert.Equal(["agent_offline", "quota_critical"], saved.GetProperty("eventTypes").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(JsonValueKind.String, saved.GetProperty("updatedAt").ValueKind);

        foreach (var (field, bad) in new (string, object)[]
        {
            ("staleAfterHours", new { body.enabled, body.eventTypes, body.minimumSeverity, body.channelKinds, body.recoveryNotifications, staleAfterHours = 0, body.agentOfflineAfterMinutes, body.reminderIntervalHours, body.maxNotificationsPerHour }),
            ("eventTypes", new { body.enabled, eventTypes = new[] { "nope" }, body.minimumSeverity, body.channelKinds, body.recoveryNotifications, body.staleAfterHours, body.agentOfflineAfterMinutes, body.reminderIntervalHours, body.maxNotificationsPerHour }),
            ("minimumSeverity", new { body.enabled, body.eventTypes, minimumSeverity = "urgent", body.channelKinds, body.recoveryNotifications, body.staleAfterHours, body.agentOfflineAfterMinutes, body.reminderIntervalHours, body.maxNotificationsPerHour })
        })
        {
            var error = await ReadAsync(await setup.Admin.SendAsync(HttpMethod.Put, path, bad), HttpStatusCode.BadRequest);
            Assert.Equal(field, error.GetProperty("field").GetString());
        }
        var missing = await ReadAsync(await setup.Admin.SendAsync(HttpMethod.Put, path, new { enabled = true }), HttpStatusCode.BadRequest);
        Assert.Equal("required", missing.GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_test_notification_is_queued_through_the_normal_pipeline_is_visible_in_the_history_and_is_rate_limited()
    {
        var (world, setup) = await CreateAsync();
        await using var _ = world;
        var hook = await ReadAsync(await setup.Admin.SendAsync(HttpMethod.Post, Notifications(setup) + "/channels", new { kind = "webhook", name = "Hook", target = WebhookUrl }), HttpStatusCode.Created);
        var mail = await ReadAsync(await setup.Admin.SendAsync(HttpMethod.Post, Notifications(setup) + "/channels", new { kind = "email", name = "Mail", target = "ops@example.com" }), HttpStatusCode.Created);

        var noTransport = await ReadAsync(await setup.Admin.SendAsync(HttpMethod.Post, Notifications(setup) + $"/channels/{mail.GetProperty("id").GetGuid()}/test"), HttpStatusCode.Conflict);
        Assert.Equal("transport_not_configured", noTransport.GetProperty("error").GetString());

        var hookId = hook.GetProperty("id").GetGuid();
        for (var i = 0; i < 5; i++)
            await ReadAsync(await setup.Admin.SendAsync(HttpMethod.Post, Notifications(setup) + $"/channels/{hookId}/test"), HttpStatusCode.Accepted);
        var limited = await ReadAsync(await setup.Admin.SendAsync(HttpMethod.Post, Notifications(setup) + $"/channels/{hookId}/test"), HttpStatusCode.Conflict);
        Assert.Equal("notification_test_rate_limited", limited.GetProperty("error").GetString());

        var history = await ReadAsync(await setup.Admin.GetAsync(Notifications(setup) + "/deliveries?pageSize=3&status=pending&kind=webhook&eventType=test"), HttpStatusCode.OK);
        Assert.Equal(5, history.GetProperty("totalCount").GetInt32());
        Assert.Equal(3, history.GetProperty("items").GetArrayLength());
        var first = history.GetProperty("items")[0];
        Assert.Equal(("test", "test", "pending", "webhook", "Hook", "https://hooks.example.com/…"),
            (first.GetProperty("eventType").GetString(), first.GetProperty("phase").GetString(), first.GetProperty("status").GetString(), first.GetProperty("channelKind").GetString(),
                first.GetProperty("channelName").GetString(), first.GetProperty("targetDisplay").GetString()));
        using var badFilter = await setup.Admin.GetAsync(Notifications(setup) + "/deliveries?status=exploded");
        Assert.Equal(HttpStatusCode.BadRequest, badFilter.StatusCode);
        using var badPage = await setup.Admin.GetAsync(Notifications(setup) + "/deliveries?pageSize=101");
        Assert.Equal(HttpStatusCode.BadRequest, badPage.StatusCode);
    }

    [Fact]
    public async Task Disabling_and_deleting_a_channel_is_reflected_and_deleted_history_keeps_its_snapshot()
    {
        var (world, setup) = await CreateAsync();
        await using var _ = world;
        var created = await ReadAsync(await setup.Admin.SendAsync(HttpMethod.Post, Notifications(setup) + "/channels", new { kind = "webhook", name = "Hook", target = WebhookUrl }), HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();
        await ReadAsync(await setup.Admin.SendAsync(HttpMethod.Post, Notifications(setup) + $"/channels/{id}/test"), HttpStatusCode.Accepted);

        var disabled = await ReadAsync(await setup.Admin.SendAsync(HttpMethod.Post, Notifications(setup) + $"/channels/{id}/disable"), HttpStatusCode.OK);
        Assert.False(disabled.GetProperty("enabled").GetBoolean());
        using var testDisabled = await setup.Admin.SendAsync(HttpMethod.Post, Notifications(setup) + $"/channels/{id}/test");
        Assert.Equal(HttpStatusCode.BadRequest, testDisabled.StatusCode);
        using var duplicate = await setup.Admin.SendAsync(HttpMethod.Post, Notifications(setup) + "/channels", new { kind = "webhook", name = "hook", target = "https://other.example.com/x" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        using var deleted = await setup.Admin.SendAsync(HttpMethod.Delete, Notifications(setup) + $"/channels/{id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using var gone = await setup.Admin.GetAsync(Notifications(setup) + $"/channels/{id}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        var history = await ReadAsync(await setup.Admin.GetAsync(Notifications(setup) + "/deliveries"), HttpStatusCode.OK);
        Assert.Equal("Hook", Assert.Single(history.GetProperty("items").EnumerateArray()).GetProperty("channelName").GetString());
    }

    [Fact]
    public async Task The_operator_configures_smtp_and_no_response_or_audit_event_returns_the_password()
    {
        var (world, setup) = await CreateAsync();
        await using var _ = world;
        var platform = await world.SignInAsync(await world.SeedUserAsync(platformAdmin: true));
        const string root = "/api/platform/notification-transports";

        var initial = await ReadAsync(await platform.GetAsync(root), HttpStatusCode.OK);
        Assert.False(initial.GetProperty("email").GetProperty("configured").GetBoolean());

        using var insecure = await platform.SendAsync(HttpMethod.Put, root + "/email", new { enabled = true, host = "smtp.example.com", port = 25, security = "none", username = "bob", fromAddress = "alerts@example.com" });
        Assert.Equal(HttpStatusCode.BadRequest, insecure.StatusCode);
        var saved = await ReadAsync(await platform.SendAsync(HttpMethod.Put, root + "/email", new { enabled = false, host = "smtp.example.com", port = 587, security = "starttls", username = "bob", fromAddress = "alerts@example.com", fromName = "Alerts" }), HttpStatusCode.OK);
        Assert.Equal(("smtp.example.com", 587, "starttls", "bob"), (saved.GetProperty("host").GetString(), saved.GetProperty("port").GetInt32(), saved.GetProperty("security").GetString(), saved.GetProperty("username").GetString()));
        using var enableWithoutPassword = await platform.SendAsync(HttpMethod.Put, root + "/email", new { enabled = true, host = "smtp.example.com", port = 587, security = "starttls", username = "bob", fromAddress = "alerts@example.com" });
        Assert.Equal(HttpStatusCode.BadRequest, enableWithoutPassword.StatusCode);

        var withSecret = await ReadAsync(await platform.SendAsync(HttpMethod.Put, root + "/email/secret", new { value = "SMTP-PASSWORD-123" }), HttpStatusCode.OK);
        Assert.True(withSecret.GetProperty("hasSecret").GetBoolean());
        var enabled = await ReadAsync(await platform.SendAsync(HttpMethod.Put, root + "/email", new { enabled = true, host = "smtp.example.com", port = 587, security = "starttls", username = "bob", fromAddress = "alerts@example.com" }), HttpStatusCode.OK);
        Assert.True(enabled.GetProperty("enabled").GetBoolean());
        var policy = await ReadAsync(await setup.Admin.GetAsync(Notifications(setup) + "/policy"), HttpStatusCode.OK);
        Assert.True(policy.GetProperty("transports").GetProperty("email").GetProperty("available").GetBoolean());

        var all = await platform.GetAsync(root);
        var text = await all.Content.ReadAsStringAsync();
        Assert.DoesNotContain("SMTP-PASSWORD-123", text);
        Assert.DoesNotContain("SMTP-PASSWORD-123", withSecret.GetRawText());
        await using var context = fixture.CreatePlatformContext();
        Assert.DoesNotContain("SMTP-PASSWORD-123", string.Join("\n", await context.AuditEvents.AsNoTracking().Select(row => row.DetailsJson).ToListAsync()));

        using var unknown = await platform.SendAsync(HttpMethod.Put, root + "/pager/secret", new { value = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }
}
