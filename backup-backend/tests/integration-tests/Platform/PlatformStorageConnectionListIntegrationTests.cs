using System.Net;
using System.Text.Json;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Storage;
using BackupPlatform.Domain.Tenancy;

namespace BackupPlatform.IntegrationTests;

/// <summary>GET /api/platform/storage-connections: search (name, provider, tenant), sort and the ownership/provider/tenant/enabled filters, all in SQL.</summary>
public sealed class PlatformStorageConnectionListIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static readonly DateTimeOffset T0 = new(2026, 2, 1, 8, 0, 0, TimeSpan.Zero);

    private async Task<(AgentWorld World, BrowserSession Admin, string Tag, Dictionary<string, Guid> Ids, Tenant Alpha, Tenant Bravo)> SeedAsync()
    {
        var world = await AgentWorld.CreateAsync(fixture);
        var admin = await world.SignInAsync(await world.SeedUserAsync(platformAdmin: true));
        var tag = $"sc{Guid.NewGuid():N}"[..12];
        var alpha = new Tenant(TenantId.New(), $"Alpha {tag}");
        var bravo = new Tenant(TenantId.New(), $"Bravo {tag}");
        var ids = new Dictionary<string, Guid>();
        await using var context = fixture.CreatePlatformContext();
        context.Tenants.AddRange(alpha, bravo);
        void Add(string label, Tenant tenant, string name, string provider, StorageOwnership ownership, int day, bool enabled = true, bool locked = false)
        {
            var connection = new StorageConnection(Guid.NewGuid(), tenant.Id, $"{name} {tag}", provider, ownership, null, null, false, T0.AddDays(day));
            if (!enabled) connection.SetEnabled(false, T0);
            if (locked) connection.SetAdministrativelyDisabled(true, T0);
            context.StorageConnections.Add(connection);
            ids[label] = connection.Id;
        }
        Add("c1", alpha, "Zulu", "sftp", StorageOwnership.PlatformManaged, 0);
        Add("c2", bravo, "alpha", "local", StorageOwnership.TenantManaged, 1, enabled: false);
        Add("c3", alpha, "Mike", "central", StorageOwnership.PlatformManaged, 2, locked: true);
        Add("c4", bravo, "Mike2", "sftp", StorageOwnership.TenantManaged, 3);
        await context.SaveChangesAsync();
        return (world, admin, tag, ids, alpha, bravo);
    }

    private static async Task<JsonElement> GetAsync(BrowserSession admin, string query, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var response = await admin.GetAsync($"/api/platform/storage-connections?{query}");
        Assert.Equal(expected, response.StatusCode);
        return AgentWorld.ReadJson(response);
    }

    private static Guid[] Ids(JsonElement page) => [.. page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid())];

    // Each inner set is a group of rows that tie on the sort key (their order is the id), compared as a set.
    private static void AssertGroups(Guid[] actual, Guid[][] groups)
    {
        var index = 0;
        foreach (var group in groups)
        {
            Assert.Equal(group.Order(), actual.Skip(index).Take(group.Length).Order());
            index += group.Length;
        }
        Assert.Equal(index, actual.Length);
    }

    [Theory]
    [InlineData("name", "asc")]
    [InlineData("name", "desc")]
    [InlineData("provider", "asc")]
    [InlineData("provider", "desc")]
    [InlineData("ownership", "asc")]
    [InlineData("ownership", "desc")]
    [InlineData("enabled", "asc")]
    [InlineData("enabled", "desc")]
    [InlineData("tenant", "asc")]
    [InlineData("tenant", "desc")]
    [InlineData("createdAt", "asc")]
    [InlineData("createdAt", "desc")]
    public async Task Sort_OrdersEachFieldBothWays(string field, string dir)
    {
        var (world, admin, tag, ids, _, _) = await SeedAsync();
        await using var _ = world;
        Guid[] One(string label) => [ids[label]];
        Guid[] Pair(string a, string b) => [ids[a], ids[b]];
        var expected = (field, dir) switch
        {
            ("name", "asc") => new[] { One("c2"), One("c3"), One("c4"), One("c1") },
            ("name", "desc") => [One("c1"), One("c4"), One("c3"), One("c2")],
            ("provider", "asc") => [One("c3"), One("c2"), Pair("c1", "c4")],
            ("provider", "desc") => [Pair("c1", "c4"), One("c2"), One("c3")],
            ("ownership", "asc") => [Pair("c1", "c3"), Pair("c2", "c4")],
            ("ownership", "desc") => [Pair("c2", "c4"), Pair("c1", "c3")],
            // Disabled by the owner or locked by the platform both count as not enabled.
            ("enabled", "asc") => [Pair("c2", "c3"), Pair("c1", "c4")],
            ("enabled", "desc") => [Pair("c1", "c4"), Pair("c2", "c3")],
            ("tenant", "asc") => [Pair("c1", "c3"), Pair("c2", "c4")],
            ("tenant", "desc") => [Pair("c2", "c4"), Pair("c1", "c3")],
            ("createdAt", "asc") => [One("c1"), One("c2"), One("c3"), One("c4")],
            _ => [One("c4"), One("c3"), One("c2"), One("c1")]
        };

        AssertGroups(Ids(await GetAsync(admin, $"search={tag}&sort={field}&dir={dir}")), expected);
    }

    [Theory]
    [InlineData("managed storage", "c3")]
    [InlineData("MANAGED STORAGE (CENTRAL)", "c3")]
    [InlineData("Agent diski", "c2")]
    [InlineData("agent dıskı", "c2")]
    public async Task Search_AlsoMatchesTheProviderDisplayName(string term, string label)
    {
        var (world, admin, tag, ids, _, _) = await SeedAsync();
        await using var _ = world;

        Assert.Equal([ids[label]], Ids(await GetAsync(admin, $"search={Uri.EscapeDataString(term)}&pageSize=100")).Where(id => ids.ContainsValue(id)).ToArray());
    }

    [Fact]
    public async Task WithoutSort_NewestFirst()
    {
        var (world, admin, tag, ids, _, _) = await SeedAsync();
        await using var _ = world;

        Assert.Equal([ids["c4"], ids["c3"], ids["c2"], ids["c1"]], Ids(await GetAsync(admin, $"search={tag}")));
        Assert.Equal([ids["c1"], ids["c2"], ids["c3"], ids["c4"]], Ids(await GetAsync(admin, $"search={tag}&dir=asc")));
    }

    [Fact]
    public async Task Search_MatchesNameProviderAndTenant_AndCombinesWithTheFilters()
    {
        var (world, admin, tag, ids, alpha, bravo) = await SeedAsync();
        await using var _ = world;

        Assert.Equal([ids["c3"]], Ids(await GetAsync(admin, $"search=MIKE%20{tag}")));
        Assert.Equal([ids["c4"]], Ids(await GetAsync(admin, $"search=mike2%20{tag}")));
        Assert.Equal([ids["c1"]], Ids(await GetAsync(admin, $"search=sftp&tenantId={alpha.Id.Value}")));
        Assert.Equal([ids["c4"]], Ids(await GetAsync(admin, $"search=SFTP&tenantId={bravo.Id.Value}")));
        Assert.Equal([ids["c2"], ids["c4"]], Ids(await GetAsync(admin, $"search=bravo%20{tag}&sort=createdAt&dir=asc")));
        Assert.Equal([ids["c4"]], Ids(await GetAsync(admin, $"search={tag}&ownership=TenantManaged&enabled=true")));
        Assert.Equal([ids["c3"], ids["c2"]], Ids(await GetAsync(admin, $"search={tag}&enabled=false&sort=name&dir=desc")));
        Assert.Equal([ids["c1"], ids["c4"]], Ids(await GetAsync(admin, $"search={tag}&providerType=sftp&sort=createdAt&dir=asc")));
        Assert.Equal(0, (await GetAsync(admin, $"search={tag}&providerType=local&ownership=PlatformManaged")).GetProperty("totalCount").GetInt64());
    }

    [Fact]
    public async Task Paging_IsStableWhenTheSortKeyTies()
    {
        var (world, admin, tag, _, _, _) = await SeedAsync();
        await using var _ = world;
        await using (var context = fixture.CreatePlatformContext())
        {
            var tenant = new Tenant(TenantId.New(), $"Paged {tag}");
            context.Tenants.Add(tenant);
            for (var index = 0; index < 5; index++)
                context.StorageConnections.Add(new StorageConnection(Guid.NewGuid(), tenant.Id, $"Same{index} {tag}", "sftp", StorageOwnership.TenantManaged, null, null, false, T0));
            await context.SaveChangesAsync();
        }

        var seen = new List<Guid>();
        foreach (var number in new[] { 1, 2, 3 })
        {
            var page = await GetAsync(admin, $"search={tag}&sort=provider&pageSize=3&page={number}");
            Assert.Equal(9, page.GetProperty("totalCount").GetInt64());
            seen.AddRange(Ids(page));
        }

        Assert.Equal(9, seen.Distinct().Count());
        Assert.Equal(Ids(await GetAsync(admin, $"search={tag}&sort=provider&pageSize=100")), seen);
    }

    [Theory]
    [InlineData("sort=size", "sort")]
    [InlineData("sort=name&dir=up", "dir")]
    [InlineData("page=0", "page")]
    public async Task InvalidListParameters_Answer400NamingTheField(string query, string field)
    {
        var (world, admin, _, _, _, _) = await SeedAsync();
        await using var _ = world;

        var body = await GetAsync(admin, query, HttpStatusCode.BadRequest);

        Assert.Equal("invalid_request", body.GetProperty("error").GetString());
        Assert.Equal(field, body.GetProperty("field").GetString());
    }
}
