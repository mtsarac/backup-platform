using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using BackupPlatform.Domain.Notifications;
using BackupPlatform.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.IntegrationTests.Notifications;

/// <summary>The anonymous confirm/unsubscribe endpoints over real HTTP: bounded without refusing a genuine one-click unsubscribe, and the token stays out of the logs.</summary>
public sealed class NotificationRecipientApiIntegrationTests : IAsyncLifetime
{
    private const string Address = "ops@example.com";
    private const string Unsubscribe = "/api/notifications/recipient/unsubscribe";
    private const string Confirm = "/api/notifications/recipient/confirm";

    private readonly PostgreSqlFixture _database = new();

    public Task InitializeAsync() => _database.InitializeAsync();

    public Task DisposeAsync() => _database.DisposeAsync();

    private sealed class CapturedLogs : ILoggerProvider
    {
        public ConcurrentQueue<string> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Capture(categoryName, Entries);

        public void Dispose() { }

        private sealed class Capture(string category, ConcurrentQueue<string> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            {
                entries.Enqueue($"{category} scope: {state} {Flatten(state)}");
                return null;
            }

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Enqueue($"{category} {logLevel}: {formatter(state, exception)} {Flatten(state)} {exception}");

            private static string Flatten(object? state) =>
                state is IEnumerable<KeyValuePair<string, object?>> pairs ? string.Join(" ", pairs.Select(pair => $"{pair.Key}={pair.Value}")) : string.Empty;
        }
    }

    private sealed record Fixture(AgentWorld World, TenantSetup Setup, Guid ChannelId, CapturedLogs Logs)
    {
        public IRecipientTokens Tokens => World.Factory.Services.GetRequiredService<IRecipientTokens>();

        public string Token(RecipientAction action, string address = Address) =>
            Tokens.Issue(new RecipientClaim(Setup.Tenant.Id, ChannelId, action, RecipientLinks.Fingerprint(address)));

        public async Task<(bool Enabled, bool? Verified)> StateAsync()
        {
            using var response = await Setup.Admin.GetAsync($"/api/tenants/{Setup.Tenant.Id.Value}/notifications/channels/{ChannelId}");
            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            return (json.GetProperty("enabled").GetBoolean(), json.GetProperty("recipientVerified").GetBoolean());
        }

        public Task<HttpResponseMessage> OneClickAsync(string path, string token) =>
            World.Anonymous.PostAsync($"{path}?token={Uri.EscapeDataString(token)}", new FormUrlEncodedContent([new("List-Unsubscribe", "One-Click")]));

        public Task<HttpResponseMessage> FormAsync(string path, string token) =>
            World.Anonymous.PostAsync(path, new FormUrlEncodedContent([new("token", token)]));
    }

    private async Task<Fixture> CreateAsync(int perToken = 3, int invalidPerClient = 3, bool traceLogging = false)
    {
        var logs = new CapturedLogs();
        var configuration = new Dictionary<string, string?>
        {
            ["Notifications:RecipientLinkAttemptsPerToken"] = perToken.ToString(),
            ["Notifications:RecipientLinkInvalidPerClient"] = invalidPerClient.ToString()
        };
        if (traceLogging)
        {
            // Everything ASP.NET Core can say, including the hosting diagnostics that print the request line.
            configuration["Logging:LogLevel:Default"] = "Trace";
            configuration["Logging:LogLevel:Microsoft.AspNetCore"] = "Trace";
            configuration["Logging:LogLevel:Microsoft.AspNetCore.Hosting.Diagnostics"] = "Trace";
        }
        var world = await AgentWorld.CreateAsync(_database, configuration: configuration, configureServices: services => services.AddSingleton<ILoggerProvider>(logs));
        var setup = await world.CreateTenantAsync();
        using var created = await setup.Admin.SendAsync(HttpMethod.Post, $"/api/tenants/{setup.Tenant.Id.Value}/notifications/channels", new { kind = "email", name = "Ops", target = Address });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        return new Fixture(world, setup, id, logs);
    }

    [Fact]
    public async Task A_genuine_one_click_unsubscribe_still_works_after_a_flood_of_invalid_requests_exhausted_the_clients_budget()
    {
        var scene = await CreateAsync(invalidPerClient: 3);
        await using var _ = scene.World;
        await scene.World.Anonymous.PostAsync(Confirm + "?token=" + Uri.EscapeDataString(scene.Token(RecipientAction.Confirm)), null);
        Assert.Equal((true, (bool?)true), await scene.StateAsync());

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            using var forged = await scene.OneClickAsync(Unsubscribe, $"forged-{i}");
            statuses.Add(forged.StatusCode);
            if (forged.StatusCode == HttpStatusCode.TooManyRequests) Assert.True(forged.Headers.RetryAfter is not null);
        }
        Assert.Equal([HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests], statuses);

        using var oneClick = await scene.OneClickAsync(Unsubscribe, scene.Token(RecipientAction.Unsubscribe));

