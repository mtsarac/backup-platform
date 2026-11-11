using System.Net;
using System.Text.Json;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Notifications;
using BackupPlatform.Infrastructure.Notifications;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.IntegrationTests.Notifications;

public sealed class UserNotificationIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlFixture fixture = new();
    public Task InitializeAsync() => fixture.InitializeAsync();
    public Task DisposeAsync() => fixture.DisposeAsync();

    private static NotificationEvent Event(TenantId? tenant, string key) => new(Guid.NewGuid(), tenant, "security_event", NotificationPhase.Raised,
        NotificationSeverity.Critical, new NotificationSubject("account", null, "Security event"), null, key, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    [Fact]
    public async Task Rollback_removes_business_change_event_and_recipients()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var setup = await world.CreateTenantAsync(false);
        var original = setup.Tenant.Name;
        await using (var context = fixture.CreatePlatformContext())
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            var tenant = await context.Tenants.SingleAsync(row => row.Id == setup.Tenant.Id);
            tenant.Rename("Rolled back");
            await UserNotificationWriter.RecordAsync(context, Event(tenant.Id, "rollback"), [], false, true, null, default);
            await context.SaveChangesAsync();
            await transaction.RollbackAsync();
        }
        await using var check = fixture.CreatePlatformContext();
        Assert.Equal(original, (await check.Tenants.SingleAsync(row => row.Id == setup.Tenant.Id)).Name);
        Assert.False(await check.NotificationEvents.AnyAsync(row => row.DedupKey == "rollback"));
        Assert.Empty(await check.UserNotifications.ToListAsync());
    }

    [Fact]
    public async Task Duplicate_events_are_unique_and_inbox_is_isolated_by_user_and_tenant()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var a = await world.CreateTenantAsync(false);
        var b = await world.CreateTenantAsync(false);
        var platformAccount = await world.SeedUserAsync(platformAdmin: true);
        var platform = await world.SignInAsync(platformAccount);
        for (var i = 0; i < 2; i++)
        {
            await using var context = fixture.CreatePlatformContext();
            await using var transaction = await context.Database.BeginTransactionAsync();
            await UserNotificationWriter.RecordAsync(context, Event(a.Tenant.Id, "once"), [a.Account.UserId], true, true, null, default);
            await context.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        await using (var check = fixture.CreatePlatformContext())
        {
            Assert.Single(await check.NotificationEvents.Where(row => row.DedupKey == "once").ToListAsync());
            Assert.Equal(2, await check.UserNotifications.CountAsync());
        }
        using var aResponse = await a.Admin.GetAsync("/api/notifications/in-app");
        using var bResponse = await b.Admin.GetAsync("/api/notifications/in-app");
        using var platformResponse = await platform.GetAsync("/api/notifications/in-app");
        Assert.Equal(HttpStatusCode.Unauthorized, (await world.Anonymous.GetAsync("/api/notifications/in-app")).StatusCode);
        var aItems = JsonDocument.Parse(await aResponse.Content.ReadAsStringAsync()).RootElement;
        var bItems = JsonDocument.Parse(await bResponse.Content.ReadAsStringAsync()).RootElement;
        var platformItems = JsonDocument.Parse(await platformResponse.Content.ReadAsStringAsync()).RootElement;
        var id = Assert.Single(aItems.EnumerateArray()).GetProperty("id").GetGuid();
        Assert.Empty(bItems.EnumerateArray());
        Assert.Single(platformItems.EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await b.Admin.SendAsync(HttpMethod.Post, $"/api/notifications/in-app/{id}/read")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await a.Admin.SendAsync(HttpMethod.Post, $"/api/notifications/in-app/{id}/read")).StatusCode);
        using var readResponse = await a.Admin.GetAsync("/api/notifications/in-app");
        var read = JsonDocument.Parse(await readResponse.Content.ReadAsStringAsync()).RootElement;
        Assert.NotEqual(JsonValueKind.Null, Assert.Single(read.EnumerateArray()).GetProperty("readAt").ValueKind);
        await using (var context = fixture.CreatePlatformContext())
            await context.TenantMemberships.Where(row => row.UserId == a.Account.UserId && row.TenantId == a.Tenant.Id).ExecuteDeleteAsync();
        using var afterRemoval = await a.Admin.GetAsync("/api/notifications/in-app");
        Assert.Empty(JsonDocument.Parse(await afterRemoval.Content.ReadAsStringAsync()).RootElement.EnumerateArray());
    }

    [Fact]
    public async Task Failed_email_does_not_remove_in_app_notification()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var setup = await world.CreateTenantAsync(false);
        var notificationEvent = Event(setup.Tenant.Id, "email-failure");
        var deliveryId = Guid.NewGuid();
        await using var context = fixture.CreatePlatformContext();
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            await UserNotificationWriter.RecordAsync(context, notificationEvent, [setup.Account.UserId], false, false, null, default);
            context.NotificationDeliveries.Add(new NotificationDelivery(deliveryId, setup.Tenant.Id, notificationEvent.Id, Guid.NewGuid(),
                NotificationChannelKind.Email, "email", "masked", DateTimeOffset.UtcNow));
            await context.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        await context.NotificationDeliveries.Where(row => row.Id == deliveryId).ExecuteUpdateAsync(set => set
            .SetProperty(row => row.Status, NotificationDeliveryStatus.Failed)
            .SetProperty(row => row.NextAttemptAt, (DateTimeOffset?)null)
            .SetProperty(row => row.CompletedAt, DateTimeOffset.UtcNow));
        Assert.Single(await context.UserNotifications.Where(row => row.EventId == notificationEvent.Id).ToListAsync());
    }

    [Fact]
    public async Task Tenantless_event_can_target_a_single_user()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var account = await world.SeedUserAsync(platformAdmin: true);
        await using var context = fixture.CreatePlatformContext();
        await using var transaction = await context.Database.BeginTransactionAsync();
        await UserNotificationWriter.RecordAsync(context, Event(null, "global"), [account.UserId], false, false, null, default);
        await context.SaveChangesAsync();
        await transaction.CommitAsync();
        Assert.Single(await context.UserNotifications.ToListAsync());
    }
}
