using System.Net;
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
        var userId = await UserBootstrapper.CreateInitialUserAsync(options, identifier, "Initial local user", password, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => UserBootstrapper.CreateInitialUserAsync(
            options, identifier, "Replacement user", $"Replacement-{Guid.NewGuid():N}!", CancellationToken.None));
        await using (var database = fixture.CreatePlatformContext())
        {
            var user = await database.Users.SingleAsync();
            Assert.Equal(userId, user.Id);
            Assert.Equal("Initial local user", user.DisplayName);
            var credential = await database.PasswordCredentials.SingleAsync();
            Assert.Equal(userId, credential.UserId);
            Assert.Equal(PasswordVerificationResult.Success, new PasswordHashService().Verify(userId, credential.PasswordHash, password));
            Assert.Equal(userId, (await database.PlatformAdmins.SingleAsync()).UserId);
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
