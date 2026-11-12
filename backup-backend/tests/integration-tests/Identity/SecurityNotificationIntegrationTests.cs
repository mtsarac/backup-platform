using System.Net;
using BackupPlatform.Api.Notifications;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Notifications;
using BackupPlatform.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BackupPlatform.IntegrationTests;

public sealed class SecurityNotificationIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private sealed class RecordingMailer : IAccountMailer
    {
        public bool Available { get; set; } = true;
        public bool? Result { get; set; } = true;
        public List<(string To, string Type)> Notices { get; } = [];
        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(Available);
        public void Enqueue(AccountMail mail) { }
        public Task<bool?> SendSecurityNoticeAsync(string to, string eventType, DateTimeOffset occurredAt, CancellationToken cancellationToken)
        {
            Notices.Add((to, eventType));
            return Task.FromResult(Result);
        }
    }

    private async Task<(AgentWorld World, RecordingMailer Mailer)> CreateWorldAsync()
    {
        var mailer = new RecordingMailer();
        var world = await AgentWorld.CreateAsync(fixture, configureServices: services =>
        {
            services.RemoveAll<IAccountMailer>();
            services.AddSingleton<IAccountMailer>(mailer);
        });
        return (world, mailer);
    }

    private async Task<List<string>> TypesForAsync(UserId user)
    {
        await using var context = fixture.CreatePlatformContext();
        return await (from item in context.UserNotifications.AsNoTracking()
                      join notificationEvent in context.NotificationEvents.AsNoTracking() on item.EventId equals notificationEvent.Id
                      where item.UserId == user
                      orderby notificationEvent.Type
                      select notificationEvent.Type).ToListAsync();
    }

    private static async Task<HttpStatusCode> LoginAsync(AgentWorld world, Account account, string password, string userAgent)
    {
        var session = new BrowserSession(world.Factory.CreateBrowser());
        using var response = await session.SendAsync(HttpMethod.Post, "/api/auth/login",
            new { loginIdentifier = account.Identifier, password }, headers: [new("User-Agent", userAgent)]);
        return response.StatusCode;
    }

    private static async Task WithStoreAsync(AgentWorld world, Func<IIdentityStore, Task> action)
    {
        await using var scope = world.Factory.Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<IIdentityStore>());
    }

    [Fact]
    public async Task FactorAndRecoveryEvents_EachNotifyTheAffectedUserOnce()
    {
        var (world, _) = await CreateWorldAsync();
        await using var _1 = world;
        var account = await world.SeedUserAsync();
        var now = world.Clock.GetUtcNow();
        var passkeyId = Guid.NewGuid();
        await WithStoreAsync(world, async store =>
        {
            Assert.Equal(TotpEnrollmentOutcome.EnrolledWithRecoveryCodes, await store.SaveTotpCredentialAsync(new TotpCredential(account.UserId, "encrypted", now), ["hash-a"], now, default));
            Assert.False(await store.AddPasskeyAsync(new PasskeyCredential(passkeyId, account.UserId, "Key", [1, 2, 3], [4, 5, 6], 0, null, now), [], now, default));
            await store.SaveRecoveryCodesAsync(account.UserId, ["hash-b"], now, default);
            Assert.True(await store.TryRedeemRecoveryCodeAsync(account.UserId, "hash-b", now, default));
            Assert.False(await store.TryRedeemRecoveryCodeAsync(account.UserId, "hash-b", now, default));
            Assert.True(await store.RemovePasskeyAsync(passkeyId, account.UserId, now, default));
            Assert.True(await store.RemoveTotpCredentialAsync(account.UserId, now, default));
            Assert.False(await store.RemoveTotpCredentialAsync(account.UserId, now, default));
            Assert.True(await store.ChangePasswordAsync(account.UserId, "new-hash", now, default));
        });

        Assert.Equal(
            [
                NotificationEventTypes.SecurityFactorAdded, NotificationEventTypes.SecurityFactorAdded,
                NotificationEventTypes.SecurityFactorRemoved, NotificationEventTypes.SecurityFactorRemoved,
                NotificationEventTypes.SecurityPasswordChanged,
                NotificationEventTypes.SecurityRecoveryRegenerated, NotificationEventTypes.SecurityRecoveryUsed
            ],
            await TypesForAsync(account.UserId));
    }

    [Fact]
    public async Task AdministratorActions_NotifyTheTargetUserNotTheAdministrator()
    {
        var (world, _) = await CreateWorldAsync();
        await using var _1 = world;
        var adminAccount = await world.SeedUserAsync(platformAdmin: true);
        var admin = await world.SignInAsync(adminAccount);
        var tenant = await world.AddTenantAsync();
        var target = await world.SeedUserAsync(email: $"{Guid.NewGuid():N}@example.com");
        await world.GrantAsync(tenant, target);
        var user = $"/api/platform/users/{target.UserId.Value}";

        async Task<HttpStatusCode> CallAsync(HttpMethod method, string path, object? body = null)
        {
            using var response = await admin.SendAsync(method, path, body);
            return response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.OK, await CallAsync(HttpMethod.Post, $"{user}/temporary-password", new { temporaryPassword = "a-temporary-passphrase-77" }));
        Assert.Equal(HttpStatusCode.OK, await CallAsync(HttpMethod.Post, $"{user}/disable", new { reason = "review" }));
        Assert.Equal(HttpStatusCode.OK, await CallAsync(HttpMethod.Post, $"{user}/enable"));
        var update = new { username = target.Identifier, email = $"new-{Guid.NewGuid():N}@example.com", displayName = "Agent test user", role = "UserAdmin" };
        Assert.Equal(HttpStatusCode.OK, await CallAsync(HttpMethod.Put, user, update));
        // The same address again changes nothing and notifies nobody.
        Assert.Equal(HttpStatusCode.OK, await CallAsync(HttpMethod.Put, user, update));
        Assert.Equal(HttpStatusCode.NoContent, await CallAsync(HttpMethod.Delete, $"/api/platform/tenants/{tenant.Id.Value}/memberships/{target.UserId.Value}"));
        Assert.Equal(HttpStatusCode.Created, await CallAsync(HttpMethod.Post, $"/api/platform/tenants/{tenant.Id.Value}/memberships", new { userId = target.UserId.Value }));

        Assert.Equal(
            [
                NotificationEventTypes.SecurityAccountDisabled, NotificationEventTypes.SecurityAccountDisabled,
                NotificationEventTypes.SecurityAccountEnabled, NotificationEventTypes.SecurityAccountEnabled,
                NotificationEventTypes.SecurityEmailChanged,
                NotificationEventTypes.SecurityTemporaryPassword
            ],
            await TypesForAsync(target.UserId));
        Assert.DoesNotContain(await TypesForAsync(adminAccount.UserId), type => NotificationEventTypes.IsSecurity(type) && type != NotificationEventTypes.SecuritySuspiciousLogin);
    }

    [Fact]
    public async Task PasswordReset_NotifiesTheUser()
    {
        var (world, _) = await CreateWorldAsync();
        await using var _1 = world;
        var account = await world.SeedUserAsync(email: $"{Guid.NewGuid():N}@example.com", emailVerified: true);
        var now = world.Clock.GetUtcNow();
        await WithStoreAsync(world, async store =>
        {
            await store.IssueEmailTokenAsync(new AccountEmailToken(Guid.NewGuid(), account.UserId, AccountEmailPurpose.PasswordReset, new string('a', 64),
                (await store.FindCredentialByUserIdAsync(account.UserId, default))!.NormalizedEmail!, now, now.AddMinutes(30)), default);
            Assert.True(await store.CompletePasswordResetAsync(new string('a', 64), "new-hash", now, default));
        });
        Assert.Equal([NotificationEventTypes.SecurityPasswordReset], await TypesForAsync(account.UserId));
    }

    [Fact]
    public async Task SuspiciousSignIn_NewDeviceNotifiesOnce_KnownDeviceDoesNot()
    {
        var (world, _) = await CreateWorldAsync();
        await using var _1 = world;
        var account = await world.SeedUserAsync();

        Assert.Equal(HttpStatusCode.NoContent, await LoginAsync(world, account, account.Password, "Browser/1"));
        Assert.Equal(HttpStatusCode.NoContent, await LoginAsync(world, account, account.Password, "Browser/1"));
        Assert.Empty(await TypesForAsync(account.UserId));

        Assert.Equal(HttpStatusCode.NoContent, await LoginAsync(world, account, account.Password, "Browser/2"));
        Assert.Equal(HttpStatusCode.NoContent, await LoginAsync(world, account, account.Password, "Browser/2"));
        Assert.Equal(HttpStatusCode.NoContent, await LoginAsync(world, account, account.Password, "Browser/1"));
        Assert.Equal([NotificationEventTypes.SecuritySuspiciousLogin], await TypesForAsync(account.UserId));
    }

    [Fact]
    public async Task ThrottledFailedSignIns_ProduceExactlyOneNotification()
    {
        var (world, _) = await CreateWorldAsync();
        await using var _1 = world;
        var account = await world.SeedUserAsync();
        for (var attempt = 0; attempt < 9; attempt++)
            Assert.Equal(HttpStatusCode.Unauthorized, await LoginAsync(world, account, "wrong-password-entirely", "Browser/1"));
        Assert.Equal([NotificationEventTypes.SecuritySuspiciousLogin], await TypesForAsync(account.UserId));
    }

    [Fact]
    public async Task Email_GoesOnlyToVerifiedAddressesWhenSmtpIsAvailable_AndFailureKeepsTheNotification()
    {
        var (world, mailer) = await CreateWorldAsync();
        await using var _1 = world;
        var verified = await world.SeedUserAsync(email: $"{Guid.NewGuid():N}@example.com", emailVerified: true);
        var unverified = await world.SeedUserAsync(email: $"{Guid.NewGuid():N}@example.com");
        var worker = world.Factory.Services.GetRequiredService<SecurityNotificationMailWorker>();
        var now = world.Clock.GetUtcNow();

        async Task ChangeAsync(Account account) => await WithStoreAsync(world, store => store.ChangePasswordAsync(account.UserId, "hash-" + Guid.NewGuid().ToString("N"), now, default));
        async Task DrainAsync() { while (await worker.ProcessAsync(default) > 0 && await PendingAsync() > 0 && mailer.Result is not null) { } }
        async Task<int> PendingAsync()
        {
            await using var context = fixture.CreatePlatformContext();
            return await context.UserNotifications.CountAsync(row => (row.UserId == verified.UserId || row.UserId == unverified.UserId) && row.EmailHandledAt == null);
        }

        // Clear whatever other tests of this fixture left pending, without sending.
        mailer.Available = false;
        while (await worker.ProcessAsync(default) > 0) { }
        mailer.Notices.Clear();

        await ChangeAsync(verified);
        await DrainAsync();
        Assert.Empty(mailer.Notices);
        Assert.Equal(0, await PendingAsync());

        mailer.Available = true;
        await ChangeAsync(verified);
        await ChangeAsync(unverified);
        await DrainAsync();
        var notice = Assert.Single(mailer.Notices);
        Assert.Equal(NotificationEventTypes.SecurityPasswordChanged, notice.Type);

        mailer.Result = false;
        await ChangeAsync(verified);
        while (await worker.ProcessAsync(default) > 0) { }
        Assert.Equal(3, (await TypesForAsync(verified.UserId)).Count);
        Assert.Single(await TypesForAsync(unverified.UserId));
    }

    [Fact]
    public async Task SecurityCategory_CannotBeSelectedOrDisabledInATenantPolicy()
    {
        var (world, _) = await CreateWorldAsync();
        await using var _1 = world;
        var setup = await world.CreateTenantAsync();
        using var response = await setup.Admin.SendAsync(HttpMethod.Put, $"/api/tenants/{setup.Tenant.Id.Value}/notifications/policy",
            new { enabled = false, eventTypes = new[] { NotificationEventTypes.SecurityPasswordChanged }, minimumSeverity = "critical" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // A disabled tenant policy does not touch the category either.
        var now = world.Clock.GetUtcNow();
        await WithStoreAsync(world, store => store.ChangePasswordAsync(setup.Account.UserId, "another-hash", now, default));
        Assert.Contains(NotificationEventTypes.SecurityPasswordChanged, await TypesForAsync(setup.Account.UserId));
    }
}
