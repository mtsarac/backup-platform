using System.Net;
using System.Net.Http.Json;
using BackupPlatform.Application.Identity;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Identity;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.IntegrationTests;

public sealed class PasswordPolicyAndForcedChangeIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    [Theory]
    [InlineData("short123456", false, "password_too_short")] // 11 characters
    [InlineData("validlowercase123", true, null)] // 17 characters, lowercase + numbers only (no composition rules)
    [InlineData("password1234", false, "password_common_or_breached")] // 12 characters, but common/breached
    [InlineData("qwertyuiop12", false, "password_common_or_breached")] // 12 characters, common keyboard sequence
    [InlineData("backupplatform", false, "password_common_or_breached")] // predictable
    [InlineData("correct-horse-battery-staple-2026", true, null)] // valid passphrase
    public void PasswordPolicy_BoundaryAndBreachedListValidation(string password, bool expectedValid, string? expectedReason)
    {
        var valid = PasswordPolicy.Validate(password, out var reason);
        Assert.Equal(expectedValid, valid);
        Assert.Equal(expectedReason, reason);
    }

    [Fact]
    public async Task InitialPassword_ForcesChangeAtFirstSignIn_RestrictedSessionCannotReachApis()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var adminAccount = await world.SeedUserAsync(platformAdmin: true);
        var admin = await world.SignInAsync(adminAccount);

        // 1. PlatformAdmin creates a new user with an initial password (sets MustChangePassword = true)
        var username = $"user_{Guid.NewGuid():N}"[..16];
        var initialPassword = "Initial-Password-123!";
        using var userResp = await admin.SendAsync(HttpMethod.Post, "/api/platform/users", new
        {
            username,
            email = $"{username}@example.com",
            displayName = "Forced Change User",
            password = initialPassword,
            role = "PlatformAdmin",
            tenantIds = Array.Empty<Guid>()
        });
        Assert.Equal(HttpStatusCode.Created, userResp.StatusCode);
        var createdUser = AgentWorld.ReadJson(userResp);
        var userId = createdUser.GetProperty("userId").GetGuid();

        // 2. User logs in with initial password -> gets passwordChangeRequired = true
        var userSession = new BrowserSession(world.Factory.CreateBrowser());
        using var loginResp = await userSession.SendAsync(HttpMethod.Post, "/api/auth/login", new
        {
            loginIdentifier = username,
            password = initialPassword
        });
        Assert.Equal(HttpStatusCode.OK, loginResp.StatusCode);
        var loginJson = AgentWorld.ReadJson(loginResp);
        Assert.True(loginJson.GetProperty("passwordChangeRequired").GetBoolean());

        // 3. Restricted session CAN reach /api/me to learn state, but passwordChangeRequired is true
        using var meResp = await userSession.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.OK, meResp.StatusCode);
        var meJson = AgentWorld.ReadJson(meResp);
        Assert.True(meJson.GetProperty("passwordChangeRequired").GetBoolean());

        // 4. Restricted session is FORBIDDEN on tenant and platform endpoints
        using var platformResp = await userSession.GetAsync("/api/platform/summary");
        Assert.Equal(HttpStatusCode.Forbidden, platformResp.StatusCode);

        using var sessionsResp = await userSession.GetAsync("/api/auth/sessions");
        Assert.Equal(HttpStatusCode.Forbidden, sessionsResp.StatusCode);

        // 5. Attempting to change password to the same initial password fails
        using var samePasswordResp = await userSession.SendAsync(HttpMethod.Post, "/api/auth/change-password", new
        {
            currentPassword = initialPassword,
            newPassword = initialPassword
        });
        Assert.Equal(HttpStatusCode.BadRequest, samePasswordResp.StatusCode);
        Assert.Equal("password_same_as_current", AgentWorld.ReadJson(samePasswordResp).GetProperty("error").GetString());

        // 6. Attempting to change password to a breached/short password fails
        using var breachedResp = await userSession.SendAsync(HttpMethod.Post, "/api/auth/change-password", new
        {
            currentPassword = initialPassword,
            newPassword = "password1234"
        });
        Assert.Equal(HttpStatusCode.BadRequest, breachedResp.StatusCode);
        Assert.Equal("password_common_or_breached", AgentWorld.ReadJson(breachedResp).GetProperty("error").GetString());

        // 7. Successful password change promotes session and enables access
        var newPassword = "New-Secure-Password-2026!";
        using var changeResp = await userSession.SendAsync(HttpMethod.Post, "/api/auth/change-password", new
        {
            currentPassword = initialPassword,
            newPassword = newPassword
        });
        Assert.Equal(HttpStatusCode.OK, changeResp.StatusCode);

        // 8. Now session CAN reach platform APIs
        using var promotedSummary = await userSession.GetAsync("/api/platform/summary");
        Assert.Equal(HttpStatusCode.OK, promotedSummary.StatusCode);

        // 9. Verify password change was audited without logging password
        await using var context = fixture.CreatePlatformContext();
        var audit = await context.AuditEvents
            .Where(e => e.Action == "user.password_changed" && e.ActorUserId == new UserId(userId))
            .FirstOrDefaultAsync();
        Assert.NotNull(audit);
        Assert.DoesNotContain(initialPassword, audit.DetailsJson);
        Assert.DoesNotContain(newPassword, audit.DetailsJson);
    }

    [Fact]
    public async Task TemporaryPassword_RevokesActiveSessions_ForcesChangeAtNextSignIn_Audited()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var adminAccount = await world.SeedUserAsync(platformAdmin: true);
        var admin = await world.SignInAsync(adminAccount);

        // 1. Create a platform user and establish an active session
        var targetAccount = await world.SeedUserAsync(platformAdmin: true);
        var targetSession = await world.SignInAsync(targetAccount);

        using var initialMe = await targetSession.GetAsync("/api/platform/summary");
        Assert.Equal(HttpStatusCode.OK, initialMe.StatusCode);

        // 2. PlatformAdmin assigns a temporary password
        var tempPassword = "Temporary-Password-456!";
        using var tempResp = await admin.SendAsync(HttpMethod.Post, $"/api/platform/users/{targetAccount.UserId.Value}/temporary-password", new
        {
            temporaryPassword = tempPassword
        });
        Assert.Equal(HttpStatusCode.OK, tempResp.StatusCode);

        // 3. Target user's existing session is IMMEDIATELY revoked (cannot reach APIs anymore)
        using var revokedReq = await targetSession.GetAsync("/api/platform/summary");
        Assert.True(revokedReq.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);

        // 4. Target user logs in with the temporary password -> forced change
        var newSession = new BrowserSession(world.Factory.CreateBrowser());
        using var loginResp = await newSession.SendAsync(HttpMethod.Post, "/api/auth/login", new
        {
            loginIdentifier = targetAccount.Identifier,
            password = tempPassword
        });
        Assert.Equal(HttpStatusCode.OK, loginResp.StatusCode);
        var loginJson = AgentWorld.ReadJson(loginResp);
        Assert.True(loginJson.GetProperty("passwordChangeRequired").GetBoolean());

        // 5. Verify temporary password assignment audit was written with acting PlatformAdmin and without password
        await using var context = fixture.CreatePlatformContext();
        var tempAudit = await context.AuditEvents
            .Where(e => e.Action == "user.temporary_password_assigned" && e.ActorUserId == adminAccount.UserId)
            .FirstOrDefaultAsync();
        Assert.NotNull(tempAudit);
        Assert.Equal(AuditActorKind.PlatformAdmin, tempAudit.ActorKind);
        Assert.DoesNotContain(tempPassword, tempAudit.DetailsJson);
    }
}
