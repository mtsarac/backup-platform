using System.Net;
using BackupPlatform.Agents.Credentials;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Domain.Agents;
using BackupPlatform.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.IntegrationTests;

public sealed class AgentAuthenticationIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static readonly string WrongSecret = new('Q', AgentSecretReference.SecretLength);

    [Fact]
    public async Task ValidCredentialAuthenticatesTheAgentAndExposesNoSecret()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var setup = await world.CreateTenantAsync();
        var agent = await world.EnrollAsync(setup);
        using var response = await world.AgentMeAsync(agent.CredentialId, agent.Secret);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(agent.Secret, text, StringComparison.Ordinal);
        var json = AgentWorld.ReadJson(response);
        Assert.Equal(agent.AgentId, json.GetProperty("agentId").GetGuid());
        Assert.Equal(setup.Tenant.Id.Value, json.GetProperty("tenantId").GetGuid());
        Assert.Equal(agent.CredentialId, json.GetProperty("credentialId").GetGuid());
        Assert.Equal(1, json.GetProperty("credentialGeneration").GetInt32());
    }

    [Fact]
    public async Task InvalidUnknownAndMalformedCredentialsAreAllRejectedWithTheSameChallenge()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var setup = await world.CreateTenantAsync();
        var agent = await world.EnrollAsync(setup);
        var attempts = new List<HttpResponseMessage>
        {
            await world.AgentMeAsync(agent.CredentialId, WrongSecret),
            await world.AgentMeAsync(Guid.NewGuid(), agent.Secret),
            await world.AgentMeAsync(Guid.NewGuid(), WrongSecret)
        };
        foreach (var header in new[] { "BackupAgent garbage", "BackupAgent", "Bearer " + agent.Secret, $"Basic {agent.CredentialId:N}" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/agent/me");
            request.Headers.TryAddWithoutValidation("Authorization", header);
            attempts.Add(await world.Anonymous.SendAsync(request));
        }
        attempts.Add(await world.Anonymous.GetAsync("/api/agent/me"));
        using var bodies = new DisposableList(attempts);
        Assert.All(attempts, response =>
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Contains("BackupAgent", response.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);
        });
        var bodyTexts = await Task.WhenAll(attempts.Select(response => response.Content.ReadAsStringAsync()));
        Assert.All(bodyTexts, text => Assert.DoesNotContain(agent.Secret, text, StringComparison.Ordinal));
    }

    [Fact]
    public async Task SecretInTheQueryStringOrACustomHeaderDoesNotAuthenticate()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var setup = await world.CreateTenantAsync();
        var agent = await world.EnrollAsync(setup);
        using var query = await world.Anonymous.GetAsync($"/api/agent/me?credential={agent.CredentialId:N}.{agent.Secret}");
        using var custom = new HttpRequestMessage(HttpMethod.Get, "/api/agent/me");
        custom.Headers.Add("X-Agent-Credential", AgentSecretReference.Format(agent.CredentialId, agent.Secret));
        using var customResponse = await world.Anonymous.SendAsync(custom);
        Assert.Equal(HttpStatusCode.Unauthorized, query.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, customResponse.StatusCode);
    }

    [Fact]
    public async Task DisabledAgentCannotAuthenticateUntilItIsEnabledAgain()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var setup = await world.CreateTenantAsync();
        var agent = await world.EnrollAsync(setup);
        await ChangeAgentAsync(agent.AgentId, row => row.Disable());
        using var disabled = await world.AgentMeAsync(agent.CredentialId, agent.Secret);
        using var rotate = await world.AgentCallAsync(HttpMethod.Post, "/api/agent/credential/rotate", agent.CredentialId, agent.Secret);
        Assert.Equal(HttpStatusCode.Unauthorized, disabled.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, rotate.StatusCode);
        await ChangeAgentAsync(agent.AgentId, row => row.Enable());
        using var enabled = await world.AgentMeAsync(agent.CredentialId, agent.Secret);
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
    }

    [Fact]
    public async Task RevokedAgentCannotAuthenticateRotateOrConfirmAndRevocationIsTerminal()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var setup = await world.CreateTenantAsync();
        var agent = await world.EnrollAsync(setup);
        var pending = await world.BeginRotationAsync(agent.CredentialId, agent.Secret);
        using var revoke = await setup.Admin.SendAsync(HttpMethod.Post, $"/api/tenants/{setup.Tenant.Id.Value}/agents/{agent.AgentId}/revoke");
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        foreach (var (id, secret) in new[] { (agent.CredentialId, agent.Secret), (pending.CredentialId, pending.Secret) })
        {
            using var me = await world.AgentMeAsync(id, secret);
            using var rotate = await world.AgentCallAsync(HttpMethod.Post, "/api/agent/credential/rotate", id, secret);
            using var confirm = await world.AgentCallAsync(HttpMethod.Post, "/api/agent/credential/confirm", id, secret);
            Assert.All(new[] { me, rotate, confirm }, response => Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode));
        }
        var credentials = await world.CredentialsOfAsync(agent.AgentId);
        Assert.All(credentials, credential =>
        {
            Assert.Equal(AgentCredentialStatus.Revoked, credential.Status);
            Assert.Equal(AgentCredentialRevocationReason.AgentRevoked, credential.RevocationReason);
        });
        Assert.Equal(2, credentials.Count);
        await using var context = fixture.CreatePlatformContext();
        var row = await context.Agents.SingleAsync(candidate => candidate.Id == new AgentId(agent.AgentId));
        Assert.Equal(AgentStatus.Revoked, row.Status);
        Assert.Throws<InvalidOperationException>(row.Enable);

        using var repeated = await setup.Admin.SendAsync(HttpMethod.Post, $"/api/tenants/{setup.Tenant.Id.Value}/agents/{agent.AgentId}/revoke");
        Assert.Equal(HttpStatusCode.NoContent, repeated.StatusCode);
        using var list = await setup.Admin.GetAsync($"/api/tenants/{setup.Tenant.Id.Value}/agents");
        Assert.Equal("Revoked", Assert.Single(AgentWorld.ReadJson(list).EnumerateArray()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task ReEnrollmentAfterRevocationRequiresANewTokenAndYieldsANewIdentity()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var setup = await world.CreateTenantAsync();
        var installation = Guid.NewGuid();
        var first = await world.EnrollAsync(setup, installation);
        using var revoke = await setup.Admin.SendAsync(HttpMethod.Post, $"/api/tenants/{setup.Tenant.Id.Value}/agents/{first.AgentId}/revoke");
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        var second = await world.EnrollAsync(setup, installation);
        Assert.NotEqual(first.AgentId, second.AgentId);
        Assert.NotEqual(first.CredentialId, second.CredentialId);
        using var oldIdentity = await world.AgentMeAsync(first.CredentialId, first.Secret);
        using var newIdentity = await world.AgentMeAsync(second.CredentialId, second.Secret);
        Assert.Equal(HttpStatusCode.Unauthorized, oldIdentity.StatusCode);
        Assert.Equal(HttpStatusCode.OK, newIdentity.StatusCode);
    }

    [Fact]
    public async Task BrowserSessionCookieNeverAuthenticatesAgentEndpoints()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var setup = await world.CreateTenantAsync();
        await world.EnrollAsync(setup);
        using var me = await setup.Admin.GetAsync("/api/agent/me");
        using var rotate = await setup.Admin.SendAsync(HttpMethod.Post, "/api/agent/credential/rotate");
        using var confirm = await setup.Admin.SendAsync(HttpMethod.Post, "/api/agent/credential/confirm");
        Assert.All(new[] { me, rotate, confirm }, response => Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode));
        using var control = await setup.Admin.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.OK, control.StatusCode);
    }

    [Fact]
    public async Task AgentCredentialNeverGrantsUserIdentityOnUserEndpoints()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var setup = await world.CreateTenantAsync();
        var agent = await world.EnrollAsync(setup);
        var tenant = setup.Tenant.Id.Value;
        foreach (var path in new[]
        {
            "/api/me", "/api/me/tenants", $"/api/tenants/{tenant}/context", $"/api/tenants/{tenant}/agents",
            $"/api/tenants/{tenant}/agent-enrollment-tokens", $"/api/platform/tenants/{tenant}/agent-enrollment"
        })
        {
            using var response = await world.AgentCallAsync(HttpMethod.Get, path, agent.CredentialId, agent.Secret);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        using var create = await world.AgentCallAsync(HttpMethod.Post, $"/api/tenants/{tenant}/agent-enrollment-tokens", agent.CredentialId, agent.Secret);
        using var revoke = await world.AgentCallAsync(HttpMethod.Post, $"/api/tenants/{tenant}/agents/{agent.AgentId}/revoke", agent.CredentialId, agent.Secret);
        Assert.NotEqual(HttpStatusCode.Created, create.StatusCode);
        Assert.NotEqual(HttpStatusCode.NoContent, revoke.StatusCode);
        using var stillActive = await world.AgentMeAsync(agent.CredentialId, agent.Secret);
        Assert.Equal(HttpStatusCode.OK, stillActive.StatusCode);
    }

    [Fact]
    public async Task AgentIdentityIsPerTenantAndANewTenantsCredentialIsNotAccepted()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var tenantA = await world.CreateTenantAsync();
        var tenantB = await world.CreateTenantAsync();
        var a = await world.EnrollAsync(tenantA);
        var b = await world.EnrollAsync(tenantB);
        using var mixed = await world.AgentMeAsync(a.CredentialId, b.Secret);
        Assert.Equal(HttpStatusCode.Unauthorized, mixed.StatusCode);
        using var meB = await world.AgentMeAsync(b.CredentialId, b.Secret);
        Assert.Equal(tenantB.Tenant.Id.Value, AgentWorld.ReadJson(meB).GetProperty("tenantId").GetGuid());
    }

    private async Task ChangeAgentAsync(Guid agentId, Action<Agent> change)
    {
        await using var context = fixture.CreatePlatformContext();
        var agent = await context.Agents.SingleAsync(row => row.Id == new AgentId(agentId));
        change(agent);
        await context.SaveChangesAsync();
    }

    private sealed class DisposableList(List<HttpResponseMessage> responses) : IDisposable
    {
        public void Dispose() => responses.ForEach(response => response.Dispose());
    }
}
