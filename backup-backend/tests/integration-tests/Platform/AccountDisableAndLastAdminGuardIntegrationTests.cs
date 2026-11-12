using System.Net;
using System.Net.Http.Json;
using BackupPlatform.Application.Identity;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.IntegrationTests;

public sealed class AccountDisableAndLastAdminGuardIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task DisableUser_RevokesSessionsImmediately_BlocksLoginAndApiRequests()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var adminAccount = await world.SeedUserAsync(platformAdmin: true);
        var admin = await world.SignInAsync(adminAccount);

        var targetAccount = await world.SeedUserAsync(platformAdmin: true);
        var targetSession = await world.SignInAsync(targetAccount);

        // Active session can call API
        using var before = await targetSession.GetAsync("/api/platform/summary");
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        // PlatformAdmin disables target user
        using var disableResp = await admin.SendAsync(HttpMethod.Post, $"/api/platform/users/{targetAccount.UserId.Value}/disable", new
        {
            reason = "Security investigation"
        });
        Assert.Equal(HttpStatusCode.OK, disableResp.StatusCode);

        // Existing session is rejected on its very next request
        using var after = await targetSession.GetAsync("/api/platform/summary");
        Assert.True(after.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);

        // New login attempts are refused
        var freshSession = new BrowserSession(world.Factory.CreateBrowser());
        using var loginResp = await freshSession.SendAsync(HttpMethod.Post, "/api/auth/login", new
        {
            loginIdentifier = targetAccount.Identifier,
            password = targetAccount.Password
        });
        Assert.Equal(HttpStatusCode.Unauthorized, loginResp.StatusCode);

        // Enabling restores ability to log in
        using var enableResp = await admin.SendAsync(HttpMethod.Post, $"/api/platform/users/{targetAccount.UserId.Value}/enable", new { });
        Assert.Equal(HttpStatusCode.OK, enableResp.StatusCode);

        var reenabledSession = await world.SignInAsync(targetAccount);
        using var reenabledSummary = await reenabledSession.GetAsync("/api/platform/summary");
        Assert.Equal(HttpStatusCode.OK, reenabledSummary.StatusCode);
    }

    [Fact]
    public async Task LastMembershipRemoval_AutoDisablesUserAdmin_ReAddingReenablesOnlyIfNoManualDisable()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var adminAccount = await world.SeedUserAsync(platformAdmin: true);
        var admin = await world.SignInAsync(adminAccount);

        var tenant1 = await world.AddTenantAsync();
        var tenant2 = await world.AddTenantAsync();

        var userAccount = await world.SeedUserAsync(platformAdmin: false);
        await world.GrantAsync(tenant1, userAccount);
        await world.GrantAsync(tenant2, userAccount);

        var userSession = await world.SignInAsync(userAccount);

        // Removing 1 of 2 memberships: account remains enabled
        using var remove1 = await admin.SendAsync(HttpMethod.Delete, $"/api/platform/tenants/{tenant1.Id.Value}/memberships/{userAccount.UserId.Value}");
        Assert.Equal(HttpStatusCode.NoContent, remove1.StatusCode);
        await using (var context = fixture.CreatePlatformContext())
        {
            var u = await context.Users.SingleAsync(x => x.Id == userAccount.UserId);
            Assert.False(u.IsNoMembershipDisabled);
            Assert.False(u.IsDisabled);
        }

        // Removing the last membership: account is automatically disabled with NoMembership reason
        using var remove2 = await admin.SendAsync(HttpMethod.Delete, $"/api/platform/tenants/{tenant2.Id.Value}/memberships/{userAccount.UserId.Value}");
        Assert.Equal(HttpStatusCode.NoContent, remove2.StatusCode);
        await using (var context = fixture.CreatePlatformContext())
        {
            var u = await context.Users.SingleAsync(x => x.Id == userAccount.UserId);
            Assert.True(u.IsNoMembershipDisabled);
            Assert.True(u.IsDisabled);
        }

        // User sessions ended
        using var checkAccess = await userSession.GetAsync("/api/me");
        Assert.True(checkAccess.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);

        // Case A: Re-adding membership re-enables account when no manual disable is set
        using var addBack = await admin.SendAsync(HttpMethod.Post, $"/api/platform/tenants/{tenant1.Id.Value}/memberships", new
        {
            userId = userAccount.UserId.Value
        });
        Assert.Equal(HttpStatusCode.Created, addBack.StatusCode);
        await using (var context = fixture.CreatePlatformContext())
        {
            var u = await context.Users.SingleAsync(x => x.Id == userAccount.UserId);
            Assert.False(u.IsNoMembershipDisabled);
            Assert.False(u.IsDisabled);
        }

        // Case B: Now manually disable the user, remove last membership, re-add -> remains disabled because manual disable is still in force
        using var manualDisable = await admin.SendAsync(HttpMethod.Post, $"/api/platform/users/{userAccount.UserId.Value}/disable", new
        {
            reason = "Manual hold"
        });
        Assert.Equal(HttpStatusCode.OK, manualDisable.StatusCode);

        using var removeAgain = await admin.SendAsync(HttpMethod.Delete, $"/api/platform/tenants/{tenant1.Id.Value}/memberships/{userAccount.UserId.Value}");
        Assert.Equal(HttpStatusCode.NoContent, removeAgain.StatusCode);
        using var addAgain = await admin.SendAsync(HttpMethod.Post, $"/api/platform/tenants/{tenant1.Id.Value}/memberships", new
        {
            userId = userAccount.UserId.Value
        });
        Assert.Equal(HttpStatusCode.Created, addAgain.StatusCode);
        await using (var context = fixture.CreatePlatformContext())
        {
            var u = await context.Users.SingleAsync(x => x.Id == userAccount.UserId);
            Assert.False(u.IsNoMembershipDisabled); // cleared by add membership
            Assert.True(u.IsManuallyDisabled);     // still in force!
            Assert.True(u.IsDisabled);             // overall disabled
        }
    }

    [Fact]
    public async Task LastPlatformAdminGuard_DisablingSoleAdminRefusedWithConflict()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var soleAdmin = await world.SeedUserAsync(platformAdmin: true);
        var adminSession = await world.SignInAsync(soleAdmin);

        // Only 1 PlatformAdmin in the system -> disable must return 409 Conflict
        using var resp = await adminSession.SendAsync(HttpMethod.Post, $"/api/platform/users/{soleAdmin.UserId.Value}/disable", new
        {
            reason = "Try disable only admin"
        });
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Equal("last_platform_admin", AgentWorld.ReadJson(resp).GetProperty("error").GetString());
    }

    [Fact]
    public async Task LastPlatformAdminGuard_ConcurrentDisableRequests_LeaveAtLeastOneActive()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);

        // Ensure clean state: disable any existing platform admins so only our two new ones will be active
        await using (var context = fixture.CreatePlatformContext())
        {
            await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE users SET \"IsManuallyDisabled\" = true WHERE \"Role\" = {(int)UserRole.PlatformAdmin}");
        }
        var adminA = await world.SeedUserAsync(platformAdmin: true);
        var adminB = await world.SeedUserAsync(platformAdmin: true);

        var sessionA = await world.SignInAsync(adminA);
        var sessionB = await world.SignInAsync(adminB);

        var taskA = sessionA.SendAsync(HttpMethod.Post, $"/api/platform/users/{adminB.UserId.Value}/disable", new { reason = "Concurrent A->B" });
        var taskB = sessionB.SendAsync(HttpMethod.Post, $"/api/platform/users/{adminA.UserId.Value}/disable", new { reason = "Concurrent B->A" });
        var responses = await Task.WhenAll(taskA, taskB);
        var statuses = responses.Select(r => r.StatusCode).ToList();
        Assert.Contains(HttpStatusCode.OK, statuses);
        Assert.Contains(HttpStatusCode.Conflict, statuses);

        // Verify in DB that at least one PlatformAdmin is active
        await using var verifyContext = fixture.CreatePlatformContext();
        var activeAdmins = await verifyContext.Users.CountAsync(
            u => u.Role == UserRole.PlatformAdmin && !u.IsManuallyDisabled && !u.IsSecurityDisabled && !u.IsNoMembershipDisabled);
    }
}
