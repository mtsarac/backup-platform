using System.Net;
using BackupPlatform.Domain.Audit;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.Extensions.DependencyInjection;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Identity;
using BackupPlatform.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.IntegrationTests;

public sealed class SessionLifecycleIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static async Task<BrowserSession> SignInAsync(AgentWorld world, Account account, bool remember = false)
    {
        var browser = new BrowserSession(world.Factory.CreateBrowser());
        browser.Client.DefaultRequestHeaders.UserAgent.ParseAdd("SessionTest/1.0");
        browser.Client.DefaultRequestHeaders.Add("X-Forwarded-For", "192.0.2.45");
        using var login = await browser.SendAsync(HttpMethod.Post, "/api/auth/login", new { loginIdentifier = account.Identifier, password = account.Password, rememberMe = remember });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        return browser;
    }

    [Fact]
    public async Task IdleAndAbsoluteExpiryUseStoredServerTime()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var account = await world.SeedUserAsync();
        var idle = await SignInAsync(world, account);
        world.Clock.Advance(TimeSpan.FromHours(12));
        using (var expired = await idle.GetAsync("/api/me")) Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
        var active = await SignInAsync(world, account);
        for (var i = 0; i < 14; i++)
        {
            world.Clock.Advance(TimeSpan.FromHours(11));
            using var ok = await active.GetAsync("/api/me");
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }
        world.Clock.Advance(TimeSpan.FromHours(7));
        using (var ok = await active.GetAsync("/api/me")) Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        world.Clock.Advance(TimeSpan.FromHours(7));
        using var absolute = await active.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.Unauthorized, absolute.StatusCode);
    }

    [Fact]
    public async Task RememberMeIgnoresIdleButExpiresAtThirtyDays()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var browser = await SignInAsync(world, await world.SeedUserAsync(), true);
        world.Clock.Advance(TimeSpan.FromDays(29));
        using (var ok = await browser.GetAsync("/api/me")) Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        world.Clock.Advance(TimeSpan.FromDays(1));
        using var expired = await browser.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
    }

    [Fact]
    public async Task ListIsIsolatedAndRevocationRequiresCsrfAndInvalidatesOnlyOwnedSessions()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var account = await world.SeedUserAsync();
        var first = await SignInAsync(world, account);
        var second = await SignInAsync(world, account);
        var other = await SignInAsync(world, await world.SeedUserAsync());
        using var list = await first.GetAsync("/api/auth/sessions");
        var sessions = AgentWorld.ReadJson(list).EnumerateArray().ToArray();
        Assert.Equal(2, sessions.Length);
        Assert.All(sessions, session => { Assert.Contains("SessionTest", session.GetProperty("device").GetString()); Assert.Equal("192.0.2.45", session.GetProperty("ipAddress").GetString()); Assert.NotEqual(default, session.GetProperty("lastActivityAt").GetDateTimeOffset()); });
        var target = sessions.Single(session => !session.GetProperty("isCurrent").GetBoolean()).GetProperty("id").GetGuid();
        using var foreignList = await other.GetAsync("/api/auth/sessions");
        Assert.Single(AgentWorld.ReadJson(foreignList).EnumerateArray());
        using (var noToken = await first.SendAsync(HttpMethod.Delete, $"/api/auth/sessions/{target}", includeAntiforgery: false)) Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
        using (var invalid = new HttpRequestMessage(HttpMethod.Delete, $"/api/auth/sessions/{target}"))
        {
            invalid.Headers.Add("X-CSRF-TOKEN", "invalid");
            using var response = await first.Client.SendAsync(invalid);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        using (var foreign = await other.SendAsync(HttpMethod.Delete, $"/api/auth/sessions/{target}")) Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        using (var revoke = await first.SendAsync(HttpMethod.Delete, $"/api/auth/sessions/{target}")) Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        using (var revoked = await second.GetAsync("/api/me")) Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        using (var still = await first.GetAsync("/api/me")) Assert.Equal(HttpStatusCode.OK, still.StatusCode);
        var third = await SignInAsync(world, account);
        using (var noToken = await first.SendAsync(HttpMethod.Delete, "/api/auth/sessions", includeAntiforgery: false)) Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
        using (var all = await first.SendAsync(HttpMethod.Delete, "/api/auth/sessions")) Assert.Equal(HttpStatusCode.NoContent, all.StatusCode);
        using (var revoked = await first.GetAsync("/api/me")) Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        using (var revoked = await third.GetAsync("/api/me")) Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        using var unaffected = await other.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.OK, unaffected.StatusCode);
        await using var auditDb = fixture.CreatePlatformContext();
        var audit = await auditDb.AuditEvents.AsNoTracking().Where(row => row.ActorUserId == account.UserId && row.Action == AuditActions.UserLogout).ToListAsync();
        Assert.Contains(audit, row => row.TargetId == target.ToString("D"));
        Assert.Contains(audit, row => row.DetailsJson.Contains("all_sessions_revoked", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ActivityWritesAreCoalesced()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var account = await world.SeedUserAsync();
        var browser = await SignInAsync(world, account);
        var created = world.Clock.GetUtcNow();
        world.Clock.Advance(TimeSpan.FromSeconds(30));
        using (var response = await browser.GetAsync("/api/me")) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using (var db = fixture.CreatePlatformContext())
            Assert.Equal(created, (await db.UserSessions.SingleAsync(row => row.UserId == account.UserId)).LastActivityAt);
        world.Clock.Advance(TimeSpan.FromSeconds(30));
        using (var response = await browser.GetAsync("/api/me")) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var updated = fixture.CreatePlatformContext();
        Assert.Equal(world.Clock.GetUtcNow(), (await updated.UserSessions.SingleAsync(row => row.UserId == account.UserId)).LastActivityAt);
    }

    [Fact]
    public async Task UntrustedForwardedAddressesCannotEvadeIpThrottle()
    {
        await using var world = await AgentWorld.CreateAsync(fixture, kestrel: true);
        using var client = AgentWorld.CreateTrustingClient(world.ServerAddress!, handleCookies: true);
        var browser = new BrowserSession(client);
        for (var i = 1; i <= 6; i++)
        {
            using var response = await browser.SendAsync(HttpMethod.Post, "/api/auth/login",
                new { loginIdentifier = $"unknown-{Guid.NewGuid():N}", password = "wrong-password" },
                headers: [new("X-Forwarded-For", $"192.0.2.{i}")]);
            Assert.Equal(i <= 5 ? HttpStatusCode.Unauthorized : HttpStatusCode.TooManyRequests, response.StatusCode);
        }
    }

    [Theory]
    [InlineData(false, HttpStatusCode.TemporaryRedirect)]
    [InlineData(true, HttpStatusCode.OK)]
    public async Task ForwardedHttpsRequiresTrustedProxyAndProcessesOnlyOneHop(bool trusted, HttpStatusCode expected)
    {
        var configuration = new Dictionary<string, string?>
        {
            ["ForwardedHeaders_Enabled"] = "true",
            ["Authentication:TrustedProxies:0"] = trusted ? "127.0.0.1" : "192.0.2.10"
        };
        await using var world = await AgentWorld.CreateAsync(fixture, configuration: configuration,
            configureServices: services => services.Configure<HttpsRedirectionOptions>(options => options.HttpsPort = 443));
        var response = await world.Factory.Server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Loopback;
            context.Request.Scheme = "http";
            context.Request.Host = new Microsoft.AspNetCore.Http.HostString("localhost");
            context.Request.Path = "/api/auth/antiforgery";
            context.Request.Headers["X-Forwarded-For"] = "192.0.2.45, 127.0.0.1";
            context.Request.Headers["X-Forwarded-Proto"] = "http, https";
        });
        Assert.Equal((int)expected, response.Response.StatusCode);
    }

    [Fact]
    public async Task ConfiguredProxySuppliesClientAddress()
    {
        await using var world = await AgentWorld.CreateAsync(fixture, kestrel: true,
            configuration: new Dictionary<string, string?> { ["Authentication:TrustedProxies:0"] = "127.0.0.1" });
        var account = await world.SeedUserAsync();
        using var client = AgentWorld.CreateTrustingClient(world.ServerAddress!, handleCookies: true);
        var browser = new BrowserSession(client);
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "192.0.2.45");
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        using var login = await browser.SendAsync(HttpMethod.Post, "/api/auth/login", new { loginIdentifier = account.Identifier, password = account.Password });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        var cookie = Assert.Single(login.Headers.GetValues("Set-Cookie"), value => value.StartsWith("__Host-BackupPlatform.Auth=", StringComparison.Ordinal));
        Assert.Contains("; secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("; httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("; samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        using var response = await browser.GetAsync("/api/auth/sessions");
        Assert.Equal("192.0.2.45", Assert.Single(AgentWorld.ReadJson(response).EnumerateArray()).GetProperty("ipAddress").GetString());
    }

    [Theory]
    [InlineData(SessionAuthenticationState.PasswordChangeRequired)]
    [InlineData(SessionAuthenticationState.SecondFactorPending)]
    public async Task RestrictedSessionCannotReachTenantOrPlatformButCanLogout(SessionAuthenticationState state)
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var account = await world.SeedUserAsync(platformAdmin: false);
        var tenant = await world.AddTenantAsync();
        await world.GrantAsync(tenant, account);
        var browser = await SignInAsync(world, account);
        await using var db = fixture.CreatePlatformContext();
        var session = await db.UserSessions.SingleAsync(row => row.UserId == account.UserId);
        var store = new PostgreSqlIdentityStore(fixture.Options);
        await store.SetSessionStateAsync(session.Id, account.UserId, state, default);
        foreach (var path in new[] { "/api/me", "/api/test/platform", $"/api/tenants/{tenant.Id.Value}/context", "/api/auth/sessions" })
        {
            using var denied = await browser.GetAsync(path);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        using var logout = await browser.SendAsync(HttpMethod.Post, "/api/auth/logout");
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
    }
}
