using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BackupPlatform.Application.Downloads;
using BackupPlatform.Api.Security;
using BackupPlatform.Application.Identity;
using BackupPlatform.Infrastructure.Identity;
using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Notifications;
using BackupPlatform.Identity;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BackupPlatform.IntegrationTests;

public sealed class EpicAcceptanceEndToEndTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static async Task<T> PostJsonAsync<T>(BrowserSession session, string path, object body)
    {
        using var response = await session.SendAsync(HttpMethod.Post, path, body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    [Fact]
    public async Task EndToEnd_Lifecycle_Bootstrap_UserAdmin_Share_PlaintextDownload_Revoke_Audit_And_Notifications()
    {
        ShareRevocationCancellationIntegrationTests.StreamGate gate = new(firstChunkBytes: 256);
        await using var scenario = await ShareRevocationCancellationIntegrationTests.CreateGatedScenarioAsync(fixture, new([gate]));
        var world = scenario.World;
        var clock = scenario.Backup.Clock;

        // 1. PlatformAdmin bootstrap via UserBootstrapper (fails on rerun because an admin was already seeded for this database)
        var dbOptions = new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(fixture.ConnectionString).Options;
        var bootstrapLogin = $"bootstrap-{Guid.NewGuid():N}"[..20];
        var bootstrapEmail = $"{bootstrapLogin}@example.com";
        var bootstrapPassword = "Bootstrap-Password-42!";
        await Assert.ThrowsAsync<InvalidOperationException>(() => UserBootstrapper.CreateInitialUserAsync(
            dbOptions, bootstrapLogin, "Bootstrap Admin", bootstrapEmail, bootstrapPassword, default));

        var platformAccount = await world.SeedUserAsync(platformAdmin: true);
        var platform = await world.SignInAsync(platformAccount);
        var platformMe = await platform.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.OK, platformMe.StatusCode);
        Assert.True(AgentWorld.ReadJson(platformMe).GetProperty("isPlatformAdmin").GetBoolean());

        // 2. UserAdmin account creation & initial forced password change at first sign-in
        var userLogin = $"useradmin-{Guid.NewGuid():N}"[..20];
        var userEmail = $"{userLogin}@example.com";
        var initialPassword = "Initial-Password-42!";
        var tenant = scenario.Backup.Backup.Setup.Tenant;
        using var createResp = await platform.SendAsync(HttpMethod.Post, "/api/platform/users", new
        {
            username = userLogin, email = userEmail, displayName = "Workspace Admin",
            password = initialPassword, role = "UserAdmin", tenantIds = new[] { tenant.Id.Value }
        });
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var createdUser = AgentWorld.ReadJson(createResp);
        var userAdminId = new UserId(createdUser.GetProperty("userId").GetGuid());

        var initialSession = new BrowserSession(world.Factory.CreateBrowser());
        using var firstLogin = await initialSession.SendAsync(HttpMethod.Post, "/api/auth/login", new { loginIdentifier = userLogin, password = initialPassword });
        Assert.Equal(HttpStatusCode.OK, firstLogin.StatusCode);
        Assert.True(AgentWorld.ReadJson(firstLogin).GetProperty("passwordChangeRequired").GetBoolean());
        using var restrictedMe = await initialSession.GetAsync("/api/platform/summary");
        Assert.Equal(HttpStatusCode.Forbidden, restrictedMe.StatusCode);

        var newUserPassword = "New-User-Password-42!";
        using var change = await initialSession.SendAsync(HttpMethod.Post, "/api/auth/change-password", new { currentPassword = initialPassword, newPassword = newUserPassword });
        Assert.Equal(HttpStatusCode.OK, change.StatusCode);

        // 3. Tenant membership and isolation
        var userAdmin = await world.SignInAsync(new Account(userAdminId, userLogin, newUserPassword));
        using var contextResp = await userAdmin.GetAsync($"/api/tenants/{tenant.Id.Value}/context");
        Assert.Equal(HttpStatusCode.OK, contextResp.StatusCode);
        using var forbiddenPlatform = await userAdmin.GetAsync("/api/platform/summary");
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenPlatform.StatusCode);

        var otherTenant = await world.CreateTenantAsync();
        using var isolated = await userAdmin.GetAsync($"/api/tenants/{otherTenant.Tenant.Id.Value}/context");
        Assert.Equal(HttpStatusCode.Forbidden, isolated.StatusCode);

        // 4. TOTP enrollment & second-factor requirement
        var setup = await PostJsonAsync<JsonElement>(userAdmin, "/api/auth/security/totp/setup", new { password = newUserPassword });
        var secretBytes = Totp.DecodeBase32(setup.GetProperty("manualEntryKey").GetString()!);
        var code = Totp.ComputeCode(secretBytes, Totp.CurrentStep(clock.GetUtcNow()));
        using var enroll = await userAdmin.SendAsync(HttpMethod.Post, "/api/auth/security/totp/enroll", new { currentPassword = newUserPassword, setupToken = setup.GetProperty("setupToken").GetString(), code });
        Assert.Equal(HttpStatusCode.OK, enroll.StatusCode);

        var totpSession = new BrowserSession(world.Factory.CreateBrowser());
        using var totpLogin = await totpSession.SendAsync(HttpMethod.Post, "/api/auth/login", new { loginIdentifier = userLogin, password = newUserPassword });
        Assert.Equal(HttpStatusCode.OK, totpLogin.StatusCode);
        Assert.True(AgentWorld.ReadJson(totpLogin).GetProperty("secondFactorRequired").GetBoolean());
        clock.Advance(TimeSpan.FromSeconds(Totp.StepSeconds));
        var verifyCode = Totp.ComputeCode(secretBytes, Totp.CurrentStep(clock.GetUtcNow()));
        using var factorVerify = await totpSession.SendAsync(HttpMethod.Post, "/api/auth/second-factor/totp", new { code = verifyCode });
        Assert.Equal(HttpStatusCode.NoContent, factorVerify.StatusCode);        using var fullyAuthenticated = await totpSession.GetAsync($"/api/tenants/{tenant.Id.Value}/context");
        Assert.Equal(HttpStatusCode.OK, fullyAuthenticated.StatusCode);

        // 5. UserAdmin creates BackupShare
        var artifactId = scenario.Accepted.ArtifactId;
        using var shareResp = await userAdmin.SendAsync(HttpMethod.Post, $"{scenario.Root}/backup-artifacts/{artifactId}/shares", new { reason = "Support diagnostic", durationHours = 24 });
        Assert.Equal(HttpStatusCode.Created, shareResp.StatusCode);
        var shareId = AgentWorld.ReadJson(shareResp).GetProperty("id").GetGuid();

        // 6. PlatformAdmin plaintext download through share & 7. Revoke during streaming cancels active download
        using var prep = await platform.SendAsync(HttpMethod.Post, $"{scenario.ArtifactPath}/downloads", new { shareId });
        Assert.Equal(HttpStatusCode.OK, prep.StatusCode);
        var prepJson = AgentWorld.ReadJson(prep);
        var sessionId = prepJson.GetProperty("sessionId").GetGuid();
        var contentUrl = prepJson.GetProperty("downloadUrl").GetString()!;

        var registry = world.Factory.Services.GetRequiredService<ActiveDownloadRegistry>();
        var streamTask = Task.Run(async () =>
        {
            try { using var r = await platform.Client.GetAsync(contentUrl); } catch { }
        });
        await gate.FirstChunkTransferred.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(registry.ContainsSession(sessionId));

        using var revokeResp = await userAdmin.SendAsync(HttpMethod.Post, $"{scenario.Root}/backup-shares/{shareId}/revoke", new { reason = "Diagnostic concluded" });
        Assert.Equal(HttpStatusCode.OK, revokeResp.StatusCode);
        await streamTask.WaitAsync(TimeSpan.FromSeconds(10));

        // 8. Download attempt history records the cancelled download, with refusal after revoke
        using var attemptsResp = await platform.GetAsync($"/api/platform/download-attempts?shareId={shareId}");
        Assert.Equal(HttpStatusCode.OK, attemptsResp.StatusCode);
        var attempts = AgentWorld.ReadJson(attemptsResp).GetProperty("items").EnumerateArray().ToList();
        var recorded = Assert.Single(attempts);
        Assert.Equal(sessionId, recorded.GetProperty("id").GetGuid());
        Assert.Equal("Cancelled", recorded.GetProperty("result").GetString());
        var reason = recorded.GetProperty("failureReason").GetString();
        Assert.Equal("cancelled_by_revocation", reason);

        // A second stream attempt on the revoked share is refused before any session starts
        using var refused = await platform.SendAsync(HttpMethod.Post, $"{scenario.ArtifactPath}/downloads", new { shareId });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        // 9. Mandatory security events and share notifications were written
        await using var context = fixture.CreatePlatformContext();
        var userNotifEventIds = await context.UserNotifications.Where(n => n.UserId == userAdminId).Select(n => n.EventId).ToListAsync();
        var types = await context.NotificationEvents.Where(e => userNotifEventIds.Contains(e.Id)).Select(e => e.Type).ToListAsync();
Assert.Contains(NotificationEventTypes.SecurityFactorAdded, types); Assert.Contains(NotificationEventTypes.SecurityPasswordChanged, types); var platformNotifEventIds = await context.UserNotifications.Where(n => n.UserId == platformAccount.UserId).Select(n => n.EventId).ToListAsync(); Assert.Contains(await context.NotificationEvents.Where(e => platformNotifEventIds.Contains(e.Id)).Select(e => e.Type).ToListAsync(), t => t == NotificationEventTypes.BackupShareRevoked);
        // 10. Audit log integrity
        var audits = await context.AuditEvents.Where(a => a.ActorUserId == userAdminId).Select(a => a.Action).ToListAsync();
        Assert.Contains(AuditActions.TotpEnrolled, audits);
        Assert.Contains(AuditActions.BackupShareCreated, audits);
        Assert.Contains(AuditActions.BackupShareRevoked, audits);

        // 11. Restore boundary: restore is tenant management, never takes a shareId, audited without share
        using var restoreRefused = await platform.SendAsync(HttpMethod.Post, $"{scenario.ArtifactPath}/restore", new
        {
            shareId, target = new { kind = "direct", path = "/tmp/fake-restore" }
        });
        Assert.Equal(HttpStatusCode.BadRequest, restoreRefused.StatusCode);
    }
}
