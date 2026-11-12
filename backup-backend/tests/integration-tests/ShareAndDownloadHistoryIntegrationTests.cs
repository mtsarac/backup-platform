using System.Net;
using System.Text.Json;
using BackupPlatform.Application.Backups;
using BackupPlatform.Application.Downloads;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Downloads;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Notifications;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Infrastructure.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BackupPlatform.IntegrationTests;

public sealed class ShareAndDownloadHistoryIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static async Task<(DownloadApiScenario Scenario, Guid ShareId)> CreateScenarioWithShareAsync(
        PostgreSqlFixture fixture,
        TimeSpan? duration = null)
    {
        var scenario = await DownloadApiScenario.CreateAsync(fixture);
        using var shareResponse = await scenario.Admin.SendAsync(
            HttpMethod.Post,
            $"{scenario.Root}/backup-artifacts/{scenario.Accepted.ArtifactId}/shares",
            new
            {
                reason = "History integration test share",
                durationHours = (int)(duration ?? TimeSpan.FromHours(24)).TotalHours
            });

        Assert.Equal(HttpStatusCode.Created, shareResponse.StatusCode);
        var shareId = AgentWorld.ReadJson(shareResponse).GetProperty("id").GetGuid();
        return (scenario, shareId);
    }

    [Fact]
    public async Task PlatformAdmin_QueriesDownloadAttempts_WithFilters_AndSeesCompletedFailedCancelledAndRefused()
    {
        var (scenarioA, shareA) = await CreateScenarioWithShareAsync(fixture);
        await using (scenarioA)
        {
            var (scenarioB, shareB) = await CreateScenarioWithShareAsync(fixture);
            await using (scenarioB)
            {
                var platformAccount = await scenarioA.World.SeedUserAsync(platformAdmin: true);
                var platform = await scenarioA.World.SignInAsync(platformAccount);

                // 1. Completed download on Share A by PlatformAdmin
                using var prepCompleted = await platform.SendAsync(
                    HttpMethod.Post,
                    $"{scenarioA.ArtifactPath}/downloads",
                    new { shareId = shareA });
                Assert.Equal(HttpStatusCode.OK, prepCompleted.StatusCode);
                var completedPrepared = AgentWorld.ReadJson(prepCompleted);
                var completedSessionId = completedPrepared.GetProperty("sessionId").GetGuid();
                var completedUrl = completedPrepared.GetProperty("downloadUrl").GetString()!;

                using var contentCompleted = await platform.Client.GetAsync(completedUrl);
                Assert.Equal(HttpStatusCode.OK, contentCompleted.StatusCode);
                var downloadedBytes = await contentCompleted.Content.ReadAsByteArrayAsync();
                Assert.Equal(scenarioA.Bytes.Length, downloadedBytes.Length);

                // 2. Refused download on Artifact A by PlatformAdmin (missing share)
                using var prepRefused = await platform.SendAsync(
                    HttpMethod.Post,
                    $"{scenarioA.ArtifactPath}/downloads",
                    new { });
                Assert.Equal(HttpStatusCode.Forbidden, prepRefused.StatusCode);

                // 3. Cancelled download on Share A
                using var prepCancelled = await platform.SendAsync(
                    HttpMethod.Post,
                    $"{scenarioA.ArtifactPath}/downloads",
                    new { shareId = shareA });
                Assert.Equal(HttpStatusCode.OK, prepCancelled.StatusCode);
                var cancelledSessionId = AgentWorld.ReadJson(prepCancelled).GetProperty("sessionId").GetGuid();
                var now = scenarioA.Backup.Clock.GetUtcNow();
                var store = scenarioA.Store();
                var partialBytes = Math.Max(1, scenarioA.Bytes.Length / 2);
                Assert.NotNull(await store.TransitionAsync(
                    cancelledSessionId,
                    new DownloadTransition(DownloadState.Ready, DownloadState.Streaming, null, "node-cancel", now, TimeSpan.FromMinutes(1)),
                    default));
                Assert.NotNull(await store.TransitionAsync(
                    cancelledSessionId,
                    new DownloadTransition(DownloadState.Streaming, DownloadState.Cancelled, "node-cancel", null, now.AddSeconds(2), Bytes: partialBytes, FailureCode: "download_cancelled"),
                    default));

                // 4. Failed download on Share B (tampered content -> integrity_mismatch)
                using var prepFailed = await platform.SendAsync(
                    HttpMethod.Post,
                    $"{scenarioB.ArtifactPath}/downloads",
                    new { shareId = shareB });
                Assert.Equal(HttpStatusCode.OK, prepFailed.StatusCode);
                var failedPrepared = AgentWorld.ReadJson(prepFailed);
                var failedSessionId = failedPrepared.GetProperty("sessionId").GetGuid();
                var failedUrl = failedPrepared.GetProperty("downloadUrl").GetString()!;

                var tampered = scenarioB.Bytes.ToArray();
                tampered[0] ^= 0xff;
                await File.WriteAllBytesAsync(scenarioB.FilePath, tampered);
                using var contentFailed = await platform.Client.GetAsync(failedUrl);
                Assert.NotEqual(HttpStatusCode.OK, contentFailed.StatusCode);

                // Query by shareId = shareA
                using var byShareAResp = await platform.GetAsync($"/api/platform/download-attempts?shareId={shareA}");
                Assert.Equal(HttpStatusCode.OK, byShareAResp.StatusCode);
                var byShareA = AgentWorld.ReadJson(byShareAResp).EnumerateArray().ToList();
                Assert.Equal(2, byShareA.Count);
                Assert.Contains(byShareA, item =>
                    item.GetProperty("id").GetGuid() == completedSessionId &&
                    item.GetProperty("result").GetString() == "Completed" &&
                    item.GetProperty("bytesTransferred").GetInt64() == scenarioA.Bytes.Length &&
                    item.GetProperty("startedAtUtc").GetDateTimeOffset() != default &&
                    item.GetProperty("endedAtUtc").GetDateTimeOffset() != default &&
                    item.GetProperty("failureReason").ValueKind == JsonValueKind.Null);
                Assert.Contains(byShareA, item =>
                    item.GetProperty("id").GetGuid() == cancelledSessionId &&
                    item.GetProperty("result").GetString() == "Cancelled" &&
                    item.GetProperty("bytesTransferred").GetInt64() == partialBytes &&
                    item.GetProperty("failureReason").GetString() == "download_cancelled");

                // Query by tenantId = Tenant A (includes completed, cancelled, and refused on Artifact A)
                var tenantAId = scenarioA.Backup.Backup.Setup.Tenant.Id.Value;
                using var byTenantAResp = await platform.GetAsync($"/api/platform/download-attempts?tenantId={tenantAId}");
                Assert.Equal(HttpStatusCode.OK, byTenantAResp.StatusCode);
                var byTenantA = AgentWorld.ReadJson(byTenantAResp).EnumerateArray().ToList();
                Assert.Equal(3, byTenantA.Count);
                Assert.Contains(byTenantA, item =>
                    item.GetProperty("result").GetString() == "Refused" &&
                    item.GetProperty("bytesTransferred").GetInt64() == 0 &&
                    item.GetProperty("failureReason").GetString() == "share_missing" &&
                    item.GetProperty("initiatingUserId").GetGuid() == platformAccount.UserId.Value);

                // Query by artifactId = Artifact B and result = Failed
                using var byFailedResp = await platform.GetAsync(
                    $"/api/platform/download-attempts?artifactId={scenarioB.Accepted.ArtifactId}&result=Failed");
                Assert.Equal(HttpStatusCode.OK, byFailedResp.StatusCode);
                var byFailed = AgentWorld.ReadJson(byFailedResp).EnumerateArray().ToList();
                var failedItem = Assert.Single(byFailed);
                Assert.Equal(failedSessionId, failedItem.GetProperty("id").GetGuid());
                Assert.Equal(shareB, failedItem.GetProperty("shareId").GetGuid());
                Assert.Equal("Failed", failedItem.GetProperty("result").GetString());
                Assert.Equal("integrity_mismatch", failedItem.GetProperty("failureReason").GetString());
                Assert.Equal(0L, failedItem.GetProperty("bytesTransferred").GetInt64());

                // Query by userId and result = Refused
                using var byUserRefusedResp = await platform.GetAsync(
                    $"/api/platform/download-attempts?userId={platformAccount.UserId.Value}&result=Refused");
                Assert.Equal(HttpStatusCode.OK, byUserRefusedResp.StatusCode);
                var byUserRefused = AgentWorld.ReadJson(byUserRefusedResp).EnumerateArray().ToList();
                Assert.Contains(byUserRefused, item =>
                    item.GetProperty("artifactId").GetGuid() == scenarioA.Accepted.ArtifactId &&
                    item.GetProperty("result").GetString() == "Refused" &&
                    item.GetProperty("failureReason").GetString() == "share_missing");
            }
        }
    }

    [Fact]
    public async Task UserAdmin_SeesOwnTenantHistoryOnly_AndForeignTenantHistoryIsIndistinguishableFromNone()
    {
        var (scenarioA, shareA) = await CreateScenarioWithShareAsync(fixture);
        await using (scenarioA)
        {
            var (scenarioB, shareB) = await CreateScenarioWithShareAsync(fixture);
            await using (scenarioB)
            {
                var platformAccount = await scenarioA.World.SeedUserAsync(platformAdmin: true);
                var platform = await scenarioA.World.SignInAsync(platformAccount);

                // Revoke shareA and attempt download so Tenant A has a refused attempt on shareA
                using var revokeA = await scenarioA.Admin.SendAsync(
                    HttpMethod.Post,
                    $"{scenarioA.Root}/backup-shares/{shareA}/revoke",
                    new { reason = "Revoke for test" });
                Assert.Equal(HttpStatusCode.OK, revokeA.StatusCode);

                using var refusedA = await platform.SendAsync(
                    HttpMethod.Post,
                    $"{scenarioA.ArtifactPath}/downloads",
                    new { shareId = shareA });
                Assert.Equal(HttpStatusCode.Forbidden, refusedA.StatusCode);

                // Perform a completed download on Tenant B's shareB
                using var prepB = await platform.SendAsync(
                    HttpMethod.Post,
                    $"{scenarioB.ArtifactPath}/downloads",
                    new { shareId = shareB });
                Assert.Equal(HttpStatusCode.OK, prepB.StatusCode);
                using var contentB = await platform.Client.GetAsync(AgentWorld.ReadJson(prepB).GetProperty("downloadUrl").GetString()!);
                Assert.Equal(HttpStatusCode.OK, contentB.StatusCode);

                // Tenant A UserAdmin sees Tenant A's refused attempt
                using var tenantAAttemptsResp = await scenarioA.Admin.GetAsync($"{scenarioA.Root}/backup-shares/download-attempts");
                Assert.Equal(HttpStatusCode.OK, tenantAAttemptsResp.StatusCode);
                var tenantAAttempts = AgentWorld.ReadJson(tenantAAttemptsResp).EnumerateArray().ToList();
                var refusedAttempt = Assert.Single(tenantAAttempts);
                Assert.Equal(shareA, refusedAttempt.GetProperty("shareId").GetGuid());
                Assert.Equal("Refused", refusedAttempt.GetProperty("result").GetString());
                Assert.Equal("share_revoked", refusedAttempt.GetProperty("failureReason").GetString());

                // Tenant A UserAdmin querying Tenant B's shareId or artifactId on Tenant A's endpoint gets empty [] (indistinguishable from none)
                using var foreignShareFilter = await scenarioA.Admin.GetAsync($"{scenarioA.Root}/backup-shares/download-attempts?shareId={shareB}");
                Assert.Equal(HttpStatusCode.OK, foreignShareFilter.StatusCode);
                Assert.Empty(AgentWorld.ReadJson(foreignShareFilter).EnumerateArray());

                using var nonexistentShareFilter = await scenarioA.Admin.GetAsync($"{scenarioA.Root}/backup-shares/download-attempts?shareId={Guid.NewGuid()}");
                Assert.Equal(HttpStatusCode.OK, nonexistentShareFilter.StatusCode);
                Assert.Empty(AgentWorld.ReadJson(nonexistentShareFilter).EnumerateArray());

                using var foreignShareRoute = await scenarioA.Admin.GetAsync($"{scenarioA.Root}/backup-shares/{shareB}/attempts");
                Assert.Equal(HttpStatusCode.OK, foreignShareRoute.StatusCode);
                Assert.Empty(AgentWorld.ReadJson(foreignShareRoute).EnumerateArray());

                using var foreignArtifactFilter = await scenarioA.Admin.GetAsync($"{scenarioA.Root}/backup-shares/download-attempts?artifactId={scenarioB.Accepted.ArtifactId}");
                Assert.Equal(HttpStatusCode.OK, foreignArtifactFilter.StatusCode);
                Assert.Empty(AgentWorld.ReadJson(foreignArtifactFilter).EnumerateArray());

                // Tenant A UserAdmin cannot access Tenant B's route directly (403) or Platform route (403)
                using var directTenantB = await scenarioA.Admin.GetAsync($"{scenarioB.Root}/backup-shares/download-attempts");
                Assert.Equal(HttpStatusCode.Forbidden, directTenantB.StatusCode);

                using var platformAttempt = await scenarioA.Admin.GetAsync("/api/platform/download-attempts");
                Assert.Equal(HttpStatusCode.Forbidden, platformAttempt.StatusCode);
            }
        }
    }

    [Fact]
    public async Task AuditRetentionSetting_DefaultsTo365Days_OnlyPlatformAdminCanReadOrUpdate_AndChangeIsAudited()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        await using (var cleanCtx = fixture.CreatePlatformContext())
        {
            await cleanCtx.PlatformAuditRetentionSettings.ExecuteDeleteAsync();
        }

        var platformAccount = await scenario.World.SeedUserAsync(platformAdmin: true);
        var platform = await scenario.World.SignInAsync(platformAccount);

        // UserAdmin cannot read or change retention setting (403)
        using var userGet = await scenario.Admin.GetAsync("/api/platform/settings/audit-retention");
        Assert.Equal(HttpStatusCode.Forbidden, userGet.StatusCode);

        using var userPut = await scenario.Admin.SendAsync(
            HttpMethod.Put,
            "/api/platform/settings/audit-retention",
            new { retentionDays = 180 });
        Assert.Equal(HttpStatusCode.Forbidden, userPut.StatusCode);

        // Default is 365 days without any configuration
        using var defaultGet = await platform.GetAsync("/api/platform/settings/audit-retention");
        Assert.Equal(HttpStatusCode.OK, defaultGet.StatusCode);
        var defaultJson = AgentWorld.ReadJson(defaultGet);
        Assert.Equal(365, defaultJson.GetProperty("retentionDays").GetInt32());
        Assert.Equal(365, defaultJson.GetProperty("defaultRetentionDays").GetInt32());

        // Invalid value rejected (400)
        using var invalidPut = await platform.SendAsync(
            HttpMethod.Put,
            "/api/platform/settings/audit-retention",
            new { retentionDays = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, invalidPut.StatusCode);

        // PlatformAdmin updates retention to 90 days
        using var updatePut = await platform.SendAsync(
            HttpMethod.Put,
            "/api/platform/settings/audit-retention",
            new { retentionDays = 90 });
        Assert.Equal(HttpStatusCode.OK, updatePut.StatusCode);
        var updatedJson = AgentWorld.ReadJson(updatePut);
        Assert.Equal(90, updatedJson.GetProperty("retentionDays").GetInt32());
        Assert.Equal(platformAccount.UserId.Value, updatedJson.GetProperty("updatedByUserId").GetGuid());

        // Subsequent GET reflects 90 days
        using var afterGet = await platform.GetAsync("/api/platform/settings/audit-retention");
        Assert.Equal(HttpStatusCode.OK, afterGet.StatusCode);
        Assert.Equal(90, AgentWorld.ReadJson(afterGet).GetProperty("retentionDays").GetInt32());

        // Change is audited with acting PlatformAdmin and old/new values
        await using var context = fixture.CreatePlatformContext();
        var audit = await context.AuditEvents
            .AsNoTracking()
            .Where(e => e.Action == AuditActions.AuditRetentionUpdated && e.ActorUserId == platformAccount.UserId)
            .OrderByDescending(e => e.At)
            .FirstOrDefaultAsync();
        Assert.NotNull(audit);
        Assert.Equal(AuditActorKind.PlatformAdmin, audit.ActorKind);
        using var details = JsonDocument.Parse(audit.DetailsJson);
        Assert.Equal(365, details.RootElement.GetProperty("previousRetentionDays").GetInt32());
        Assert.Equal(90, details.RootElement.GetProperty("retentionDays").GetInt32());
    }

    [Fact]
    public async Task Pruning_RemovesRecordsOlderThanRetention_PreservesActiveSharesAndInProgressDownloads_AndIsIndependentOfNotificationRetention()
    {
        var (scenario, activeShareId) = await CreateScenarioWithShareAsync(fixture, duration: TimeSpan.FromHours(48));
        await using (scenario)
        {
            var platformAccount = await scenario.World.SeedUserAsync(platformAdmin: true);
            var platform = await scenario.World.SignInAsync(platformAccount);

            // Configure retention to 30 days
            using var putRetention = await platform.SendAsync(
                HttpMethod.Put,
                "/api/platform/settings/audit-retention",
                new { retentionDays = 30 });
            Assert.Equal(HttpStatusCode.OK, putRetention.StatusCode);

            var now = scenario.Backup.Clock.GetUtcNow();
            var oldTime = now.AddDays(-45);
            var recentTime = now.AddDays(-10);
            var tenantId = scenario.Backup.Backup.Setup.Tenant.Id;
            var userId = scenario.Backup.Backup.Setup.Account.UserId;
            var artifactId = scenario.Accepted.ArtifactId;

            Guid oldExpiredShareId;
            Guid recentExpiredShareId;
            Guid oldTerminalSessionId;
            Guid inProgressSessionId;
            Guid oldAuditId;
            Guid recentAuditId;
            Guid activeShareAuditId;
            Guid inProgressDownloadAuditId;
            Guid notificationEventId;

            // Create two more shares and two download sessions via API, then adjust timestamps in DB to test the pruning boundary
            using var oldShareResp = await scenario.Admin.SendAsync(
                HttpMethod.Post,
                $"{scenario.Root}/backup-artifacts/{artifactId}/shares",
                new { reason = "Old expired share", durationHours = 12 });
            oldExpiredShareId = AgentWorld.ReadJson(oldShareResp).GetProperty("id").GetGuid();

            using var recentShareResp = await scenario.Admin.SendAsync(
                HttpMethod.Post,
                $"{scenario.Root}/backup-artifacts/{artifactId}/shares",
                new { reason = "Recent expired share", durationHours = 12 });
            recentExpiredShareId = AgentWorld.ReadJson(recentShareResp).GetProperty("id").GetGuid();

            // Session 1: completed (will be backdated to oldTime)
            var prepOld = await scenario.CreateSessionAsync();
            oldTerminalSessionId = prepOld.GetProperty("sessionId").GetGuid();
            using var streamOld = await scenario.Admin.Client.GetAsync(prepOld.GetProperty("downloadUrl").GetString()!);
            Assert.Equal(HttpStatusCode.OK, streamOld.StatusCode);

            // Session 2: in-progress (Ready / Streaming, ReleasedAt == null, CreatedAt backdated to oldTime)
            var prepInProgress = await scenario.CreateSessionAsync();
            inProgressSessionId = prepInProgress.GetProperty("sessionId").GetGuid();

            await using (var context = fixture.CreatePlatformContext())
            {
                // Backdate oldExpiredShare to 45 days ago
                await context.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE backup_shares
                    SET "CreatedAtUtc" = {oldTime.AddHours(-12)}, "ExpiresAtUtc" = {oldTime}
                    WHERE "Id" = {oldExpiredShareId}
                    """);

                // Backdate recentExpiredShare to 10 days ago
                await context.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE backup_shares
                    SET "CreatedAtUtc" = {recentTime.AddHours(-12)}, "ExpiresAtUtc" = {recentTime}
                    WHERE "Id" = {recentExpiredShareId}
                    """);

                // Backdate oldTerminalSession to 45 days ago
                await context.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE download_sessions
                    SET "CreatedAt" = {oldTime.AddMinutes(-10)}, "ExpiresAt" = {oldTime.AddMinutes(5)}, "ReleasedAt" = {oldTime}
                    WHERE "Id" = {oldTerminalSessionId}
                    """);

                // Backdate inProgressSession CreatedAt to 45 days ago while keeping it active/in-progress (ReleasedAt IS NULL)
                await context.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE download_sessions
                    SET "CreatedAt" = {oldTime}, "ExpiresAt" = {now.AddHours(2)}
                    WHERE "Id" = {inProgressSessionId}
                    """);

                var oldAudit = AuditEvent.Record(
                    oldTime,
                    userId,
                    AuditActorKind.TenantAdministrator,
                    "download.refused",
                    tenantId,
                    "download",
                    artifactId.ToString("D"),
                    new Dictionary<string, object?>
                    {
                        ["artifactId"] = artifactId,
                        ["tenantId"] = tenantId.Value,
                        ["initiatingUser"] = userId.Value,
                        ["failureReason"] = "old_refusal"
                    });
                oldAuditId = oldAudit.Id;

                var recentAudit = AuditEvent.Record(
                    recentTime,
                    userId,
                    AuditActorKind.TenantAdministrator,
                    "download.refused",
                    tenantId,
                    "download",
                    artifactId.ToString("D"),
                    new Dictionary<string, object?>
                    {
                        ["artifactId"] = artifactId,
                        ["tenantId"] = tenantId.Value,
                        ["initiatingUser"] = userId.Value,
                        ["failureReason"] = "recent_refusal"
                    });
                recentAuditId = recentAudit.Id;

                var activeShareAudit = AuditEvent.Record(
                    oldTime,
                    userId,
                    AuditActorKind.TenantAdministrator,
                    AuditActions.BackupShareCreated,
                    tenantId,
                    "backup_share",
                    activeShareId.ToString("D"),
                    new Dictionary<string, object?> { ["result"] = "created" });
                activeShareAuditId = activeShareAudit.Id;

                var inProgressAudit = AuditEvent.Record(
                    oldTime,
                    userId,
                    AuditActorKind.TenantAdministrator,
                    "download.requested",
                    tenantId,
                    "download",
                    inProgressSessionId.ToString("D"),
                    new Dictionary<string, object?> { ["sessionId"] = inProgressSessionId });
                inProgressDownloadAuditId = inProgressAudit.Id;

                var notificationEvent = new NotificationEvent(
                    Guid.NewGuid(),
                    tenantId,
                    NotificationEventTypes.BackupFailed,
                    NotificationPhase.Raised,
                    NotificationSeverity.Critical,
                    new NotificationSubject("job", Guid.NewGuid(), "TestJob"),
                    null,
                    $"test-dedup-{Guid.NewGuid():N}",
                    oldTime,
                    oldTime);
                notificationEventId = notificationEvent.Id;

                context.AuditEvents.AddRange(oldAudit, recentAudit, activeShareAudit, inProgressAudit);
                context.NotificationEvents.Add(notificationEvent);
                await context.SaveChangesAsync();
            }

            // Run audit & share history pruning
            await using (var scope = scenario.World.Factory.Services.CreateAsyncScope())
            {
                var pruningService = scope.ServiceProvider.GetRequiredService<AuditAndShareHistoryPruningService>();
                var pruneResult = await pruningService.PruneAsync(CancellationToken.None, nowOverride: now);
                Assert.True(pruneResult.SharesRemoved >= 1);
                Assert.True(pruneResult.DownloadSessionsRemoved >= 1);
                Assert.True(pruneResult.AuditEventsRemoved >= 1);
            }

            await using (var verifyContext = fixture.CreatePlatformContext())
            {
                // Old expired share, old terminal download session, and old standalone audit event are pruned
                Assert.False(await verifyContext.BackupShares.AnyAsync(s => s.Id == oldExpiredShareId));
                Assert.False(await verifyContext.DownloadSessions.AnyAsync(d => d.Id == oldTerminalSessionId));
                Assert.False(await verifyContext.AuditEvents.AnyAsync(e => e.Id == oldAuditId));

                // Recent expired share and recent audit event are preserved
                Assert.True(await verifyContext.BackupShares.AnyAsync(s => s.Id == recentExpiredShareId));
                Assert.True(await verifyContext.AuditEvents.AnyAsync(e => e.Id == recentAuditId));

                // Active share and its audit events are preserved
                Assert.True(await verifyContext.BackupShares.AnyAsync(s => s.Id == activeShareId));
                Assert.True(await verifyContext.AuditEvents.AnyAsync(e => e.Id == activeShareAuditId));

                // In-progress download session and its audit events are preserved
                Assert.True(await verifyContext.DownloadSessions.AnyAsync(d => d.Id == inProgressSessionId));
                Assert.True(await verifyContext.AuditEvents.AnyAsync(e => e.Id == inProgressDownloadAuditId));

                // Notification event is untouched by audit/share history pruning
                Assert.True(await verifyContext.NotificationEvents.AnyAsync(n => n.Id == notificationEventId));
            }

            // Now run Notification pruning with a 1-day cutoff: removes old notification event, leaves recent audit/share history untouched
            var notificationStore = new PostgreSqlNotificationStore(fixture.Options, scenario.Backup.Clock);
            var notificationsRemoved = await notificationStore.PruneAsync(now.AddDays(-1), CancellationToken.None);
            Assert.True(notificationsRemoved >= 1);

            await using (var verifyAfterNotificationPrune = fixture.CreatePlatformContext())
            {
                Assert.False(await verifyAfterNotificationPrune.NotificationEvents.AnyAsync(n => n.Id == notificationEventId));
                Assert.True(await verifyAfterNotificationPrune.BackupShares.AnyAsync(s => s.Id == recentExpiredShareId));
                Assert.True(await verifyAfterNotificationPrune.AuditEvents.AnyAsync(e => e.Id == recentAuditId));
            }
        }
    }
}
