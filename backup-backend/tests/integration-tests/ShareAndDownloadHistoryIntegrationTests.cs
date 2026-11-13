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

    /// <summary>Walks every page of the history by following the cursor.</summary>
    private static async Task<List<JsonElement>> AttemptsAsync(BrowserSession session, string path, int? limit = null, string? cursor = null)
    {
        var all = new List<JsonElement>();
        do
        {
            var (items, next) = await PageAsync(session, path, limit, cursor);
            all.AddRange(items);
            cursor = next;
        }
        while (cursor is not null);
        return all;
    }

    private static async Task<(List<JsonElement> Items, string? Next)> PageAsync(BrowserSession session, string path, int? limit = null, string? cursor = null)
    {
        var query = string.Join('&', new[] { limit is null ? null : $"limit={limit}", cursor is null ? null : $"cursor={cursor}" }.Where(part => part is not null));
        using var response = await session.GetAsync(query.Length == 0 ? path : path + (path.Contains('?') ? '&' : '?') + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = AgentWorld.ReadJson(response);
        var next = json.GetProperty("nextCursor");
        return (json.GetProperty("items").EnumerateArray().ToList(), next.ValueKind == JsonValueKind.Null ? null : next.GetString());
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
    public async Task SeparateRefusalDoesNotHidePreparedDownload()
    {
        var (scenario, shareId) = await CreateScenarioWithShareAsync(fixture);
        await using var _ = scenario;
        var account = await scenario.World.SeedUserAsync(platformAdmin: true);
        var platform = await scenario.World.SignInAsync(account);
        using var prepared = await platform.SendAsync(HttpMethod.Post, $"{scenario.ArtifactPath}/downloads", new { shareId });
        Assert.Equal(HttpStatusCode.OK, prepared.StatusCode);
        var sessionId = AgentWorld.ReadJson(prepared).GetProperty("sessionId").GetGuid();
        await scenario.Store().RecordRefusalAsync(new DownloadRefusalCommand(
            scenario.Backup.Backup.Setup.Tenant.Id, scenario.Accepted.ArtifactId, account.UserId.Value,
            shareId, "replica_not_found", scenario.Backup.Clock.GetUtcNow().AddSeconds(1), true), default);
        var attempts = await AttemptsAsync(platform, $"/api/platform/download-attempts?shareId={shareId}");
        Assert.Contains(attempts, item => item.GetProperty("id").GetGuid() == sessionId);
        Assert.Contains(attempts, item => item.GetProperty("result").GetString() == "Refused");
    }

    [Fact]
    public async Task Pagination_ReachesEveryAttemptBeyond500_InStableOrder_WhileNewAttemptsArrive()
    {
        var (scenario, shareId) = await CreateScenarioWithShareAsync(fixture);
        await using var _ = scenario;
        var account = await scenario.World.SeedUserAsync(platformAdmin: true);
        var platform = await scenario.World.SignInAsync(account);
        var tenant = scenario.Backup.Backup.Setup.Tenant.Id;
        var store = scenario.Store();
        var start = scenario.Backup.Clock.GetUtcNow().AddDays(-1);
        // 520 refused attempts; every three share one timestamp, so the id tie-break is exercised.
        for (var index = 0; index < 520; index++)
            await store.RecordRefusalAsync(new DownloadRefusalCommand(tenant, scenario.Accepted.ArtifactId, account.UserId.Value, shareId, "share_expired", start.AddSeconds(index / 3), true), default);

        var path = $"/api/platform/download-attempts?tenantId={tenant.Value}";
        var (first, cursor) = await PageAsync(platform, path, limit: 100);
        Assert.Equal(100, first.Count);
        Assert.NotNull(cursor);

        // Attempts made while someone is paging: a prepared download and a new refusal, both newer than the cursor.
        var arrived = (await scenario.CreateSessionAsync()).GetProperty("sessionId").GetGuid();
        await store.RecordRefusalAsync(new DownloadRefusalCommand(tenant, scenario.Accepted.ArtifactId, account.UserId.Value, shareId, "share_expired", scenario.Backup.Clock.GetUtcNow(), true), default);

        var walked = first.Concat(await AttemptsAsync(platform, path, limit: 100, cursor: cursor)).ToList();
        Assert.Equal(520, walked.Count);
        Assert.Equal(520, walked.Select(item => item.GetProperty("id").GetGuid()).Distinct().Count());
        Assert.DoesNotContain(walked, item => item.GetProperty("id").GetGuid() == arrived);
        var times = walked.Select(item => item.GetProperty("startedAtUtc").GetDateTimeOffset()).ToList();
        Assert.Equal(times.OrderByDescending(time => time), times);
        Assert.Equal(start.AddSeconds(0), times[^1]);

        // A fresh walk sees everything, on both routes and with a different page size.
        Assert.Equal(522, (await AttemptsAsync(platform, path)).Count);
        var tenantWalk = await AttemptsAsync(scenario.Admin, $"{scenario.Root}/backup-shares/download-attempts", limit: 200);
        Assert.Equal(522, tenantWalk.Select(item => item.GetProperty("id").GetGuid()).Distinct().Count());
        Assert.Equal(521, (await AttemptsAsync(platform, $"{path}&shareId={shareId}&result=Refused", limit: 200)).Count);

        foreach (var bad in new[] { "limit=0", "limit=201", "cursor=nonsense" })
        {
            using var response = await platform.GetAsync($"{path}&{bad}");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        using var anonymous = await scenario.World.Anonymous.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task RefusalOfAPreparedDownload_IsShownOnThatSessionOnly_AmongConcurrentAttemptsOnTheSameShare()
    {
        var (scenario, shareId) = await CreateScenarioWithShareAsync(fixture);
        await using var _ = scenario;
        var account = await scenario.World.SeedUserAsync(platformAdmin: true);
        var platform = await scenario.World.SignInAsync(account);

        // Two independent attempts with the same tenant, artifact, share and user, prepared at the same time.
        async Task<JsonElement> PrepareAsync()
        {
            using var prepared = await platform.SendAsync(HttpMethod.Post, $"{scenario.ArtifactPath}/downloads", new { shareId });
            Assert.Equal(HttpStatusCode.OK, prepared.StatusCode);
            return AgentWorld.ReadJson(prepared);
        }
        var prepared = await Task.WhenAll(PrepareAsync(), PrepareAsync());
        var refusedSession = prepared[0].GetProperty("sessionId").GetGuid();
        var otherSession = prepared[1].GetProperty("sessionId").GetGuid();

        using (var revoke = await scenario.Admin.SendAsync(HttpMethod.Post, $"{scenario.Root}/backup-shares/{shareId}/revoke", new { reason = "Correlation test" }))
            Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        using (var content = await platform.Client.GetAsync(prepared[0].GetProperty("downloadUrl").GetString()!))
            Assert.NotEqual(HttpStatusCode.OK, content.StatusCode);

        var attempts = await AttemptsAsync(platform, $"/api/platform/download-attempts?shareId={shareId}");
        Assert.Equal(new[] { refusedSession, otherSession }.Order(), attempts.Select(item => item.GetProperty("id").GetGuid()).Order());
        var refused = attempts.Single(item => item.GetProperty("id").GetGuid() == refusedSession);
        Assert.Equal("Refused", refused.GetProperty("result").GetString());
        Assert.Equal("share_revoked", refused.GetProperty("failureReason").GetString());
        Assert.NotEqual(JsonValueKind.Null, refused.GetProperty("endedAtUtc").ValueKind);
        Assert.NotEqual("Refused", attempts.Single(item => item.GetProperty("id").GetGuid() == otherSession).GetProperty("result").GetString());

        // A refused request that names the other session's id as its share id is its own attempt; it cannot mark that session as refused.
        using (var forged = await platform.SendAsync(HttpMethod.Post, $"{scenario.ArtifactPath}/downloads", new { shareId = otherSession }))
            Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);
        var afterForgery = await AttemptsAsync(platform, $"/api/platform/download-attempts?tenantId={scenario.Backup.Backup.Setup.Tenant.Id.Value}");
        Assert.NotEqual("Refused", afterForgery.Single(item => item.GetProperty("id").GetGuid() == otherSession).GetProperty("result").GetString());
        Assert.Equal(3, afterForgery.Count);
        using (var list = await platform.GetAsync("/api/platform/download-attempts?result=Ready,Failed"))
            Assert.Equal(HttpStatusCode.BadRequest, list.StatusCode);

        // The result filter follows the same rule, and the refusal is not a second row.
        Assert.Equal([refusedSession], (await AttemptsAsync(platform, $"/api/platform/download-attempts?shareId={shareId}&result=Refused")).Select(item => item.GetProperty("id").GetGuid()));
        Assert.DoesNotContain(await AttemptsAsync(platform, $"/api/platform/download-attempts?shareId={shareId}&result=Ready"), item => item.GetProperty("id").GetGuid() == refusedSession);
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
        var oldRefusal = AuditEvent.Record(now.AddDays(-45), platformAccount.UserId, AuditActorKind.PlatformAdmin,
            "download.refused", tenantId, "download", activeShare.ToString("D"), new Dictionary<string, object?> { ["shareId"] = activeShare });
        var oldNotification = new NotificationEvent(Guid.NewGuid(), tenantId, NotificationEventTypes.BackupFailed, NotificationPhase.Raised, NotificationSeverity.Warning,
            new NotificationSubject("backup_job", Guid.NewGuid(), "Old job"), null, $"prune-test:{Guid.NewGuid():N}", now.AddDays(-45), now.AddDays(-45));
        await using (var seed = fixture.CreatePlatformContext())
        {
            seed.AuditEvents.AddRange(oldPlain, recentPlain, oldForActiveShare, oldForRunningDownload, oldRefusal);
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
            Assert.True(await context.AuditEvents.AnyAsync(row => row.Id == oldRefusal.Id));
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

    [Fact]
    public async Task RetentionChangeAndPruning_AreSerializedAcrossApiInstances_AndAPruneUsesTheCommittedSetting()
    {
        var (scenario, _) = await CreateScenarioWithShareAsync(fixture);
        await using var _1 = scenario;
        // A second API host on the same database.
        await using var second = await AgentWorld.CreateAsync(fixture, clock: scenario.Backup.Clock);
        var actor = (await scenario.World.SeedUserAsync(platformAdmin: true)).UserId;
        var now = scenario.Backup.Clock.GetUtcNow();
        await using var firstScope = scenario.World.Factory.Services.CreateAsyncScope();
        await using var secondScope = second.Factory.Services.CreateAsyncScope();
        var firstStore = firstScope.ServiceProvider.GetRequiredService<IBackupShareStore>();
        var secondStore = secondScope.ServiceProvider.GetRequiredService<IBackupShareStore>();
        await firstStore.SaveAuditRetentionAsync(30, actor, now, default);
        var old = AuditEvent.Record(now.AddDays(-45), actor, AuditActorKind.PlatformAdmin, "test.lock_marker", null, "marker", "old", new Dictionary<string, object?>());
        await using (var seed = fixture.CreatePlatformContext())
        {
            seed.AuditEvents.Add(old);
            await seed.SaveChangesAsync();
        }

        Task<AuditHistoryPruneResult> prune;
        Task<AuditRetentionSettingsView> update;
        await using (var holder = fixture.CreatePlatformContext())
        {
            // Stands for a retention change in flight on a third connection: it holds the shared lock and raises the retention to 90 days.
            await using var transaction = await holder.Database.BeginTransactionAsync();
            await holder.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtext('audit_retention'))");
            prune = secondStore.PruneHistoryAsync(now, default);
            update = firstStore.SaveAuditRetentionAsync(120, actor, now, default);
            await Task.Delay(TimeSpan.FromSeconds(1));
            Assert.False(prune.IsCompleted);
            Assert.False(update.IsCompleted);
            await holder.Database.ExecuteSqlRawAsync("UPDATE platform_audit_retention_settings SET \"RetentionDays\" = 90");
            await transaction.CommitAsync();
        }
        await Task.WhenAll(prune, update).WaitAsync(TimeSpan.FromSeconds(20));

        // The prune started while 30 days was current, but it ran after the change was committed: the 45-day-old record survives.
        await using var context = fixture.CreatePlatformContext();
        Assert.True(await context.AuditEvents.AnyAsync(row => row.Id == old.Id));
        Assert.Equal(120, (await secondStore.GetAuditRetentionAsync(default)).RetentionDays);
    }

    [Fact]
    public async Task Pruning_LeavesTheSealsOfRemainingAuditEventsVerifiable()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var actor = (await scenario.World.SeedUserAsync(platformAdmin: true)).UserId;
        var now = scenario.Clock.GetUtcNow();
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var signing = new DbContextOptionsBuilder<BackupPlatform.Infrastructure.Persistence.PlatformDbContext>(fixture.Options)
            .AddInterceptors(new BackupPlatform.Infrastructure.Persistence.AuditSigningInterceptor("prune-test-key", key.ExportPkcs8PrivateKeyPem())).Options;
        var old = AuditEvent.Record(now.AddDays(-400), actor, AuditActorKind.PlatformAdmin, "test.sealed_marker", null, "marker", "old", new Dictionary<string, object?>());
        var kept = AuditEvent.Record(now.AddDays(-5), actor, AuditActorKind.PlatformAdmin, "test.sealed_marker", null, "marker", "kept", new Dictionary<string, object?> { ["value"] = 1.5 });
        await using (var writer = BackupPlatform.Infrastructure.Persistence.PlatformDbContext.CreateForPlatformOperations(signing))
        {
            writer.AuditEvents.AddRange(old, kept);
            await writer.SaveChangesAsync();
        }
        await using (var scope = scenario.World.Factory.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IBackupShareStore>();
            await store.SaveAuditRetentionAsync(365, actor, now, default);
            Assert.True((await store.PruneHistoryAsync(now, default)).AuditEventsRemoved >= 1);
        }

        await using var reader = fixture.CreatePlatformContext();
        var remaining = await reader.AuditEvents.Where(row => row.Action == "test.sealed_marker").ToListAsync();
        var survivor = Assert.Single(remaining);
        Assert.Equal(kept.Id, survivor.Id);
        var signature = (string)reader.Entry(survivor).Property(BackupPlatform.Infrastructure.Persistence.AuditSigningInterceptor.SignatureProperty).CurrentValue!;
        Assert.True(BackupPlatform.Infrastructure.Persistence.AuditSigningInterceptor.Verify(survivor, signature, key.ExportSubjectPublicKeyInfoPem()));
    }
}
