using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.IntegrationTests.Notifications;

/// <summary>A channel name's stored key must satisfy the database's own lower(); names with Turkish letters used to answer 500.</summary>
public sealed class NotificationChannelNameIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlFixture fixture = new();

    public Task InitializeAsync() => fixture.InitializeAsync();

    public Task DisposeAsync() => fixture.DisposeAsync();

    [Theory]
    [InlineData("İSTANBUL", "istanbul")]
    [InlineData("İstanbul kanalı", "istanbul kanalı")]
    [InlineData("ŞİFRELİ Iığ", "şifreli iığ")]
    [InlineData("QA kanalı", "qa kanalı")]
    public async Task A_webhook_channel_is_created_whichever_letters_its_name_has(string name, string key)
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var setup = await world.CreateTenantAsync();

        using var response = await setup.Admin.SendAsync(HttpMethod.Post, $"/api/tenants/{setup.Tenant.Id.Value}/notifications/channels",
            new { kind = "webhook", name, target = "https://qa-nohost.invalid/yol" });

        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{(int)response.StatusCode}: {text}");
        Assert.Equal(name, JsonDocument.Parse(text).RootElement.GetProperty("name").GetString());
        await using var context = fixture.CreatePlatformContext();
        Assert.Equal(key, await context.NotificationChannels.AsNoTracking().Select(channel => channel.NormalizedName).SingleAsync());
    }

    [Fact]
    public async Task Renaming_a_channel_to_a_Turkish_name_works_and_names_differing_by_case_are_one_name()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var setup = await world.CreateTenantAsync();
        var path = $"/api/tenants/{setup.Tenant.Id.Value}/notifications/channels";

        using var first = await setup.Admin.SendAsync(HttpMethod.Post, path, new { kind = "webhook", name = "Ops", target = "https://hooks.example.com/a" });
        var id = AgentWorld.ReadJson(first).GetProperty("id").GetGuid();
        using var renamed = await setup.Admin.SendAsync(HttpMethod.Put, $"{path}/{id}", new { name = "İstanbul" });
        using var clash = await setup.Admin.SendAsync(HttpMethod.Post, path, new { kind = "webhook", name = "istanbul", target = "https://hooks.example.com/b" });

        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
    }
}
