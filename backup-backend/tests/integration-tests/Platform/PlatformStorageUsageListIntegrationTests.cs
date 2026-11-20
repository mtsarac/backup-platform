using System.Net;
using System.Text.Json;
using BackupPlatform.Domain.Identity;
using BackupPlatform.IntegrationTests.Quota;
using BackupPlatform.ManagedService.Subscriptions;
using Microsoft.EntityFrameworkCore;
using static BackupPlatform.IntegrationTests.ManagedService.ManagedServiceTestSupport;

namespace BackupPlatform.IntegrationTests;

/// <summary>
/// GET /api/platform/storage-usage: search, sort and paging done by the database over the DERIVED standing (used, limit, state, subscription status).
/// The sort expectations are computed from the values the same response reports, so the SQL restatement of the resolver cannot drift unnoticed.
/// </summary>
public sealed class PlatformStorageUsageListIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private const long Gb = QuotaWorld.Gb;
    private readonly QuotaWorld _quota = new(fixture);

    private async Task<(AgentWorld World, BrowserSession Admin, string Tag, Dictionary<string, Guid> Ids)> SeedAsync()
    {
        var world = await AgentWorld.CreateAsync(fixture);
        var admin = await world.SignInAsync(await world.SeedUserAsync(platformAdmin: true));
        var tag = $"u{Guid.NewGuid():N}"[..12];
        var ids = new Dictionary<string, Guid>();

        // A policy that never enforces: a tenant under it is Unlimited however full it is.
        var policyKey = $"none-{Guid.NewGuid():N}";
        var policy = await CallAsync(admin, HttpMethod.Post, "/api/platform/quota-policies", new
        {
            key = policyKey, name = UniqueName("Policy"), accountingMode = "LogicalArtifactBytes", includedStorageScopes = new[] { new { kind = "all" } },
            enforcementMode = "None", warningThresholdPercent = 80, criticalThresholdPercent = 90, enabled = true
        });
        Assert.Equal(HttpStatusCode.Created, policy.Status);

        async Task AddAsync(string label, long? limit, long used, long reserved = 0, bool subscribe = true, StorageLimitOverride? limitOverride = null, string? key = null)
        {
            var tenant = await world.AddTenantAsync();
            await using (var context = fixture.CreatePlatformContext())
            {
                await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE tenants SET \"Name\" = {$"{label} {tag}"} WHERE \"Id\" = {tenant.Id.Value}");
            }
            if (subscribe) await _quota.AssignAsync(tenant.Id, await _quota.PlanAsync(limit, key), limitOverride);
            if (used >= 0)
            {
                await _quota.Usage().GetUsageAsync(tenant.Id, default);
                await _quota.CorruptUsageAsync(tenant.Id, used, reserved);
            }
            ids[label] = tenant.Id.Value;
        }

        await AddAsync("aa", 10 * Gb, 1 * Gb);                                              // Normal
        await AddAsync("bb", 5 * Gb, 4 * Gb, reserved: 1 * Gb);                             // OverQuota (used + reserved reach the limit)
        await AddAsync("cc", null, 2 * Gb);                                                 // Unlimited plan
        await AddAsync("dd", null, -1, subscribe: false);                                   // no subscription, no usage record
        await AddAsync("ee", 10 * Gb, 8_500_000_000);                                       // Warning
        await AddAsync("ff", 10 * Gb, 9_500_000_000);                                       // Critical
        await AddAsync("gg", 4 * Gb, 1 * Gb);                                               // Expired below
        await AddAsync("hh", 10 * Gb, 1 * Gb);                                              // ties with aa on every figure
        await AddAsync("ii", 10 * Gb, 3 * Gb);                                              // Suspended below
        await AddAsync("jj", 50 * Gb, 1_500_000_000, limitOverride: StorageLimitOverride.Limited(2 * Gb));   // override beats the plan
        await AddAsync("kk", 5 * Gb, 4_900_000_000, key: policyKey);                         // the plan's own policy: never enforces
        await AddAsync("ll", 5 * Gb, -1);                                                     // subscribed, nothing stored yet: assigning a plan starts its projection at zero

        await using (var context = fixture.CreatePlatformContext())
        {
            // An open row whose end has passed is Expired for every reader before any writer stores it.
            var gg = ids["gg"];
            var started = new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);
            var ended = started.AddDays(1);
            Assert.Equal(1, await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE tenant_subscriptions SET \"StartsAt\" = {started}, \"EndsAt\" = {ended} WHERE \"TenantId\" = {gg}"));
        }
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(admin, HttpMethod.Post, SubscriptionPath(ids["ii"], "/suspend"))).Status);
        return (world, admin, tag, ids);
    }

    private static async Task<JsonElement> GetAsync(BrowserSession admin, string tag, string query = "", HttpStatusCode expected = HttpStatusCode.OK)
    {
        var reply = await CallAsync(admin, HttpMethod.Get, $"/api/platform/storage-usage?search={tag}&{query}");
        Assert.Equal(expected, reply.Status);
        return reply.Json;
    }

    private static string[] Labels(JsonElement page, string tag) =>
        page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("tenantName").GetString()![..^(tag.Length + 1)]).ToArray();

    // The states in the order of their Turkish labels: Kota aşıldı, Kritik, Normal, Sınırsız, Uyarı.
    private static readonly string[] States = ["OverQuota", "Critical", "Normal", "Unlimited", "Warning"];

    [Fact]
    public async Task TheDerivedStandingIsTheOneTheResolverReports()
    {
        var (world, admin, tag, _) = await SeedAsync();
        await using var _ = world;

        var page = await GetAsync(admin, tag, "pageSize=100");

        var rows = page.GetProperty("items").EnumerateArray().ToDictionary(item => item.GetProperty("tenantName").GetString()![..^(tag.Length + 1)]);
        string? State(string label) => rows[label].GetProperty("state").GetString();
        string Status(string label) => rows[label].GetProperty("subscriptionStatus").GetString()!;
        Assert.Equal(new string?[] { "Normal", "OverQuota", "Unlimited", null, "Warning", "Critical", "Normal", "Normal", "Normal", "Normal", "Unlimited", "Normal" },
            new[] { "aa", "bb", "cc", "dd", "ee", "ff", "gg", "hh", "ii", "jj", "kk", "ll" }.Select(State));
        Assert.Equal(["Expired", "Suspended", "NoSubscription"], [Status("gg"), Status("ii"), Status("dd")]);
        Assert.Equal(2 * Gb, rows["jj"].GetProperty("limitBytes").GetInt64());
    }

    public static TheoryData<string, string> SortFields
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var key in new[] { "tenant", "used", "quota", "state" })
                foreach (var dir in new[] { "asc", "desc" })
                    data.Add(key, dir);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(SortFields))]
    public async Task Sort_OrdersByTheDerivedValuesWithMissingOnesLastAndTiesByTenantId(string field, string dir)
    {
        var (world, admin, tag, _) = await SeedAsync();
        await using var _ = world;

        var page = await GetAsync(admin, tag, $"pageSize=100&sort={field}&dir={dir}");

        var items = page.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(12, items.Count);
        long? Number(JsonElement element, string name) => element.GetProperty(name).ValueKind == JsonValueKind.Null ? null : element.GetProperty(name).GetInt64();
        object? Key(JsonElement item) => field switch
        {
            "tenant" => item.GetProperty("tenantName").GetString()!.ToLowerInvariant(),
            "used" => Number(item, "usedBytes") is { } used ? used + Number(item, "reservedBytes") : null,
            "quota" => Number(item, "limitBytes"),
            _ => item.GetProperty("state").GetString() is { } state ? Array.IndexOf(States, state) : (int?)null
        };
        var descending = dir == "desc";
        var expected = items
            .OrderBy(item => Key(item) is null)
            .ThenBy(item => descending ? null : Key(item), Comparer<object?>.Create(Compare))
            .ThenByDescending(item => descending ? Key(item) : null, Comparer<object?>.Create(Compare))
            .ThenBy(item => item.GetProperty("tenantId").GetGuid().ToString("D"), StringComparer.Ordinal)
            .Select(item => item.GetProperty("tenantId").GetGuid());
        Assert.Equal(expected, items.Select(item => item.GetProperty("tenantId").GetGuid()));
    }

    private static int Compare(object? left, object? right) => (left, right) switch
    {
        (null, null) => 0,
        (string a, string b) => string.CompareOrdinal(a, b),
        (long a, long b) => a.CompareTo(b),
        (int a, int b) => a.CompareTo(b),
        _ => throw new InvalidOperationException("Keys of one field have one type.")
    };

    [Fact]
    public async Task Sort_ByStateAndUsedPutsTheExpectedTenantsFirstAndLast()
    {
        var (world, admin, tag, _) = await SeedAsync();
        await using var _ = world;

        var byState = Labels(await GetAsync(admin, tag, "pageSize=100&sort=state&dir=asc"), tag);
        var byStateDesc = Labels(await GetAsync(admin, tag, "pageSize=100&sort=state&dir=desc"), tag);
        var byUsed = Labels(await GetAsync(admin, tag, "pageSize=100&sort=used&dir=desc"), tag);

        Assert.Equal("bb", byState[0]);                       // OverQuota first
        Assert.Equal("ee", byStateDesc[0]);                   // Warning is the last label
        Assert.Equal("dd", byState[^1]);                      // no usage record: last, in both directions
        Assert.Equal("dd", byStateDesc[^1]);
        Assert.Equal(["ff", "ee"], byUsed[..2]);              // 9.5 GB, 8.5 GB
        Assert.Equal("dd", byUsed[^1]);
    }

    [Fact]
    public async Task DefaultOrderIsTenantNameAZ_AndSearchFindsTenantsCaseInsensitively()
    {
        var (world, admin, tag, _) = await SeedAsync();
        await using var _ = world;

        var all = Labels(await GetAsync(admin, tag, "pageSize=100"), tag);
        var upper = await GetAsync(admin, tag.ToUpperInvariant());

        Assert.Equal(["aa", "bb", "cc", "dd", "ee", "ff", "gg", "hh", "ii", "jj", "kk", "ll"], all);
        Assert.Equal(12, upper.GetProperty("totalCount").GetInt64());
        Assert.Equal(1, (await GetAsync(admin, $"gg%20{tag}")).GetProperty("totalCount").GetInt64());
    }

    [Fact]
    public async Task Filters_CombineWithSearchAndSort()
    {
        var (world, admin, tag, ids) = await SeedAsync();
        await using var _ = world;

        var normal = await GetAsync(admin, tag, "quotaState=Normal&sort=used&dir=desc&pageSize=100");
        var expired = await GetAsync(admin, tag, "subscriptionStatus=Expired");
        var suspended = await GetAsync(admin, tag, "subscriptionStatus=Suspended&quotaState=Normal");
        var none = await GetAsync(admin, tag, "subscriptionStatus=NoSubscription&quotaState=Normal");
        var narrowed = await GetAsync(admin, $"hh%20{tag}", "quotaState=Normal");

        var normalLabels = Labels(normal, tag);
        Assert.Equal(["ii", "jj"], normalLabels[..2]);
        Assert.Equal(["aa", "gg", "hh"], normalLabels[2..5].Order());
        Assert.Equal("ll", normalLabels[5]);
        Assert.Equal(6, normal.GetProperty("totalCount").GetInt64());
        Assert.Equal([ids["gg"]], expired.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("tenantId").GetGuid()));
        Assert.Equal([ids["ii"]], suspended.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("tenantId").GetGuid()));
        Assert.Equal(0, none.GetProperty("totalCount").GetInt64());   // a tenant without a record has no state to match
        Assert.Equal([ids["hh"]], narrowed.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("tenantId").GetGuid()));
    }

    [Fact]
    public async Task Paging_IsStableWhenTheSortKeyTies()
    {
        var (world, admin, tag, _) = await SeedAsync();
        await using var _ = world;

        var seen = new List<Guid>();
        foreach (var number in new[] { 1, 2, 3, 4 })
        {
            var page = await GetAsync(admin, tag, $"sort=state&pageSize=3&page={number}");
            Assert.Equal(12, page.GetProperty("totalCount").GetInt64());
            seen.AddRange(page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("tenantId").GetGuid()));
        }
        var whole = await GetAsync(admin, tag, "sort=state&pageSize=100");

        Assert.Equal(12, seen.Distinct().Count());
        Assert.Equal(whole.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("tenantId").GetGuid()), seen);
    }

    [Theory]
    [InlineData("sort=limit", "sort")]
    [InlineData("sort=state&dir=sideways", "dir")]
    [InlineData("pageSize=0", "pageSize")]
    public async Task InvalidListParameters_Answer400NamingTheField(string query, string field)
    {
        var (world, admin, tag, _) = await SeedAsync();
        await using var _ = world;

        var body = await GetAsync(admin, tag, query, HttpStatusCode.BadRequest);

        Assert.Equal("invalid_request", body.GetProperty("error").GetString());
        Assert.Equal(field, body.GetProperty("field").GetString());
    }
}
