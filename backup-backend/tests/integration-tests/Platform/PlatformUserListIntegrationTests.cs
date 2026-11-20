using System.Net;
using System.Text.Json;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Identity;

namespace BackupPlatform.IntegrationTests;

/// <summary>The shared list contract (search, sort, dir, paging) proven on GET /api/platform/users.</summary>
public sealed class PlatformUserListIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private sealed record Seed(string Name, string? Login = null, string? Email = null, UserRole Role = UserRole.UserAdmin, int Memberships = 0, bool Disabled = false);

    private static string NewTag() => $"tag{Guid.NewGuid():N}"[..14];

    private async Task<(AgentWorld World, BrowserSession Admin)> StartAsync()
    {
        var world = await AgentWorld.CreateAsync(fixture);
        var admin = await world.SignInAsync(await world.SeedUserAsync(platformAdmin: true));
        return (world, admin);
    }

    private async Task SeedAsync(IEnumerable<Seed> seeds)
    {
        await using var context = fixture.CreatePlatformContext();
        foreach (var seed in seeds)
        {
            var user = new User(UserId.New(), seed.Name, seed.Role);
            if (seed.Disabled) user.DisableManual("test");
            context.Users.Add(user);
            if (seed.Login is not null) context.PasswordCredentials.Add(new PasswordCredential(user.Id, seed.Login, "hash", seed.Email));
            for (var i = 0; i < seed.Memberships; i++)
            {
                var tenant = new Tenant(TenantId.New(), $"Tenant {Guid.NewGuid():N}");
                context.Tenants.Add(tenant);
                context.TenantMemberships.Add(new TenantMembership(tenant.Id, user.Id));
            }
        }
        await context.SaveChangesAsync();
    }

    private static async Task<JsonElement> GetAsync(BrowserSession admin, string query, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var response = await admin.GetAsync($"/api/platform/users?{query}");
        Assert.Equal(expected, response.StatusCode);
        return AgentWorld.ReadJson(response);
    }

    private static string[] Names(JsonElement page, string tag) =>
        page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("displayName").GetString()!.Replace(tag + " ", "")).ToArray();

    // Each inner set is a group of rows whose relative order is a tie (broken by id), so it is compared as a set.
    private static void AssertGroups(string[] actual, string[][] groups)
    {
        var index = 0;
        foreach (var group in groups)
        {
            Assert.Equal(group.Order(), actual.Skip(index).Take(group.Length).Order());
            index += group.Length;
        }
        Assert.Equal(index, actual.Length);
    }

    public static TheoryData<string, string, string[][]> Orders => new()
    {
        { "displayName", "asc", [["Alpha"], ["Bravo"], ["charlie"], ["delta"]] },
        { "displayName", "desc", [["delta"], ["charlie"], ["Bravo"], ["Alpha"]] },
        { "username", "asc", [["Alpha"], ["delta"], ["Bravo"], ["charlie"]] },
        { "username", "desc", [["Bravo"], ["delta"], ["Alpha"], ["charlie"]] },
        { "email", "asc", [["Alpha"], ["Bravo"], ["charlie", "delta"]] },
        { "email", "desc", [["Bravo"], ["Alpha"], ["charlie", "delta"]] },
        { "role", "asc", [["Alpha"], ["Bravo", "charlie", "delta"]] },
        { "role", "desc", [["Bravo", "charlie", "delta"], ["Alpha"]] },
        { "membershipCount", "asc", [["Alpha"], ["charlie", "delta"], ["Bravo"]] },
        { "membershipCount", "desc", [["Bravo"], ["charlie", "delta"], ["Alpha"]] },
        { "isDisabled", "asc", [["Alpha", "charlie", "delta"], ["Bravo"]] },
        { "isDisabled", "desc", [["Bravo"], ["Alpha", "charlie", "delta"]] }
    };

    [Theory]
    [MemberData(nameof(Orders))]
    public async Task Sort_OrdersEachFieldBothWaysWithCaseInsensitiveTextAndNullsLast(string field, string dir, string[][] groups)
    {
        var (world, admin) = await StartAsync();
        await using var _ = world;
        var tag = NewTag();
        await SeedAsync(
        [
            new($"{tag} Bravo", $"{tag}-l3", $"c@{tag}.test", Memberships: 2, Disabled: true),
            new($"{tag} Alpha", $"{tag}-l1", $"a@{tag}.test", UserRole.PlatformAdmin),
            new($"{tag} charlie", Memberships: 1),
            new($"{tag} delta", $"{tag}-l2", Memberships: 1)
        ]);

        var page = await GetAsync(admin, $"search={tag}&sort={field}&dir={dir}");

        AssertGroups(Names(page, tag), groups);
    }

    [Fact]
    public async Task Sort_WithoutParametersKeepsNameOrder_AndDirDefaultsToTheFieldDefault()
    {
        var (world, admin) = await StartAsync();
        await using var _ = world;
        var tag = NewTag();
        await SeedAsync([new($"{tag} b"), new($"{tag} C"), new($"{tag} a")]);

        Assert.Equal(["a", "b", "C"], Names(await GetAsync(admin, $"search={tag}"), tag));
        Assert.Equal(["a", "b", "C"], Names(await GetAsync(admin, $"search={tag}&sort=displayName"), tag));
        Assert.Equal(["C", "b", "a"], Names(await GetAsync(admin, $"search={tag}&dir=desc"), tag));
    }

    [Theory]
    [InlineData("sort=password", "sort")]
    [InlineData("sort=displayName&dir=sideways", "dir")]
    [InlineData("pageSize=0", "pageSize")]
    [InlineData("page=0", "page")]
    public async Task InvalidListParameters_Answer400InvalidRequestNamingTheField(string query, string field)
    {
        var (world, admin) = await StartAsync();
        await using var _ = world;

        var body = await GetAsync(admin, query, HttpStatusCode.BadRequest);

        Assert.Equal("invalid_request", body.GetProperty("error").GetString());
        Assert.Equal(field, body.GetProperty("field").GetString());
        Assert.Equal("invalid_value", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Paging_IsStableAcrossPagesWhenTheSortKeyTies()
    {
        var (world, admin) = await StartAsync();
        await using var _ = world;
        var tag = NewTag();
        await SeedAsync(Enumerable.Range(0, 7).Select(_ => new Seed($"{tag} same")));

        var ids = new List<string>();
        foreach (var number in new[] { 1, 2, 3 })
        {
            var page = await GetAsync(admin, $"search={tag}&sort=displayName&pageSize=3&page={number}");
            Assert.Equal(7, page.GetProperty("totalCount").GetInt64());
            ids.AddRange(page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("userId").GetString()!));
        }
        var all = await GetAsync(admin, $"search={tag}&sort=displayName&pageSize=100");

        Assert.Equal(7, ids.Distinct().Count());
        Assert.Equal(all.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("userId").GetString()), ids);
    }

    [Fact]
    public async Task Search_SortAndPageWorkTogether_AndWildcardsAreLiteral()
    {
        var (world, admin) = await StartAsync();
        await using var _ = world;
        var tag = NewTag();
        await SeedAsync(
        [
            new($"{tag} n1"), new($"{tag} n2"), new($"{tag} n3"), new($"{tag} n4", Login: $"{tag}-login"), new($"other {tag}"),
            new($"{tag} 100% sure"), new($"{tag} 1000 sure"), new($"{tag} a_b"), new($"{tag} axb")
        ]);

        var second = await GetAsync(admin, $"search={tag.ToUpperInvariant()}%20n&sort=displayName&dir=desc&pageSize=2&page=2");

        Assert.Equal(4, second.GetProperty("totalCount").GetInt64());
        Assert.Equal(["n2", "n1"], Names(second, tag));
        Assert.Equal([$"{tag} 100% sure"], (await GetAsync(admin, $"search={tag}%20100%25")).GetProperty("items").EnumerateArray().Select(item => item.GetProperty("displayName").GetString()));
        Assert.Equal([$"{tag} a_b"], (await GetAsync(admin, $"search={tag}%20a_b")).GetProperty("items").EnumerateArray().Select(item => item.GetProperty("displayName").GetString()));
        Assert.Equal(1, (await GetAsync(admin, $"search={tag}-LOGIN")).GetProperty("totalCount").GetInt64());
    }
}
