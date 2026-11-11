using System.Net;
using System.Text.Json;
using BackupPlatform.Api.Security;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Identity;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BackupPlatform.IntegrationTests;

public sealed class TotpAndRecoveryIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static BrowserSession CreateBrowser(AgentWorld world, string? ip = null)
    {
        var browser = new BrowserSession(world.Factory.CreateBrowser());
        browser.Client.DefaultRequestHeaders.UserAgent.ParseAdd("TotpTest/1.0");
        var clientIp = ip ?? $"198.51.{Random.Shared.Next(1, 250)}.{Random.Shared.Next(1, 250)}";
        browser.Client.DefaultRequestHeaders.Add("X-Forwarded-For", clientIp);
        return browser;
    }

    private static async Task<(BrowserSession Browser, bool SecondFactorRequired)> SignInAsync(AgentWorld world, Account account, string? ip = null)
    {
        var browser = CreateBrowser(world, ip);
        using var response = await browser.SendAsync(HttpMethod.Post, "/api/auth/login", new { loginIdentifier = account.Identifier, password = account.Password });
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return (browser, false);
        }
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = AgentWorld.ReadJson(response);
        Assert.True(json.GetProperty("secondFactorRequired").GetBoolean());
        return (browser, true);
    }

    private static async Task<(string ManualEntryKey, List<string> RecoveryCodes)> EnrolTotpAsync(AgentWorld world, BrowserSession browser, Account account)
    {
        using var setupResp = await browser.SendAsync(HttpMethod.Post, "/api/auth/security/totp/setup");
        Assert.Equal(HttpStatusCode.OK, setupResp.StatusCode);
        var setupJson = AgentWorld.ReadJson(setupResp);
        var setupToken = setupJson.GetProperty("setupToken").GetString()!;
        var manualEntryKey = setupJson.GetProperty("manualEntryKey").GetString()!;

        var secretBytes = Totp.DecodeBase32(manualEntryKey);
        var step = Totp.CurrentStep(world.Clock.GetUtcNow());
        var code = Totp.ComputeCode(secretBytes, step);

        using var enrollResp = await browser.SendAsync(HttpMethod.Post, "/api/auth/security/totp/enroll", new
        {
            setupToken,
            code,
            currentPassword = account.Password
        });
        Assert.Equal(HttpStatusCode.OK, enrollResp.StatusCode);
        var enrollJson = AgentWorld.ReadJson(enrollResp);
        var codes = enrollJson.GetProperty("recoveryCodes").EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.Equal(10, codes.Count);
        return (manualEntryKey, codes);
    }

    [Fact]
    public async Task SignInWithAndWithoutFactor_RestrictedSessionCannotReachApis()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var account = await world.SeedUserAsync();

        // 1. Without factor: logs in directly to full session
        var (browser1, secondFactorRequired1) = await SignInAsync(world, account);
        Assert.False(secondFactorRequired1);
        using (var me = await browser1.GetAsync("/api/me"))
        {
            Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        }

        // 2. Enrol factor
        var (manualKey, _) = await EnrolTotpAsync(world, browser1, account);
        world.Clock.Advance(TimeSpan.FromSeconds(Totp.StepSeconds));

        // 3. With factor enrolled: password alone yields restricted session
        var (browser2, secondFactorRequired2) = await SignInAsync(world, account);
        Assert.True(secondFactorRequired2);

        // Restricted session cannot reach APIs
        using (var me = await browser2.GetAsync("/api/me"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, me.StatusCode);
        }
        using (var sessions = await browser2.GetAsync("/api/auth/sessions"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, sessions.StatusCode);
        }

        // Complete second factor with TOTP code
        var secretBytes = Totp.DecodeBase32(manualKey);
        var code = Totp.ComputeCode(secretBytes, Totp.CurrentStep(world.Clock.GetUtcNow()));
        using (var verify = await browser2.SendAsync(HttpMethod.Post, "/api/auth/second-factor/totp", new { code }))
        {
            Assert.Equal(HttpStatusCode.NoContent, verify.StatusCode);
        }

        // Now session is fully authenticated and can reach APIs
        using (var me = await browser2.GetAsync("/api/me"))
        {
            Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        }
    }

    [Fact]
    public async Task CodeReuseAndClockSkewBoundaries()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var account = await world.SeedUserAsync();

        var (initialBrowser, _) = await SignInAsync(world, account);
        var (manualKey, _) = await EnrolTotpAsync(world, initialBrowser, account);
        world.Clock.Advance(TimeSpan.FromSeconds(Totp.StepSeconds));
        var secretBytes = Totp.DecodeBase32(manualKey);

        // Create two restricted sessions
        var (session1, _) = await SignInAsync(world, account);
        var (session2, _) = await SignInAsync(world, account);

        var currentStep = Totp.CurrentStep(world.Clock.GetUtcNow());
        var code = Totp.ComputeCode(secretBytes, currentStep);

        // Session 1 uses the code -> succeeds
        using (var v1 = await session1.SendAsync(HttpMethod.Post, "/api/auth/second-factor/totp", new { code }))
        {
            Assert.Equal(HttpStatusCode.NoContent, v1.StatusCode);
        }

        // Session 2 attempts to reuse the SAME code in the same window -> rejected!
        using (var v2 = await session2.SendAsync(HttpMethod.Post, "/api/auth/second-factor/totp", new { code }))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, v2.StatusCode);
        }

        // Clock skew: code from T-1 never used before is accepted at step T (boundary -1)
        var (session3, _) = await SignInAsync(world, account);
        world.Clock.Advance(TimeSpan.FromSeconds(Totp.StepSeconds * 5));
        var skewStep = Totp.CurrentStep(world.Clock.GetUtcNow());
        var validSkewCode = Totp.ComputeCode(secretBytes, skewStep);
        // Advance by 1 step (30s) so skewStep is in the past (T-1)
        world.Clock.Advance(TimeSpan.FromSeconds(Totp.StepSeconds));
        using (var v3 = await session3.SendAsync(HttpMethod.Post, "/api/auth/second-factor/totp", new { code = validSkewCode }))
        {
            Assert.Equal(HttpStatusCode.NoContent, v3.StatusCode);
        }

        // Out of window: code from 3 steps ago rejected
        var (session4, _) = await SignInAsync(world, account);
        world.Clock.Advance(TimeSpan.FromSeconds(Totp.StepSeconds * 5));
        var oldStep = Totp.CurrentStep(world.Clock.GetUtcNow());
        var expiredCode = Totp.ComputeCode(secretBytes, oldStep);
        world.Clock.Advance(TimeSpan.FromSeconds(Totp.StepSeconds * 3));
        using (var v4 = await session4.SendAsync(HttpMethod.Post, "/api/auth/second-factor/totp", new { code = expiredCode }))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, v4.StatusCode);
        }
    }

    [Fact]
    public async Task RecoveryCodeSingleUseAndConcurrentRacingUse()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var account = await world.SeedUserAsync();

        var (initialBrowser, _) = await SignInAsync(world, account);
        var (_, recoveryCodes) = await EnrolTotpAsync(world, initialBrowser, account);

        // Use recovery code 0 once -> succeeds
        var (session1, _) = await SignInAsync(world, account);
        using (var r1 = await session1.SendAsync(HttpMethod.Post, "/api/auth/second-factor/recovery-code", new { recoveryCode = recoveryCodes[0] }))
        {
            Assert.Equal(HttpStatusCode.NoContent, r1.StatusCode);
        }
        using (var me = await session1.GetAsync("/api/me"))
        {
            Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        }

        // Second use of recovery code 0 -> rejected!
        var (session2, _) = await SignInAsync(world, account);
        using (var r2 = await session2.SendAsync(HttpMethod.Post, "/api/auth/second-factor/recovery-code", new { recoveryCode = recoveryCodes[0] }))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, r2.StatusCode);
        }

        // Concurrent racing use of recovery code 1
        var (race1, _) = await SignInAsync(world, account);
        var (race2, _) = await SignInAsync(world, account);

        var task1 = race1.SendAsync(HttpMethod.Post, "/api/auth/second-factor/recovery-code", new { recoveryCode = recoveryCodes[1] });
        var task2 = race2.SendAsync(HttpMethod.Post, "/api/auth/second-factor/recovery-code", new { recoveryCode = recoveryCodes[1] });

        var responses = await Task.WhenAll(task1, task2);
        var statuses = responses.Select(r => r.StatusCode).ToList();

        // Exactly one succeeded (204) and one failed (401)
        Assert.Contains(HttpStatusCode.NoContent, statuses);
        Assert.Contains(HttpStatusCode.Unauthorized, statuses);
    }

    [Fact]
    public async Task RegeneratingRecoveryCodesInvalidatesOldCodes()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var account = await world.SeedUserAsync();

        var (browser, _) = await SignInAsync(world, account);
        var (_, initialCodes) = await EnrolTotpAsync(world, browser, account);

        // Regenerate codes
        using var regenResp = await browser.SendAsync(HttpMethod.Post, "/api/auth/security/recovery-codes/regenerate", new { currentPassword = account.Password });
        Assert.Equal(HttpStatusCode.OK, regenResp.StatusCode);
        var regenJson = AgentWorld.ReadJson(regenResp);
        var newCodes = regenJson.GetProperty("recoveryCodes").EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.Equal(10, newCodes.Count);

        // Old code fails
        var (session1, _) = await SignInAsync(world, account);
        using (var oldCheck = await session1.SendAsync(HttpMethod.Post, "/api/auth/second-factor/recovery-code", new { recoveryCode = initialCodes[0] }))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, oldCheck.StatusCode);
        }

        // New code succeeds
        var (session2, _) = await SignInAsync(world, account);
        using (var newCheck = await session2.SendAsync(HttpMethod.Post, "/api/auth/second-factor/recovery-code", new { recoveryCode = newCodes[0] }))
        {
            Assert.Equal(HttpStatusCode.NoContent, newCheck.StatusCode);
        }
    }

    [Fact]
    public async Task RateLimitingOnTotpAndRecoveryAttempts()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var account = await world.SeedUserAsync();

        var (browser, _) = await SignInAsync(world, account);
        var (manualKey, _) = await EnrolTotpAsync(world, browser, account);

        // TOTP rate limiting
        var (sessionTotp, _) = await SignInAsync(world, account);
        for (var i = 0; i < LoginThrottle.MaxFailures; i++)
        {
            using var fail = await sessionTotp.SendAsync(HttpMethod.Post, "/api/auth/second-factor/totp", new { code = "000000" });
            Assert.Equal(HttpStatusCode.Unauthorized, fail.StatusCode);
        }

        // 6th attempt is throttled
        using (var locked = await sessionTotp.SendAsync(HttpMethod.Post, "/api/auth/second-factor/totp", new { code = "000000" }))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
            Assert.NotNull(locked.Headers.RetryAfter);
        }

        // Advance past throttle window
        world.Clock.Advance(TimeSpan.FromSeconds(Totp.StepSeconds * 2));

        var secretBytes = Totp.DecodeBase32(manualKey);
        var validCode = Totp.ComputeCode(secretBytes, Totp.CurrentStep(world.Clock.GetUtcNow()));
        using (var ok = await sessionTotp.SendAsync(HttpMethod.Post, "/api/auth/second-factor/totp", new { code = validCode }))
        {
            Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
        }

        // Recovery code rate limiting
        var (sessionRecovery, _) = await SignInAsync(world, account);
        for (var i = 0; i < LoginThrottle.MaxFailures; i++)
        {
            using var fail = await sessionRecovery.SendAsync(HttpMethod.Post, "/api/auth/second-factor/recovery-code", new { recoveryCode = "WRONG-CODE-0000" });
            Assert.Equal(HttpStatusCode.Unauthorized, fail.StatusCode);
        }

        using (var locked = await sessionRecovery.SendAsync(HttpMethod.Post, "/api/auth/second-factor/recovery-code", new { recoveryCode = "WRONG-CODE-0000" }))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        }
    }

    [Fact]
    public async Task EnrolmentAndRemovalWithoutSecurityVerificationRefused()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var account = await world.SeedUserAsync();

        var (browser, _) = await SignInAsync(world, account);

        // 1. Enrol with wrong password -> refused
        using (var setup = await browser.SendAsync(HttpMethod.Post, "/api/auth/security/totp/setup"))
        {
            Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
            var setupJson = AgentWorld.ReadJson(setup);
            var setupToken = setupJson.GetProperty("setupToken").GetString()!;
            var manualKey = setupJson.GetProperty("manualEntryKey").GetString()!;
            var code = Totp.ComputeCode(Totp.DecodeBase32(manualKey), Totp.CurrentStep(world.Clock.GetUtcNow()));

            using var badPw = await browser.SendAsync(HttpMethod.Post, "/api/auth/security/totp/enroll", new
            {
                setupToken,
                code,
                currentPassword = "Wrong-password!"
            });
            Assert.Equal(HttpStatusCode.BadRequest, badPw.StatusCode);
        }

        // Enrol properly
        await EnrolTotpAsync(world, browser, account);

        // 2. Removal without password and without valid code -> refused
        using (var badRemove = await browser.SendAsync(HttpMethod.Post, "/api/auth/security/totp/remove", new
        {
            currentPassword = "Wrong-password!",
            code = "000000"
        }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, badRemove.StatusCode);
        }

        // Removal with valid password -> succeeded
        using (var goodRemove = await browser.SendAsync(HttpMethod.Post, "/api/auth/security/totp/remove", new
        {
            currentPassword = account.Password
        }))
        {
            Assert.Equal(HttpStatusCode.NoContent, goodRemove.StatusCode);
        }

        // User can now sign in without second factor
        var (afterRemove, secondFactorRequired) = await SignInAsync(world, account);
        Assert.False(secondFactorRequired);
    }

    [Fact]
    public async Task AuditEventsRecordedWithoutPlaintextSecretsOrCodes()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var account = await world.SeedUserAsync();

        var (browser, _) = await SignInAsync(world, account);
        var (secret, codes) = await EnrolTotpAsync(world, browser, account);

        // Regenerate codes
        await browser.SendAsync(HttpMethod.Post, "/api/auth/security/recovery-codes/regenerate", new { currentPassword = account.Password });

        // Consume one recovery code
        var (session, _) = await SignInAsync(world, account);
        await session.SendAsync(HttpMethod.Post, "/api/auth/second-factor/recovery-code", new { recoveryCode = codes[0] });

        // Remove factor
        await browser.SendAsync(HttpMethod.Post, "/api/auth/security/totp/remove", new { currentPassword = account.Password });
        await using var db = fixture.CreatePlatformContext();
        var audits = await db.AuditEvents
            .Where(a => a.ActorUserId == account.UserId)
            .OrderBy(a => a.At)
            .ToListAsync();

        var actions = audits.Select(a => a.Action).ToList();
        Assert.Contains(AuditActions.TotpEnrolled, actions);
        Assert.Contains(AuditActions.TotpRemoved, actions);
        Assert.Contains(AuditActions.RecoveryCodesRegenerated, actions);

        // Verify NO secret or plain recovery code appears in audit details or targets
        foreach (var audit in audits)
        {
            var rawJson = audit.DetailsJson ?? "";
            Assert.DoesNotContain(secret, rawJson, StringComparison.OrdinalIgnoreCase);
            foreach (var code in codes)
            {
                Assert.DoesNotContain(code, rawJson, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
