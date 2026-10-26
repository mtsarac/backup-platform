using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using BackupPlatform.Api.Security;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Identity;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Npgsql;

namespace BackupPlatform.IntegrationTests;

public sealed class AuthIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task LoginNormalizesIdentifierAndIssuesProtectedSecretFreeBrowserCookie()
    {
        var seed = await SeedUserAsync();
        await using var factory = new AuthApiFactory(fixture.ConnectionString);
        using var browser = factory.CreateBrowser();
        var token = await FetchTokenAsync(browser);
        using var response = await LoginAsync(browser, token, $"  {seed.Identifier.ToUpperInvariant()}  ", seed.Password);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var cookie = GetAuthenticationCookie(response);
        Assert.True(cookie.Secure);
        Assert.True(cookie.HttpOnly);
        Assert.Equal("Strict", cookie.SameSite.ToString());
        Assert.Equal("/", cookie.Path.ToString());
        Assert.False(cookie.Domain.HasValue);
        Assert.Null(cookie.Expires);
        var options = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(BrowserAuthentication.Scheme);
        var ticket = options.TicketDataFormat.Unprotect(cookie.Value.ToString());
        Assert.NotNull(ticket);
        Assert.Collection(ticket.Principal.Claims.OrderBy(claim => claim.Type, StringComparer.Ordinal),
            claim => { Assert.Equal(BrowserAuthentication.SessionClaimType, claim.Type); Assert.NotEqual(Guid.Empty, Guid.Parse(claim.Value)); },
            claim => { Assert.Equal(ClaimTypes.NameIdentifier, claim.Type); Assert.Equal(seed.UserId.Value.ToString(), claim.Value); });
        using var profileResponse = await browser.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.OK, profileResponse.StatusCode);
        var profile = await ReadJsonAsync(profileResponse);
        Assert.Equal(seed.UserId.Value, profile.GetProperty("userId").GetGuid());
        Assert.Equal(seed.DisplayName, profile.GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task RepeatedWrongPasswordsAreAnswered429WithRetryAfterUntilTheWindowEndsAndASuccessClearsTheCount()
    {
        var seed = await SeedUserAsync();
        var clock = new MutableClock();
        await using var factory = new AuthApiFactory(fixture.ConnectionString, clock);
        using var browser = factory.CreateBrowser();
        var token = await FetchTokenAsync(browser);
        for (var attempt = 0; attempt < LoginThrottle.MaxFailures - 1; attempt++)
        {
            using var wrong = await LoginAsync(browser, token, seed.Identifier, "Wrong-local-password!");
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }
        using (var cleared = await LoginAsync(browser, token, seed.Identifier, seed.Password))
            Assert.Equal(HttpStatusCode.NoContent, cleared.StatusCode);
        // The antiforgery token of the first browser is bound to its now signed-in session.
        using var second = factory.CreateBrowser();
        var secondToken = await FetchTokenAsync(second);
        for (var attempt = 0; attempt < LoginThrottle.MaxFailures; attempt++)
        {
            using var wrong = await LoginAsync(second, secondToken, seed.Identifier, "Wrong-local-password!");
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        using var locked = await LoginAsync(second, secondToken, seed.Identifier.ToUpperInvariant(), seed.Password);
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.Equal(((int)LoginThrottle.Window.TotalSeconds).ToString(), Assert.Single(locked.Headers.GetValues("Retry-After")));

        clock.Advance(LoginThrottle.Window);
        using var allowed = await LoginAsync(second, secondToken, seed.Identifier, seed.Password);
        Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode);
    }

    [Fact]
    public async Task WrongUnknownAndInvalidIdentifiersReturnSameGenericUnauthorizedResponse()
    {
        var seed = await SeedUserAsync();
        await using var factory = new AuthApiFactory(fixture.ConnectionString);
        using var browser = factory.CreateBrowser();
        var token = await FetchTokenAsync(browser);
        using var wrong = await LoginAsync(browser, token, seed.Identifier, "Wrong-local-password!");
        using var unknown = await LoginAsync(browser, token, $"unknown-{Guid.NewGuid():N}", seed.Password);
        using var invalid = await LoginAsync(browser, token, "invalid identifier", seed.Password);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(wrong.StatusCode, unknown.StatusCode);
        Assert.Equal(wrong.StatusCode, invalid.StatusCode);
        var generic = await wrong.Content.ReadAsStringAsync();
        Assert.Equal(generic, await unknown.Content.ReadAsStringAsync());
        Assert.Equal(generic, await invalid.Content.ReadAsStringAsync());
        Assert.Equal("Invalid login identifier or password.", (await ReadJsonAsync(wrong)).GetProperty("error").GetString());
        Assert.False(wrong.Headers.TryGetValues("Set-Cookie", out _));
    }

    [Fact]
    public async Task LogoutClearsBrowserAuthenticationAndCapturedCookieCannotBeReplayed()
    {
        var seed = await SeedUserAsync();
        await using var factory = new AuthApiFactory(fixture.ConnectionString);
        using var browser = factory.CreateBrowser();
        var anonymousToken = await FetchTokenAsync(browser);
        using var login = await LoginAsync(browser, anonymousToken, seed.Identifier, seed.Password);
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        var cookie = GetAuthenticationCookie(login);
        var captured = $"{cookie.Name}={cookie.Value}";
        var authenticatedToken = await FetchTokenAsync(browser);
        using var logout = await PostAsync(browser, "/api/auth/logout", authenticatedToken, null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.True(GetAuthenticationCookie(logout).Expires < DateTimeOffset.UtcNow);
        using var afterLogout = await browser.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.Unauthorized, afterLogout.StatusCode);
        using var replay = factory.CreateReplayClient();
        replay.DefaultRequestHeaders.Add("Cookie", captured);
        using var replayResponse = await replay.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.Unauthorized, replayResponse.StatusCode);
    }

    [Fact]
    public async Task AnonymousProtectedReadsAreUnauthorizedWhileSafeAntiforgeryReadWorks()
    {
        await using var factory = new AuthApiFactory(fixture.ConnectionString);
        using var browser = factory.CreateBrowser();
        foreach (var path in new[] { "/api/me", "/api/me/tenants", $"/api/tenants/{Guid.NewGuid()}/context" })
        {
            using var response = await browser.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Null(response.Headers.Location);
        }
        using var tokenResponse = await browser.GetAsync("/api/auth/antiforgery");
        Assert.Equal(HttpStatusCode.OK, tokenResponse.StatusCode);
        var cookie = Assert.Single(tokenResponse.Headers.GetValues("Set-Cookie").Select(header => SetCookieHeaderValue.Parse(header)));
        Assert.True(cookie.Secure);
        Assert.True(cookie.HttpOnly);
        Assert.Equal("Strict", cookie.SameSite.ToString());
    }

    [Fact]
    public async Task LoginAndLogoutRejectMissingWrongAndStaleAntiforgeryTokens()
    {
        var seed = await SeedUserAsync();
        await using var factory = new AuthApiFactory(fixture.ConnectionString);
        using var browser = factory.CreateBrowser();
        var anonymousToken = await FetchTokenAsync(browser);
        foreach (var token in new string?[] { null, "invalid-token" })
        {
            using var response = await LoginAsync(browser, token, seed.Identifier, seed.Password);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        using var login = await LoginAsync(browser, anonymousToken, seed.Identifier, seed.Password);
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        foreach (var token in new string?[] { null, "invalid-token", anonymousToken })
        {
            using var response = await PostAsync(browser, "/api/auth/logout", token, null);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        using var stillAuthenticated = await browser.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.OK, stillAuthenticated.StatusCode);
        var authenticatedToken = await FetchTokenAsync(browser);
        using var logout = await PostAsync(browser, "/api/auth/logout", authenticatedToken, null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
    }

    [Fact]
    public async Task TenantRolesStayIndependentAndPlatformAdminCannotBypassMembership()
    {
        var seed = await SeedUserAsync();
        var adminTenant = await AddTenantAsync(seed.UserId, TenantRole.Admin);
        var viewerTenant = await AddTenantAsync(seed.UserId, TenantRole.Viewer);
        var absentTenant = await AddTenantAsync(null, null);
        await using (var database = fixture.CreatePlatformContext())
        {
            database.PlatformAdmins.Add(new PlatformAdmin(seed.UserId));
            await database.SaveChangesAsync();
        }
        await using var factory = new AuthApiFactory(fixture.ConnectionString);
        using var browser = factory.CreateBrowser();
        await AuthenticateAsync(browser, seed);
        using var listed = await browser.GetAsync("/api/me/tenants");
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        var memberships = (await ReadJsonAsync(listed)).EnumerateArray().ToArray();
        Assert.Equal(2, memberships.Length);
        Assert.Equal("Administrator", memberships.Single(row => row.GetProperty("tenantId").GetGuid() == adminTenant.Id.Value).GetProperty("role").GetString());
        Assert.Equal("Viewer", memberships.Single(row => row.GetProperty("tenantId").GetGuid() == viewerTenant.Id.Value).GetProperty("role").GetString());
        using var adminContext = await browser.GetAsync($"/api/tenants/{adminTenant.Id.Value}/context");
        Assert.Equal(HttpStatusCode.OK, adminContext.StatusCode);
        var selected = await ReadJsonAsync(adminContext);
        Assert.Equal(adminTenant.Id.Value, selected.GetProperty("tenantId").GetGuid());
        Assert.Equal(adminTenant.Name, selected.GetProperty("name").GetString());
        using var viewerContext = await browser.GetAsync($"/api/tenants/{viewerTenant.Id.Value}/context");
        Assert.Equal(HttpStatusCode.OK, viewerContext.StatusCode);
        Assert.Equal("Viewer", (await ReadJsonAsync(viewerContext)).GetProperty("role").GetString());
        using var missingContext = await browser.GetAsync($"/api/tenants/{absentTenant.Id.Value}/context");
        Assert.Equal(HttpStatusCode.Forbidden, missingContext.StatusCode);
        using var platform = await browser.GetAsync("/api/test/platform");
        Assert.Equal(HttpStatusCode.OK, platform.StatusCode);
        using var viewerAdmin = await browser.GetAsync($"/api/test/tenants/{viewerTenant.Id.Value}/administrator");
        Assert.Equal(HttpStatusCode.Forbidden, viewerAdmin.StatusCode);
    }

    [Theory]
    [InlineData(TenantRole.Owner, true, true)]
    [InlineData(TenantRole.Admin, true, true)]
    [InlineData(TenantRole.Operator, false, true)]
    [InlineData(TenantRole.Viewer, false, false)]
    public async Task TenantRolePoliciesDoNotConferPlatformAdministration(TenantRole role, bool administer, bool operate)
    {
        var seed = await SeedUserAsync();
        var tenant = await AddTenantAsync(seed.UserId, role);
        await using var factory = new AuthApiFactory(fixture.ConnectionString);
        using var browser = factory.CreateBrowser();
        await AuthenticateAsync(browser, seed);
        using var administration = await browser.GetAsync($"/api/test/tenants/{tenant.Id.Value}/administrator");
        Assert.Equal(administer ? HttpStatusCode.OK : HttpStatusCode.Forbidden, administration.StatusCode);
        using var operation = await browser.GetAsync($"/api/test/tenants/{tenant.Id.Value}/operator");
        Assert.Equal(operate ? HttpStatusCode.OK : HttpStatusCode.Forbidden, operation.StatusCode);
        using var read = await browser.GetAsync($"/api/tenants/{tenant.Id.Value}/context");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var platform = await browser.GetAsync("/api/test/platform");
        Assert.Equal(HttpStatusCode.Forbidden, platform.StatusCode);
    }

    [Fact]
    public async Task TenantAndCurrentUserSnapshotsCannotBeSwitchedDuringRequest()
    {
        var seed = await SeedUserAsync();
        var other = await SeedUserAsync();
        var firstTenant = await AddTenantAsync(seed.UserId, TenantRole.Viewer);
        var secondTenant = await AddTenantAsync(other.UserId, TenantRole.Owner);
        await using var factory = new AuthApiFactory(fixture.ConnectionString);
        using var browser = factory.CreateBrowser();
        await AuthenticateAsync(browser, seed);
        using var response = await browser.GetAsync($"/api/test/tenants/{firstTenant.Id.Value}/immutable?replacementTenant={secondTenant.Id.Value}&replacementUser={other.UserId.Value}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var snapshot = await ReadJsonAsync(response);
        Assert.Equal(firstTenant.Id.Value, snapshot.GetProperty("tenantId").GetGuid());
        Assert.Equal(seed.UserId.Value, snapshot.GetProperty("userId").GetGuid());
        Assert.Equal(new[] { firstTenant.Id.Value }, snapshot.GetProperty("visibleTenantIds").EnumerateArray().Select(id => id.GetGuid()).ToArray());
    }

    [Fact]
    public async Task DuplicateNormalizedIdentifierIsRejectedByPostgreSql()
    {
        var seed = await SeedUserAsync();
        var secondUser = new User(UserId.New(), "Separate local user");
        await using var database = fixture.CreatePlatformContext();
        database.Users.Add(secondUser);
        database.PasswordCredentials.Add(new PasswordCredential(secondUser.Id, seed.Identifier.ToUpperInvariant(), new PasswordHashService().Hash(secondUser.Id, seed.Password)));
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => database.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
    }

    [Fact]
    public async Task CredentialPreventsDeletingItsUser()
    {
        var seed = await SeedUserAsync();
        await using var database = fixture.CreatePlatformContext();
        database.Users.Remove(await database.Users.SingleAsync(user => user.Id == seed.UserId));
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => database.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.RestrictViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
    }

    private async Task<SeededUser> SeedUserAsync()
    {
        var user = new User(UserId.New(), "Local test user");
        var identifier = $"user-{Guid.NewGuid():N}";
        var password = $"Local-only-{Guid.NewGuid():N}!";
        var hash = new PasswordHashService().Hash(user.Id, password);
        await using var database = fixture.CreatePlatformContext();
        database.Users.Add(user);
        database.PasswordCredentials.Add(new PasswordCredential(user.Id, identifier, hash));
        await database.SaveChangesAsync();
        return new SeededUser(user.Id, identifier, password, user.DisplayName);
    }

    private async Task<Tenant> AddTenantAsync(UserId? userId, TenantRole? role)
    {
        var tenant = new Tenant(TenantId.New(), $"Tenant {Guid.NewGuid():N}");
        await using var database = fixture.CreatePlatformContext();
        database.Tenants.Add(tenant);
        if (userId is not null && role is not null)
        {
            database.TenantMemberships.Add(new TenantMembership(tenant.Id, userId, role.Value));
        }
        await database.SaveChangesAsync();
        return tenant;
    }

    private static async Task AuthenticateAsync(HttpClient browser, SeededUser seed)
    {
        var token = await FetchTokenAsync(browser);
        using var response = await LoginAsync(browser, token, seed.Identifier, seed.Password);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task<string> FetchTokenAsync(HttpClient browser)
    {
        using var response = await browser.GetAsync("/api/auth/antiforgery");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await ReadJsonAsync(response);
        return json.GetProperty("requestToken").GetString()!;
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient browser, string? token, string identifier, string password) =>
        PostAsync(browser, "/api/auth/login", token, new { loginIdentifier = identifier, password });

    private static async Task<HttpResponseMessage> PostAsync(HttpClient browser, string path, string? token, object? body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        if (token is not null)
        {
            request.Headers.Add("X-CSRF-TOKEN", token);
        }
        return await browser.SendAsync(request);
    }

    private static SetCookieHeaderValue GetAuthenticationCookie(HttpResponseMessage response) =>
        Assert.Single(response.Headers.GetValues("Set-Cookie").Select(header => SetCookieHeaderValue.Parse(header)),
            cookie => cookie.Name.ToString() == "__Host-BackupPlatform.Auth");

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private sealed class SeededUser(UserId userId, string identifier, string password, string displayName)
    {
        public UserId UserId { get; } = userId;
        public string Identifier { get; } = identifier;
        public string Password { get; } = password;
        public string DisplayName { get; } = displayName;
    }
}
