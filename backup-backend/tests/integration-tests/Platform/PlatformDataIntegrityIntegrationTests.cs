using System.Net;

namespace BackupPlatform.IntegrationTests;

public sealed class PlatformDataIntegrityIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task UpdateUser_PersistsDisplayNameChange()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var admin = await world.SignInAsync(await world.SeedUserAsync(platformAdmin: true));
        var target = await world.SeedUserAsync(platformAdmin: true);
        var name = $"Renamed {Guid.NewGuid():N}";

        using var put = await admin.SendAsync(HttpMethod.Put, $"/api/platform/users/{target.UserId.Value}",
            new { username = target.Identifier, email = $"{Guid.NewGuid():N}@example.test", displayName = name, role = "PlatformAdmin" });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        using var byName = await admin.GetAsync($"/api/platform/users?search={name.Replace(" ", "%20")}");
        Assert.Contains(name, await byName.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TenantName_MustBeUnique_OnCreateAndRename()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var admin = await world.SignInAsync(await world.SeedUserAsync(platformAdmin: true));
        var name = $"Unique {Guid.NewGuid():N}";

        using var first = await admin.SendAsync(HttpMethod.Post, "/api/platform/tenants", new { name });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        using var duplicate = await admin.SendAsync(HttpMethod.Post, "/api/platform/tenants", new { name = name.ToUpperInvariant() });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        using var other = await admin.SendAsync(HttpMethod.Post, "/api/platform/tenants", new { name = name + " other" });
        var otherId = AgentWorld.ReadJson(other).GetProperty("tenantId").GetGuid();
        using var rename = await admin.SendAsync(HttpMethod.Put, $"/api/platform/tenants/{otherId}", new { name });
        Assert.Equal(HttpStatusCode.Conflict, rename.StatusCode);
    }

    [Fact]
    public async Task ConcurrentCreatesOfOneTenantName_YieldOneWinnerAndConflicts()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var admin = await world.SignInAsync(await world.SeedUserAsync(platformAdmin: true));
        var name = $"Race {Guid.NewGuid():N}";

        var codes = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            using var response = await admin.SendAsync(HttpMethod.Post, "/api/platform/tenants", new { name });
            return response.StatusCode;
        }));

        Assert.Equal(1, codes.Count(code => code == HttpStatusCode.Created));
        Assert.Equal(5, codes.Count(code => code == HttpStatusCode.Conflict));
    }
}
