using System.Net;
using System.Text.Json;
using BackupPlatform.Agents.Connectivity;
using BackupPlatform.Domain.Agents;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Identity;
using BackupPlatform.IntegrationTests.ManagedService;
using BackupPlatform.ManagedService.Plans;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.IntegrationTests;

/// <summary>The shared list contract (search, sort, dir, paging) on the platform tenants, tenant members, agents and plans endpoints.</summary>
public sealed class PlatformListsIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static string NewTag() => $"tag{Guid.NewGuid():N}"[..14];

    private async Task<(AgentWorld World, BrowserSession Admin)> StartAsync()
    {
        var world = await AgentWorld.CreateAsync(fixture);
        return (world, await world.SignInAsync(await world.SeedUserAsync(platformAdmin: true)));
    }

    private static async Task<JsonElement> GetAsync(BrowserSession admin, string path, string query, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var response = await admin.GetAsync($"{path}?{query}");
        Assert.Equal(expected, response.StatusCode);
        return AgentWorld.ReadJson(response);
    }

    private static string[] Strings(JsonElement page, string property, string tag) =>
        page.GetProperty("items").EnumerateArray().Select(item => tag.Length == 0 ? item.GetProperty(property).GetString()! : item.GetProperty(property).GetString()!.Replace(tag + " ", "")).ToArray();

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

    private static async Task AssertRejectsBadListParametersAsync(BrowserSession admin, string path)
    {
        foreach (var (query, field) in new[] { ("sort=password", "sort"), ("sort=name&dir=sideways", "dir"), ("pageSize=0", "pageSize"), ("page=0", "page") })
        {
            var body = await GetAsync(admin, path, query, HttpStatusCode.BadRequest);
            Assert.Equal("invalid_request", body.GetProperty("error").GetString());
            Assert.Equal(field, body.GetProperty("field").GetString());
            Assert.Equal("invalid_value", body.GetProperty("code").GetString());
        }
    }

    private static async Task AssertStablePagingAsync(BrowserSession admin, string path, string search, string sort, string idProperty, int total)
    {
        var ids = new List<string>();
        for (var number = 1; number <= 3; number++)
        {
            var page = await GetAsync(admin, path, $"search={search}&sort={sort}&pageSize=3&page={number}");
            Assert.Equal(total, page.GetProperty("totalCount").GetInt64());
            ids.AddRange(page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty(idProperty).GetString()!));
        }
        var all = await GetAsync(admin, path, $"search={search}&sort={sort}&pageSize=100");
        Assert.Equal(total, ids.Distinct().Count());
        Assert.Equal(all.GetProperty("items").EnumerateArray().Select(item => item.GetProperty(idProperty).GetString()), ids);
    }

    // ---- tenants ----

    private async Task<Tenant> SeedTenantAsync(AgentWorld world, string name, int members, int agents, bool enrollment)
    {
        var tenant = new Tenant(TenantId.New(), name);
        await using (var context = fixture.CreatePlatformContext())
        {
            context.Tenants.Add(tenant);
            for (var i = 0; i < members; i++)
            {
                var user = new User(UserId.New(), $"member {i}", UserRole.UserAdmin);
                context.Users.Add(user);
                context.TenantMemberships.Add(new TenantMembership(tenant.Id, user.Id));
            }
            for (var i = 0; i < agents; i++) context.Agents.Add(NewAgent(tenant.Id, $"host-{i}"));
            await context.SaveChangesAsync();
        }
        if (enrollment) await world.SetEnrollmentAsync(tenant, true);
        return tenant;
    }

    private static Agent NewAgent(TenantId tenantId, string host) =>
        new(AgentId.New(), tenantId, new AgentSoftware(AgentPlatform.Linux, new Version(1, 0), new AgentProtocolVersion(1, 0)), host, Guid.NewGuid());

    public static TheoryData<string, string, string[][]> TenantOrders => new()
    {
        { "name", "asc", [["Bravo"], ["charlie"], ["delta"]] },
        { "name", "desc", [["delta"], ["charlie"], ["Bravo"]] },
        { "memberCount", "asc", [["delta"], ["charlie"], ["Bravo"]] },
        { "memberCount", "desc", [["Bravo"], ["charlie"], ["delta"]] },
        { "agentCount", "asc", [["Bravo"], ["delta"], ["charlie"]] },
        { "agentCount", "desc", [["charlie"], ["delta"], ["Bravo"]] },
        { "enrollmentEnabled", "asc", [["charlie", "delta"], ["Bravo"]] },
        { "enrollmentEnabled", "desc", [["Bravo"], ["charlie", "delta"]] }
    };

    [Theory]
    [MemberData(nameof(TenantOrders))]
    public async Task Tenants_SortEachFieldBothWays(string field, string dir, string[][] groups)
    {
        var (world, admin) = await StartAsync();
        await using var _ = world;
        var tag = NewTag();
        await SeedTenantAsync(world, $"{tag} Bravo", members: 2, agents: 0, enrollment: true);
        await SeedTenantAsync(world, $"{tag} charlie", members: 1, agents: 2, enrollment: false);
        await SeedTenantAsync(world, $"{tag} delta", members: 0, agents: 1, enrollment: false);

        AssertGroups(Strings(await GetAsync(admin, "/api/platform/tenants", $"search={tag}&sort={field}&dir={dir}"), "name", tag), groups);
    }

    [Fact]
    public async Task Tenants_DefaultToNameAToZ_SearchIsLiteralAndCaseInsensitive_BadParametersAre400_PagingIsStable()
    {
        var (world, admin) = await StartAsync();
        await using var _ = world;
        var tag = NewTag();
        foreach (var name in new[] { "b", "C", "a", "100% x", "1000 x", "a_b", "axb" }) await SeedTenantAsync(world, $"{tag} {name}", 0, 0, false);
        var pagingTag = NewTag();
        for (var i = 0; i < 5; i++) await SeedTenantAsync(world, $"{pagingTag} same", 0, 0, false);

        Assert.Equal(["100% x", "1000 x", "a", "a_b", "axb", "b", "C"], Strings(await GetAsync(admin, "/api/platform/tenants", $"search={tag.ToUpperInvariant()}%20&pageSize=100"), "name", tag));
        Assert.Equal(["C", "b"], Strings(await GetAsync(admin, "/api/platform/tenants", $"search={tag}%20&sort=name&dir=desc&pageSize=2"), "name", tag));
        Assert.Equal([$"{tag} 100% x"], Strings(await GetAsync(admin, "/api/platform/tenants", $"search={tag}%20100%25"), "name", ""));
        Assert.Equal([$"{tag} a_b"], Strings(await GetAsync(admin, "/api/platform/tenants", $"search={tag}%20a_b"), "name", ""));
        await AssertStablePagingAsync(admin, "/api/platform/tenants", pagingTag, "name", "tenantId", 5);
        await AssertRejectsBadListParametersAsync(admin, "/api/platform/tenants");
    }

    // ---- tenant members ----

    private sealed record Member(string Name, string? Login = null);

    private async Task<Tenant> SeedMembersAsync(AgentWorld world, IEnumerable<Member> members)
    {
        var tenant = await world.AddTenantAsync();
        await using var context = fixture.CreatePlatformContext();
        foreach (var member in members)
        {
            var user = new User(UserId.New(), member.Name, UserRole.UserAdmin);
            context.Users.Add(user);
            if (member.Login is not null) context.PasswordCredentials.Add(new PasswordCredential(user.Id, member.Login, "hash", null));
            context.TenantMemberships.Add(new TenantMembership(tenant.Id, user.Id));
        }
        await context.SaveChangesAsync();
        return tenant;
    }

    public static TheoryData<string, string, string[][]> MemberOrders => new()
    {
        { "displayName", "asc", [["Alpha"], ["Bravo"], ["charlie"]] },
        { "displayName", "desc", [["charlie"], ["Bravo"], ["Alpha"]] },
        { "username", "asc", [["Alpha"], ["charlie"], ["Bravo"]] },
        { "username", "desc", [["charlie"], ["Alpha"], ["Bravo"]] }
    };

    [Theory]
    [MemberData(nameof(MemberOrders))]
    public async Task Members_SortEachFieldBothWaysWithMissingLoginsLast(string field, string dir, string[][] groups)
    {
        var (world, admin) = await StartAsync();
        await using var _ = world;
        var tag = NewTag();
        var tenant = await SeedMembersAsync(world, [new($"{tag} Bravo"), new($"{tag} Alpha", $"{tag}-a"), new($"{tag} charlie", $"{tag}-B")]);

        AssertGroups(Strings(await GetAsync(admin, $"/api/platform/tenants/{tenant.Id.Value}/memberships", $"sort={field}&dir={dir}"), "displayName", tag), groups);
    }

    [Fact]
    public async Task Members_DefaultToNameAToZ_SearchNameOrLogin_UnknownTenantIs404_BadParametersAre400_PagingIsStable()
    {
        var (world, admin) = await StartAsync();
        await using var _ = world;
        var tag = NewTag();
        var tenant = await SeedMembersAsync(world, [new($"{tag} b", "zz-login"), new($"{tag} C"), new($"{tag} a"), .. Enumerable.Range(0, 6).Select(_ => new Member($"{tag} same"))]);
        var path = $"/api/platform/tenants/{tenant.Id.Value}/memberships";

        Assert.Equal(["a", "b", "C"], Strings(await GetAsync(admin, path, "search=" + tag + "%20&pageSize=3"), "displayName", tag));
        Assert.Equal([$"{tag} b"], Strings(await GetAsync(admin, path, "search=ZZ-LOGIN"), "displayName", ""));
        await GetAsync(admin, $"/api/platform/tenants/{Guid.NewGuid()}/memberships", "", HttpStatusCode.NotFound);
        await AssertStablePagingAsync(admin, path, "same", "displayName", "userId", 6);
        await AssertRejectsBadListParametersAsync(admin, path);
    }

    // ---- agents ----

    private async Task SeedAgentAsync(Tenant tenant, string host, AgentStatus status, DateTimeOffset? enrolledAt)
    {
        var agent = NewAgent(tenant.Id, host);
        if (status != AgentStatus.PendingEnrollment) agent.RecordEnrollment();
        if (status == AgentStatus.Disabled) agent.Disable();
        if (status == AgentStatus.Revoked) agent.Revoke();
        await using var context = fixture.CreatePlatformContext();
        context.Agents.Add(agent);
        if (enrolledAt is { } at) context.AgentCredentials.Add(BackupPlatform.Agents.Credentials.AgentCredential.IssueInitial(agent.Id, agent.TenantId, at).Credential);
        await context.SaveChangesAsync();
    }

    public static TheoryData<string, string, string[][]> AgentOrders => new()
    {
        { "hostName", "asc", [["Bravo"], ["charlie"], ["delta"]] },
        { "hostName", "desc", [["delta"], ["charlie"], ["Bravo"]] },
        { "status", "asc", [["delta"], ["charlie"], ["Bravo"]] },
        { "status", "desc", [["Bravo"], ["charlie"], ["delta"]] },
        { "enrolledAt", "asc", [["charlie"], ["Bravo"], ["delta"]] },
        { "enrolledAt", "desc", [["Bravo"], ["charlie"], ["delta"]] }
    };

    [Theory]
    [MemberData(nameof(AgentOrders))]
    public async Task Agents_SortHostStatusAndEnrolmentBothWaysWithPendingLast(string field, string dir, string[][] groups)
    {
        var (world, admin) = await StartAsync();
        await using var _ = world;
        var tag = NewTag();
        var tenant = await world.AddTenantAsync();
        var now = DateTimeOffset.UtcNow;
        await SeedAgentAsync(tenant, $"{tag} Bravo", AgentStatus.Revoked, now);
        await SeedAgentAsync(tenant, $"{tag} charlie", AgentStatus.Disabled, now.AddDays(-2));
        await SeedAgentAsync(tenant, $"{tag} delta", AgentStatus.Active, null);

        AssertGroups(Strings(await GetAsync(admin, "/api/platform/agents", $"search={tag}&sort={field}&dir={dir}"), "hostName", tag), groups);
    }

    [Fact]
    public async Task Agents_SortByTenantName()
    {
        var (world, admin) = await StartAsync();
        await using var _ = world;
        var tag = NewTag();
        foreach (var name in new[] { "Bravo", "alpha" })
        {
            var tenant = new Tenant(TenantId.New(), $"{tag} {name}");
            await using (var context = fixture.CreatePlatformContext()) { context.Tenants.Add(tenant); await context.SaveChangesAsync(); }
            await SeedAgentAsync(tenant, $"host-of-{name}", AgentStatus.Active, null);
        }

        Assert.Equal(["alpha", "Bravo"], Strings(await GetAsync(admin, "/api/platform/agents", $"search={tag}&sort=tenantName"), "tenantName", tag));
        Assert.Equal(["Bravo", "alpha"], Strings(await GetAsync(admin, "/api/platform/agents", $"search={tag}&sort=tenantName&dir=desc"), "tenantName", tag));
    }

    [Fact]
    public async Task Agents_DefaultNewestEnrolledFirst_SortByLastSeenAndConnectivity_KeepFilters_BadParametersAre400()
    {
        var (world, admin) = await StartAsync();
        await using var _ = world;
        var setup = await world.CreateTenantAsync();
        var first = await world.EnrollAsync(setup);
        world.Clock.Advance(TimeSpan.FromMinutes(1));
        var second = await world.EnrollAsync(setup);
        world.Clock.Advance(TimeSpan.FromMinutes(1));
        var third = await world.EnrollAsync(setup);
        (await world.HeartbeatAsync(first)).Dispose();
        world.Clock.Advance(TimeSpan.FromMinutes(10));
        (await world.HeartbeatAsync(second)).Dispose();
        var query = $"tenantId={setup.Tenant.Id.Value}";
        string[] Ids(JsonElement page) => page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("agentId").GetString()!).ToArray();
        string[] Of(params EnrolledAgent[] agents) => agents.Select(agent => agent.AgentId.ToString()).ToArray();
        const string path = "/api/platform/agents";

        Assert.Equal(Of(third, second, first), Ids(await GetAsync(admin, path, query)));
        Assert.Equal(Of(first, second, third), Ids(await GetAsync(admin, path, $"{query}&sort=enrolledAt&dir=asc")));
        Assert.Equal(Of(second, first, third), Ids(await GetAsync(admin, path, $"{query}&sort=lastSeenAt")));
        Assert.Equal(Of(first, second, third), Ids(await GetAsync(admin, path, $"{query}&sort=lastSeenAt&dir=asc")));
        Assert.Equal(Of(second, first, third), Ids(await GetAsync(admin, path, $"{query}&sort=connectivity")));
        Assert.Equal(Of(third, first, second), Ids(await GetAsync(admin, path, $"{query}&sort=connectivity&dir=desc")));
        Assert.Equal(Of(second), Ids(await GetAsync(admin, path, $"{query}&connectivity=online&sort=hostName&search=integration")));
        Assert.Equal(Of(third), Ids(await GetAsync(admin, path, $"{query}&connectivity=unknown")));
        await AssertRejectsBadListParametersAsync(admin, path);
        await AssertStablePagingAsync(admin, path, "integration", "hostName", "agentId", 3);
    }

    // ---- plans ----

    private sealed record Seed(string Name, long? Bytes, bool Enabled, DateTimeOffset CreatedAt);

    private async Task<List<Guid>> SeedPlansAsync(params Seed[] seeds)
    {
        await using var context = fixture.CreatePlatformContext();
        var plans = seeds.Select(seed => new Plan(PlanId.New(), seed.Name, seed.Bytes, null, seed.Enabled, seed.CreatedAt)).ToList();
        context.Plans.AddRange(plans);
        await context.SaveChangesAsync();
        return plans.Select(plan => plan.Id.Value).ToList();
    }

    public static TheoryData<string, string, string[][]> PlanOrders => new()
    {
        { "name", "asc", [["Bravo"], ["charlie"], ["delta"]] },
        { "name", "desc", [["delta"], ["charlie"], ["Bravo"]] },
        { "enabled", "asc", [["charlie"], ["Bravo", "delta"]] },
        { "enabled", "desc", [["Bravo", "delta"], ["charlie"]] },
        { "storageLimitBytes", "asc", [["charlie"], ["Bravo"], ["delta"]] },
        { "storageLimitBytes", "desc", [["Bravo"], ["charlie"], ["delta"]] },
        { "tenantCount", "asc", [["charlie", "delta"], ["Bravo"]] },
        { "tenantCount", "desc", [["Bravo"], ["charlie", "delta"]] },
        { "createdAt", "asc", [["charlie"], ["Bravo"], ["delta"]] },
        { "createdAt", "desc", [["delta"], ["Bravo"], ["charlie"]] }
    };

    [Theory]
    [MemberData(nameof(PlanOrders))]
    public async Task Plans_SortEachFieldBothWaysWithUnlimitedLast(string field, string dir, string[][] groups)
    {
        var (world, admin) = await StartAsync();
        await using var _ = world;
        var tag = NewTag();
        var now = DateTimeOffset.UtcNow;
        var ids = await SeedPlansAsync(
            new($"{tag} Bravo", 5 * ManagedServiceTestSupport.Gb, true, now.AddDays(-1)),
            new($"{tag} charlie", 1 * ManagedServiceTestSupport.Gb, false, now.AddDays(-3)),
            new($"{tag} delta", null, true, now));
        var tenant = await world.AddTenantAsync();
        Assert.Equal(HttpStatusCode.OK, (await ManagedServiceTestSupport.CallAsync(admin, HttpMethod.Put, ManagedServiceTestSupport.SubscriptionPath(tenant.Id.Value), new { planId = ids[0] })).Status);

        AssertGroups(Strings(await GetAsync(admin, "/api/platform/plans", $"search={tag}&sort={field}&dir={dir}"), "name", tag), groups);
    }

    [Fact]
    public async Task Plans_DefaultNewestFirst_KeepTheEnabledFilter_SearchIsLiteral_BadParametersAre400_PagingIsStable()
    {
        var (world, admin) = await StartAsync();
        await using var _ = world;
        var tag = NewTag();
        var now = DateTimeOffset.UtcNow;
        await SeedPlansAsync(
            new($"{tag} old", 1, true, now.AddDays(-2)), new($"{tag} new", 1, false, now), new($"{tag} mid", 1, true, now.AddDays(-1)),
            new($"{tag} 100% x", 1, true, now.AddDays(-5)), new($"{tag} 1000 x", 1, true, now.AddDays(-6)));
        var pagingTag = NewTag();
        await SeedPlansAsync(Enumerable.Range(0, 5).Select(i => new Seed($"{pagingTag} {i}", 1, true, now)).ToArray());

        Assert.Equal(["new", "mid", "old"], Strings(await GetAsync(admin, "/api/platform/plans", $"search={tag.ToUpperInvariant()}%20&pageSize=3"), "name", tag));
        Assert.Equal(["new"], Strings(await GetAsync(admin, "/api/platform/plans", $"search={tag}&enabled=false"), "name", tag));
        Assert.Equal([$"{tag} 100% x"], Strings(await GetAsync(admin, "/api/platform/plans", $"search={tag}%20100%25"), "name", ""));
        await AssertStablePagingAsync(admin, "/api/platform/plans", pagingTag, "createdAt", "planId", 5);
        await AssertRejectsBadListParametersAsync(admin, "/api/platform/plans");
    }
}
