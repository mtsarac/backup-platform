using System.Net;
using System.Text.Json;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Identity;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BackupPlatform.IntegrationTests;

public sealed class PasskeyIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static async Task<BrowserSession> SignInWithPasswordAsync(AgentWorld world, Account account)
    {
        var browser = new BrowserSession(world.Factory.CreateBrowser());
        using var response = await browser.SendAsync(HttpMethod.Post, "/api/auth/login", new
        {
            loginIdentifier = account.Identifier,
            password = account.Password
        });
        return browser;
    }

    private static async Task<(VirtualAuthenticator Authenticator, Guid CredentialId)> EnrolPasskeyAsync(
        BrowserSession browser,
        string password,
        string name = "Primary Key")
    {
        using var optionsResponse = await browser.SendAsync(HttpMethod.Post, "/api/auth/passkeys/register/options");
        Assert.Equal(HttpStatusCode.OK, optionsResponse.StatusCode);
        var optionsJson = AgentWorld.ReadJson(optionsResponse);
        var challengeId = optionsJson.GetProperty("challengeId").GetGuid();
        var challengeBase64 = optionsJson.GetProperty("options").GetProperty("challenge").GetString()!;

        var authenticator = new VirtualAuthenticator();
        var attestation = authenticator.CreateAttestation(challengeBase64);

        using var registerResponse = await browser.SendAsync(HttpMethod.Post, "/api/auth/passkeys/register", new
        {
            challengeId,
            name,
            response = attestation,
            currentPassword = password
        });
        Assert.Equal(HttpStatusCode.Created, registerResponse.StatusCode);
        var regJson = AgentWorld.ReadJson(registerResponse);
        var credId = regJson.GetProperty("id").GetGuid();
        return (authenticator, credId);
    }

    [Fact]
    public async Task PasswordlessAssertion_ProducesFullyAuthenticatedSession()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var account = await world.SeedUserAsync();

        var browser = await SignInWithPasswordAsync(world, account);
        var (authenticator, _) = await EnrolPasskeyAsync(browser, account.Password);

        // Anonymous client performs passwordless login
        var anonBrowser = new BrowserSession(world.Factory.CreateBrowser());
        using var optionsResponse = await anonBrowser.SendAsync(HttpMethod.Post, "/api/auth/passkeys/login/options", new
        {
            loginIdentifier = account.Identifier
        });
        Assert.Equal(HttpStatusCode.OK, optionsResponse.StatusCode);
        var optionsJson = AgentWorld.ReadJson(optionsResponse);
        var challengeId = optionsJson.GetProperty("challengeId").GetGuid();
        var challengeBase64 = optionsJson.GetProperty("options").GetProperty("challenge").GetString()!;

        var assertion = authenticator.CreateAssertion(challengeBase64);

        using var loginResponse = await anonBrowser.SendAsync(HttpMethod.Post, "/api/auth/passkeys/login", new
        {
            challengeId,
            response = assertion,
            rememberMe = true
        });
        Assert.Equal(HttpStatusCode.NoContent, loginResponse.StatusCode);

        // Fully authenticated session can reach /api/me
        using var meResponse = await anonBrowser.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);
    }

    [Fact]
    public async Task SecondFactorAssertion_RestrictedSessionTransitionsToFullyAuthenticated()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var account = await world.SeedUserAsync();

        var setupBrowser = await SignInWithPasswordAsync(world, account);
        var (authenticator, _) = await EnrolPasskeyAsync(setupBrowser, account.Password);

        // Password login when passkey is enrolled yields restricted session
        var browser = new BrowserSession(world.Factory.CreateBrowser());
        using var loginResponse = await browser.SendAsync(HttpMethod.Post, "/api/auth/login", new
        {
            loginIdentifier = account.Identifier,
            password = account.Password
        });
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        var loginBody = AgentWorld.ReadJson(loginResponse);
        Assert.True(loginBody.GetProperty("requiresSecondFactor").GetBoolean());

        // Restricted session cannot reach /api/me (403 Forbidden)
        using var deniedMe = await browser.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.Forbidden, deniedMe.StatusCode);

        // Request second factor options
        using var sfOptionsResponse = await browser.SendAsync(HttpMethod.Post, "/api/auth/passkeys/second-factor/options");
        Assert.Equal(HttpStatusCode.OK, sfOptionsResponse.StatusCode);
        var sfOptionsJson = AgentWorld.ReadJson(sfOptionsResponse);
        var challengeId = sfOptionsJson.GetProperty("challengeId").GetGuid();
        var challengeBase64 = sfOptionsJson.GetProperty("options").GetProperty("challenge").GetString()!;

        var assertion = authenticator.CreateAssertion(challengeBase64);

        // Complete second factor assertion
        using var verifyResponse = await browser.SendAsync(HttpMethod.Post, "/api/auth/passkeys/second-factor", new
        {
            challengeId,
            response = assertion
        });
        Assert.Equal(HttpStatusCode.NoContent, verifyResponse.StatusCode);

        // Now session is fully authenticated: /api/me succeeds!
        using var allowedMe = await browser.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.OK, allowedMe.StatusCode);
    }

    [Fact]
    public async Task ReplayAssertion_AndChallengeSingleUse_IsRefused()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var account = await world.SeedUserAsync();

        var browser = await SignInWithPasswordAsync(world, account);
        var (authenticator, _) = await EnrolPasskeyAsync(browser, account.Password);

        var anonBrowser = new BrowserSession(world.Factory.CreateBrowser());
        using var optionsResponse = await anonBrowser.SendAsync(HttpMethod.Post, "/api/auth/passkeys/login/options", new
        {
            loginIdentifier = account.Identifier
        });
        var optionsJson = AgentWorld.ReadJson(optionsResponse);
        var challengeId = optionsJson.GetProperty("challengeId").GetGuid();
        var challengeBase64 = optionsJson.GetProperty("options").GetProperty("challenge").GetString()!;

        var assertion = authenticator.CreateAssertion(challengeBase64);

        // First assertion succeeds
        using var first = await anonBrowser.SendAsync(HttpMethod.Post, "/api/auth/passkeys/login", new
        {
            challengeId,
            response = assertion
        });
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        // Replay with the same challenge is refused
        using var replay = await anonBrowser.SendAsync(HttpMethod.Post, "/api/auth/passkeys/login", new
        {
            challengeId,
            response = assertion
        });
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
    }

    [Fact]
    public async Task ExpiredChallenge_IsRefused()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var account = await world.SeedUserAsync();

        var browser = await SignInWithPasswordAsync(world, account);
        var (authenticator, _) = await EnrolPasskeyAsync(browser, account.Password);

        var anonBrowser = new BrowserSession(world.Factory.CreateBrowser());
        using var optionsResponse = await anonBrowser.SendAsync(HttpMethod.Post, "/api/auth/passkeys/login/options", new
        {
            loginIdentifier = account.Identifier
        });
        var optionsJson = AgentWorld.ReadJson(optionsResponse);
        var challengeId = optionsJson.GetProperty("challengeId").GetGuid();
        var challengeBase64 = optionsJson.GetProperty("options").GetProperty("challenge").GetString()!;

        // Advance clock past 5 minutes expiry
        world.Clock.Advance(TimeSpan.FromMinutes(6));

        var assertion = authenticator.CreateAssertion(challengeBase64);
        using var response = await anonBrowser.SendAsync(HttpMethod.Post, "/api/auth/passkeys/login", new
        {
            challengeId,
            response = assertion
        });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ForeignOriginAssertion_IsRefused()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var account = await world.SeedUserAsync();

        var browser = await SignInWithPasswordAsync(world, account);
        var (authenticator, _) = await EnrolPasskeyAsync(browser, account.Password);

        var anonBrowser = new BrowserSession(world.Factory.CreateBrowser());
        using var optionsResponse = await anonBrowser.SendAsync(HttpMethod.Post, "/api/auth/passkeys/login/options", new
        {
            loginIdentifier = account.Identifier
        });
        var optionsJson = AgentWorld.ReadJson(optionsResponse);
        var challengeId = optionsJson.GetProperty("challengeId").GetGuid();
        var challengeBase64 = optionsJson.GetProperty("options").GetProperty("challenge").GetString()!;

        // Foreign origin: evil.com
        var assertion = authenticator.CreateAssertion(challengeBase64, origin: "https://evil.com");
        using var response = await anonBrowser.SendAsync(HttpMethod.Post, "/api/auth/passkeys/login", new
        {
            challengeId,
            response = assertion
        });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task WrongRelyingParty_IsRefused()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var account = await world.SeedUserAsync();

        var browser = await SignInWithPasswordAsync(world, account);
        var (authenticator, _) = await EnrolPasskeyAsync(browser, account.Password);

        var anonBrowser = new BrowserSession(world.Factory.CreateBrowser());
        using var optionsResponse = await anonBrowser.SendAsync(HttpMethod.Post, "/api/auth/passkeys/login/options", new
        {
            loginIdentifier = account.Identifier
        });
        var optionsJson = AgentWorld.ReadJson(optionsResponse);
        var challengeId = optionsJson.GetProperty("challengeId").GetGuid();
        var challengeBase64 = optionsJson.GetProperty("options").GetProperty("challenge").GetString()!;

        // Wrong RP ID: other-host.com
        var assertion = authenticator.CreateAssertion(challengeBase64, rpId: "other-host.com");
        using var response = await anonBrowser.SendAsync(HttpMethod.Post, "/api/auth/passkeys/login", new
        {
            challengeId,
            response = assertion
        });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task MultipleCredentials_AddListRemoveOne()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var account = await world.SeedUserAsync();

        var browser = await SignInWithPasswordAsync(world, account);
        var (auth1, id1) = await EnrolPasskeyAsync(browser, account.Password, "First Key");
        var (auth2, id2) = await EnrolPasskeyAsync(browser, account.Password, "Second Key");

        // List passkeys: both should be present
        using var listResponse = await browser.GetAsync("/api/auth/passkeys");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var listJson = AgentWorld.ReadJson(listResponse);
        var items = listJson.EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.Contains(items, item => item.GetProperty("id").GetGuid() == id1 && item.GetProperty("name").GetString() == "First Key");
        Assert.Contains(items, item => item.GetProperty("id").GetGuid() == id2 && item.GetProperty("name").GetString() == "Second Key");

        // Remove First Key
        using var deleteResponse = await browser.SendAsync(HttpMethod.Delete, $"/api/auth/passkeys/{id1}", new
        {
            currentPassword = account.Password
        });
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        // List again: only Second Key remains
        using var listResponse2 = await browser.GetAsync("/api/auth/passkeys");
        var items2 = AgentWorld.ReadJson(listResponse2).EnumerateArray().ToList();
        Assert.Single(items2);
        Assert.Equal(id2, items2[0].GetProperty("id").GetGuid());

        // Second Key can still authenticate
        var anonBrowser = new BrowserSession(world.Factory.CreateBrowser());
        using var optionsResponse = await anonBrowser.SendAsync(HttpMethod.Post, "/api/auth/passkeys/login/options", new
        {
            loginIdentifier = account.Identifier
        });
        var optionsJson = AgentWorld.ReadJson(optionsResponse);
        var challengeId = optionsJson.GetProperty("challengeId").GetGuid();
        var challengeBase64 = optionsJson.GetProperty("options").GetProperty("challenge").GetString()!;

        var assertion = auth2.CreateAssertion(challengeBase64);
        using var loginResponse = await anonBrowser.SendAsync(HttpMethod.Post, "/api/auth/passkeys/login", new
        {
            challengeId,
            response = assertion
        });
        Assert.Equal(HttpStatusCode.NoContent, loginResponse.StatusCode);
    }

    [Fact]
    public async Task EnrolmentAndRemoval_WithoutSecurityVerification_IsRefused()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var account = await world.SeedUserAsync();

        var browser = await SignInWithPasswordAsync(world, account);

        using var optionsResponse = await browser.SendAsync(HttpMethod.Post, "/api/auth/passkeys/register/options");
        var optionsJson = AgentWorld.ReadJson(optionsResponse);
        var challengeId = optionsJson.GetProperty("challengeId").GetGuid();
        var challengeBase64 = optionsJson.GetProperty("options").GetProperty("challenge").GetString()!;

        var authenticator = new VirtualAuthenticator();
        var attestation = authenticator.CreateAttestation(challengeBase64);

        // Enrol with wrong password -> Refused (401)
        using var wrongPassResponse = await browser.SendAsync(HttpMethod.Post, "/api/auth/passkeys/register", new
        {
            challengeId,
            name = "Key",
            response = attestation,
            currentPassword = "Wrong-password-123!"
        });
        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassResponse.StatusCode);

        // Enrol with missing password -> Refused (401)
        using var missingPassResponse = await browser.SendAsync(HttpMethod.Post, "/api/auth/passkeys/register", new
        {
            challengeId,
            name = "Key",
            response = attestation
        });
        Assert.Equal(HttpStatusCode.Unauthorized, missingPassResponse.StatusCode);

        // Enrol with correct password succeeds
        var (_, credId) = await EnrolPasskeyAsync(browser, account.Password);

        // Remove with wrong password -> Refused (401)
        using var wrongRemove = await browser.SendAsync(HttpMethod.Delete, $"/api/auth/passkeys/{credId}", new
        {
            currentPassword = "Wrong-password-123!"
        });
        Assert.Equal(HttpStatusCode.Unauthorized, wrongRemove.StatusCode);

        // Remove with missing password -> Refused (401)
        using var missingRemove = await browser.SendAsync(HttpMethod.Delete, $"/api/auth/passkeys/{credId}", new { });
        Assert.Equal(HttpStatusCode.Unauthorized, missingRemove.StatusCode);
    }

    [Fact]
    public async Task DisabledAccount_CannotSignInWithPasskey()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var account = await world.SeedUserAsync();

        var browser = await SignInWithPasswordAsync(world, account);
        var (authenticator, _) = await EnrolPasskeyAsync(browser, account.Password);

        // Disable user account in DB
        await using (var db = fixture.CreatePlatformContext())
        {
            var user = await db.Users.SingleAsync(u => u.Id == account.UserId);
            user.Disable();
            await db.SaveChangesAsync();
        }

        // Passkey passwordless login is refused
        var anonBrowser = new BrowserSession(world.Factory.CreateBrowser());
        using var optionsResponse = await anonBrowser.SendAsync(HttpMethod.Post, "/api/auth/passkeys/login/options", new
        {
            loginIdentifier = account.Identifier
        });
        var optionsJson = AgentWorld.ReadJson(optionsResponse);
        var challengeId = optionsJson.GetProperty("challengeId").GetGuid();
        var challengeBase64 = optionsJson.GetProperty("options").GetProperty("challenge").GetString()!;

        var assertion = authenticator.CreateAssertion(challengeBase64);
        using var response = await anonBrowser.SendAsync(HttpMethod.Post, "/api/auth/passkeys/login", new
        {
            challengeId,
            response = assertion
        });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // Password login is also refused
        using var pwdResponse = await anonBrowser.SendAsync(HttpMethod.Post, "/api/auth/login", new
        {
            loginIdentifier = account.Identifier,
            password = account.Password
        });
        Assert.Equal(HttpStatusCode.Unauthorized, pwdResponse.StatusCode);
    }

    [Fact]
    public async Task FailedAssertions_AreRateLimited_PerAccountAndIP()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var account = await world.SeedUserAsync();

        var browser = await SignInWithPasswordAsync(world, account);
        var (authenticator, _) = await EnrolPasskeyAsync(browser, account.Password);

        var anonBrowser = new BrowserSession(world.Factory.CreateBrowser());
        anonBrowser.Client.DefaultRequestHeaders.Add("X-Forwarded-For", "198.51.100.12");

        for (var i = 0; i < 5; i++)
        {
            using var opt = await anonBrowser.SendAsync(HttpMethod.Post, "/api/auth/passkeys/login/options", new { loginIdentifier = account.Identifier });
            var cid = AgentWorld.ReadJson(opt).GetProperty("challengeId").GetGuid();
            var ch = AgentWorld.ReadJson(opt).GetProperty("options").GetProperty("challenge").GetString()!;
            // Corrupt signature
            var badAssertion = authenticator.CreateAssertion(ch, origin: "https://evil.com");
            using var fail = await anonBrowser.SendAsync(HttpMethod.Post, "/api/auth/passkeys/login", new { challengeId = cid, response = badAssertion });
            Assert.Equal(HttpStatusCode.Unauthorized, fail.StatusCode);
        }

        // 6th attempt is throttled (429)
        using var optThrottled = await anonBrowser.SendAsync(HttpMethod.Post, "/api/auth/passkeys/login/options", new { loginIdentifier = account.Identifier });
        var cidThrottled = AgentWorld.ReadJson(optThrottled).GetProperty("challengeId").GetGuid();
        var chThrottled = AgentWorld.ReadJson(optThrottled).GetProperty("options").GetProperty("challenge").GetString()!;
        var assertionThrottled = authenticator.CreateAssertion(chThrottled);
        using var throttledResponse = await anonBrowser.SendAsync(HttpMethod.Post, "/api/auth/passkeys/login", new { challengeId = cidThrottled, response = assertionThrottled });
        Assert.Equal(HttpStatusCode.TooManyRequests, throttledResponse.StatusCode);
        Assert.NotNull(throttledResponse.Headers.RetryAfter);
    }

    [Fact]
    public async Task AddingAndRemovingPasskey_AreAudited()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var account = await world.SeedUserAsync();

        var browser = await SignInWithPasswordAsync(world, account);
        var (_, credId) = await EnrolPasskeyAsync(browser, account.Password, "Audited Key");

        await using (var db = fixture.CreatePlatformContext())
        {
            var registeredEvent = await db.AuditEvents.AsNoTracking().SingleOrDefaultAsync(e =>
                e.ActorUserId == account.UserId && e.Action == AuditActions.PasskeyRegistered);
            Assert.NotNull(registeredEvent);
            Assert.Equal("passkey", registeredEvent.TargetType);
            Assert.Equal(credId.ToString("D"), registeredEvent.TargetId);
            Assert.Contains("Audited Key", registeredEvent.DetailsJson);
            // Verify no secret or key in audit details
            Assert.DoesNotContain("publicKey", registeredEvent.DetailsJson, StringComparison.OrdinalIgnoreCase);
        }

        // Remove
        using var del = await browser.SendAsync(HttpMethod.Delete, $"/api/auth/passkeys/{credId}", new
        {
            currentPassword = account.Password
        });
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        await using (var db = fixture.CreatePlatformContext())
        {
            var removedEvent = await db.AuditEvents.AsNoTracking().SingleOrDefaultAsync(e =>
                e.ActorUserId == account.UserId && e.Action == AuditActions.PasskeyRemoved);
            Assert.NotNull(removedEvent);
            Assert.Equal("passkey", removedEvent.TargetType);
            Assert.Equal(credId.ToString("D"), removedEvent.TargetId);
        }
    }

    [Fact]
    public async Task PasskeyOnlyUser_CanRedeemRecoveryCode_DuringSecondFactorChallenge()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var account = await world.SeedUserAsync();

        var setupBrowser = await SignInWithPasswordAsync(world, account);
        var (_, _) = await EnrolPasskeyAsync(setupBrowser, account.Password);

        // Generate recovery codes for passkey user
        using var regenResp = await setupBrowser.SendAsync(HttpMethod.Post, "/api/auth/security/recovery-codes/regenerate", new
        {
            currentPassword = account.Password
        });
        Assert.Equal(HttpStatusCode.OK, regenResp.StatusCode);
        var regenJson = AgentWorld.ReadJson(regenResp);
        var recoveryCodes = regenJson.GetProperty("recoveryCodes").EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.Equal(10, recoveryCodes.Count);

        // Sign in with password -> restricted session requires second factor
        var browser = new BrowserSession(world.Factory.CreateBrowser());
        using var loginResp = await browser.SendAsync(HttpMethod.Post, "/api/auth/login", new
        {
            loginIdentifier = account.Identifier,
            password = account.Password
        });
        Assert.Equal(HttpStatusCode.OK, loginResp.StatusCode);
        var loginBody = AgentWorld.ReadJson(loginResp);
        Assert.True(loginBody.GetProperty("requiresSecondFactor").GetBoolean());

        // Restricted session cannot reach /api/me
        using var deniedMe = await browser.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.Forbidden, deniedMe.StatusCode);

        // Redeem recovery code during second-factor challenge
        using var redeemResp = await browser.SendAsync(HttpMethod.Post, "/api/auth/second-factor/recovery-code", new
        {
            recoveryCode = recoveryCodes[0]
        });
        Assert.Equal(HttpStatusCode.NoContent, redeemResp.StatusCode);

        // Session is now fully authenticated
        using var allowedMe = await browser.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.OK, allowedMe.StatusCode);
    }
}
