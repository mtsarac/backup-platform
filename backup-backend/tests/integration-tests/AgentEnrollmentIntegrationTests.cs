using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using BackupPlatform.Agents;
using BackupPlatform.Agents.Enrollment;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Contracts.Protocol;
using BackupPlatform.Domain.Agents;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Infrastructure.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BackupPlatform.IntegrationTests;

public sealed class AgentEnrollmentIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task TokenLifecycleCreateListRevokeNeverExposesPlaintextAfterCreation()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var setup = await world.CreateTenantAsync();
        var (tokenId, token) = await world.CreateTokenAsync(setup);
        Assert.True(AgentSecretReference.TryParse(token, out var parsedId, out var secret));
        Assert.Equal(tokenId, parsedId);

        using var list = await setup.Admin.GetAsync($"/api/tenants/{setup.Tenant.Id.Value}/agent-enrollment-tokens");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var listText = await list.Content.ReadAsStringAsync();
        Assert.DoesNotContain(secret, listText, StringComparison.Ordinal);
        Assert.DoesNotContain("token\"", listText, StringComparison.OrdinalIgnoreCase);
        var entry = Assert.Single(System.Text.Json.JsonDocument.Parse(listText).RootElement.EnumerateArray());
        Assert.Equal(tokenId, entry.GetProperty("tokenId").GetGuid());
        Assert.Equal(setup.Account.UserId.Value, entry.GetProperty("createdBy").GetGuid());

        using var revoke = await setup.Admin.SendAsync(HttpMethod.Delete, $"/api/tenants/{setup.Tenant.Id.Value}/agent-enrollment-tokens/{tokenId}");
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        using var afterRevoke = await setup.Admin.GetAsync($"/api/tenants/{setup.Tenant.Id.Value}/agent-enrollment-tokens");
        Assert.Empty(AgentWorld.ReadJson(afterRevoke).EnumerateArray());
        using var again = await setup.Admin.SendAsync(HttpMethod.Delete, $"/api/tenants/{setup.Tenant.Id.Value}/agent-enrollment-tokens/{tokenId}");
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);

        using var enroll = await world.PostEnrollAsync(AgentWorld.EnrollmentRequest(token));
        Assert.Equal(HttpStatusCode.Unauthorized, enroll.StatusCode);
    }

    [Fact]
    public async Task NoPlaintextTokenOrCredentialSecretIsEverStoredInTheDatabase()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var setup = await world.CreateTenantAsync();
        var (_, token) = await world.CreateTokenAsync(setup);
        AgentSecretReference.TryParse(token, out _, out var tokenSecret);
        using var response = await world.PostEnrollAsync(AgentWorld.EnrollmentRequest(token));
        var credential = AgentWorld.ReadJson(response).GetProperty("credential");
        var credentialSecret = credential.GetProperty("secret").GetString()!;
        var pending = await world.BeginRotationAsync(credential.GetProperty("credentialId").GetGuid(), credentialSecret);

        var dump = await world.DumpAgentTablesAsync();
        foreach (var secret in new[] { tokenSecret, credentialSecret, pending.Secret, token })
        {
            Assert.DoesNotContain(secret, dump, StringComparison.Ordinal);
            Assert.DoesNotContain(Convert.ToHexString(Encoding.UTF8.GetBytes(secret)), dump, StringComparison.OrdinalIgnoreCase);
        }
        await using var context = fixture.CreatePlatformContext();
        var storedHashes = (await context.EnrollmentTokens.Select(row => row.SecretHash).ToListAsync())
            .Concat(await context.AgentCredentials.Select(row => row.SecretHash).ToListAsync());
        Assert.All(storedHashes, hash => Assert.Equal(32, hash.Length));
        Assert.DoesNotContain(storedHashes, hash => hash.SequenceEqual(SHA256.HashData(Encoding.UTF8.GetBytes(tokenSecret))));
    }

    [Fact]
    public async Task EnrollmentIsSingleUse()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var setup = await world.CreateTenantAsync();
        var (_, token) = await world.CreateTokenAsync(setup);
        using var first = await world.PostEnrollAsync(AgentWorld.EnrollmentRequest(token));
        using var second = await world.PostEnrollAsync(AgentWorld.EnrollmentRequest(token));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
        await using var context = fixture.CreatePlatformContext();
        Assert.Equal(1, await context.Agents.CountAsync(agent => agent.TenantId == setup.Tenant.Id));
        var row = await context.EnrollmentTokens.SingleAsync(candidate => candidate.TenantId == setup.Tenant.Id);
        Assert.NotNull(row.ConsumedAt);
        Assert.Equal(AgentWorld.ReadJson(first).GetProperty("agentId").GetGuid(), row.ConsumedByAgentId!.Value);
        using var list = await setup.Admin.GetAsync($"/api/tenants/{setup.Tenant.Id.Value}/agent-enrollment-tokens");
        Assert.Empty(AgentWorld.ReadJson(list).EnumerateArray());
    }

    [Fact]
    public async Task ConcurrentEnrollmentsWithOneTokenYieldExactlyOneAgent()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var setup = await world.CreateTenantAsync();
        var (_, token) = await world.CreateTokenAsync(setup);
        var attempts = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
        {
            using var response = await world.PostEnrollAsync(AgentWorld.EnrollmentRequest(token));
            return response.StatusCode;
        })));
        Assert.Equal(1, attempts.Count(status => status == HttpStatusCode.Created));
        // Losers are refused either because the token was already consumed (401) or by the per-token attempt limit (429).
        Assert.All(attempts.Where(status => status != HttpStatusCode.Created),
            status => Assert.True(status is HttpStatusCode.Unauthorized or HttpStatusCode.TooManyRequests, $"Unexpected status {status}."));
        await using var context = fixture.CreatePlatformContext();
        Assert.Equal(1, await context.Agents.CountAsync(agent => agent.TenantId == setup.Tenant.Id));
        Assert.Equal(1, await (from credential in context.AgentCredentials
                               join agent in context.Agents on credential.AgentId equals agent.Id
                               where agent.TenantId == setup.Tenant.Id
                               select credential).CountAsync());
    }

    [Fact]
    public async Task ExpiredTokensAreRejectedAndNoLongerListed()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var setup = await world.CreateTenantAsync();
        var (_, token) = await world.CreateTokenAsync(setup, lifetimeMinutes: 5);
        world.Clock.Advance(TimeSpan.FromMinutes(4));
        using var list = await setup.Admin.GetAsync($"/api/tenants/{setup.Tenant.Id.Value}/agent-enrollment-tokens");
        Assert.Single(AgentWorld.ReadJson(list).EnumerateArray());
        world.Clock.Advance(TimeSpan.FromMinutes(1));
        using var expiredList = await setup.Admin.GetAsync($"/api/tenants/{setup.Tenant.Id.Value}/agent-enrollment-tokens");
        Assert.Empty(AgentWorld.ReadJson(expiredList).EnumerateArray());
        using var enroll = await world.PostEnrollAsync(AgentWorld.EnrollmentRequest(token));
        Assert.Equal(HttpStatusCode.Unauthorized, enroll.StatusCode);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(60 * 24 * 8)]
    public async Task TokenLifetimeOutsideTheAllowedRangeIsRejected(int minutes)
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var setup = await world.CreateTenantAsync();
        using var response = await setup.Admin.SendAsync(HttpMethod.Post,
            $"/api/tenants/{setup.Tenant.Id.Value}/agent-enrollment-tokens", new { lifetimeMinutes = minutes });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TokensAreBoundToTheirTenantAndCannotBeUsedOrManagedAcrossTenants()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var tenantA = await world.CreateTenantAsync();
        var tenantB = await world.CreateTenantAsync();
        var (tokenId, token) = await world.CreateTokenAsync(tenantA);

        // Tenant B's administrator cannot see, list or revoke tenant A's token, by either route.
        using var viaA = await tenantB.Admin.SendAsync(HttpMethod.Delete, $"/api/tenants/{tenantA.Tenant.Id.Value}/agent-enrollment-tokens/{tokenId}");
        using var viaB = await tenantB.Admin.SendAsync(HttpMethod.Delete, $"/api/tenants/{tenantB.Tenant.Id.Value}/agent-enrollment-tokens/{tokenId}");
        using var listA = await tenantB.Admin.GetAsync($"/api/tenants/{tenantA.Tenant.Id.Value}/agent-enrollment-tokens");
        Assert.Equal(HttpStatusCode.Forbidden, viaA.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, viaB.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, listA.StatusCode);
        using var listB = await tenantB.Admin.GetAsync($"/api/tenants/{tenantB.Tenant.Id.Value}/agent-enrollment-tokens");
        Assert.Empty(AgentWorld.ReadJson(listB).EnumerateArray());

        // The token always yields an agent of its own tenant; the request cannot choose another one.
        using var enroll = await world.PostEnrollAsync(AgentWorld.EnrollmentRequest(token));
        Assert.Equal(HttpStatusCode.Created, enroll.StatusCode);
        await using var context = fixture.CreatePlatformContext();
        var agent = await context.Agents.SingleAsync(row => row.Id == new AgentId(AgentWorld.ReadJson(enroll).GetProperty("agentId").GetGuid()));
        Assert.Equal(tenantA.Tenant.Id, agent.TenantId);
        Assert.False(await context.Agents.AnyAsync(row => row.TenantId == tenantB.Tenant.Id));
    }

    [Fact]
    public async Task StoreRefusesToConsumeATokenForAnotherTenantAndLeavesItUsable()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var tenantA = await world.CreateTenantAsync();
        var tenantB = await world.CreateTenantAsync();
        var (tokenId, _) = await world.CreateTokenAsync(tenantA);
        var store = new PostgreSqlAgentEnrollmentStore(world.Factory.Services.GetRequiredService<DbContextOptions<BackupPlatform.Infrastructure.Persistence.PlatformDbContext>>());
        var software = new AgentSoftware(AgentPlatform.Linux, new Version(1, 0), new AgentProtocolVersion(1, 0));
        var agent = new Agent(AgentId.New(), tenantB.Tenant.Id, software, "host", Guid.NewGuid());
        agent.RecordEnrollment();
        var credential = BackupPlatform.Agents.Credentials.AgentCredential.IssueInitial(agent.Id, agent.TenantId, DateTimeOffset.UtcNow);
        var outcome = await store.EnrollAsync(tokenId, tenantB.Tenant.Id, agent, credential.Credential, DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal(EnrollmentCommit.TokenUnavailable, outcome);
        await using var context = fixture.CreatePlatformContext();
        Assert.Null((await context.EnrollmentTokens.SingleAsync(row => row.Id == tokenId)).ConsumedAt);
        Assert.False(await context.Agents.AnyAsync(row => row.Id == agent.Id));
    }

    [Fact]
    public async Task DisablingEnrollmentAfterTokenCreationBlocksEnrollmentWithoutConsumingTheToken()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var setup = await world.CreateTenantAsync();
        var (_, token) = await world.CreateTokenAsync(setup);
        await world.SetEnrollmentAsync(setup.Tenant, false);
        using var blocked = await world.PostEnrollAsync(AgentWorld.EnrollmentRequest(token));
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        await world.SetEnrollmentAsync(setup.Tenant, true);
        using var allowed = await world.PostEnrollAsync(AgentWorld.EnrollmentRequest(token));
        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
    }

    [Fact]
    public async Task DuplicateInstallationIsRejectedWithoutBurningTheSecondToken()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var setup = await world.CreateTenantAsync();
        var installation = Guid.NewGuid();
        await world.EnrollAsync(setup, installation);
        var (_, token) = await world.CreateTokenAsync(setup);
        using var duplicate = await world.PostEnrollAsync(AgentWorld.EnrollmentRequest(token, installation));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        using var different = await world.PostEnrollAsync(AgentWorld.EnrollmentRequest(token, Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.Created, different.StatusCode);
    }

    [Fact]
    public async Task EnrollmentResponseCarriesIdentityCredentialAndServerProtocolAndStoresAgentMetadata()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var setup = await world.CreateTenantAsync();
        var (_, token) = await world.CreateTokenAsync(setup);
        var request = AgentWorld.EnrollmentRequest(token);
        using var response = await world.PostEnrollAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var json = AgentWorld.ReadJson(response);
        var credential = json.GetProperty("credential");
        Assert.Equal(1, credential.GetProperty("generation").GetInt32());
        Assert.True(AgentSecretReference.IsWellFormedSecret(credential.GetProperty("secret").GetString()!));
        Assert.Equal("1.0", json.GetProperty("protocol").GetProperty("current").GetString());
        await using var context = fixture.CreatePlatformContext();
        var agent = await context.Agents.SingleAsync(row => row.Id == new AgentId(json.GetProperty("agentId").GetGuid()));
        Assert.Equal(AgentStatus.Active, agent.Status);
        Assert.Equal(request.InstallationId, agent.InstallationId);
        Assert.Equal("integration-host", agent.HostName);
        Assert.Equal(AgentPlatform.Linux, agent.Software.Platform);
        Assert.Equal(new Version(1, 2, 3), agent.Software.Version);
        Assert.Equal(1, agent.CredentialGeneration);
    }

    [Fact]
    public async Task CompatibleProtocolIsAcceptedAndIncompatibleMajorIsRejectedWithTheServerWindow()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var setup = await world.CreateTenantAsync();
        var (_, token) = await world.CreateTokenAsync(setup);
        using var incompatible = await world.PostEnrollAsync(AgentWorld.EnrollmentRequest(token, protocolVersion: "2.0"));
        Assert.Equal(HttpStatusCode.BadRequest, incompatible.StatusCode);
        var error = AgentWorld.ReadJson(incompatible);
        Assert.Equal("protocol_incompatible", error.GetProperty("error").GetString());
        Assert.Equal("1.0", error.GetProperty("supportedProtocol").GetProperty("maximumSupported").GetString());
        using var compatible = await world.PostEnrollAsync(AgentWorld.EnrollmentRequest(token, protocolVersion: "1.0"));
        Assert.Equal(HttpStatusCode.Created, compatible.StatusCode);
    }

    [Fact]
    public async Task ABackendWithAWiderWindowAcceptsOlderMinorVersionsOfItsMajor()
    {
        var options = new AgentSecurityOptions
        {
            Protocol = new ProtocolNegotiationContract(new ProtocolVersion(1, 3),
                new ProtocolVersionRange(new ProtocolVersion(1, 0), new ProtocolVersion(1, 3)))
        };
        await using var world = await AgentWorld.CreateAsync(fixture, options);
        var setup = await world.CreateTenantAsync();
        var (_, minorToken) = await world.CreateTokenAsync(setup);
        var (_, majorToken) = await world.CreateTokenAsync(setup);
        using var minor = await world.PostEnrollAsync(AgentWorld.EnrollmentRequest(minorToken, protocolVersion: "1.2"));
        using var major = await world.PostEnrollAsync(AgentWorld.EnrollmentRequest(majorToken, protocolVersion: "2.0"));
        Assert.Equal(HttpStatusCode.Created, minor.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, major.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("00000000000000000000000000000001.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task MalformedAndUnknownTokensGetOneUniformUnauthorizedAnswer(string token)
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        using var response = await world.PostEnrollAsync(AgentWorld.EnrollmentRequest(token));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("invalid_enrollment_token", AgentWorld.ReadJson(response).GetProperty("error").GetString());
    }

    [Fact]
    public async Task EnrollmentRejectsInvalidMetadataWithoutConsumingTheToken()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var setup = await world.CreateTenantAsync();
        var (_, token) = await world.CreateTokenAsync(setup);
        using var invalid = await world.PostEnrollAsync(AgentWorld.EnrollmentRequest(token) with { Platform = "plan9" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var valid = await world.PostEnrollAsync(AgentWorld.EnrollmentRequest(token));
        Assert.Equal(HttpStatusCode.Created, valid.StatusCode);
    }
}
