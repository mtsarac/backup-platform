using System.Collections.Concurrent;
using System.Net;
using BackupPlatform.Api.Push;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Notifications;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Infrastructure.Notifications;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.IntegrationTests.Notifications;

public sealed class BackupShareNotificationsIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlFixture fixture = new();
    public Task InitializeAsync() => fixture.InitializeAsync();
    public Task DisposeAsync() => fixture.DisposeAsync();

    private static async Task<(BackupApiScenario Scenario, Guid ArtifactId)> SetupScenarioWithArtifactAsync(
        PostgreSqlFixture fixture,
        bool kestrel = false)
    {
        var scenario = await BackupApiScenario.CreateAsync(fixture, kestrel: kestrel);
        var job = await scenario.CreateJobAsync();
        await scenario.StartRunAsync(job);
        var claim = await scenario.ClaimAsync();
        await scenario.StartAttemptAsync(claim);

        using var completed = await scenario.CompleteRawAsync(claim, scenario.SuccessRequest(claim));
        Assert.Equal(HttpStatusCode.NoContent, completed.StatusCode);

        await using var context = fixture.CreatePlatformContext();
        var artifact = await context.BackupArtifacts
            .AsNoTracking()
            .SingleAsync(a => a.TenantId == scenario.Setup.Tenant.Id && a.AttemptId == claim.AttemptId);

        return (scenario, artifact.Id);
    }

    private static HubConnection CreateHubClient(Uri address, CookieContainer cookies, ConcurrentQueue<InAppNotificationMessage> received)
    {
        var connection = new HubConnectionBuilder().WithUrl(new Uri(address, "/hubs/notifications"), options =>
        {
            options.Transports = HttpTransportType.WebSockets;
            options.Headers["Cookie"] = cookies.GetCookieHeader(address);
            options.HttpMessageHandlerFactory = _ => new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };
            options.WebSocketConfiguration = socket => socket.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }).Build();
        connection.On<InAppNotificationMessage>("notificationReceived", message => received.Enqueue(message));
        return connection;
    }

    [Fact]
    public async Task Recipient_rules_for_create_extend_and_revoke_across_roles_and_tenants()
    {
        var (scenario, artifactId) = await SetupScenarioWithArtifactAsync(fixture);
        await using (scenario)
        {
            var tenantA = scenario.Setup.Tenant;
            var userAdminA1 = scenario.Setup.Account;
            var userAdminA2 = await scenario.World.SeedUserAsync(platformAdmin: false);
            await scenario.World.GrantAsync(tenantA, userAdminA2);
            var userAdminA2Session = await scenario.World.SignInAsync(userAdminA2);

            // A user whose membership in Tenant A was removed (inactive/revoked access in Tenant A)
            var removedUserAdmin = await scenario.World.SeedUserAsync(platformAdmin: false);
            await scenario.World.GrantAsync(tenantA, removedUserAdmin);
            var removedSession = await scenario.World.SignInAsync(removedUserAdmin);
            await using (var removeContext = fixture.CreatePlatformContext())
            {
                await removeContext.TenantMemberships
                    .Where(m => m.TenantId == tenantA.Id && m.UserId == removedUserAdmin.UserId)
                    .ExecuteDeleteAsync();
            }

            // Another tenant's UserAdmin
            var tenantBSetup = await scenario.World.CreateTenantAsync(enrollmentEnabled: false);
            var userAdminB = tenantBSetup.Account;

            // Two PlatformAdmins
            var platformAdmin1 = await scenario.World.SeedUserAsync(platformAdmin: true);
            var platformSession1 = await scenario.World.SignInAsync(platformAdmin1);
            var platformAdmin2 = await scenario.World.SeedUserAsync(platformAdmin: true);
            var platformSession2 = await scenario.World.SignInAsync(platformAdmin2);

            List<UserId> allPlatformAdminIds;
            await using (var context = fixture.CreatePlatformContext())
            {
                allPlatformAdminIds = await context.Users
                    .AsNoTracking()
                    .Where(u => u.Role == UserRole.PlatformAdmin)
                    .Select(u => u.Id)
                    .ToListAsync();
            }

            // 1. UserAdmin creates a share -> notifies all active PlatformAdmins and NO UserAdmins
            using var uaCreateResponse = await scenario.Admin.SendAsync(
                HttpMethod.Post,
                $"{scenario.Root}/backup-artifacts/{artifactId}/shares",
                new { durationHours = 24, reason = "UserAdmin share" });
            Assert.Equal(HttpStatusCode.Created, uaCreateResponse.StatusCode);
            var uaShareId = AgentWorld.ReadJson(uaCreateResponse).GetProperty("id").GetGuid();

            await using (var context = fixture.CreatePlatformContext())
            {
                var createdEvent = await context.NotificationEvents
                    .AsNoTracking()
                    .SingleAsync(e => e.TenantId == tenantA.Id && e.Type == NotificationEventTypes.BackupShareCreated && e.SubjectId == uaShareId);
                Assert.Equal("backup_share", createdEvent.SubjectKind);
                Assert.Equal("master", createdEvent.SubjectName);

                var recipients = await context.UserNotifications
                    .AsNoTracking()
                    .Where(n => n.EventId == createdEvent.Id)
                    .Select(n => n.UserId)
                    .ToListAsync();

                Assert.Equal(allPlatformAdminIds.OrderBy(id => id.Value), recipients.OrderBy(id => id.Value));
                Assert.DoesNotContain(userAdminA1.UserId, recipients);
                Assert.DoesNotContain(userAdminA2.UserId, recipients);
                Assert.DoesNotContain(userAdminB.UserId, recipients);
                Assert.DoesNotContain(removedUserAdmin.UserId, recipients);
            }

            // Verify inbox visibility for UserAdmin-created share
            using (var pa1Inbox = await platformSession1.GetAsync("/api/notifications/in-app"))
            {
                var items = AgentWorld.ReadJson(pa1Inbox).EnumerateArray().ToList();
                var item = Assert.Single(items, i => i.GetProperty("subjectId").GetGuid() == uaShareId);
                Assert.Equal(NotificationEventTypes.BackupShareCreated, item.GetProperty("type").GetString());
                Assert.Equal(tenantA.Id.Value, item.GetProperty("tenantId").GetGuid());
                Assert.Equal("backup_share", item.GetProperty("subjectKind").GetString());
            }
            using (var ua1Inbox = await scenario.Admin.GetAsync("/api/notifications/in-app"))
            {
                Assert.Empty(AgentWorld.ReadJson(ua1Inbox).EnumerateArray());
            }
            using (var ua2Inbox = await userAdminA2Session.GetAsync("/api/notifications/in-app"))
            {
                Assert.Empty(AgentWorld.ReadJson(ua2Inbox).EnumerateArray());
            }

            // 2. PlatformAdmin1 creates a share -> notifies all active UserAdmins of Tenant A, and NO other tenants or PlatformAdmins
            using var paCreateResponse = await platformSession1.SendAsync(
                HttpMethod.Post,
                "/api/platform/backup-shares",
                new { tenantId = tenantA.Id.Value, artifactId, durationHours = 24, reason = "PlatformAdmin share" });
            Assert.Equal(HttpStatusCode.Created, paCreateResponse.StatusCode);
            var paShareId = AgentWorld.ReadJson(paCreateResponse).GetProperty("id").GetGuid();

            await using (var context = fixture.CreatePlatformContext())
            {
                var createdEvent = await context.NotificationEvents
                    .AsNoTracking()
                    .SingleAsync(e => e.TenantId == tenantA.Id && e.Type == NotificationEventTypes.BackupShareCreated && e.SubjectId == paShareId);

                var recipients = await context.UserNotifications
                    .AsNoTracking()
                    .Where(n => n.EventId == createdEvent.Id)
                    .Select(n => n.UserId)
                    .ToListAsync();

                var expectedTenantAdmins = new[] { userAdminA1.UserId, userAdminA2.UserId };
                Assert.Equal(expectedTenantAdmins.OrderBy(id => id.Value), recipients.OrderBy(id => id.Value));
                Assert.DoesNotContain(userAdminB.UserId, recipients);
                Assert.DoesNotContain(removedUserAdmin.UserId, recipients);
                Assert.DoesNotContain(platformAdmin1.UserId, recipients);
                Assert.DoesNotContain(platformAdmin2.UserId, recipients);
            }

            using (var uaBInbox = await tenantBSetup.Admin.GetAsync("/api/notifications/in-app"))
            {
                Assert.Empty(AgentWorld.ReadJson(uaBInbox).EnumerateArray());
            }
            using (var removedInbox = await removedSession.GetAsync("/api/notifications/in-app"))
            {
                Assert.Empty(AgentWorld.ReadJson(removedInbox).EnumerateArray());
            }

            // 3. UserAdmin1 extends uaShareId -> notifies all active PlatformAdmins + UserAdminA2, excluding acting UserAdminA1
            using var uaExtendResponse = await scenario.Admin.SendAsync(
                HttpMethod.Post,
                $"{scenario.Root}/backup-shares/{uaShareId}/extend",
                new { durationHours = 48 });
            Assert.Equal(HttpStatusCode.OK, uaExtendResponse.StatusCode);

            await using (var context = fixture.CreatePlatformContext())
            {
                var extendedEvent = await context.NotificationEvents
                    .AsNoTracking()
                    .SingleAsync(e => e.TenantId == tenantA.Id && e.Type == NotificationEventTypes.BackupShareExtended && e.SubjectId == uaShareId);

                var recipients = await context.UserNotifications
                    .AsNoTracking()
                    .Where(n => n.EventId == extendedEvent.Id)
                    .Select(n => n.UserId)
                    .ToListAsync();

                var expected = allPlatformAdminIds.Append(userAdminA2.UserId).Distinct();
                Assert.Equal(expected.OrderBy(id => id.Value), recipients.OrderBy(id => id.Value));
                Assert.DoesNotContain(userAdminA1.UserId, recipients);
                Assert.DoesNotContain(userAdminB.UserId, recipients);
                Assert.DoesNotContain(removedUserAdmin.UserId, recipients);
            }

            // 4. PlatformAdmin1 revokes uaShareId -> notifies all OTHER PlatformAdmins + all active UserAdmins of Tenant A, excluding acting PlatformAdmin1
            using var paRevokeResponse = await platformSession1.SendAsync(
                HttpMethod.Post,
                $"{scenario.Root}/backup-shares/{uaShareId}/revoke",
                new { reason = "Done" });
            Assert.Equal(HttpStatusCode.OK, paRevokeResponse.StatusCode);

            await using (var context = fixture.CreatePlatformContext())
            {
                var revokedEvent = await context.NotificationEvents
                    .AsNoTracking()
                    .SingleAsync(e => e.TenantId == tenantA.Id && e.Type == NotificationEventTypes.BackupShareRevoked && e.SubjectId == uaShareId);

                var recipients = await context.UserNotifications
                    .AsNoTracking()
                    .Where(n => n.EventId == revokedEvent.Id)
                    .Select(n => n.UserId)
                    .ToListAsync();

                var expected = allPlatformAdminIds
                    .Where(id => id != platformAdmin1.UserId)
                    .Concat([userAdminA1.UserId, userAdminA2.UserId])
                    .Distinct();
                Assert.Equal(expected.OrderBy(id => id.Value), recipients.OrderBy(id => id.Value));
                Assert.DoesNotContain(platformAdmin1.UserId, recipients);
                Assert.Contains(platformAdmin2.UserId, recipients);
                Assert.Contains(userAdminA1.UserId, recipients);
                Assert.Contains(userAdminA2.UserId, recipients);
                Assert.DoesNotContain(userAdminB.UserId, recipients);
            }

            using (var pa2Inbox = await platformSession2.GetAsync("/api/notifications/in-app"))
            {
                var items = AgentWorld.ReadJson(pa2Inbox).EnumerateArray().ToList();
                Assert.Contains(items, i => i.GetProperty("type").GetString() == NotificationEventTypes.BackupShareRevoked && i.GetProperty("subjectId").GetGuid() == uaShareId);
            }
        }
    }

    [Fact]
    public async Task Rollback_produces_zero_notifications_and_duplicate_processing_is_idempotent()
    {
        var (scenario, artifactId) = await SetupScenarioWithArtifactAsync(fixture);
        await using (scenario)
        {
            var tenantId = scenario.Setup.Tenant.Id;
            var platformAdmin = await scenario.World.SeedUserAsync(platformAdmin: true);

            // 1. Rolled-back share transaction produces zero notifications
            var rolledBackShareId = Guid.NewGuid();
            var rolledBackDedup = $"{NotificationEventTypes.BackupShareCreated}:{rolledBackShareId:N}";
            await using (var context = fixture.CreatePlatformContext())
            await using (var transaction = await context.Database.BeginTransactionAsync())
            {
                var now = scenario.Clock.GetUtcNow();
                var share = new BackupShare(
                    rolledBackShareId,
                    tenantId,
                    artifactId,
                    scenario.Setup.Account.UserId,
                    UserRole.UserAdmin,
                    now,
                    now.AddHours(24),
                    "Rolled back share");
                context.BackupShares.Add(share);
                var notificationEvent = new NotificationEvent(
                    Guid.NewGuid(),
                    tenantId,
                    NotificationEventTypes.BackupShareCreated,
                    NotificationPhase.Raised,
                    NotificationSeverity.Info,
                    new NotificationSubject("backup_share", rolledBackShareId, "master"),
                    null,
                    rolledBackDedup,
                    now,
                    now);
                await UserNotificationWriter.RecordAsync(
                    context,
                    notificationEvent,
                    [],
                    platformAdmins: true,
                    tenantAdmins: false,
                    except: scenario.Setup.Account.UserId,
                    default);
                await context.SaveChangesAsync();
                await transaction.RollbackAsync();
            }

            // Failed share creation (invalid duration) also produces zero notifications
            using var invalidResponse = await scenario.Admin.SendAsync(
                HttpMethod.Post,
                $"{scenario.Root}/backup-artifacts/{artifactId}/shares",
                new { durationHours = 100 });
            Assert.Equal(HttpStatusCode.BadRequest, invalidResponse.StatusCode);

            await using (var check = fixture.CreatePlatformContext())
            {
                Assert.False(await check.BackupShares.AnyAsync(s => s.Id == rolledBackShareId));
                Assert.False(await check.NotificationEvents.AnyAsync(e => e.DedupKey == rolledBackDedup));
                Assert.Empty(await check.NotificationEvents.Where(e => e.SubjectKind == "backup_share").ToListAsync());
                Assert.Empty(await check.UserNotifications.ToListAsync());
            }

            // 2. Duplicate DedupKey processing produces one event and one notification per recipient
            using var createResponse = await scenario.Admin.SendAsync(
                HttpMethod.Post,
                $"{scenario.Root}/backup-artifacts/{artifactId}/shares",
                new { durationHours = 24, reason = "Idempotent share" });
            Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
            var shareId = AgentWorld.ReadJson(createResponse).GetProperty("id").GetGuid();
            var dedupKey = $"{NotificationEventTypes.BackupShareCreated}:{shareId:N}";

            await using (var duplicateContext = fixture.CreatePlatformContext())
            await using (var transaction = await duplicateContext.Database.BeginTransactionAsync())
            {
                var now = scenario.Clock.GetUtcNow();
                var duplicateEvent = new NotificationEvent(
                    Guid.NewGuid(),
                    tenantId,
                    NotificationEventTypes.BackupShareCreated,
                    NotificationPhase.Raised,
                    NotificationSeverity.Info,
                    new NotificationSubject("backup_share", shareId, "master"),
                    null,
                    dedupKey,
                    now,
                    now);
                await UserNotificationWriter.RecordAsync(
                    duplicateContext,
                    duplicateEvent,
                    [],
                    platformAdmins: true,
                    tenantAdmins: false,
                    except: scenario.Setup.Account.UserId,
                    default);
                await duplicateContext.SaveChangesAsync();
                await transaction.CommitAsync();
            }

            await using (var verify = fixture.CreatePlatformContext())
            {
                var events = await verify.NotificationEvents.Where(e => e.DedupKey == dedupKey).ToListAsync();
                var singleEvent = Assert.Single(events);
                var platformNotifications = await verify.UserNotifications
                    .Where(n => n.EventId == singleEvent.Id && n.UserId == platformAdmin.UserId)
                    .ToListAsync();
                Assert.Single(platformNotifications);
            }
        }
    }

    [Fact]
    public async Task Downloading_shared_backup_produces_no_notification()
    {
        await using var scenario = await DownloadApiScenario.CreateAsync(fixture);
        var platformAccount = await scenario.World.SeedUserAsync(platformAdmin: true);
        var platform = await scenario.World.SignInAsync(platformAccount);

        using var shareResponse = await scenario.Admin.SendAsync(
            HttpMethod.Post,
            $"{scenario.Root}/backup-artifacts/{scenario.Accepted.ArtifactId}/shares",
            new { reason = "Download test", durationHours = 24 });
        Assert.Equal(HttpStatusCode.Created, shareResponse.StatusCode);
        var shareId = AgentWorld.ReadJson(shareResponse).GetProperty("id").GetGuid();

        int eventsBeforeDownload;
        int userNotificationsBeforeDownload;
        await using (var before = fixture.CreatePlatformContext())
        {
            eventsBeforeDownload = await before.NotificationEvents.CountAsync();
            userNotificationsBeforeDownload = await before.UserNotifications.CountAsync();
        }

        using var prep = await platform.SendAsync(HttpMethod.Post, $"{scenario.ArtifactPath}/downloads", new { shareId });
        Assert.Equal(HttpStatusCode.OK, prep.StatusCode);
        var prepared = AgentWorld.ReadJson(prep);
        var contentUrl = prepared.GetProperty("downloadUrl").GetString()!;

        using var content = await platform.Client.GetAsync(contentUrl, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, content.StatusCode);
        var receivedBytes = await content.Content.ReadAsByteArrayAsync();
        Assert.Equal(scenario.Bytes, receivedBytes);

        await using (var after = fixture.CreatePlatformContext())
        {
            Assert.Equal(eventsBeforeDownload, await after.NotificationEvents.CountAsync());
            Assert.Equal(userNotificationsBeforeDownload, await after.UserNotifications.CountAsync());
        }
    }

    [Fact]
    public async Task Real_time_delivery_over_SignalR_across_nodes_includes_share_link_metadata()
    {
        var (nodeA, artifactId) = await SetupScenarioWithArtifactAsync(fixture, kestrel: true);
        await using (nodeA)
        await using (var nodeB = await AgentWorld.CreateAsync(fixture, kestrel: true))
        {
            var platformAccount = await nodeA.World.SeedUserAsync(platformAdmin: true);
            var paCookies = new CookieContainer();
            await nodeB.SignInAsync(platformAccount, paCookies);
            var paReceived = new ConcurrentQueue<InAppNotificationMessage>();
            await using var paClient = CreateHubClient(nodeB.ServerAddress!, paCookies, paReceived);
            await paClient.StartAsync();

            var uaCookies = new CookieContainer();
            await nodeB.SignInAsync(nodeA.Setup.Account, uaCookies);
            var uaReceived = new ConcurrentQueue<InAppNotificationMessage>();
            await using var uaClient = CreateHubClient(nodeB.ServerAddress!, uaCookies, uaReceived);
            await uaClient.StartAsync();

            await Task.Delay(500);

            // UserAdmin creates share on Node A -> PlatformAdmin connected to Node B receives real-time push
            using var createResp = await nodeA.Admin.SendAsync(
                HttpMethod.Post,
                $"{nodeA.Root}/backup-artifacts/{artifactId}/shares",
                new { durationHours = 24, reason = "Cross-node share" });
            Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
            var shareId = AgentWorld.ReadJson(createResp).GetProperty("id").GetGuid();

            await Eventually.AssertAsync(
                () => paReceived.Any(m => m.Type == NotificationEventTypes.BackupShareCreated && m.SubjectId == shareId),
                10_000);

            var pushedCreate = paReceived.Single(m => m.Type == NotificationEventTypes.BackupShareCreated && m.SubjectId == shareId);
            Assert.Equal(nodeA.Setup.Tenant.Id.Value, pushedCreate.TenantId);
            Assert.Equal("backup_share", pushedCreate.SubjectKind);
            Assert.Equal("master", pushedCreate.SubjectName);
            Assert.DoesNotContain(uaReceived, m => m.SubjectId == shareId);

            // PlatformAdmin extends share on Node A -> UserAdmin connected to Node B receives real-time push
            var paSessionA = await nodeA.World.SignInAsync(platformAccount);
            using var extendResp = await paSessionA.SendAsync(
                HttpMethod.Post,
                $"{nodeA.Root}/backup-shares/{shareId}/extend",
                new { durationHours = 48 });
            Assert.Equal(HttpStatusCode.OK, extendResp.StatusCode);

            await Eventually.AssertAsync(
                () => uaReceived.Any(m => m.Type == NotificationEventTypes.BackupShareExtended && m.SubjectId == shareId),
                10_000);

            var pushedExtend = uaReceived.Single(m => m.Type == NotificationEventTypes.BackupShareExtended && m.SubjectId == shareId);
            Assert.Equal(nodeA.Setup.Tenant.Id.Value, pushedExtend.TenantId);
            Assert.Equal("backup_share", pushedExtend.SubjectKind);
        }
    }
}
