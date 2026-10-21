extern alias AgentCore;
extern alias AgentTransport;

using System.Net;
using AgentCore::BackupPlatform.Agent.Core.Storage;
using AgentCore::BackupPlatform.Agent.Core.Identity;
using AgentCore::BackupPlatform.Agent.Core.Tasks;
using AgentCore::BackupPlatform.Agent.Core.Connectivity;
using AgentTransport::BackupPlatform.Agent.Transport;
using BackupPlatform.Contracts.Agents;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using BackupPlatform.Contracts.Storage;
using BackupPlatform.Api.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Storage;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace BackupPlatform.IntegrationTests.Storage.Connections;

public sealed class StorageCapacityProbeIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private sealed class FakeGoogleDriveCapacityClient(long totalBytes, long usedBytes, long freeBytes) : IGoogleDriveCapacityClient
    {
        public Task<(long? TotalBytes, long? UsedBytes, long? FreeBytes)> QueryCapacityAsync(StorageConnection connection, CancellationToken cancellationToken) =>
            Task.FromResult<(long?, long?, long?)>((totalBytes, usedBytes, freeBytes));
    }

    private sealed class FakeSftpCapacityProbeExecutor(long totalBytes, long usedBytes, long freeBytes) : ISftpCapacityProbeExecutor
    {
        public Task<CapacityProbeTaskResult> ProbeAsync(CapacityProbeTaskRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(CapacityProbeTaskResult.Success(request.TaskId, request.ConnectionId, totalBytes, usedBytes, freeBytes));
    }

    [Fact]
    public async Task BackendGoogleDriveProbe_QueriesDirectAndPersistsSnapshot()
    {
        const long total = 107_374_182_400; // 100 GB
        const long used = 26_843_545_600;  // 25 GB
        const long free = 80_530_636_800;  // 75 GB

        await using var world = await AgentWorld.CreateAsync(fixture, configureServices: services =>
        {
            services.RemoveAll<IGoogleDriveCapacityClient>();
            services.AddSingleton<IGoogleDriveCapacityClient>(new FakeGoogleDriveCapacityClient(total, used, free));
        });

        var setup = await world.CreateTenantAsync();
        var connectionId = Guid.NewGuid();

        await using (var context = fixture.CreatePlatformContext())
        {
            var connection = new StorageConnection(
                connectionId,
                setup.Tenant.Id,
                "Google Drive Primary",
                StorageProviderTypes.GoogleDrive,
                StorageOwnership.TenantManaged,
                StorageQuotaScopes.Tenant,
                new Dictionary<string, string> { ["folderId"] = "folder-123" },
                false,
                DateTimeOffset.UtcNow);
            connection.AttachSecret("ref-gdrive-1", ["refreshToken"], DateTimeOffset.UtcNow);
            context.StorageConnections.Add(connection);
            await context.SaveChangesAsync();
        }

        // 1. Probe via REST API POST /api/v1/storage/connections/{id}/probe
        using var probeResponse = await setup.Admin.SendAsync(HttpMethod.Post, $"/api/v1/storage/connections/{connectionId}/probe");
        Assert.Equal(HttpStatusCode.OK, probeResponse.StatusCode);

        var probeJson = AgentWorld.ReadJson(probeResponse);
        Assert.Equal("Available", probeJson.GetProperty("status").GetString());
        Assert.Equal(total, probeJson.GetProperty("totalBytes").GetInt64());
        Assert.Equal(used, probeJson.GetProperty("usedBytes").GetInt64());
        Assert.Equal(free, probeJson.GetProperty("freeBytes").GetInt64());

        // 2. Query via REST API GET /api/v1/storage/connections/{id}/capacity
        using var getResponse = await setup.Admin.GetAsync($"/api/v1/storage/connections/{connectionId}/capacity");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);

        var getJson = AgentWorld.ReadJson(getResponse);
        Assert.Equal("Available", getJson.GetProperty("status").GetString());
        Assert.Equal(total, getJson.GetProperty("totalBytes").GetInt64());
        Assert.Equal(used, getJson.GetProperty("usedBytes").GetInt64());
        Assert.Equal(free, getJson.GetProperty("freeBytes").GetInt64());

        // 3. Verify in PostgreSQL database
        await using (var context = fixture.CreatePlatformContext())
        {
            var snapshot = await context.StorageCapacitySnapshots
                .Where(s => s.TenantId == setup.Tenant.Id && s.ConnectionId == connectionId)
                .OrderByDescending(s => s.ReportedAt)
                .FirstOrDefaultAsync();

            Assert.NotNull(snapshot);
            Assert.Equal("Available", snapshot.Status);
            Assert.Equal(total, snapshot.TotalBytes);
            Assert.Equal(used, snapshot.UsedBytes);
            Assert.Equal(free, snapshot.FreeBytes);
        }
    }

    [Fact]
    public async Task AgentLocalDiskProbe_QueuesAuthenticatedTask_RealWorkerMeasuresRequestedFilesystem()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var setup = await world.CreateTenantAsync();
        var agent = await ReadyAgentAsync(world, setup);
        var root = Path.GetPathRoot(Environment.CurrentDirectory)!;
        var connectionId = await AddConnectionAsync(world, setup, "local", new() { ["volume"] = "test-disk" });
        using var queued = await setup.Admin.SendAsync(HttpMethod.Post, $"/api/v1/storage/connections/{connectionId}/probe");
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        var pending = AgentWorld.ReadJson(queued);
        Assert.Equal("Pending", pending.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, pending.GetProperty("totalBytes").ValueKind);
        using var duplicate = await setup.Admin.SendAsync(HttpMethod.Post, $"/api/v1/storage/connections/{connectionId}/probe");
        Assert.Equal(pending.GetProperty("taskId").GetGuid(), AgentWorld.ReadJson(duplicate).GetProperty("taskId").GetGuid());
        var measured = await ExecuteWorkerAsync(world, setup, agent, connectionId, new CapacityProbeTaskHandler(localVolumeDirectories: new Dictionary<string, string> { ["test-disk"] = root }));
        Assert.Contains(measured.GetProperty("status").GetString(), new[] { "Available", "Warning", "Critical" });
        Assert.Equal(new DriveInfo(root).TotalSize, measured.GetProperty("totalBytes").GetInt64());
        Assert.True(measured.GetProperty("freeBytes").GetInt64() > 0);
        await using var db = fixture.CreatePlatformContext();
        var snapshot = Assert.Single(await db.StorageCapacitySnapshots.Where(s => s.TenantId == setup.Tenant.Id && s.ConnectionId == connectionId).ToListAsync());
        Assert.Equal(measured.GetProperty("totalBytes").GetInt64(), snapshot.TotalBytes);
        world.Clock.Advance(TimeSpan.FromSeconds(121));
        using var offline = await setup.Admin.GetAsync($"/api/v1/storage/connections/{connectionId}/capacity");
        var unavailable = AgentWorld.ReadJson(offline);
        Assert.Equal("CapacityUnavailable", unavailable.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, unavailable.GetProperty("totalBytes").ValueKind);
        Assert.Equal(snapshot.TotalBytes, Assert.Single(unavailable.GetProperty("history").EnumerateArray()).GetProperty("totalBytes").GetInt64());
    }

    [Fact]
    public async Task AgentSftpProbe_UsesDurableHttpTaskAndRealWorkerWithProviderMeasurement()
    {
        const long total = 500_000_000_000, used = 100_000_000_000, free = 400_000_000_000;
        await using var world = await AgentWorld.CreateAsync(fixture);
        var setup = await world.CreateTenantAsync();
        var agent = await ReadyAgentAsync(world, setup);
        var connectionId = await AddConnectionAsync(world, setup, "sftp", new()
        {
            ["host"] = "sftp.lan", ["port"] = "22", ["username"] = "backupuser",
            ["remoteBaseDirectory"] = "/backups", ["hostKeyFingerprint"] = "SHA256:abcd"
        });
        using var queued = await setup.Admin.SendAsync(HttpMethod.Post, $"/api/v1/storage/connections/{connectionId}/probe");
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        var measured = await ExecuteWorkerAsync(world, setup, agent, connectionId,
            new CapacityProbeTaskHandler(new FakeSftpCapacityProbeExecutor(total, used, free)));
        Assert.Equal("Available", measured.GetProperty("status").GetString());
        Assert.Equal(total, measured.GetProperty("totalBytes").GetInt64());
        Assert.Equal(used, measured.GetProperty("usedBytes").GetInt64());
        Assert.Equal(free, measured.GetProperty("freeBytes").GetInt64());
        await using var db = fixture.CreatePlatformContext();
        Assert.Equal(total, (await db.StorageCapacitySnapshots.SingleAsync(s => s.TenantId == setup.Tenant.Id && s.ConnectionId == connectionId)).TotalBytes);
    }

    [Fact]
    public async Task WhenNoActiveAgentOnline_ReportsCapacityUnavailableWithoutFakeData()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);

        var setup = await world.CreateTenantAsync();
        // Notice: NO agent enrolled or online!
        var connectionId = Guid.NewGuid();

        await using (var context = fixture.CreatePlatformContext())
        {
            var connection = new StorageConnection(
                connectionId,
                setup.Tenant.Id,
                "Orphan Local",
                StorageProviderTypes.Local,
                StorageOwnership.TenantManaged,
                StorageQuotaScopes.Tenant,
                new Dictionary<string, string> { ["volume"] = "C:" },
                false,
                DateTimeOffset.UtcNow);
            context.StorageConnections.Add(connection);
            await context.SaveChangesAsync();
        }

        using var probeResponse = await setup.Admin.SendAsync(HttpMethod.Post, $"/api/v1/storage/connections/{connectionId}/probe");
        Assert.Equal(HttpStatusCode.OK, probeResponse.StatusCode);

        var probeJson = AgentWorld.ReadJson(probeResponse);
        Assert.Equal("CapacityUnavailable", probeJson.GetProperty("status").GetString());
        Assert.True(probeJson.GetProperty("totalBytes").ValueKind == System.Text.Json.JsonValueKind.Null);
        Assert.True(probeJson.GetProperty("usedBytes").ValueKind == System.Text.Json.JsonValueKind.Null);
        Assert.True(probeJson.GetProperty("freeBytes").ValueKind == System.Text.Json.JsonValueKind.Null);

        // Verify in PostgreSQL database
        await using (var context = fixture.CreatePlatformContext())
        {
            var snapshot = await context.StorageCapacitySnapshots
                .Where(s => s.TenantId == setup.Tenant.Id && s.ConnectionId == connectionId)
                .OrderByDescending(s => s.ReportedAt)
                .FirstOrDefaultAsync();

            Assert.NotNull(snapshot);
            Assert.Equal("CapacityUnavailable", snapshot.Status);
            Assert.Null(snapshot.TotalBytes);
            Assert.Null(snapshot.UsedBytes);
            Assert.Null(snapshot.FreeBytes);
        }
    }

    [Fact]
    public async Task CapacityProbe_EnforcesTenantIsolation()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);

        var tenantA = await world.CreateTenantAsync();
        var tenantB = await world.CreateTenantAsync();

        var connectionAId = Guid.NewGuid();
        await using (var context = fixture.CreatePlatformContext())
        {
            var connectionA = new StorageConnection(
                connectionAId,
                tenantA.Tenant.Id,
                "Tenant A Storage",
                StorageProviderTypes.Local,
                StorageOwnership.TenantManaged,
                StorageQuotaScopes.Tenant,
                new Dictionary<string, string>(),
                false,
                DateTimeOffset.UtcNow);
            context.StorageConnections.Add(connectionA);
            await context.SaveChangesAsync();
        }

        // Tenant B attempts to probe Tenant A's connection -> 404 NotFound
        using var probeResponse = await tenantB.Admin.SendAsync(HttpMethod.Post, $"/api/v1/storage/connections/{connectionAId}/probe");
        Assert.Equal(HttpStatusCode.NotFound, probeResponse.StatusCode);

        // Tenant B attempts to get Tenant A's capacity -> 404 NotFound
        using var getResponse = await tenantB.Admin.GetAsync($"/api/v1/storage/connections/{connectionAId}/capacity");
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [Fact]
    public async Task ClaimsAreDurable_ReclaimedAfterExpiry_AndCompletionsAreFencedAndIdempotent()
    {
        TenantSetup setup;
        EnrolledAgent agent;
        EnrolledAgent foreign;
        Guid connectionId;
        CapacityProbeTaskRequest first;
        var clock = new MutableClock();
        await using (var original = await AgentWorld.CreateAsync(fixture, clock: clock))
        {
            setup = await original.CreateTenantAsync();
            agent = await ReadyAgentAsync(original, setup);
            foreign = await ReadyAgentAsync(original, await original.CreateTenantAsync());
            connectionId = await AddConnectionAsync(original, setup, "local", new() { ["volume"] = "local" });
            using var queued = await setup.Admin.SendAsync(HttpMethod.Post, $"/api/v1/storage/connections/{connectionId}/probe");
            Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
            first = await ClaimAsync(original, agent);
            using var empty = await original.PollAsync(agent);
            Assert.Empty(AgentWorld.ReadJson(empty).GetProperty("tasks").EnumerateArray());
            using var foreignPoll = await original.PollAsync(foreign);
            Assert.Empty(AgentWorld.ReadJson(foreignPoll).GetProperty("tasks").EnumerateArray());
            using var denied = await CompleteAsync(original, foreign, first);
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
            var sibling = await original.EnrollAsync(setup, installationId: Guid.NewGuid());
            await DeclareCapacityAsync(original, sibling);
            using var wrongOwner = await CompleteAsync(original, sibling, first);
            Assert.Equal(HttpStatusCode.Conflict, wrongOwner.StatusCode);
            using var siblingPoll = await original.PollAsync(sibling);
            Assert.Empty(AgentWorld.ReadJson(siblingPoll).GetProperty("tasks").EnumerateArray());
        }

        clock.Advance(TimeSpan.FromSeconds(121));
        await using var restarted = await AgentWorld.CreateAsync(fixture, clock: clock);
        using (await restarted.HeartbeatAsync(agent, AgentWorld.Heartbeat(capabilityRevision: AgentWorld.CapabilitySnapshot(AgentWorld.CapabilityReport(CapacityProbeProtocol.Capability)).Revision))) { }
        var second = await ClaimAsync(restarted, agent);
        Assert.Equal(first.TaskId, second.TaskId);
        Assert.NotEqual(first.LeaseToken, second.LeaseToken);
        using var stale = await CompleteAsync(restarted, agent, first);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var completed = await CompleteAsync(restarted, agent, second);
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        using var replay = await CompleteAsync(restarted, agent, second);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using var altered = await restarted.SendAgentAsync(HttpMethod.Post, $"/api/agent/capacity-probes/{second.TaskId}/complete",
            agent.CredentialId, agent.Secret, body: new CapacityProbeCompletionRequest
            {
                LeaseToken = second.LeaseToken,
                Result = CapacityProbeTaskResult.Success(second.TaskId, connectionId, 100, 91, 9)
            });
        Assert.Equal(HttpStatusCode.Conflict, altered.StatusCode);
        var admin = await restarted.SignInAsync(setup.Account);
        using var capacity = await admin.GetAsync($"/api/v1/storage/connections/{connectionId}/capacity");
        Assert.Equal("Available", AgentWorld.ReadJson(capacity).GetProperty("status").GetString());
        Assert.Equal(20, AgentWorld.ReadJson(capacity).GetProperty("usedBytes").GetInt64());
        await using var db = fixture.CreatePlatformContext();
        Assert.Single(await db.StorageCapacitySnapshots.Where(s => s.TenantId == setup.Tenant.Id && s.ConnectionId == connectionId).ToListAsync());
    }

    [Fact]
    public async Task OldAgentCannotReceiveTasks_AndInvalidLocalTargetNeverReportsAnotherVolume()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var setup = await world.CreateTenantAsync();
        var old = await world.EnrollAsync(setup);
        using (await world.HeartbeatAsync(old)) { }
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var connectionId = await AddConnectionAsync(world, setup, "local", new() { ["volume"] = "missing-disk" });
        using var refused = await setup.Admin.SendAsync(HttpMethod.Post, $"/api/v1/storage/connections/{connectionId}/probe");
        Assert.Equal("CapacityUnavailable", AgentWorld.ReadJson(refused).GetProperty("status").GetString());
        using var oldPoll = await world.PollAsync(old);
        Assert.Empty(AgentWorld.ReadJson(oldPoll).GetProperty("tasks").EnumerateArray());
        await DeclareCapacityAsync(world, old);
        using var queued = await setup.Admin.SendAsync(HttpMethod.Post, $"/api/v1/storage/connections/{connectionId}/probe");
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        var measured = await ExecuteWorkerAsync(world, setup, old, connectionId, new CapacityProbeTaskHandler(localVolumeDirectories: new Dictionary<string, string> { ["missing-disk"] = missing }));
        Assert.Equal("CapacityUnavailable", measured.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, measured.GetProperty("totalBytes").ValueKind);
        Assert.Equal("TargetNotFound", measured.GetProperty("errorMessage").GetString());
    }

    [Fact]
    public async Task CentralProbe_MeasuresConfiguredTenantScopeDirectly_AndHistoryIsBoundedAndTenantIsolated()
    {
        var root = Path.Combine(Path.GetTempPath(), "bp-capacity-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "tenants", "capacity-test");
        Directory.CreateDirectory(target);
        try
        {
            await using var world = await AgentWorld.CreateAsync(fixture);
            var setup = await world.CreateTenantAsync();
            var foreign = await world.CreateTenantAsync();
            var options = world.Factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<BackupStorageOptions>>().Value;
            options.Central = new BackupCentralStorageSettings
            {
                RootDirectory = root, TenantScopes = new() { [setup.Tenant.Id.Value.ToString()] = "capacity-test" }
            };
            var id = await AddConnectionAsync(world, setup, "central", new());
            using var probed = await setup.Admin.SendAsync(HttpMethod.Post, $"/api/v1/storage/connections/{id}/probe");
            Assert.Equal(HttpStatusCode.OK, probed.StatusCode);
            Assert.True(AgentWorld.ReadJson(probed).GetProperty("totalBytes").GetInt64() > 0);
            await using (var db = fixture.CreatePlatformContext())
            {
                for (var index = 1; index <= 35; index++)
                    db.StorageCapacitySnapshots.Add(StorageCapacitySnapshot.AvailableResult(setup.Tenant.Id, id, 100, index, 100 - index, world.Clock.GetUtcNow().AddSeconds(index)));
                await db.SaveChangesAsync();
            }
            using var history = await setup.Admin.GetAsync($"/api/v1/storage/connections/{id}/capacity");
            var json = AgentWorld.ReadJson(history);
            Assert.Equal(30, json.GetProperty("historyLimit").GetInt32());
            var entries = json.GetProperty("history").EnumerateArray().ToArray();
            Assert.Equal(30, entries.Length);
            Assert.Equal(35, entries[0].GetProperty("usedBytes").GetInt64());
            Assert.Equal(6, entries[^1].GetProperty("usedBytes").GetInt64());
            using var denied = await foreign.Admin.GetAsync($"/api/v1/storage/connections/{id}/capacity");
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
            var unauthorized = await AddConnectionAsync(world, foreign, "central", new());
            using var unavailable = await foreign.Admin.SendAsync(HttpMethod.Post, $"/api/v1/storage/connections/{unauthorized}/probe");
            Assert.Equal("CapacityUnavailable", AgentWorld.ReadJson(unavailable).GetProperty("status").GetString());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task CriticalAgentCompletion_CommitsSnapshotOutboxAndAuditAtomically_AndRetriesAfterDatabaseFailure()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var setup = await world.CreateTenantAsync();
        var agent = await ReadyAgentAsync(world, setup);
        var notifications = new BackupPlatform.IntegrationTests.Notifications.NotificationWorld(fixture);
        await notifications.EnableAsync(setup.Tenant.Id, eventTypes: [BackupPlatform.Domain.Notifications.NotificationEventTypes.StorageCapacityCritical], severity: "warning");
        await notifications.AddWebhookAsync(setup.Tenant.Id, "https://webhook.example.com/capacity", "Capacity webhook");
        var id = await AddConnectionAsync(world, setup, "local", new() { ["volume"] = "local" });
        using var queued = await setup.Admin.SendAsync(HttpMethod.Post, $"/api/v1/storage/connections/{id}/probe");
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        var task = await ClaimAsync(world, agent);
        var body = new CapacityProbeCompletionRequest
        {
            LeaseToken = task.LeaseToken,
            Result = CapacityProbeTaskResult.Success(task.TaskId, id, 100, 92, 8)
        };
        var name = "p5a_capacity_" + Guid.NewGuid().ToString("N");
        await using (var db = fixture.CreatePlatformContext())
            await db.Database.ExecuteSqlRawAsync($"""
                CREATE FUNCTION {name}() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                    IF NEW."TenantId" = '{setup.Tenant.Id.Value}'::uuid THEN
                        RAISE EXCEPTION 'p5a_capacity_atomic_failure';
                    END IF;
                    RETURN NEW;
                END $fn$;
                CREATE TRIGGER {name} BEFORE INSERT ON notification_events FOR EACH ROW EXECUTE FUNCTION {name}();
                """);
        try
        {
            var failure = await Record.ExceptionAsync(async () =>
            {
                using var response = await world.SendAgentAsync(HttpMethod.Post, $"/api/agent/capacity-probes/{task.TaskId}/complete",
                    agent.CredentialId, agent.Secret, body: body);
                Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            });
            if (failure is not null) Assert.Contains("p5a_capacity_atomic_failure", failure.ToString());
            await using var db = fixture.CreatePlatformContext();
            Assert.Empty(await db.StorageCapacitySnapshots.Where(s => s.TenantId == setup.Tenant.Id && s.ConnectionId == id).ToListAsync());
            Assert.Empty(await db.NotificationEvents.Where(e => e.TenantId == setup.Tenant.Id).ToListAsync());
            Assert.Empty(await db.AuditEvents.Where(e => e.TenantId == setup.Tenant.Id && e.Action == "storage_capacity_critical").ToListAsync());
            Assert.Null((await db.StorageCapacityTasks.SingleAsync(t => t.Id == task.TaskId)).CompletedAt);
        }
        finally
        {
            await using var db = fixture.CreatePlatformContext();
            await db.Database.ExecuteSqlRawAsync($"DROP TRIGGER {name} ON notification_events; DROP FUNCTION {name}();");
        }
        using var accepted = await world.SendAgentAsync(HttpMethod.Post, $"/api/agent/capacity-probes/{task.TaskId}/complete",
            agent.CredentialId, agent.Secret, body: body);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        using var replay = await world.SendAgentAsync(HttpMethod.Post, $"/api/agent/capacity-probes/{task.TaskId}/complete",
            agent.CredentialId, agent.Secret, body: body);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using var capacity = await setup.Admin.GetAsync($"/api/v1/storage/connections/{id}/capacity");
        Assert.Equal("Critical", AgentWorld.ReadJson(capacity).GetProperty("status").GetString());
        await using (var db = fixture.CreatePlatformContext())
        {
            var measurement = Assert.Single(await db.StorageCapacitySnapshots.Where(s => s.TenantId == setup.Tenant.Id && s.ConnectionId == id).ToListAsync());
            Assert.Equal(92, measurement.UsedBytes);
            var notification = Assert.Single(await db.NotificationEvents.Where(e => e.TenantId == setup.Tenant.Id).ToListAsync());
            Assert.Equal("storage_capacity_critical", notification.Type);
            Assert.NotEmpty(await db.NotificationDeliveries.Where(d => d.EventId == notification.Id).ToListAsync());
            Assert.Single(await db.AuditEvents.Where(e => e.TenantId == setup.Tenant.Id && e.Action == "storage_capacity_critical").ToListAsync());
        }
    }

    [Fact]
    public async Task AbandonedTaskExpires_ReportsUnavailable_AndNextProbeGetsNewDurableTask()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var setup = await world.CreateTenantAsync();
        var agent = await ReadyAgentAsync(world, setup);
        var id = await AddConnectionAsync(world, setup, "local", new() { ["volume"] = "local" });
        using var queued = await setup.Admin.SendAsync(HttpMethod.Post, $"/api/v1/storage/connections/{id}/probe");
        var task = await ClaimAsync(world, agent);
        world.Clock.Advance(TimeSpan.FromMinutes(11));
        await DeclareCapacityAsync(world, agent);
        using var capacity = await setup.Admin.GetAsync($"/api/v1/storage/connections/{id}/capacity");
        Assert.Equal("CapacityUnavailable", AgentWorld.ReadJson(capacity).GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, AgentWorld.ReadJson(capacity).GetProperty("totalBytes").ValueKind);
        using var stale = await CompleteAsync(world, agent, task);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var next = await setup.Admin.SendAsync(HttpMethod.Post, $"/api/v1/storage/connections/{id}/probe");
        Assert.Equal(HttpStatusCode.Accepted, next.StatusCode);
        Assert.NotEqual(task.TaskId, AgentWorld.ReadJson(next).GetProperty("taskId").GetGuid());
    }

    private static async Task<EnrolledAgent> ReadyAgentAsync(AgentWorld world, TenantSetup setup)
    {
        var agent = await world.EnrollAsync(setup);
        await DeclareCapacityAsync(world, agent);
        return agent;
    }

    private static async Task DeclareCapacityAsync(AgentWorld world, EnrolledAgent agent)
    {
        var capabilities = AgentWorld.CapabilitySnapshot(AgentWorld.CapabilityReport(CapacityProbeProtocol.Capability));
        using var sync = await world.SyncCapabilitiesAsync(agent, capabilities);
        Assert.Equal(HttpStatusCode.OK, sync.StatusCode);
        using var heartbeat = await world.HeartbeatAsync(agent, AgentWorld.Heartbeat(capabilityRevision: capabilities.Revision));
        Assert.Equal(HttpStatusCode.OK, heartbeat.StatusCode);
    }

    internal static async Task<Guid> AddConnectionAsync(AgentWorld world, TenantSetup setup, string provider, Dictionary<string, string> configuration)
    {
        var id = Guid.NewGuid();
        await using var db = world.Database.CreatePlatformContext();
        db.StorageConnections.Add(new StorageConnection(id, setup.Tenant.Id, "Capacity " + id, provider,
            StorageOwnership.TenantManaged, StorageQuotaScopes.Tenant, configuration, false, world.Clock.GetUtcNow()));
        await db.SaveChangesAsync();
        return id;
    }

    private static async Task<CapacityProbeTaskRequest> ClaimAsync(AgentWorld world, EnrolledAgent agent)
    {
        using var response = await world.PollAsync(agent);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var envelope = Assert.Single(AgentWorld.ReadJson(response).GetProperty("tasks").EnumerateArray());
        Assert.Equal(CapacityProbeProtocol.TaskType, envelope.GetProperty("type").GetString());
        return envelope.GetProperty("capacityProbe").Deserialize<CapacityProbeTaskRequest>(JsonSerializerOptions.Web)!;
    }

    private static Task<HttpResponseMessage> CompleteAsync(AgentWorld world, EnrolledAgent agent, CapacityProbeTaskRequest task) =>
        world.SendAgentAsync(HttpMethod.Post, $"/api/agent/capacity-probes/{task.TaskId}/complete",
            agent.CredentialId, agent.Secret, body: new CapacityProbeCompletionRequest
            {
                LeaseToken = task.LeaseToken,
                Result = CapacityProbeTaskResult.Success(task.TaskId, task.ConnectionId, 100, 20, 80)
            });

    private static async Task<JsonElement> ExecuteWorkerAsync(AgentWorld world, TenantSetup setup, EnrolledAgent agent,
        Guid connectionId, CapacityProbeTaskHandler handler)
    {
        var task = await ClaimAsync(world, agent);
        var identity = new AgentLocalIdentity(agent.AgentId, new Uri("https://localhost/"), agent.InstallationId,
            new AgentStoredCredential(agent.CredentialId, agent.Secret, 1));
        var client = new HttpCapacityProbeTaskClient(world.Anonymous, new AgentBackendEndpoint(identity.ServerUri), world.Clock);
        var worker = new CapacityProbeTaskWorker(client, new Credentials(identity), handler, world.Clock, NullLogger<CapacityProbeTaskWorker>.Instance);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var running = worker.RunAsync(stop.Token);
        try
        {
            Assert.True(worker.TryPublish(new AgentTaskEnvelope { Id = task.TaskId.ToString(), Type = CapacityProbeProtocol.TaskType, CapacityProbe = task }));
            Assert.False(worker.TryPublish(new AgentTaskEnvelope { Id = task.TaskId.ToString(), Type = CapacityProbeProtocol.TaskType, CapacityProbe = task }));
            while (true)
            {
                stop.Token.ThrowIfCancellationRequested();
                using var response = await setup.Admin.GetAsync($"/api/v1/storage/connections/{connectionId}/capacity");
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var capacity = AgentWorld.ReadJson(response);
                if (capacity.GetProperty("status").GetString() != "Pending") return capacity;
                await Task.Delay(25, stop.Token);
            }
        }
        finally { await stop.CancelAsync(); await running; }
    }

    private sealed class Credentials(AgentLocalIdentity identity) : IAgentCredentialStore
    {
        public Task<AgentLocalIdentity?> LoadAsync(CancellationToken cancellationToken) => Task.FromResult<AgentLocalIdentity?>(identity);
        public Task SaveAsync(AgentLocalIdentity value, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