        Assert.Equal(HttpStatusCode.OK, oneClick.StatusCode);
        Assert.Equal((false, (bool?)false), await scene.StateAsync());
    }

    [Fact]
    public async Task A_confirmation_link_mailed_before_the_recipient_unsubscribed_does_not_verify_the_address_again_but_a_new_invitation_does()
    {
        var scene = await CreateAsync(perToken: 10);
        await using var _ = scene.World;
        var oldConfirm = scene.Token(RecipientAction.Confirm);
        using (var confirmed = await scene.FormAsync(Confirm, oldConfirm)) Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        using (var unsubscribed = await scene.OneClickAsync(Unsubscribe, scene.Token(RecipientAction.Unsubscribe))) Assert.Equal(HttpStatusCode.OK, unsubscribed.StatusCode);
        Assert.Equal((false, (bool?)false), await scene.StateAsync());

        using (var replayed = await scene.FormAsync(Confirm, oldConfirm)) Assert.Equal(HttpStatusCode.NotFound, replayed.StatusCode);
        Assert.Equal((false, (bool?)false), await scene.StateAsync());

        using (var enabled = await scene.Setup.Admin.SendAsync(HttpMethod.Post, $"/api/tenants/{scene.Setup.Tenant.Id.Value}/notifications/channels/{scene.ChannelId}/enable"))
            Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        using (var invited = await scene.FormAsync(Confirm, scene.Token(RecipientAction.Confirm))) Assert.Equal(HttpStatusCode.OK, invited.StatusCode);
        Assert.Equal((true, (bool?)true), await scene.StateAsync());
    }

    [Fact]
    public async Task A_valid_token_cannot_be_replayed_beyond_its_share_of_the_window_while_other_tokens_are_unaffected()
    {
        var scene = await CreateAsync(perToken: 3);
        await using var _ = scene.World;
        var token = scene.Token(RecipientAction.Unsubscribe);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            using var response = await scene.OneClickAsync(Unsubscribe, token);
            statuses.Add(response.StatusCode);
        }

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests], statuses);
        using var other = await scene.OneClickAsync(Unsubscribe, scene.Token(RecipientAction.Unsubscribe));
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    [Fact]
    public async Task A_token_only_works_on_the_route_of_its_own_action_and_unknown_routes_do_not_exist()
    {
        var scene = await CreateAsync();
        await using var _ = scene.World;

        using var wrongRoute = await scene.FormAsync(Unsubscribe, scene.Token(RecipientAction.Confirm));
        using var unknownPost = await scene.FormAsync("/api/notifications/recipient/other", scene.Token(RecipientAction.Confirm));
        using var unknownPage = await scene.World.Anonymous.GetAsync("/api/notifications/recipient/other");

        Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.NotFound, HttpStatusCode.NotFound), (wrongRoute.StatusCode, unknownPost.StatusCode, unknownPage.StatusCode));
        Assert.Equal((true, (bool?)false), await scene.StateAsync());
    }

    [Fact]
    public async Task The_page_is_static_carries_no_token_and_allows_exactly_its_own_script()
    {
        var scene = await CreateAsync();
        await using var _ = scene.World;
        var token = scene.Token(RecipientAction.Confirm);

        using var page = await scene.World.Anonymous.GetAsync(Confirm);
        var html = await page.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.DoesNotContain(token, html);
        Assert.Contains("location.hash", html);
        Assert.Matches(@"script-src 'sha256-[A-Za-z0-9+/=]+'", string.Join(";", page.Headers.GetValues("Content-Security-Policy")));
        Assert.Contains("no-referrer", page.Headers.GetValues("Referrer-Policy"));
        Assert.Equal((true, (bool?)false), await scene.StateAsync()); // opening the page changed nothing
    }

    [Fact]
    public async Task A_token_posted_in_a_form_body_or_in_the_query_never_appears_in_any_application_log_even_at_trace_level()
    {
        var scene = await CreateAsync(traceLogging: true);
        await using var _ = scene.World;
        var confirm = scene.Token(RecipientAction.Confirm);
        var unsubscribe = scene.Token(RecipientAction.Unsubscribe);

        using var form = await scene.FormAsync(Confirm, confirm);
        using var oneClick = await scene.OneClickAsync(Unsubscribe, unsubscribe);
        using var forged = await scene.OneClickAsync(Unsubscribe, "forged-token-value");

        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.BadRequest), (form.StatusCode, oneClick.StatusCode, forged.StatusCode));
        var logged = scene.Logs.Entries.ToList();
        Assert.NotEmpty(logged); // the capture really sees ASP.NET Core's trace output
        Assert.Contains(logged, entry => entry.Contains("Microsoft.AspNetCore"));
        foreach (var secret in new[] { confirm, unsubscribe, Uri.EscapeDataString(confirm), Uri.EscapeDataString(unsubscribe), "forged-token-value" })
            Assert.DoesNotContain(logged, entry => entry.Contains(secret, StringComparison.Ordinal));
    }
}
