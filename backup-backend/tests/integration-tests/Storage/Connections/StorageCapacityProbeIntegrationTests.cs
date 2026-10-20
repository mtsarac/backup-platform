extern alias AgentCore;

using System.Net;
using AgentCore::BackupPlatform.Agent.Core.Storage;
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
    public async Task AgentLocalDiskProbe_DelegatesToActiveAgentAndPersistsSnapshot()
    {
        var localHandler = new CapacityProbeTaskHandler();

        await using var world = await AgentWorld.CreateAsync(fixture, configureServices: services =>
        {
            services.RemoveAll<IAgentCapacityProbeDispatcher>();
            services.AddSingleton<IAgentCapacityProbeDispatcher>(
                new DefaultAgentCapacityProbeDispatcher((_, req, ct) => localHandler.ExecuteAsync(req, ct)));
        });

        var setup = await world.CreateTenantAsync();
        var agent = await world.EnrollAsync(setup);
        using (await world.HeartbeatAsync(agent)) { }

        var connectionId = Guid.NewGuid();
        var rootDir = Path.GetPathRoot(Environment.CurrentDirectory) ?? "/";

        await using (var context = fixture.CreatePlatformContext())
        {
            var connection = new StorageConnection(
                connectionId,
                setup.Tenant.Id,
                "Local Disk",
                StorageProviderTypes.Local,
                StorageOwnership.TenantManaged,
                StorageQuotaScopes.Tenant,
                new Dictionary<string, string> { ["path"] = rootDir },
                false,
                DateTimeOffset.UtcNow);
            context.StorageConnections.Add(connection);
            await context.SaveChangesAsync();
        }

        // Probe via REST API
        using var probeResponse = await setup.Admin.SendAsync(HttpMethod.Post, $"/api/v1/storage/connections/{connectionId}/probe");
        Assert.Equal(HttpStatusCode.OK, probeResponse.StatusCode);

        var probeJson = AgentWorld.ReadJson(probeResponse);
        Assert.Contains(probeJson.GetProperty("status").GetString(), new[] { "Available", "Warning", "Critical" });
        Assert.True(probeJson.GetProperty("totalBytes").GetInt64() > 0);
        Assert.True(probeJson.GetProperty("freeBytes").GetInt64() > 0);

        // Verify in PostgreSQL database
        await using (var context = fixture.CreatePlatformContext())
        {
            var snapshot = await context.StorageCapacitySnapshots
                .Where(s => s.TenantId == setup.Tenant.Id && s.ConnectionId == connectionId)
                .OrderByDescending(s => s.ReportedAt)
                .FirstOrDefaultAsync();

            Assert.NotNull(snapshot);
            Assert.Equal("Available", snapshot.Status);
            Assert.True(snapshot.TotalBytes > 0);
        }
    }

    [Fact]
    public async Task AgentSftpProbe_DelegatesToActiveAgentAndPersistsSnapshot()
    {
        const long total = 500_000_000_000;
        const long used = 100_000_000_000;
        const long free = 400_000_000_000;

        var sftpHandler = new CapacityProbeTaskHandler(new FakeSftpCapacityProbeExecutor(total, used, free));

        await using var world = await AgentWorld.CreateAsync(fixture, configureServices: services =>
        {
            services.RemoveAll<IAgentCapacityProbeDispatcher>();
            services.AddSingleton<IAgentCapacityProbeDispatcher>(
                new DefaultAgentCapacityProbeDispatcher((_, req, ct) => sftpHandler.ExecuteAsync(req, ct)));
        });

        var setup = await world.CreateTenantAsync();
        var agent = await world.EnrollAsync(setup);
        using (await world.HeartbeatAsync(agent)) { }

        var connectionId = Guid.NewGuid();

        await using (var context = fixture.CreatePlatformContext())
        {
            var connection = new StorageConnection(
                connectionId,
                setup.Tenant.Id,
                "SFTP LAN",
                StorageProviderTypes.Sftp,
                StorageOwnership.TenantManaged,
                StorageQuotaScopes.Tenant,
                new Dictionary<string, string>
                {
                    ["host"] = "sftp.lan",
                    ["port"] = "22",
                    ["username"] = "backupuser",
                    ["remoteBaseDirectory"] = "/backups",
                    ["hostKeyFingerprint"] = "SHA256:abcd"
                },
                false,
                DateTimeOffset.UtcNow);
            connection.AttachSecret("ref-sftp-1", ["password"], DateTimeOffset.UtcNow);
            context.StorageConnections.Add(connection);
            await context.SaveChangesAsync();
        }

        // Probe via REST API
        using var probeResponse = await setup.Admin.SendAsync(HttpMethod.Post, $"/api/v1/storage/connections/{connectionId}/probe");
        Assert.Equal(HttpStatusCode.OK, probeResponse.StatusCode);

        var probeJson = AgentWorld.ReadJson(probeResponse);
        Assert.Equal("Available", probeJson.GetProperty("status").GetString());
        Assert.Equal(total, probeJson.GetProperty("totalBytes").GetInt64());
        Assert.Equal(used, probeJson.GetProperty("usedBytes").GetInt64());
        Assert.Equal(free, probeJson.GetProperty("freeBytes").GetInt64());

        // Verify in PostgreSQL database
        await using (var context = fixture.CreatePlatformContext())
        {
            var snapshot = await context.StorageCapacitySnapshots
                .Where(s => s.TenantId == setup.Tenant.Id && s.ConnectionId == connectionId)
                .OrderByDescending(s => s.ReportedAt)
                .FirstOrDefaultAsync();

            Assert.NotNull(snapshot);
            Assert.Equal("Available", snapshot.Status);
            Assert.Equal(total, snapshot.TotalBytes);
        }
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
}
