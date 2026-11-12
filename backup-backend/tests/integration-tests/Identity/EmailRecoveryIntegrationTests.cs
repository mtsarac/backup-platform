using System.Net;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BackupPlatform.IntegrationTests;

public sealed class EmailRecoveryIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private sealed class RecordingMailer : IAccountMailer
    {
        public bool Available { get; set; } = true;
        public List<AccountMail> Sent { get; } = [];
        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(Available);
        public void Enqueue(AccountMail mail) { lock (Sent) Sent.Add(mail); }
        public Task<bool?> SendSecurityNoticeAsync(string to, string eventType, DateTimeOffset occurredAt, CancellationToken cancellationToken) => Task.FromResult<bool?>(false);
    }

    private async Task<(AgentWorld World, RecordingMailer Mailer)> CreateWorldAsync(bool available = true)
    {
        var mailer = new RecordingMailer { Available = available };
        var world = await AgentWorld.CreateAsync(fixture, configureServices: services =>
        {
            services.RemoveAll<IAccountMailer>();
            services.AddSingleton<IAccountMailer>(mailer);
        });
        return (world, mailer);
    }

    private async Task<(Account Account, string Email)> SeedAccountAsync(AgentWorld world, bool verified)
    {
        var email = $"{Guid.NewGuid():N}@example.com";
        return (await world.SeedUserAsync(email: email, emailVerified: verified), email);
    }

    private static BrowserSession Anonymous(AgentWorld world) => new(world.Factory.CreateBrowser());

    private static async Task<(HttpStatusCode Status, string Body)> PostAsync(BrowserSession session, string path, object body)
    {
        using var response = await session.SendAsync(HttpMethod.Post, path, body);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ResetRequest_VerifiedUnverifiedAndUnknownAreIndistinguishable_AndOnlyVerifiedReceivesMail()
    {
        var (world, mailer) = await CreateWorldAsync();
        await using var _ = world;
        var verified = await SeedAccountAsync(world, true);
        var unverified = await SeedAccountAsync(world, false);
        var anonymous = Anonymous(world);

        var replies = new List<(HttpStatusCode, string)>();
        foreach (var identifier in new[] { verified.Email, unverified.Email, $"nobody-{Guid.NewGuid():N}@example.com" })
            replies.Add(await PostAsync(anonymous, "/api/auth/password-reset/request", new { loginIdentifier = identifier }));

        Assert.All(replies, reply => Assert.Equal(replies[0], reply));
        Assert.Equal(HttpStatusCode.Accepted, replies[0].Item1);
        var mail = Assert.Single(mailer.Sent);
        Assert.Equal(AccountMailKind.PasswordReset, mail.Kind);
        Assert.Equal(verified.Email, mail.To);
    }

    [Fact]
    public async Task ResetToken_IsSingleUse_EnforcesPolicy_EndsSessions_AndExpires()
    {
        var (world, mailer) = await CreateWorldAsync();
        await using var _ = world;
        var (account, email) = await SeedAccountAsync(world, true);
        var existing = await world.SignInAsync(account);
        var anonymous = Anonymous(world);

        await PostAsync(anonymous, "/api/auth/password-reset/request", new { loginIdentifier = email });
        var token = Assert.Single(mailer.Sent).Token;

        var weak = await PostAsync(anonymous, "/api/auth/password-reset/complete", new { token, newPassword = "short" });
        Assert.Equal(HttpStatusCode.BadRequest, weak.Status);
        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(anonymous, "/api/auth/password-reset/complete", new { token, newPassword = "a-new-long-passphrase-42" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(anonymous, "/api/auth/password-reset/complete", new { token, newPassword = "another-long-passphrase-43" })).Status);

        using var stale = await existing.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
        await world.SignInAsync(account with { Password = "a-new-long-passphrase-42" });

        await using var context = fixture.CreatePlatformContext();
        var audit = await context.AuditEvents.AsNoTracking().Where(row => row.ActorUserId == account.UserId).Select(row => new { row.Action, row.DetailsJson }).ToListAsync();
        Assert.Contains(audit, row => row.Action == AuditActions.PasswordResetRequested);
        Assert.Contains(audit, row => row.Action == AuditActions.PasswordResetCompleted);
        Assert.All(audit, row => Assert.DoesNotContain(token, row.DetailsJson));

        mailer.Sent.Clear();
        await PostAsync(anonymous, "/api/auth/password-reset/request", new { loginIdentifier = email });
        var expiring = Assert.Single(mailer.Sent).Token;
        world.Clock.Advance(AccountEmailService.ResetLifetime + TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(anonymous, "/api/auth/password-reset/complete", new { token = expiring, newPassword = "yet-another-long-passphrase-44" })).Status);
    }

    [Fact]
    public async Task ResetToken_StopsWorkingWhenTheAddressChanges()
    {
        var (world, mailer) = await CreateWorldAsync();
        await using var _ = world;
        var (account, email) = await SeedAccountAsync(world, true);
        var anonymous = Anonymous(world);
        await PostAsync(anonymous, "/api/auth/password-reset/request", new { loginIdentifier = account.Identifier });
        var token = Assert.Single(mailer.Sent).Token;

        await using (var context = fixture.CreatePlatformContext())
        {
            var credential = await context.PasswordCredentials.SingleAsync(row => row.UserId == account.UserId);
            credential.UpdateIdentity(credential.LoginIdentifier, $"changed-{email}");
            await context.SaveChangesAsync();
            Assert.Null(credential.EmailVerifiedAt);
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(anonymous, "/api/auth/password-reset/complete", new { token, newPassword = "a-new-long-passphrase-42" })).Status);
    }

    [Fact]
    public async Task Verification_IsOptional_SingleUse_AndMarksTheAddress()
    {
        var (world, mailer) = await CreateWorldAsync();
        await using var _ = world;
        var (account, _) = await SeedAccountAsync(world, false);
        var session = await world.SignInAsync(account);

        using var status = await session.GetAsync("/api/auth/security");
        Assert.False(AgentWorld.ReadJson(status).GetProperty("emailVerified").GetBoolean());
        Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(session, "/api/auth/email-verification/request", new { })).Status);
        var mail = Assert.Single(mailer.Sent);
        Assert.Equal(AccountMailKind.Verification, mail.Kind);

        var anonymous = Anonymous(world);
        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(anonymous, "/api/auth/email-verification/confirm", new { token = mail.Token })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(anonymous, "/api/auth/email-verification/confirm", new { token = mail.Token })).Status);
        using var after = await session.GetAsync("/api/auth/security");
        Assert.True(AgentWorld.ReadJson(after).GetProperty("emailVerified").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(session, "/api/auth/email-verification/request", new { })).Status);
    }

    [Fact]
    public async Task WithoutSmtp_EntryPointsReportUnavailable_AndSignInStillWorks()
    {
        var (world, mailer) = await CreateWorldAsync(available: false);
        await using var _ = world;
        var (account, email) = await SeedAccountAsync(world, true);
        var anonymous = Anonymous(world);

        using var availability = await anonymous.GetAsync("/api/auth/email-recovery");
        Assert.False(AgentWorld.ReadJson(availability).GetProperty("available").GetBoolean());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await PostAsync(anonymous, "/api/auth/password-reset/request", new { loginIdentifier = email })).Status);
        var session = await world.SignInAsync(account);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await PostAsync(session, "/api/auth/email-verification/request", new { })).Status);
        Assert.Empty(mailer.Sent);
    }

    [Fact]
    public async Task PlatformWithoutConfiguredRelay_StartsAndReportsEmailRecoveryUnavailable()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        using var availability = await Anonymous(world).GetAsync("/api/auth/email-recovery");
        Assert.Equal(HttpStatusCode.OK, availability.StatusCode);
        Assert.False(AgentWorld.ReadJson(availability).GetProperty("available").GetBoolean());
    }

    [Fact]
    public async Task ResetRequests_AreRateLimitedPerAccount()
    {
        var (world, _) = await CreateWorldAsync();
        await using var _ = world;
        var anonymous = Anonymous(world);
        var identifier = $"nobody-{Guid.NewGuid():N}@example.com";
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < LoginThrottleLimit + 1; i++)
            statuses.Add((await PostAsync(anonymous, "/api/auth/password-reset/request", new { loginIdentifier = identifier })).Status);
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
        Assert.All(statuses.Take(LoginThrottleLimit), status => Assert.Equal(HttpStatusCode.Accepted, status));
    }

    private const int LoginThrottleLimit = 5;
}
