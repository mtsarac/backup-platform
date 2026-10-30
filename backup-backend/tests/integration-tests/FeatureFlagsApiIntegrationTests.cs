using System.Net;
using BackupPlatform.Contracts.Flags;
using BackupPlatform.IntegrationTests.Storage.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BackupPlatform.IntegrationTests;

public sealed class FeatureFlagsApiIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static readonly Feature[] Off = [Features.SupportSessions, Features.Smb, Features.Sms, Features.SizeAnomaly, Features.ReplicaRepair];

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
            (HttpMethod.Post, env.TenantRoot + "/notifications/channels", new { kind = "sms", name = "Mobile", target = "+15551234567" }),
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
    public async Task Existing_smb_connections_and_sms_channels_are_hidden_and_inaccessible_until_reenabled()
    {
        var enabled = true;
        await using var env = await StorageConnectionApiIntegrationTests.ApiWorld.CreateAsync(fixture, configureServices: services =>
        {
            services.RemoveAll<FeatureFlags>();
            services.AddSingleton(new FeatureFlags(key => key is "smb" or "sms" ? enabled : true));
        });
        var storage = env.TenantRoot + "/storage-connections";
        var channels = env.TenantRoot + "/notifications/channels";
        using var createdStorage = await env.Admin.SendAsync(HttpMethod.Post, storage, new
        {
            providerType = "smb", name = "NAS", configuration = new { host = "nas.example", share = "backups", site = "office" },
            secret = new { username = "user", password = "password" }
        });
        Assert.Equal(HttpStatusCode.Created, createdStorage.StatusCode);
        var storageId = AgentWorld.ReadJson(createdStorage).GetProperty("id").GetGuid();
        using var createdChannel = await env.Admin.SendAsync(HttpMethod.Post, channels, new { kind = "sms", name = "Mobile", target = "+15551234567" });
        Assert.Equal(HttpStatusCode.Created, createdChannel.StatusCode);
        var channelId = AgentWorld.ReadJson(createdChannel).GetProperty("id").GetGuid();
        var platform = await env.PlatformAdminAsync();

        enabled = false;
        using var storageList = await env.Admin.GetAsync(storage);
        Assert.DoesNotContain(storageId.ToString(), await storageList.Content.ReadAsStringAsync());
        using var channelList = await env.Admin.GetAsync(channels);
        Assert.DoesNotContain(channelId.ToString(), await channelList.Content.ReadAsStringAsync());
        using var search = await platform.GetAsync("/api/platform/storage-connections?providerType=smb");
        Assert.Equal(0, AgentWorld.ReadJson(search).GetProperty("totalCount").GetInt32());
        foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
        {
            (HttpMethod.Get, $"{storage}/{storageId}", null),
            (HttpMethod.Put, $"{storage}/{storageId}", new { name = "changed" }),
            (HttpMethod.Post, $"{storage}/{storageId}/test", null),
            (HttpMethod.Get, $"/api/v1/storage/connections/{storageId}/capacity", null),
            (HttpMethod.Get, $"/api/platform/storage-connections/{storageId}", null),
            (HttpMethod.Get, $"{channels}/{channelId}", null),
            (HttpMethod.Put, $"{channels}/{channelId}", new { name = "changed" }),
            (HttpMethod.Post, $"{channels}/{channelId}/test", null),
            (HttpMethod.Delete, $"{channels}/{channelId}", null)
        })
        {
            using var response = await (path.StartsWith("/api/platform/") ? platform : env.Admin).SendAsync(method, path, body);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        enabled = true;
        using var restoredStorage = await env.Admin.GetAsync($"{storage}/{storageId}");
        using var restoredChannel = await env.Admin.GetAsync($"{channels}/{channelId}");
        Assert.Equal(HttpStatusCode.OK, restoredStorage.StatusCode);
        Assert.Equal(HttpStatusCode.OK, restoredChannel.StatusCode);
        Assert.Equal("NAS", AgentWorld.ReadJson(restoredStorage).GetProperty("name").GetString());
        Assert.Equal("Mobile", AgentWorld.ReadJson(restoredChannel).GetProperty("name").GetString());
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
