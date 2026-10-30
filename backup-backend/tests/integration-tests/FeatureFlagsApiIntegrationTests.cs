using System.Net;
using BackupPlatform.Contracts.Flags;
using BackupPlatform.IntegrationTests.Storage.Connections;

namespace BackupPlatform.IntegrationTests;

public sealed class FeatureFlagsApiIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static readonly Feature[] Off = [Features.SupportSessions, Features.Smb, Features.SizeAnomaly, Features.ReplicaRepair];

    [Fact]
    public async Task A_disabled_feature_is_reported_off_and_its_routes_answer_404()
    {
        await using var env = await StorageConnectionApiIntegrationTests.ApiWorld.CreateAsync(fixture,
            configuration: Off.ToDictionary(feature => $"Features:{feature.Key}", feature => (string?)"false"));

        var features = AgentWorld.ReadJson(await env.Admin.GetAsync("/api/features"));
        Assert.All(Off, feature => Assert.False(features.GetProperty(feature.Key).GetBoolean()));

        var artifact = Guid.NewGuid();
        foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
        {
            (HttpMethod.Post, env.TenantRoot + "/storage-connections", new { providerType = "smb", name = "nas", configuration = new { host = "nas.example", share = "x", site = "o" }, secret = new { username = "u", password = "p" } }),
            (HttpMethod.Post, $"{env.TenantRoot}/backup-artifacts/{artifact}/repairs", new { }),
            (HttpMethod.Get, $"{env.TenantRoot}/backup-artifacts/{artifact}/repairs", null),
            (HttpMethod.Get, "/api/platform/support-sessions", null),
            (HttpMethod.Get, env.TenantRoot + "/support-access/sessions", null),
            (HttpMethod.Put, env.TenantRoot + "/notifications/policy", new { eventTypes = new[] { "backup_size_anomaly" } }),
        })
        {
            using var response = await env.Admin.SendAsync(method, path, body);
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{method} {path} answered {(int)response.StatusCode} while off");
        }

        using var list = await env.Admin.GetAsync(env.TenantRoot + "/storage-providers");
        var providers = await list.Content.ReadAsStringAsync();
        Assert.DoesNotContain("\"smb\"", providers);
    }

    [Fact]
    public async Task Every_feature_but_repair_is_off_by_default()
    {
        await using var env = await StorageConnectionApiIntegrationTests.ApiWorld.CreateAsync(fixture,
            configuration: Features.All.ToDictionary(feature => $"Features:{feature.Key}", feature => (string?)null));
        var features = AgentWorld.ReadJson(await env.Admin.GetAsync("/api/features"));
        Assert.All(Features.All, feature => Assert.Equal(feature == Features.ReplicaRepair, features.GetProperty(feature.Key).GetBoolean()));
    }
}
