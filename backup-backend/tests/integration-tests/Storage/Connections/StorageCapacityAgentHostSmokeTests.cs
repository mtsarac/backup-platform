using System.Net;
using System.Text.Json;
using BackupPlatform.IntegrationTests.Backup;
using Xunit.Abstractions;

namespace BackupPlatform.IntegrationTests.Storage.Connections;

public sealed class StorageCapacityAgentHostSmokeTests(PostgreSqlFixture fixture, ITestOutputHelper output) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task PublishedAgent_DeclaresCapability_PollsAndMeasuresItsConfiguredLocalFilesystem()
    {
        await using var world = await AgentWorld.CreateAsync(fixture, clock: new MutableClock(live: true), kestrel: true);
        var setup = await world.CreateTenantAsync();
        var agent = await world.EnrollAsync(setup);
        await using var host = await AgentHostSmokeProcess.StartAsync(world, agent, output.WriteLine);
        var connection = await StorageCapacityProbeIntegrationTests.AddConnectionAsync(world, setup, "local", new() { ["volume"] = "local" });
        var capacity = await ProbeAndWaitAsync(setup, host, connection);
        Assert.Contains(capacity.GetProperty("status").GetString(), new[] { "Available", "Warning", "Critical" });
        var measured = capacity.GetProperty("totalBytes").GetInt64();
        Assert.True(capacity.GetProperty("freeBytes").GetInt64() > 0);
        Assert.True(measured > 0);
        Assert.NotEmpty(capacity.GetProperty("history").EnumerateArray());
        output.WriteLine("Actual Agent.Host capacity: total={0}, free={1}; production poll/lease/completion, no dispatcher substitution.", measured, capacity.GetProperty("freeBytes").GetInt64());
    }

    [Fact]
    public async Task PublishedAgent_ProbesRealSftpStatvfs_WithPinnedHostAndOperatorNetworkPolicy()
    {
        var hostName = AgentHostSmokeProcess.Required("BACKUP_PLATFORM_TEST_SFTP_HOST");
        var port = AgentHostSmokeProcess.Required("BACKUP_PLATFORM_TEST_SFTP_PORT");
        var username = AgentHostSmokeProcess.Required("BACKUP_PLATFORM_TEST_SFTP_USER");
        var password = AgentHostSmokeProcess.Required("BACKUP_PLATFORM_TEST_SFTP_PASSWORD");
        var fingerprint = AgentHostSmokeProcess.Required("BACKUP_PLATFORM_TEST_SFTP_FINGERPRINT");
        await using var world = await AgentWorld.CreateAsync(fixture, clock: new MutableClock(live: true), kestrel: true);
        var setup = await world.CreateTenantAsync();
        var agent = await world.EnrollAsync(setup);
        var connection = await StorageCapacityProbeIntegrationTests.AddConnectionAsync(world, setup, "sftp", new()
        {
            ["host"] = hostName, ["port"] = port, ["username"] = username, ["authenticationType"] = "password",
            ["remoteBaseDirectory"] = "/", ["hostKeyFingerprint"] = fingerprint
        });
        using var saved = await setup.Admin.SendAsync(HttpMethod.Put, $"/api/tenants/{setup.Tenant.Id.Value}/storage-connections/{connection}/secret",
            new { values = new Dictionary<string, string> { ["password"] = password } });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        await using var host = await AgentHostSmokeProcess.StartAsync(world, agent, output.WriteLine);
        var capacity = await ProbeAndWaitAsync(setup, host, connection);
        Assert.Contains(capacity.GetProperty("status").GetString(), new[] { "Available", "Warning", "Critical" });
        Assert.True(capacity.GetProperty("totalBytes").GetInt64() > 0);
        Assert.True(capacity.GetProperty("freeBytes").GetInt64() > 0);
        output.WriteLine("Actual Agent.Host SFTP statvfs: total={0}, free={1}; pinned host key and configured network allowlist.",
            capacity.GetProperty("totalBytes").GetInt64(), capacity.GetProperty("freeBytes").GetInt64());
    }

    private static async Task<JsonElement> ProbeAndWaitAsync(TenantSetup setup, AgentHostSmokeProcess host, Guid connection)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var accepted = false;
        while (!accepted)
        {
            timeout.Token.ThrowIfCancellationRequested();
            host.AssertAlive();
            using var queued = await setup.Admin.SendAsync(HttpMethod.Post, $"/api/v1/storage/connections/{connection}/probe");
            Assert.Contains(queued.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Accepted });
            accepted = queued.StatusCode == HttpStatusCode.Accepted;
            if (!accepted) await Task.Delay(250, timeout.Token);
        }
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            host.AssertAlive();
            using var response = await setup.Admin.GetAsync($"/api/v1/storage/connections/{connection}/capacity");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var capacity = AgentWorld.ReadJson(response);
            if (capacity.GetProperty("status").GetString() != "Pending") return capacity;
            await Task.Delay(250, timeout.Token);
        }
    }
}
