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

    private static async Task<List<JsonElement>> AttemptsAsync(BrowserSession session, string path)
    {
        using var response = await session.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return AgentWorld.ReadJson(response).EnumerateArray().ToList();
    }

    private static async Task<Guid> DownloadAsync(BrowserSession session, DownloadApiScenario scenario, object body, HttpStatusCode expectedContent = HttpStatusCode.OK)
    {
        using var prepared = await session.SendAsync(HttpMethod.Post, $"{scenario.ArtifactPath}/downloads", body);
        Assert.Equal(HttpStatusCode.OK, prepared.StatusCode);
        var json = AgentWorld.ReadJson(prepared);
        using var content = await session.Client.GetAsync(json.GetProperty("downloadUrl").GetString()!);
        if (expectedContent == HttpStatusCode.OK)
        {
            Assert.Equal(HttpStatusCode.OK, content.StatusCode);
            await content.Content.ReadAsByteArrayAsync();
        }
        else Assert.NotEqual(HttpStatusCode.OK, content.StatusCode);
        return json.GetProperty("sessionId").GetGuid();
    }

    [Fact]
    public async Task PlatformAdmin_FiltersAttempts_AndSeesCompletedRefusedAndFailed()
    {
        var (scenario, shareId) = await CreateScenarioWithShareAsync(fixture);
        await using var _ = scenario;
        var platformAccount = await scenario.World.SeedUserAsync(platformAdmin: true);
        var platform = await scenario.World.SignInAsync(platformAccount);
        var tenantId = scenario.Backup.Backup.Setup.Tenant.Id.Value;

        var completedId = await DownloadAsync(platform, scenario, new { shareId });
        using (var refused = await platform.SendAsync(HttpMethod.Post, $"{scenario.ArtifactPath}/downloads", new { }))
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        var tampered = scenario.Bytes.ToArray();
        tampered[0] ^= 0xff;
        await File.WriteAllBytesAsync(scenario.FilePath, tampered);
        var failedId = await DownloadAsync(platform, scenario, new { shareId }, HttpStatusCode.InternalServerError);

        var byShare = await AttemptsAsync(platform, $"/api/platform/download-attempts?shareId={shareId}");
        Assert.Equal(new[] { completedId, failedId }.Order(), byShare.Select(item => item.GetProperty("id").GetGuid()).Order());

        var completed = Assert.Single(await AttemptsAsync(platform, $"/api/platform/download-attempts?shareId={shareId}&result=Completed"));
        Assert.Equal(completedId, completed.GetProperty("id").GetGuid());
        Assert.Equal(scenario.Bytes.Length, completed.GetProperty("bytesTransferred").GetInt64());
        Assert.NotEqual(JsonValueKind.Null, completed.GetProperty("endedAtUtc").ValueKind);
        Assert.Equal(platformAccount.UserId.Value, completed.GetProperty("initiatingUserId").GetGuid());

        var failed = Assert.Single(await AttemptsAsync(platform, $"/api/platform/download-attempts?artifactId={scenario.Accepted.ArtifactId}&result=Failed"));
        Assert.Equal(failedId, failed.GetProperty("id").GetGuid());
        Assert.False(string.IsNullOrEmpty(failed.GetProperty("failureReason").GetString()));

        var refusedAttempt = Assert.Single(await AttemptsAsync(platform, $"/api/platform/download-attempts?tenantId={tenantId}&userId={platformAccount.UserId.Value}&result=Refused"));
        Assert.Equal("Refused", refusedAttempt.GetProperty("result").GetString());
        Assert.Equal(scenario.Accepted.ArtifactId, refusedAttempt.GetProperty("artifactId").GetGuid());
        Assert.False(string.IsNullOrEmpty(refusedAttempt.GetProperty("failureReason").GetString()));

        Assert.Equal(3, (await AttemptsAsync(platform, $"/api/platform/download-attempts?tenantId={tenantId}")).Count);
        Assert.Empty(await AttemptsAsync(platform, $"/api/platform/download-attempts?tenantId={Guid.NewGuid()}"));
        Assert.Empty(await AttemptsAsync(platform, $"/api/platform/download-attempts?tenantId={tenantId}&userId={Guid.NewGuid()}"));
        using var invalid = await platform.GetAsync("/api/platform/download-attempts?result=nonsense");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task UserAdmin_SeesOnlyOwnTenantHistory_AndAnotherTenantLooksLikeNone()
    {
        var (own, ownShare) = await CreateScenarioWithShareAsync(fixture);
        await using var _1 = own;
        var (other, otherShare) = await CreateScenarioWithShareAsync(fixture);
        await using var _2 = other;
        var ownTenant = own.Backup.Backup.Setup.Tenant.Id.Value;

        var ownPlatform = await own.World.SignInAsync(await own.World.SeedUserAsync(platformAdmin: true));
        var otherPlatform = await other.World.SignInAsync(await other.World.SeedUserAsync(platformAdmin: true));
        var ownAttempt = await DownloadAsync(ownPlatform, own, new { shareId = ownShare });
        await DownloadAsync(otherPlatform, other, new { shareId = otherShare });
        using (var refused = await ownPlatform.SendAsync(HttpMethod.Post, $"{own.ArtifactPath}/downloads", new { }))
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        var history = $"{own.Root}/backup-shares/download-attempts";
        var mine = await AttemptsAsync(own.Admin, history);
        Assert.Equal(2, mine.Count);
        Assert.All(mine, item => Assert.Equal(ownTenant, item.GetProperty("tenantId").GetGuid()));
        Assert.Contains(mine, item => item.GetProperty("id").GetGuid() == ownAttempt);
        Assert.Contains(mine, item => item.GetProperty("result").GetString() == "Refused");

        // Another tenant's share or artifact gives exactly what an unknown one gives.
        Assert.Empty(await AttemptsAsync(own.Admin, $"{history}?shareId={otherShare}"));
        Assert.Empty(await AttemptsAsync(own.Admin, $"{history}?shareId={Guid.NewGuid()}"));
        Assert.Empty(await AttemptsAsync(own.Admin, $"{history}?artifactId={other.Accepted.ArtifactId}"));
        Assert.Empty(await AttemptsAsync(own.Admin, $"{history}?artifactId={Guid.NewGuid()}"));

        using var foreignRoot = await own.Admin.GetAsync($"{other.Root}/backup-shares/download-attempts");
        Assert.NotEqual(HttpStatusCode.OK, foreignRoot.StatusCode);
        using var platformRoute = await own.Admin.GetAsync("/api/platform/download-attempts");
        Assert.Equal(HttpStatusCode.Forbidden, platformRoute.StatusCode);
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
    public async Task Pruning_RemovesOnlyRecordsPastRetention_KeepsActiveSharesAndRunningDownloads_AndLeavesNotificationsAlone()
    {
        var (scenario, activeShare) = await CreateScenarioWithShareAsync(fixture, duration: TimeSpan.FromHours(48));
        await using var _ = scenario;
        var platformAccount = await scenario.World.SeedUserAsync(platformAdmin: true);
        var platform = await scenario.World.SignInAsync(platformAccount);
        using (var put = await platform.SendAsync(HttpMethod.Put, "/api/platform/settings/audit-retention", new { retentionDays = 30 }))
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        using (var shortShare = await scenario.Admin.SendAsync(HttpMethod.Post, $"{scenario.Root}/backup-artifacts/{scenario.Accepted.ArtifactId}/shares", new { reason = "Short share", durationHours = 1 }))
            Assert.Equal(HttpStatusCode.Created, shortShare.StatusCode);
        var finished = await DownloadAsync(scenario.Admin, scenario, new { replicaId = scenario.ReplicaId });
        var running = (await scenario.CreateSessionAsync()).GetProperty("sessionId").GetGuid();

        var now = scenario.Backup.Clock.GetUtcNow();
        var tenantId = scenario.Backup.Backup.Setup.Tenant.Id;
        AuditEvent Old(string targetType, string targetId, int daysAgo) => AuditEvent.Record(now.AddDays(-daysAgo), platformAccount.UserId, AuditActorKind.PlatformAdmin,
            "test.marker", tenantId, targetType, targetId, new Dictionary<string, object?>());
        var oldPlain = Old("marker", "old", 45);
        var recentPlain = Old("marker", "recent", 10);
        var oldForActiveShare = Old("backup_share", activeShare.ToString("D"), 45);
        var oldForRunningDownload = Old("download", running.ToString("D"), 45);
        var oldNotification = new NotificationEvent(Guid.NewGuid(), tenantId, NotificationEventTypes.BackupFailed, NotificationPhase.Raised, NotificationSeverity.Warning,
            new NotificationSubject("backup_job", Guid.NewGuid(), "Old job"), null, $"prune-test:{Guid.NewGuid():N}", now.AddDays(-45), now.AddDays(-45));
        await using (var seed = fixture.CreatePlatformContext())
        {
            seed.AuditEvents.AddRange(oldPlain, recentPlain, oldForActiveShare, oldForRunningDownload);
            seed.NotificationEvents.Add(oldNotification);
            await seed.SaveChangesAsync();
        }

        async Task<AuditHistoryPruneResult> PruneAsync(DateTimeOffset at)
        {
            await using var scope = scenario.World.Factory.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IBackupShareStore>().PruneHistoryAsync(at, default);
        }

        await PruneAsync(now);
        await using (var context = fixture.CreatePlatformContext())
        {
            var kept = await context.AuditEvents.AsNoTracking().Where(row => row.Action == "test.marker").Select(row => row.Id).ToListAsync();
            Assert.DoesNotContain(oldPlain.Id, kept);
            Assert.Contains(recentPlain.Id, kept);
            Assert.Contains(oldForActiveShare.Id, kept);
            Assert.Contains(oldForRunningDownload.Id, kept);
            Assert.True(await context.NotificationEvents.AnyAsync(row => row.Id == oldNotification.Id));
            Assert.Equal(2, await context.BackupShares.CountAsync(row => row.TenantId == tenantId));
            Assert.Equal(2, await context.DownloadSessions.CountAsync(row => row.Id == finished || row.Id == running));
        }

        // 31 days on: the finished download and the short share are past retention; the running download and the 48-hour share are not.
        await PruneAsync(now.AddDays(31));
        await using (var context = fixture.CreatePlatformContext())
        {
            Assert.False(await context.DownloadSessions.AnyAsync(row => row.Id == finished));
            Assert.True(await context.DownloadSessions.AnyAsync(row => row.Id == running));
            Assert.Equal([activeShare], await context.BackupShares.Where(row => row.TenantId == tenantId).Select(row => row.Id).ToListAsync());
            Assert.True(await context.NotificationEvents.AnyAsync(row => row.Id == oldNotification.Id));
        }
    }
}
