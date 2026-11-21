using System.Net;
using System.Text.Json;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;

namespace BackupPlatform.IntegrationTests;

/// <summary>GET /api/platform/audit: search (action, actor, target, tenant), sort (time, action, actor, tenant) and the tenantId filter, all in SQL.</summary>
public sealed class PlatformAuditListIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 8, 0, 0, TimeSpan.Zero);

    private sealed record Seed(string Action, string? Actor, Tenant? Tenant, int Hour, AuditActorKind Kind = AuditActorKind.UserAdmin);

    private async Task<(AgentWorld World, BrowserSession Admin, string Tag)> StartAsync()
    {
        var world = await AgentWorld.CreateAsync(fixture);
        return (world, await world.SignInAsync(await world.SeedUserAsync(platformAdmin: true)), $"au{Guid.NewGuid():N}"[..12]);
    }

    private async Task<Dictionary<string, Guid>> SeedAsync(string tag, Tenant?[] tenants, params (string Label, Seed Seed)[] seeds)
    {
        await using var context = fixture.CreatePlatformContext();
        foreach (var tenant in tenants.OfType<Tenant>()) context.Tenants.Add(tenant);
        var users = new Dictionary<string, UserId>();
        var ids = new Dictionary<string, Guid>();
        foreach (var (label, seed) in seeds)
        {
            var actor = UserId.New();
            if (seed.Actor is not null)
            {
                if (!users.TryGetValue(seed.Actor, out actor!))
                {
                    var user = new User(UserId.New(), seed.Actor, UserRole.UserAdmin);
                    context.Users.Add(user);
                    users[seed.Actor] = actor = user.Id;
                }
            }
            var entry = AuditEvent.Record(T0.AddHours(seed.Hour), actor, seed.Kind, seed.Action, seed.Tenant?.Id, "thing", tag);
            context.AuditEvents.Add(entry);
            ids[label] = entry.Id;
        }
        await context.SaveChangesAsync();
        return ids;
    }

    private static async Task<JsonElement> GetAsync(BrowserSession admin, string query, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var response = await admin.GetAsync($"/api/platform/audit?{query}");
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

    private async Task<(AgentWorld World, BrowserSession Admin, string Tag, Dictionary<string, Guid> Ids, Tenant Alpha, Tenant Bravo)> SeedMixAsync()
    {
        var (world, admin, tag) = await StartAsync();
        var alpha = new Tenant(TenantId.New(), $"Alpha {tag}");
        var bravo = new Tenant(TenantId.New(), $"Bravo {tag}");
        var ids = await SeedAsync(tag, [alpha, bravo],
            ("e1", new($"{tag}.b", $"Zed {tag}", alpha, 0)),
            ("e2", new($"{tag}.a", $"Amy {tag}", bravo, 1)),
            ("e3", new($"{tag}.c", null, null, 2, AuditActorKind.System)),
            ("e4", new($"{tag}.a", $"Amy {tag}", null, 3)));
        return (world, admin, tag, ids, alpha, bravo);
    }

    [Theory]
    [InlineData("at", "asc")]
    [InlineData("at", "desc")]
    [InlineData("action", "asc")]
    [InlineData("action", "desc")]
    [InlineData("actor", "asc")]
    [InlineData("actor", "desc")]
    [InlineData("tenant", "asc")]
    [InlineData("tenant", "desc")]
    public async Task Sort_OrdersEachFieldBothWaysWithMissingTenantsLast(string field, string dir)
    {
        var (world, admin, tag, ids, _, _) = await SeedMixAsync();
        await using var _ = world;
        Guid[] One(string label) => [ids[label]];
        Guid[] Pair(string a, string b) => [ids[a], ids[b]];
        var expected = (field, dir) switch
        {
            ("at", "asc") => new[] { One("e1"), One("e2"), One("e3"), One("e4") },
            ("at", "desc") => [One("e4"), One("e3"), One("e2"), One("e1")],
            ("action", "asc") => [Pair("e2", "e4"), One("e1"), One("e3")],
            ("action", "desc") => [One("e3"), One("e1"), Pair("e2", "e4")],
            ("actor", "asc") => [Pair("e2", "e4"), One("e3"), One("e1")],
            ("actor", "desc") => [One("e1"), One("e3"), Pair("e2", "e4")],
            ("tenant", "asc") => [One("e1"), One("e2"), Pair("e3", "e4")],
            _ => [One("e2"), One("e1"), Pair("e3", "e4")]
        };

        var page = await GetAsync(admin, $"search={tag}&sort={field}&dir={dir}");

        AssertGroups(Ids(page), expected);
    }

    [Fact]
    public async Task WithoutSort_NewestFirst_AndDirOnlyFlipsTheDefaultField()
    {
        var (world, admin, tag, ids, _, _) = await SeedMixAsync();
        await using var _ = world;

        Assert.Equal([ids["e4"], ids["e3"], ids["e2"], ids["e1"]], Ids(await GetAsync(admin, $"search={tag}")));
        Assert.Equal([ids["e1"], ids["e2"], ids["e3"], ids["e4"]], Ids(await GetAsync(admin, $"search={tag}&dir=asc")));
    }

    [Fact]
    public async Task Search_MatchesActionActorAndTenantCaseInsensitively_AndCombinesWithTheTenantFilter()
    {
        var (world, admin, tag, ids, alpha, bravo) = await SeedMixAsync();
        await using var _ = world;

        Assert.Equal([ids["e2"], ids["e4"]], Ids(await GetAsync(admin, $"search={tag.ToUpperInvariant()}.A&sort=at&dir=asc")));
        Assert.Equal([ids["e1"]], Ids(await GetAsync(admin, $"search=zed%20{tag}")));
        Assert.Equal([ids["e1"]], Ids(await GetAsync(admin, $"search=alpha%20{tag}")));
        Assert.Equal([ids["e2"], ids["e4"]], Ids(await GetAsync(admin, $"search=amy%20{tag}&sort=at&dir=asc")));
        Assert.Equal([ids["e2"]], Ids(await GetAsync(admin, $"search=amy%20{tag}&tenantId={bravo.Id.Value}")));
        Assert.Equal([ids["e1"]], Ids(await GetAsync(admin, $"search={tag}&tenantId={alpha.Id.Value}")));
        Assert.Equal(0, (await GetAsync(admin, $"tenantId={alpha.Id.Value}&search=zzz-nothing")).GetProperty("totalCount").GetInt64());
    }

    [Fact]
    public async Task Paging_IsStableWhenTheSortKeyTies()
    {
        var (world, admin, tag) = await StartAsync();
        await using var _ = world;
        await SeedAsync(tag, [], Enumerable.Range(0, 7).Select(index => ($"s{index}", new Seed($"{tag}.same", "Same", null, 0))).ToArray());

        var seen = new List<Guid>();
        foreach (var number in new[] { 1, 2, 3 })
        {
            var page = await GetAsync(admin, $"search={tag}&sort=action&pageSize=3&page={number}");
            Assert.Equal(7, page.GetProperty("totalCount").GetInt64());
            seen.AddRange(Ids(page));
        }

        Assert.Equal(7, seen.Distinct().Count());
        Assert.Equal(Ids(await GetAsync(admin, $"search={tag}&sort=action&pageSize=100")), seen);
    }

    [Theory]
    [InlineData("sort=details", "sort")]
    [InlineData("sort=at&dir=sideways", "dir")]
    [InlineData("pageSize=101", "pageSize")]
    public async Task InvalidListParameters_Answer400NamingTheField(string query, string field)
    {
        var (world, admin, _) = await StartAsync();
        await using var _ = world;

        var body = await GetAsync(admin, query, HttpStatusCode.BadRequest);

        Assert.Equal("invalid_request", body.GetProperty("error").GetString());
        Assert.Equal(field, body.GetProperty("field").GetString());
    }
}
