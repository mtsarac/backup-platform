using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using static BackupPlatform.IntegrationTests.ManagedService.ManagedServiceTestSupport;

namespace BackupPlatform.IntegrationTests.ManagedService;

/// <summary>GET /api/platform/tenants/{id}/subscriptions: search by plan, sort by start, plan and the EFFECTIVE status, paging; all in SQL.</summary>
public sealed class SubscriptionHistoryListIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static readonly DateTimeOffset Base = new(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Four rows of one tenant. r1 Delta and r2 Alpha are superseded; r3 Charlie was stored as Expired; r4 Bravo is stored Suspended but its end has
    /// passed, so every reader (and so the sort) sees it as Expired, tying with r3 even though the stored values differ.
    /// </summary>
    private async Task<(AgentWorld World, BrowserSession Admin, string Path, string Tag, Dictionary<string, string> Ids)> SeedAsync()
    {
        var world = await AgentWorld.CreateAsync(fixture);
        var admin = await world.SignInAsync(await world.SeedUserAsync(platformAdmin: true));
        var tenant = await world.AddTenantAsync();
        var tag = $"sh{Guid.NewGuid():N}"[..12];
        var path = SubscriptionPath(tenant.Id.Value, "s");
        foreach (var name in new[] { "Delta", "Alpha", "Charlie", "Bravo" })
        {
            var plan = await CreatePlanAsync(admin, $"{name} {tag}");
            Assert.Equal(HttpStatusCode.OK, (await CallAsync(admin, HttpMethod.Put, SubscriptionPath(tenant.Id.Value), new { planId = plan })).Status);
        }
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(admin, HttpMethod.Post, SubscriptionPath(tenant.Id.Value, "/suspend"))).Status);

        var ids = new Dictionary<string, string>();
        await using (var context = fixture.CreatePlatformContext())
        {
            // The host clock may be frozen, so the creation time cannot tell the rows apart: identify each by its plan.
            var rows = await (from row in context.TenantSubscriptions.AsNoTracking()
                              where row.TenantId == tenant.Id
                              join plan in context.Plans.AsNoTracking() on row.PlanId equals plan.Id
                              select new { row.Id, plan.Name }).ToListAsync();
            Assert.Equal(4, rows.Count);
            var order = new[] { "Delta", "Alpha", "Charlie", "Bravo" };
            for (var index = 0; index < order.Length; index++)
            {
                var id = rows.Single(row => row.Name.StartsWith(order[index] + " ", StringComparison.Ordinal)).Id.Value;
                var start = Base.AddDays(index);
                await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE tenant_subscriptions SET \"StartsAt\" = {start} WHERE \"Id\" = {id}");
                ids[$"r{index + 1}"] = id.ToString("D");
            }
            var r3 = Guid.Parse(ids["r3"]);
            var r4 = Guid.Parse(ids["r4"]);
            await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE tenant_subscriptions SET \"Status\" = 3 WHERE \"Id\" = {r3}");
            await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE tenant_subscriptions SET \"EndsAt\" = \"StartsAt\" WHERE \"Id\" = {r4}");
        }
        return (world, admin, path, tag, ids);
    }

    private static async Task<JsonElement> GetAsync(BrowserSession admin, string path, string query, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var reply = await CallAsync(admin, HttpMethod.Get, $"{path}?{query}");
        Assert.Equal(expected, reply.Status);
        return reply.Json;
    }

    private static string[] Ids(JsonElement page) => [.. page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("subscriptionId").GetString()!)];

    // Each inner set is a group of rows that tie on the sort key (their order is the id), compared as a set.
    private static void AssertGroups(string[] actual, string[][] groups)
    {
        var index = 0;
        foreach (var group in groups)
        {
            Assert.Equal(group.Order(StringComparer.Ordinal), actual.Skip(index).Take(group.Length).Order(StringComparer.Ordinal));
            index += group.Length;
        }
        Assert.Equal(index, actual.Length);
    }

    private static string[][] Groups(Dictionary<string, string> ids, params string[][] labels) => [.. labels.Select(group => group.Select(label => ids[label]).ToArray())];

    [Theory]
    [InlineData("startsAt", "asc")]
    [InlineData("startsAt", "desc")]
    [InlineData("plan", "asc")]
    [InlineData("plan", "desc")]
    [InlineData("status", "asc")]
    [InlineData("status", "desc")]
    public async Task Sort_OrdersEachFieldBothWays_AndStatusIsTheEffectiveOne(string field, string dir)
    {
        var (world, admin, path, _, ids) = await SeedAsync();
        await using var _ = world;
        string[][] asc = field switch
        {
            "startsAt" => [["r1"], ["r2"], ["r3"], ["r4"]],
            "plan" => [["r2"], ["r4"], ["r3"], ["r1"]],
            _ => [["r1", "r2"], ["r3", "r4"]] // Değiştirildi (Superseded), Süresi doldu (Expired)
        };

        var page = await GetAsync(admin, path, $"sort={field}&dir={dir}");

        AssertGroups(Ids(page), Groups(ids, dir == "asc" ? asc : [.. asc.Reverse()]));
        if (field == "status")
        {
            var statuses = page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("status").GetString()).ToArray();
            Assert.Equal(dir == "asc" ? ["Superseded", "Superseded", "Expired", "Expired"] : ["Expired", "Expired", "Superseded", "Superseded"], statuses);
        }
    }

    [Fact]
    public async Task WithoutSort_NewestStartFirst_AndTheShownStatusIsTheEffectiveOne()
    {
        var (world, admin, path, _, ids) = await SeedAsync();
        await using var _ = world;

        var page = await GetAsync(admin, path, "");

        Assert.Equal([ids["r4"], ids["r3"], ids["r2"], ids["r1"]], Ids(page));
        Assert.Equal("Expired", page.GetProperty("items")[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Search_MatchesThePlanNameCaseInsensitively_AndCombinesWithSortAndPaging()
    {
        var (world, admin, path, tag, ids) = await SeedAsync();
        await using var _ = world;

        Assert.Equal([ids["r4"]], Ids(await GetAsync(admin, path, "search=BRAV")));
        Assert.Equal([ids["r1"], ids["r2"]], Ids(await GetAsync(admin, path, $"search=a%20{tag}&sort=startsAt&dir=asc")));
        var second = await GetAsync(admin, path, $"search={tag}&sort=plan&dir=desc&pageSize=3&page=2");
        Assert.Equal(4, second.GetProperty("totalCount").GetInt64());
        Assert.Equal([ids["r2"]], Ids(second));
        Assert.Equal(0, (await GetAsync(admin, path, "search=nothing-like-this")).GetProperty("totalCount").GetInt64());
    }

    [Fact]
    public async Task Paging_IsStableWhenTheSortKeyTies()
    {
        var (world, admin, path, _, _) = await SeedAsync();
        await using var _ = world;

        var seen = new List<string>();
        foreach (var number in new[] { 1, 2, 3, 4 })
            seen.AddRange(Ids(await GetAsync(admin, path, $"sort=status&pageSize=1&page={number}")));

        Assert.Equal(4, seen.Distinct().Count());
        Assert.Equal(Ids(await GetAsync(admin, path, "sort=status&pageSize=100")), seen);
    }

    [Theory]
    [InlineData("sort=endsAt", "sort")]
    [InlineData("sort=status&dir=up", "dir")]
    [InlineData("page=0", "page")]
    public async Task InvalidListParameters_Answer400NamingTheField(string query, string field)
    {
        var (world, admin, path, _, _) = await SeedAsync();
        await using var _ = world;

        var reply = await CallAsync(admin, HttpMethod.Get, $"{path}?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, reply.Status);
        Assert.Equal("invalid_request", reply.Error);
        Assert.Equal(field, reply.Json.GetProperty("field").GetString());
    }
}
