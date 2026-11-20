using System.Net;
using System.Text.Json;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Notifications;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.IntegrationTests.Notifications;

/// <summary>
/// The two delivery lists: the tenant's history (GET /api/tenants/{id}/notifications/deliveries) and the platform's failed deliveries
/// (GET /api/platform/notifications/deliveries/failed). Search, sort and the existing filters all run in SQL.
/// </summary>
public sealed class NotificationDeliveryListIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static readonly DateTimeOffset T0 = new(2026, 4, 1, 8, 0, 0, TimeSpan.Zero);

    private sealed record Seed(string Status, NotificationChannelKind Kind, string EventType, string Channel, int Attempts, string? Error, int Day);

    private sealed record Scenario(AgentWorld World, BrowserSession Tenant, BrowserSession Platform, string TenantPath, string Tag, Dictionary<string, Guid> Ids);

    private async Task<Scenario> SeedAsync()
    {
        var world = await AgentWorld.CreateAsync(fixture);
        var setup = await world.CreateTenantAsync();
        var other = await world.CreateTenantAsync();
        var platform = await world.SignInAsync(await world.SeedUserAsync(platformAdmin: true));
        var tag = $"nd{Guid.NewGuid():N}"[..12];
        var ids = new Dictionary<string, Guid>();
        await using var context = fixture.CreatePlatformContext();

        async Task AddAsync(string label, TenantId tenant, Seed seed)
        {
            var notificationEvent = new NotificationEvent(Guid.NewGuid(), tenant, seed.EventType, NotificationPhase.Raised, NotificationSeverity.Critical,
                new NotificationSubject("agent", Guid.NewGuid(), $"{label} {tag}"), null, $"seed:{Guid.NewGuid():N}", T0, T0);
            var delivery = new NotificationDelivery(Guid.NewGuid(), tenant, notificationEvent.Id, Guid.NewGuid(), seed.Kind, seed.Channel, $"{seed.Channel}@example.test", T0.AddDays(seed.Day));
            context.NotificationEvents.Add(notificationEvent);
            context.NotificationDeliveries.Add(delivery);
            await context.SaveChangesAsync();
            if (seed.Status != "Pending")
            {
                await context.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE notification_deliveries SET "Status" = {seed.Status}, "AttemptCount" = {seed.Attempts}, "ErrorCode" = {seed.Error}, "NextAttemptAt" = NULL, "CompletedAt" = "CreatedAt"
                    WHERE "Id" = {delivery.Id}
                    """);
            }
            ids[label] = delivery.Id;
        }

        await AddAsync("d1", setup.Tenant.Id, new("Failed", NotificationChannelKind.Email, NotificationEventTypes.AgentOffline, "zeta", 3, "smtp_timeout", 0));
        await AddAsync("d2", setup.Tenant.Id, new("Sent", NotificationChannelKind.Webhook, NotificationEventTypes.BackupFailed, "Alpha", 1, null, 1));
        await AddAsync("d3", setup.Tenant.Id, new("Pending", NotificationChannelKind.Email, NotificationEventTypes.QuotaCritical, "mid", 0, null, 2));
        await AddAsync("d4", setup.Tenant.Id, new("Failed", NotificationChannelKind.Webhook, NotificationEventTypes.AgentOffline, "beta", 5, "http_500", 3));
        await AddAsync("d5", setup.Tenant.Id, new("Sent", NotificationChannelKind.Sms, NotificationEventTypes.BackupFailed, "gamma", 1, null, 4));
        await AddAsync("f1", other.Tenant.Id, new("Failed", NotificationChannelKind.Sms, NotificationEventTypes.BackupFailed, "omega", 2, "sms_rejected", 5));
        return new Scenario(world, setup.Admin, platform, $"/api/tenants/{setup.Tenant.Id.Value}/notifications/deliveries", tag, ids);
    }

    private static async Task<JsonElement> GetAsync(BrowserSession session, string path, string query, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var response = await session.GetAsync($"{path}?{query}");
        Assert.Equal(expected, response.StatusCode);
        return AgentWorld.ReadJson(response);
    }

    private const string FailedPath = "/api/platform/notifications/deliveries/failed";

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

    private static Guid[][] Groups(Dictionary<string, Guid> ids, params string[][] labels) => [.. labels.Select(group => group.Select(label => ids[label]).ToArray())];

    [Theory]
    [InlineData("createdAt", "asc")]
    [InlineData("createdAt", "desc")]
    [InlineData("status", "asc")]
    [InlineData("status", "desc")]
    [InlineData("kind", "asc")]
    [InlineData("kind", "desc")]
    [InlineData("eventType", "asc")]
    [InlineData("eventType", "desc")]
    [InlineData("channel", "asc")]
    [InlineData("channel", "desc")]
    [InlineData("attempts", "asc")]
    [InlineData("attempts", "desc")]
    public async Task TenantHistory_SortsEachFieldBothWays(string field, string dir)
    {
        var s = await SeedAsync();
        await using var _ = s.World;
        string[][] asc = field switch
        {
            "createdAt" => [["d1"], ["d2"], ["d3"], ["d4"], ["d5"]],
            "status" => [["d1", "d4"], ["d3"], ["d2", "d5"]],
            "kind" => [["d1", "d3"], ["d5"], ["d2", "d4"]],
            "eventType" => [["d1", "d4"], ["d2", "d5"], ["d3"]],
            "channel" => [["d2"], ["d4"], ["d5"], ["d3"], ["d1"]],
            _ => [["d3"], ["d2", "d5"], ["d1"], ["d4"]]
        };
        var expected = dir == "asc" ? asc : [.. asc.Reverse()];

        AssertGroups(Ids(await GetAsync(s.Tenant, s.TenantPath, $"sort={field}&dir={dir}")), Groups(s.Ids, expected));
    }

    [Fact]
    public async Task TenantHistory_DefaultsToNewestFirst_AndNeverShowsAnotherTenantsDeliveries()
    {
        var s = await SeedAsync();
        await using var _ = s.World;

        var page = await GetAsync(s.Tenant, s.TenantPath, "");

        Assert.Equal(5, page.GetProperty("totalCount").GetInt64());
        Assert.Equal(Groups(s.Ids, ["d5"], ["d4"], ["d3"], ["d2"], ["d1"]).Select(group => group[0]), Ids(page));
    }

    [Fact]
    public async Task TenantHistory_SearchMatchesChannelTargetEventSubjectAndErrorCode_AndCombinesWithTheFilters()
    {
        var s = await SeedAsync();
        await using var _ = s.World;
        async Task<Guid[]> Search(string query) => Ids(await GetAsync(s.Tenant, s.TenantPath, $"{query}&sort=createdAt&dir=asc"));

        Assert.Equal(Groups(s.Ids, ["d2"]).Single(), await Search("search=ALPHA"));                                     // channel, case-insensitive
        Assert.Equal(Groups(s.Ids, ["d3"]).Single(), await Search("search=mid%40example"));                              // target
        Assert.Equal(Groups(s.Ids, ["d3"]).Single(), await Search("search=quota_critical"));                            // event type
        Assert.Equal(Groups(s.Ids, ["d1"]).Single(), await Search($"search=d1%20{s.Tag}"));                              // subject
        Assert.Equal(Groups(s.Ids, ["d4"]).Single(), await Search("search=HTTP_500"));                                   // error code
        Assert.Equal(Groups(s.Ids, ["d1", "d4"]).SelectMany(group => group).ToArray(), await Search($"search={s.Tag}&status=failed"));
        Assert.Equal(Groups(s.Ids, ["d4"]).Single(), await Search($"search={s.Tag}&status=failed&kind=webhook"));
        Assert.Equal(Groups(s.Ids, ["d2"]).Single(), await Search($"search={s.Tag}&eventType=backup_failed&kind=webhook"));
        Assert.Empty(await Search($"search=omega"));                                                                      // another tenant's channel
    }

    [Fact]
    public async Task TenantHistory_PagingIsStableWhenTheSortKeyTies()
    {
        var s = await SeedAsync();
        await using var _ = s.World;

        var seen = new List<Guid>();
        foreach (var number in new[] { 1, 2, 3 })
        {
            var page = await GetAsync(s.Tenant, s.TenantPath, $"sort=status&pageSize=2&page={number}");
            Assert.Equal(5, page.GetProperty("totalCount").GetInt64());
            seen.AddRange(Ids(page));
        }

        Assert.Equal(5, seen.Distinct().Count());
        Assert.Equal(Ids(await GetAsync(s.Tenant, s.TenantPath, "sort=status&pageSize=100")), seen);
    }

    [Theory]
    [InlineData("sort=nope", "sort")]
    [InlineData("sort=status&dir=up", "dir")]
    [InlineData("pageSize=101", "pageSize")]
    public async Task TenantHistory_InvalidListParametersAnswer400NamingTheField(string query, string field)
    {
        var s = await SeedAsync();
        await using var _ = s.World;

        var body = await GetAsync(s.Tenant, s.TenantPath, query, HttpStatusCode.BadRequest);

        Assert.Equal("invalid_request", body.GetProperty("error").GetString());
        Assert.Equal(field, body.GetProperty("field").GetString());
    }

    [Theory]
    [InlineData("createdAt", "asc")]
    [InlineData("createdAt", "desc")]
    [InlineData("status", "asc")]
    [InlineData("kind", "asc")]
    [InlineData("kind", "desc")]
    [InlineData("eventType", "asc")]
    [InlineData("eventType", "desc")]
    [InlineData("channel", "asc")]
    [InlineData("channel", "desc")]
    [InlineData("attempts", "asc")]
    [InlineData("attempts", "desc")]
    public async Task FailedDeliveries_SortEachFieldBothWaysAndListOnlyFailedOnes(string field, string dir)
    {
        var s = await SeedAsync();
        await using var _ = s.World;
        string[][] asc = field switch
        {
            "createdAt" => [["d1"], ["d4"], ["f1"]],
            "status" => [["d1", "d4", "f1"]],
            "kind" => [["d1"], ["f1"], ["d4"]],
            "eventType" => [["d1", "d4"], ["f1"]],
            "channel" => [["d4"], ["f1"], ["d1"]],
            _ => [["f1"], ["d1"], ["d4"]]
        };
        var expected = dir == "asc" ? asc : [.. asc.Reverse()];

        var page = await GetAsync(s.Platform, FailedPath, $"search={s.Tag}&sort={field}&dir={dir}");

        AssertGroups(Ids(page), Groups(s.Ids, expected));
        Assert.Equal(3, page.GetProperty("totalCount").GetInt64());
    }

    [Fact]
    public async Task FailedDeliveries_DefaultToNewestFirst_AndSearchCombinesWithPaging()
    {
        var s = await SeedAsync();
        await using var _ = s.World;

        Assert.Equal(Groups(s.Ids, ["f1"], ["d4"], ["d1"]).Select(group => group[0]), Ids(await GetAsync(s.Platform, FailedPath, $"search={s.Tag}")));
        var second = await GetAsync(s.Platform, FailedPath, $"search={s.Tag}&pageSize=2&page=2");
        Assert.Equal([s.Ids["d1"]], Ids(second));
        Assert.Equal(3, second.GetProperty("totalCount").GetInt64());
        Assert.Equal([s.Ids["f1"]], Ids(await GetAsync(s.Platform, FailedPath, $"search=F1%20{s.Tag}")));
        Assert.Equal([s.Ids["d4"]], Ids(await GetAsync(s.Platform, FailedPath, $"search=d4%20{s.Tag}")));
    }

    [Fact]
    public async Task FailedDeliveries_PagingIsStableWhenTheSortKeyTies()
    {
        var s = await SeedAsync();
        await using var _ = s.World;

        var seen = new List<Guid>();
        foreach (var number in new[] { 1, 2, 3 })
            seen.AddRange(Ids(await GetAsync(s.Platform, FailedPath, $"search={s.Tag}&sort=status&pageSize=1&page={number}")));

        Assert.Equal(3, seen.Distinct().Count());
        Assert.Equal(Ids(await GetAsync(s.Platform, FailedPath, $"search={s.Tag}&sort=status&pageSize=100")), seen);
    }
}
