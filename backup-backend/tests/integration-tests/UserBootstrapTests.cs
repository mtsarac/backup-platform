using System.Net;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;
using System.Net.Http.Json;
using BackupPlatform.Identity;
using BackupPlatform.Infrastructure.Identity;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.IntegrationTests;

public sealed class UserBootstrapTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task FirstUserCanAuthenticateAsPlatformAdminAndBootstrapCannotOverwriteCredential()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(fixture.ConnectionString).Options;
        var identifier = $"initial-{Guid.NewGuid():N}";
        var password = $"Local-only-{Guid.NewGuid():N}!";
        foreach (var (candidateLogin, email, candidate) in new[]
        {
            ("bad login!", "admin@example.com", password),
            (identifier, "invalid", password),
            (identifier, "admin@example.com", "short")
        })
            await Assert.ThrowsAnyAsync<ArgumentException>(() => UserBootstrapper.CreateInitialUserAsync(
                options, candidateLogin, "Initial local user", email, candidate, CancellationToken.None));
        await using (var empty = fixture.CreatePlatformContext())
            Assert.Empty(await empty.Users.ToListAsync());

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<UserId?> Attempt(string login)
        {
            await start.Task;
            try
            {
                return await UserBootstrapper.CreateInitialUserAsync(options, login, "Initial local user",
                    "admin@example.com", password, CancellationToken.None);
            }
            catch (InvalidOperationException) { return null; }
        }
        var attempts = new[] { Attempt(identifier), Attempt(identifier + "-other") };
        start.SetResult();
        var results = await Task.WhenAll(attempts);
        var userId = Assert.Single(results, id => id is not null)!;
        if (results[1] is not null) identifier += "-other";
        await Assert.ThrowsAsync<InvalidOperationException>(() => UserBootstrapper.CreateInitialUserAsync(
            options, "replacement", "Replacement user", "replacement@example.com", password, CancellationToken.None));
        await using (var database = fixture.CreatePlatformContext())
        {
            var user = await database.Users.SingleAsync();
            Assert.Equal(userId, user.Id);
            Assert.Equal("Initial local user", user.DisplayName);
            Assert.Empty(await database.TenantMemberships.ToListAsync());
            var audit = await database.AuditEvents.SingleAsync();
            Assert.Equal(UserId.System, audit.ActorUserId);
            Assert.Equal(AuditActorKind.System, audit.ActorKind);
            Assert.Equal("user.bootstrap", audit.Action);
            Assert.DoesNotContain(password, audit.DetailsJson);
            var credential = await database.PasswordCredentials.SingleAsync();
            Assert.Equal(userId, credential.UserId);
            Assert.Equal("admin@example.com", credential.Email);
            Assert.Equal(PasswordVerificationResult.Success, new PasswordHashService().Verify(userId, credential.PasswordHash, password));
            Assert.Equal(UserRole.PlatformAdmin, user.Role);
        }
        await using var factory = new AuthApiFactory(fixture.ConnectionString);
        using var browser = factory.CreateBrowser();
        using var tokenResponse = await browser.GetAsync("/api/auth/antiforgery");
        Assert.Equal(HttpStatusCode.OK, tokenResponse.StatusCode);
        var token = await tokenResponse.Content.ReadFromJsonAsync<AntiforgeryToken>();
        Assert.NotNull(token);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { loginIdentifier = identifier, password })
        };
        request.Headers.Add("X-CSRF-TOKEN", token.RequestToken);
        using var login = await browser.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        using var platform = await browser.GetAsync("/api/test/platform");
        Assert.Equal(HttpStatusCode.OK, platform.StatusCode);
    }

    private sealed record AntiforgeryToken(string RequestToken);
}
