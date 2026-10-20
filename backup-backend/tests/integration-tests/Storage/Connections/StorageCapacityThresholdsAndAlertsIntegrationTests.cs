using System.Net;
using BackupPlatform.Api.Backups;
using BackupPlatform.Api.Storage;
using BackupPlatform.Application.Agents;
using BackupPlatform.Application.Backups;
using BackupPlatform.Application.Storage;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Domain.Agents;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Notifications;
using BackupPlatform.Domain.Storage;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Infrastructure.Persistence;
using BackupPlatform.IntegrationTests.Notifications;
using BackupPlatform.Notifications;
using BackupPlatform.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using BackupCompletion = BackupPlatform.Application.Backups.BackupCompletion;

namespace BackupPlatform.IntegrationTests.Storage.Connections;

public sealed class StorageCapacityThresholdsAndAlertsIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private sealed class DynamicGoogleDriveClient : IGoogleDriveCapacityClient
    {
        public long TotalBytes { get; set; } = 100_000_000_000;
        public long UsedBytes { get; set; } = 50_000_000_000;
        public long FreeBytes => TotalBytes - UsedBytes;
        public bool ShouldThrow { get; set; }

        public Task<(long? TotalBytes, long? UsedBytes, long? FreeBytes)> QueryCapacityAsync(StorageConnection connection, CancellationToken cancellationToken)
        {
            if (ShouldThrow) throw new InvalidOperationException("Simulated drive API outage");
            return Task.FromResult<(long?, long?, long?)>((TotalBytes, UsedBytes, FreeBytes));
        }
    }

    [Fact]
    public void CapacityStatus_EvaluatesThresholdsCorrectly()
    {
        const long total = 100_000_000_000;

        // < 80% -> Available
        Assert.Equal(StorageCapacityStatus.Available, StorageCapacityStatus.Evaluate(total, 50_000_000_000, 50_000_000_000));
        Assert.Equal(StorageCapacityStatus.Available, StorageCapacityStatus.Evaluate(total, 79_900_000_000, 20_100_000_000));

        // 80% to < 90% -> Warning
        Assert.Equal(StorageCapacityStatus.Warning, StorageCapacityStatus.Evaluate(total, 80_000_000_000, 20_000_000_000));
        Assert.Equal(StorageCapacityStatus.Warning, StorageCapacityStatus.Evaluate(total, 85_000_000_000, 15_000_000_000));
        Assert.Equal(StorageCapacityStatus.Warning, StorageCapacityStatus.Evaluate(total, 89_900_000_000, 10_100_000_000));

        // >= 90% -> Critical
        Assert.Equal(StorageCapacityStatus.Critical, StorageCapacityStatus.Evaluate(total, 90_000_000_000, 10_000_000_000));
        Assert.Equal(StorageCapacityStatus.Critical, StorageCapacityStatus.Evaluate(total, 95_000_000_000, 5_000_000_000));
    }

    [Fact]
    public async Task CriticalThreshold_EmitsN1EventAndDeduplicates()
    {
        var driveClient = new DynamicGoogleDriveClient();
        await using var world = await AgentWorld.CreateAsync(fixture, configureServices: services =>
        {
            services.RemoveAll<IGoogleDriveCapacityClient>();
            services.AddSingleton<IGoogleDriveCapacityClient>(driveClient);
        });

        var setup = await world.CreateTenantAsync();
        var tenantId = setup.Tenant.Id;
        var connectionId = Guid.NewGuid();

        // 1. Create a storage connection
        await using (var context = fixture.CreatePlatformContext())
        {
            var connection = new StorageConnection(
                connectionId,
                tenantId,
                "Backup Drive",
                StorageProviderTypes.GoogleDrive,
                StorageOwnership.TenantManaged,
                StorageQuotaScopes.Tenant,
                new Dictionary<string, string> { ["folderId"] = "f-1" },
                false,
                DateTimeOffset.UtcNow);
            connection.AttachSecret("ref-1", ["refreshToken"], DateTimeOffset.UtcNow);
            context.StorageConnections.Add(connection);
            await context.SaveChangesAsync();
        }

        // 2. Set up notification policy and channel for this tenant
        var notifWorld = new NotificationWorld(fixture);
        await notifWorld.EnableAsync(tenantId, eventTypes: [NotificationEventTypes.StorageCapacityCritical], severity: "warning");
        await notifWorld.AddWebhookAsync(tenantId, "https://webhook.example.com/alerts", "Webhook Channel");

        // 3. Probe with 50% usage (< 80%) -> Available, no critical notification
        driveClient.UsedBytes = 50_000_000_000;
        using var probe1 = await setup.Admin.SendAsync(HttpMethod.Post, $"/api/v1/storage/connections/{connectionId}/probe");
        Assert.Equal(HttpStatusCode.OK, probe1.StatusCode);
        var probe1Json = AgentWorld.ReadJson(probe1);
        Assert.Equal("Available", probe1Json.GetProperty("status").GetString());

        await using (var context = fixture.CreatePlatformContext())
        {
            var events = await context.NotificationEvents.Where(e => e.TenantId == tenantId && e.Type == NotificationEventTypes.StorageCapacityCritical).ToListAsync();
            Assert.Empty(events);
        }

        // 4. Probe with 85% usage (>= 80% and < 90%) -> Warning, no critical notification
        world.Clock.Advance(TimeSpan.FromSeconds(1));
        driveClient.UsedBytes = 85_000_000_000;
        using var probe2 = await setup.Admin.SendAsync(HttpMethod.Post, $"/api/v1/storage/connections/{connectionId}/probe");
        Assert.Equal(HttpStatusCode.OK, probe2.StatusCode);
        var probe2Json = AgentWorld.ReadJson(probe2);
        Assert.True(probe2Json.GetProperty("status").GetString() == "Warning", $"Probe2 status is {probe2Json.GetProperty("status").GetString()}, error: {(probe2Json.TryGetProperty("errorMessage", out var err) ? err.GetString() : "none")}");

        await using (var context = fixture.CreatePlatformContext())
        {
            var events = await context.NotificationEvents.Where(e => e.TenantId == tenantId && e.Type == NotificationEventTypes.StorageCapacityCritical).ToListAsync();
            Assert.Empty(events);
        }

        // 5. Probe with 95% usage (>= 90%) -> Critical, emits storage_capacity_critical event!
        world.Clock.Advance(TimeSpan.FromSeconds(1));
        driveClient.UsedBytes = 95_000_000_000;
        using var probe3 = await setup.Admin.SendAsync(HttpMethod.Post, $"/api/v1/storage/connections/{connectionId}/probe");
        Assert.Equal(HttpStatusCode.OK, probe3.StatusCode);
        var probe3Json = AgentWorld.ReadJson(probe3);
        Assert.Equal("Critical", probe3Json.GetProperty("status").GetString());

        await using (var context = fixture.CreatePlatformContext())
        {
            var events = await context.NotificationEvents.Where(e => e.TenantId == tenantId && e.Type == NotificationEventTypes.StorageCapacityCritical).ToListAsync();
            var ev = Assert.Single(events);
            Assert.Equal(NotificationEventTypes.StorageCapacityCritical, ev.Type);
            Assert.Equal(NotificationSeverity.Critical, ev.Severity);
            Assert.Equal(NotificationPhase.Raised, ev.Phase);
            Assert.Equal(connectionId, ev.SubjectId);

            var deliveries = await context.NotificationDeliveries.Where(d => d.EventId == ev.Id).ToListAsync();
            Assert.NotEmpty(deliveries);
        }

        // 6. Deduplication: Probe again while still at 95% -> No new event should be created
        world.Clock.Advance(TimeSpan.FromSeconds(1));
        using var probe4 = await setup.Admin.SendAsync(HttpMethod.Post, $"/api/v1/storage/connections/{connectionId}/probe");

        await using (var context = fixture.CreatePlatformContext())
        {
            var events = await context.NotificationEvents.Where(e => e.TenantId == tenantId && e.Type == NotificationEventTypes.StorageCapacityCritical).ToListAsync();
            Assert.Single(events); // Still only 1 event, not duplicated!
        }
        // 6b. Flap Prevention: Probe fails and returns CapacityUnavailable -> Condition must NOT resolve or flap!
        world.Clock.Advance(TimeSpan.FromSeconds(1));
        driveClient.ShouldThrow = true;
        using var probeUnavailable = await setup.Admin.SendAsync(HttpMethod.Post, $"/api/v1/storage/connections/{connectionId}/probe");
        var unavailJson = AgentWorld.ReadJson(probeUnavailable);
        Assert.Equal("CapacityUnavailable", unavailJson.GetProperty("status").GetString());

        await using (var context = fixture.CreatePlatformContext())
        {
            var events = await context.NotificationEvents.Where(e => e.TenantId == tenantId && e.Type == NotificationEventTypes.StorageCapacityCritical).ToListAsync();
            Assert.Single(events); // Still only 1 event: no fake "Recovered" event!
        }

        // 6c. Probe succeeds at 95% again -> Still active, no new Raised event!
        world.Clock.Advance(TimeSpan.FromSeconds(1));
        driveClient.ShouldThrow = false;
        driveClient.UsedBytes = 95_000_000_000;
        using var probeBack = await setup.Admin.SendAsync(HttpMethod.Post, $"/api/v1/storage/connections/{connectionId}/probe");
        Assert.Equal("Critical", AgentWorld.ReadJson(probeBack).GetProperty("status").GetString());

        await using (var context = fixture.CreatePlatformContext())
        {
            var events = await context.NotificationEvents.Where(e => e.TenantId == tenantId && e.Type == NotificationEventTypes.StorageCapacityCritical).ToListAsync();
            Assert.Single(events); // Still only 1 event: no duplicate!
        }
        world.Clock.Advance(TimeSpan.FromSeconds(1));
        driveClient.UsedBytes = 60_000_000_000;
        using var probe5 = await setup.Admin.SendAsync(HttpMethod.Post, $"/api/v1/storage/connections/{connectionId}/probe");

        await using (var context = fixture.CreatePlatformContext())
        {
            var events = await context.NotificationEvents.Where(e => e.TenantId == tenantId && e.Type == NotificationEventTypes.StorageCapacityCritical).OrderBy(e => e.CreatedAt).ToListAsync();
            Assert.Equal(2, events.Count);
            Assert.Equal(NotificationPhase.Raised, events[0].Phase);
            Assert.Equal(NotificationPhase.Recovered, events[1].Phase);
        }
    }


    [Fact]
    public async Task PeriodicProbeWorker_ProbesStaleConnections_AndSkipsRecent()
    {
        var driveClient = new DynamicGoogleDriveClient();
        await using var world = await AgentWorld.CreateAsync(fixture, configureServices: services =>
        {
            services.RemoveAll<IGoogleDriveCapacityClient>();
            services.AddSingleton<IGoogleDriveCapacityClient>(driveClient);
        });

        var setup = await world.CreateTenantAsync();
        var tenantId = setup.Tenant.Id;

        var staleConnectionId = Guid.NewGuid();
        var recentConnectionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await using (var context = fixture.CreatePlatformContext())
        {
            var staleConn = new StorageConnection(
                staleConnectionId, tenantId, "Stale Drive", StorageProviderTypes.GoogleDrive,
                StorageOwnership.TenantManaged, StorageQuotaScopes.Tenant,
                new Dictionary<string, string> { ["folderId"] = "f-1" }, false, now.AddDays(-5));
            staleConn.AttachSecret("ref-stale", ["refreshToken"], now.AddDays(-5));

            var recentConn = new StorageConnection(
                recentConnectionId, tenantId, "Recent Drive", StorageProviderTypes.GoogleDrive,
                StorageOwnership.TenantManaged, StorageQuotaScopes.Tenant,
                new Dictionary<string, string> { ["folderId"] = "f-2" }, false, now);
            recentConn.AttachSecret("ref-recent", ["refreshToken"], now);

            context.StorageConnections.AddRange(staleConn, recentConn);

            // Stale snapshot: 25 hours ago (> 24 hours)
            context.StorageCapacitySnapshots.Add(StorageCapacitySnapshot.AvailableResult(
                tenantId, staleConnectionId, 100_000_000_000, 20_000_000_000, 80_000_000_000, now.AddHours(-25)));

            // Recent snapshot: 2 hours ago (< 24 hours)
            context.StorageCapacitySnapshots.Add(StorageCapacitySnapshot.AvailableResult(
                tenantId, recentConnectionId, 100_000_000_000, 30_000_000_000, 70_000_000_000, now.AddHours(-2)));

            await context.SaveChangesAsync();
        }

        var worker = world.Factory.Services.GetRequiredService<StorageCapacityPeriodicProbeWorker>();
        var probedCount = await worker.RunPeriodicSweepAsync(default);

        // Only the stale connection was probed
        Assert.True(probedCount >= 1);

        await using (var context = fixture.CreatePlatformContext())
        {
            var latestStaleSnapshot = await context.StorageCapacitySnapshots
                .Where(s => s.TenantId == tenantId && s.ConnectionId == staleConnectionId)
                .OrderByDescending(s => s.ReportedAt)
                .FirstAsync();

            // ReportedAt should now be updated to fresh time
            Assert.True(latestStaleSnapshot.ReportedAt > now.AddMinutes(-5));

            var latestRecentSnapshot = await context.StorageCapacitySnapshots
                .Where(s => s.TenantId == tenantId && s.ConnectionId == recentConnectionId)
                .OrderByDescending(s => s.ReportedAt)
                .FirstAsync();

            // Recent snapshot should remain 2 hours ago (skipped)
            Assert.True(latestRecentSnapshot.ReportedAt < now.AddHours(-1));
        }
    }

    [Fact]
    public async Task TransferCompletion_WorkerDrainQueue_ExecutesProbeAndPersistsSnapshot()
    {
        var driveClient = new DynamicGoogleDriveClient { TotalBytes = 200_000_000_000, UsedBytes = 50_000_000_000 };
        await using var world = await AgentWorld.CreateAsync(fixture, configureServices: services =>
        {
            services.RemoveAll<IGoogleDriveCapacityClient>();
            services.AddSingleton<IGoogleDriveCapacityClient>(driveClient);
        });

        var setup = await world.CreateTenantAsync();
        var tenantId = setup.Tenant.Id;
        var connectionId = Guid.NewGuid();

        await using (var context = fixture.CreatePlatformContext())
        {
            var connection = new StorageConnection(
                connectionId, tenantId, "Target Drive", StorageProviderTypes.GoogleDrive,
                StorageOwnership.TenantManaged, StorageQuotaScopes.Tenant,
                new Dictionary<string, string> { ["folderId"] = "f-1" }, false, DateTimeOffset.UtcNow);
            connection.AttachSecret("ref-1", ["refreshToken"], DateTimeOffset.UtcNow);
            context.StorageConnections.Add(connection);
            await context.SaveChangesAsync();
        }

        var worker = world.Factory.Services.GetRequiredService<StorageCapacityPeriodicProbeWorker>();
        worker.EnqueueProbe(tenantId, connectionId);

        // The background worker processes the queued probe
        StorageCapacitySnapshot? snapshot = null;
        for (var i = 0; i < 50; i++)
        {
            await using var context = fixture.CreatePlatformContext();
            snapshot = await context.StorageCapacitySnapshots
                .Where(s => s.TenantId == tenantId && s.ConnectionId == connectionId)
                .OrderByDescending(s => s.ReportedAt)
                .FirstOrDefaultAsync();
            if (snapshot != null) break;
            await Task.Delay(100);
        }
        Assert.NotNull(snapshot);
        Assert.Equal("Available", snapshot.Status);
        Assert.Equal(200_000_000_000, snapshot.TotalBytes);
        Assert.Equal(50_000_000_000, snapshot.UsedBytes);
    }
}
