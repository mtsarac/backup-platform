using System.Net;
using System.Text.Json;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Notifications;
using BackupPlatform.IntegrationTests.Notifications;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.IntegrationTests;

public sealed class ArtifactBreakGlassApiTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private const string Root = "/api/platform/artifact-break-glass";

    private static async Task<JsonElement> OpenAsync(DownloadApiScenario scenario, BrowserSession requester, int ttlMinutes = 60)
    {
        using var response = await requester.SendAsync(HttpMethod.Post, Root, new
        {
            tenantId = scenario.Backup.TenantId, artifactId = scenario.Accepted.ArtifactId, reason = "Restore incident 42", ttlMinutes
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return AgentWorld.ReadJson(response);
    }

    [Fact]
    public async Task DualControlSingleUseRequesterBindingAuditAndTenantTransparencyAreEnforced()
    {
        await using var scenario = await DownloadApiScenario.CreateAsync(fixture);
        await scenario.EncryptAsync();
        var requesterAccount = await scenario.World.SeedUserAsync(platformAdmin: true);
        var requester = await scenario.World.SignInAsync(requesterAccount);
        var approverAccount = await scenario.World.SeedUserAsync(platformAdmin: true);
        var approver = await scenario.World.SignInAsync(approverAccount);
        var opened = await OpenAsync(scenario, requester);
        var id = opened.GetProperty("id").GetGuid();
        var path = Root + "/" + id;
        using var unapproved = await requester.SendAsync(HttpMethod.Post, path + "/download", new { });
        Assert.Equal(HttpStatusCode.Conflict, unapproved.StatusCode);
        Assert.Equal("request_not_approved", AgentWorld.ReadJson(unapproved).GetProperty("error").GetString());
        using var self = await requester.SendAsync(HttpMethod.Post, path + "/approve");
        Assert.Equal(HttpStatusCode.Forbidden, self.StatusCode);
        Assert.Equal("self_approval_forbidden", AgentWorld.ReadJson(self).GetProperty("error").GetString());
        using var approved = await approver.SendAsync(HttpMethod.Post, path + "/approve");
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.Equal("Approved", AgentWorld.ReadJson(approved).GetProperty("status").GetString());
        using var foreign = await approver.SendAsync(HttpMethod.Post, path + "/download", new { });
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        // Competing consumers serialize under the grant's row lock and can only create one hold.
        var attempts = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => requester.SendAsync(HttpMethod.Post, path + "/download", new { })));
        try
        {
            var prepared = AgentWorld.ReadJson(Assert.Single(attempts, response => response.StatusCode == HttpStatusCode.OK));
            Assert.Equal("request_consumed", AgentWorld.ReadJson(Assert.Single(attempts, response => response.StatusCode == HttpStatusCode.Conflict)).GetProperty("error").GetString());
            var session = prepared.GetProperty("sessionId").GetGuid();
            var url = prepared.GetProperty("downloadUrl").GetString()!;
            using var stolen = await approver.GetAsync(url);
            using var tenantChannel = await scenario.Admin.GetAsync(scenario.ArtifactPath + $"/downloads/{session}/content");
            Assert.Equal(HttpStatusCode.NotFound, stolen.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, tenantChannel.StatusCode);
            using var content = await requester.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, content.StatusCode);
            Assert.Equal(scenario.Bytes, await content.Content.ReadAsByteArrayAsync());
            using var replay = await requester.GetAsync(url);
            Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        }
        finally { foreach (var response in attempts) response.Dispose(); }
        using var tenant = await scenario.Admin.GetAsync(scenario.Root + "/artifact-break-glass");
        var visible = Assert.Single(AgentWorld.ReadJson(tenant).GetProperty("items").EnumerateArray());
        Assert.Equal((id, "Consumed"), (visible.GetProperty("id").GetGuid(), visible.GetProperty("status").GetString()));
        await using var context = fixture.CreatePlatformContext();
        var audit = await context.AuditEvents.AsNoTracking().Where(e => e.TenantId == scenario.Backup.Backup.Setup.Tenant.Id && e.Action.StartsWith("break_glass.")).ToListAsync();
        Assert.Contains(audit, e => e.Action == AuditActions.BreakGlassRequested && e.ActorUserId == requesterAccount.UserId);
        Assert.Contains(audit, e => e.Action == AuditActions.BreakGlassApproved && e.ActorUserId == approverAccount.UserId);
        Assert.Single(audit, e => e.Action == AuditActions.BreakGlassConsumed);
        Assert.Contains(audit, e => e.Action == AuditActions.BreakGlassDownloadCompleted);
        Assert.Contains(audit, e => e.Action == AuditActions.BreakGlassOwnerNotificationUnavailable && e.DetailsJson.Contains("no_deliverable_owner_channel"));
        Assert.DoesNotContain("PRIVATE KEY", string.Concat(audit.Select(e => e.DetailsJson)));
    }

    [Fact]
    public async Task ExpiryPreventsApprovalAndUseAndCannotBeExtendedByAStreamingLease()
    {
        await using var scenario = await DownloadApiScenario.CreateAsync(fixture);
        await scenario.EncryptAsync();
        var requester = await scenario.World.SignInAsync(await scenario.World.SeedUserAsync(platformAdmin: true));
        var approver = await scenario.World.SignInAsync(await scenario.World.SeedUserAsync(platformAdmin: true));
        var pending = await OpenAsync(scenario, requester, 1);
        var approved = await OpenAsync(scenario, requester, 1);
        var consumed = await OpenAsync(scenario, requester, 1);
        foreach (var request in new[] { approved, consumed })
        {
            using var approval = await approver.SendAsync(HttpMethod.Post, Root + "/" + request.GetProperty("id").GetGuid() + "/approve");
            Assert.Equal(HttpStatusCode.OK, approval.StatusCode);
        }
        using var prepared = await requester.SendAsync(HttpMethod.Post, Root + "/" + consumed.GetProperty("id").GetGuid() + "/download", new { });
        var download = AgentWorld.ReadJson(prepared);
        var session = (await scenario.Store().GetAsync(scenario.Backup.Backup.Setup.Tenant.Id, download.GetProperty("sessionId").GetGuid(), default))!;
        var expires = session.ExpiresAt;
        Assert.True(session.Transition(BackupPlatform.Domain.Downloads.DownloadState.Ready, null, BackupPlatform.Domain.Downloads.DownloadState.Streaming,
            "lease", scenario.Backup.Clock.GetUtcNow().AddSeconds(59), TimeSpan.FromMinutes(1), 0, null));
        Assert.Equal(expires, session.ExpiresAt);
        Assert.Equal(expires, session.LeaseExpiresAt);
        scenario.Backup.Clock.Advance(TimeSpan.FromMinutes(1));
        using var lateApproval = await approver.SendAsync(HttpMethod.Post, Root + "/" + pending.GetProperty("id").GetGuid() + "/approve");
        using var lateUse = await requester.SendAsync(HttpMethod.Post, Root + "/" + approved.GetProperty("id").GetGuid() + "/download", new { });
        using var lateContent = await requester.GetAsync(download.GetProperty("downloadUrl").GetString()!);
        Assert.All(new[] { lateApproval, lateUse, lateContent }, response => Assert.Equal(HttpStatusCode.Conflict, response.StatusCode));
        Assert.Equal("request_expired", AgentWorld.ReadJson(lateApproval).GetProperty("error").GetString());
        Assert.Equal("request_expired", AgentWorld.ReadJson(lateUse).GetProperty("error").GetString());
    }

    [Fact]
    public async Task ApprovalRaisesAnAlwaysSelectedTenantNoticeOnceWithoutLeakingTheReason()
    {
        await using var scenario = await DownloadApiScenario.CreateAsync(fixture);
        await using (var db = scenario.World.Database.CreatePlatformContext())
        {
            var m = await db.TenantMemberships.SingleAsync(row => row.TenantId == scenario.Backup.Backup.Setup.Tenant.Id && row.UserId == scenario.Backup.Backup.Setup.Account.UserId);
            m.ChangeRole(BackupPlatform.Domain.Tenancy.TenantRole.Owner);
            await db.SaveChangesAsync();
        }
        await scenario.EncryptAsync();
        var notifications = new NotificationWorld(fixture);
        var tenant = scenario.Backup.Backup.Setup.Tenant.Id;
        var ownerChannel = await notifications.AddEmailAsync(tenant, "owner@example.com", "Owner security");
        await notifications.AddEmailAsync(tenant, "ops@example.com", "General operations");
        var designation = scenario.Root + $"/notifications/channels/{ownerChannel.Id}/owner-security-recipient";
        using var designated = await scenario.Admin.SendAsync(HttpMethod.Post, designation, new { enabled = true });
        Assert.Equal(HttpStatusCode.OK, designated.StatusCode);
        Assert.Equal(scenario.Backup.Backup.Setup.Account.UserId.Value, AgentWorld.ReadJson(designated).GetProperty("securityOwnerUserId").GetGuid());
        // No ordinary notification policy exists; mandatory security mail must still be queued and claimable.
        var requester = await scenario.World.SignInAsync(await scenario.World.SeedUserAsync(platformAdmin: true));
        var approver = await scenario.World.SignInAsync(await scenario.World.SeedUserAsync(platformAdmin: true));
        // Match the notification policy's activation time, then move forward to the grant.
        scenario.Backup.Clock.Advance(notifications.Clock.GetUtcNow() - scenario.Backup.Clock.GetUtcNow() + TimeSpan.FromSeconds(2));
        notifications.Clock.Advance(TimeSpan.FromSeconds(2));
        var opened = await OpenAsync(scenario, requester);
        await notifications.Engine().SweepAsync(default);
        Assert.DoesNotContain(await notifications.EventsAsync(tenant), e => e.Type == NotificationEventTypes.BreakGlassAccess);
        using var approved = await approver.SendAsync(HttpMethod.Post, Root + "/" + opened.GetProperty("id").GetGuid() + "/approve");
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        await notifications.Engine().SweepAsync(default);
        await notifications.Engine().SweepAsync(default);
        var notice = Assert.Single(await notifications.EventsAsync(tenant), e => e.Type == NotificationEventTypes.BreakGlassAccess);
        Assert.Equal(NotificationSeverity.Critical, notice.Severity);
        Assert.Equal(ownerChannel.Id, Assert.Single(await notifications.DeliveriesAsync(tenant), d => d.EventId == notice.Id).ChannelId);
        var claimed = await notifications.Store().ClaimDueAsync("security-test", notifications.Clock.GetUtcNow(), 100, TimeSpan.FromMinutes(1), 3, default);
        Assert.Null(Assert.Single(claimed, delivery => delivery.Event.Id == notice.Id).Skip);
        Assert.Empty(notice.Facts);
        Assert.DoesNotContain(NotificationEventTypes.BreakGlassAccess, NotificationEventTypes.Selectable);
    }
    [Fact]
    public async Task OnlyAnOwnerCanDesignateAConfirmedEmailAndRetargetingClearsThatDesignation()
    {
        await using var scenario = await DownloadApiScenario.CreateAsync(fixture);
        var ownerAccount = await scenario.World.SeedUserAsync();
        await scenario.World.GrantAsync(scenario.Backup.Backup.Setup.Tenant, ownerAccount, BackupPlatform.Domain.Tenancy.TenantRole.Owner);
        var owner = await scenario.World.SignInAsync(ownerAccount);
        var notifications = new NotificationWorld(fixture);
        var tenant = scenario.Backup.Backup.Setup.Tenant.Id;
        var channel = await notifications.AddEmailAsync(tenant, confirm: false);
        var route = scenario.Root + $"/notifications/channels/{channel.Id}/owner-security-recipient";
        using var unconfirmed = await owner.SendAsync(HttpMethod.Post, route, new { enabled = true });
        Assert.Equal(HttpStatusCode.BadRequest, unconfirmed.StatusCode);
        await notifications.RecipientActionAsync(tenant, channel.Id, "ops@example.com", BackupPlatform.Notifications.RecipientAction.Confirm);
        var adminAccount = await scenario.World.SeedUserAsync();
        await scenario.World.GrantAsync(scenario.Backup.Backup.Setup.Tenant, adminAccount, BackupPlatform.Domain.Tenancy.TenantRole.Admin);
        var admin = await scenario.World.SignInAsync(adminAccount);
        using var notOwner = await admin.SendAsync(HttpMethod.Post, route, new { enabled = true });
        Assert.Equal(HttpStatusCode.Forbidden, notOwner.StatusCode);
        using var confirmedOwner = await owner.SendAsync(HttpMethod.Post, route, new { enabled = true });
        Assert.Equal(HttpStatusCode.OK, confirmedOwner.StatusCode);
        using var moved = await owner.SendAsync(HttpMethod.Put, scenario.Root + $"/notifications/channels/{channel.Id}", new { target = "another@example.com" });
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        Assert.Equal(JsonValueKind.Null, AgentWorld.ReadJson(moved).GetProperty("securityOwnerUserId").ValueKind);
    }


    [Fact]
    public async Task OrdinaryTenantAndAnonymousUsersCannotEnterPlatformBreakGlassAndCrossTenantArtifactsAreHidden()
    {
        await using var scenario = await DownloadApiScenario.CreateAsync(fixture);
        await scenario.EncryptAsync();
        using var tenantList = await scenario.Admin.GetAsync(Root);
        using var anonymousList = await scenario.World.Anonymous.GetAsync(Root);
        Assert.Equal(HttpStatusCode.Forbidden, tenantList.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousList.StatusCode);
        var requester = await scenario.World.SignInAsync(await scenario.World.SeedUserAsync(platformAdmin: true));
        using var crossTenant = await requester.SendAsync(HttpMethod.Post, Root, new { tenantId = Guid.NewGuid(), artifactId = scenario.Accepted.ArtifactId, reason = "Incident" });
        Assert.Equal(HttpStatusCode.NotFound, crossTenant.StatusCode);
        using var csrf = await requester.SendAsync(HttpMethod.Post, Root, new { tenantId = scenario.Backup.TenantId, artifactId = scenario.Accepted.ArtifactId, reason = "Incident" }, includeAntiforgery: false);
        Assert.Equal(HttpStatusCode.BadRequest, csrf.StatusCode);
    }
}
