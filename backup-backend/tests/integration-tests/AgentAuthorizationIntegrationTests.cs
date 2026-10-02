using System.Net;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.IntegrationTests;

public sealed class AgentAuthorizationIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task PlatformAdminEnablesAndDisablesEnrollmentPerTenantAndNothingElse()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var tenant = await world.AddTenantAsync();
        var other = await world.AddTenantAsync();
        var admin = await world.SignInAsync(await world.SeedUserAsync(platformAdmin: true));
        var path = $"/api/platform/tenants/{tenant.Id.Value}/agent-enrollment";

        using var initial = await admin.GetAsync(path);
        Assert.False(AgentWorld.ReadJson(initial).GetProperty("enabled").GetBoolean());
        using var enable = await admin.SendAsync(HttpMethod.Put, path, new { enabled = true });
        var enabled = AgentWorld.ReadJson(enable);
        Assert.True(enabled.GetProperty("enabled").GetBoolean());
        Assert.True(enabled.GetProperty("changed").GetBoolean());
        using var repeat = await admin.SendAsync(HttpMethod.Put, path, new { enabled = true });
        Assert.False(AgentWorld.ReadJson(repeat).GetProperty("changed").GetBoolean());
        using var otherPolicy = await admin.GetAsync($"/api/platform/tenants/{other.Id.Value}/agent-enrollment");
        Assert.False(AgentWorld.ReadJson(otherPolicy).GetProperty("enabled").GetBoolean());
        using var disable = await admin.SendAsync(HttpMethod.Put, path, new { enabled = false });
        Assert.False(AgentWorld.ReadJson(disable).GetProperty("enabled").GetBoolean());
        using var missing = await admin.SendAsync(HttpMethod.Put, $"/api/platform/tenants/{Guid.NewGuid()}/agent-enrollment", new { enabled = true });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        await using var context = fixture.CreatePlatformContext();
        var policy = await context.TenantAgentEnrollmentPolicies.SingleAsync(row => row.TenantId == tenant.Id);
        Assert.False(policy.IsEnabled);
        Assert.False(await context.TenantAgentEnrollmentPolicies.AnyAsync(row => row.TenantId == other.Id));
    }

    [Theory]
    [InlineData(TenantRole.Owner)]
    [InlineData(TenantRole.Admin)]
    [InlineData(TenantRole.Operator)]
    [InlineData(TenantRole.Viewer)]
    public async Task TenantRolesCannotChangeTheEnrollmentPermission(TenantRole role)
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var setup = await world.CreateTenantAsync(role, enrollmentEnabled: false);
        using var response = await setup.Admin.SendAsync(HttpMethod.Put,
            $"/api/platform/tenants/{setup.Tenant.Id.Value}/agent-enrollment", new { enabled = true });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var read = await setup.Admin.GetAsync($"/api/platform/tenants/{setup.Tenant.Id.Value}/agent-enrollment");
        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
    }

    [Fact]
    public async Task AnonymousCallersCannotReachAdministrationRoutes()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var tenant = await world.AddTenantAsync();
        var id = tenant.Id.Value;
        foreach (var (method, path) in new[]
        {
            (HttpMethod.Get, $"/api/platform/tenants/{id}/agent-enrollment"),
            (HttpMethod.Post, $"/api/tenants/{id}/agent-enrollment-tokens"),
            (HttpMethod.Get, $"/api/tenants/{id}/agent-enrollment-tokens"),
            (HttpMethod.Get, $"/api/tenants/{id}/agents"),
            (HttpMethod.Post, $"/api/tenants/{id}/agents/{Guid.NewGuid()}/revoke")
        })
        {
            using var request = new HttpRequestMessage(method, path);
            using var response = await world.Anonymous.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Theory]
    [InlineData(TenantRole.Admin)]
    [InlineData(TenantRole.Owner)]
    public async Task TenantAdministratorsCreateListAndRevokeTokensForTheirOwnEnabledTenant(TenantRole role)
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var setup = await world.CreateTenantAsync(role);
        var (tokenId, _) = await world.CreateTokenAsync(setup);
        using var revoke = await setup.Admin.SendAsync(HttpMethod.Delete, $"/api/tenants/{setup.Tenant.Id.Value}/agent-enrollment-tokens/{tokenId}");
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
    }

    [Theory]
    [InlineData(TenantRole.Operator)]
    [InlineData(TenantRole.Viewer)]
    public async Task OperatorsAndViewersCannotManageEnrollmentOrAgents(TenantRole role)
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var admin = await world.CreateTenantAsync();
        var (tokenId, _) = await world.CreateTokenAsync(admin);
        var agent = await world.EnrollAsync(admin);
        var lowRole = await world.SeedUserAsync();
        await world.GrantAsync(admin.Tenant, lowRole, role);
        var session = await world.SignInAsync(lowRole);
        var tenant = admin.Tenant.Id.Value;
        using var create = await session.SendAsync(HttpMethod.Post, $"/api/tenants/{tenant}/agent-enrollment-tokens");
        using var list = await session.GetAsync($"/api/tenants/{tenant}/agent-enrollment-tokens");
        using var revokeToken = await session.SendAsync(HttpMethod.Delete, $"/api/tenants/{tenant}/agent-enrollment-tokens/{tokenId}");
        using var agents = await session.GetAsync($"/api/tenants/{tenant}/agents");
        using var revokeAgent = await session.SendAsync(HttpMethod.Post, $"/api/tenants/{tenant}/agents/{agent.AgentId}/revoke");
        Assert.All(new[] { create, list, revokeToken, agents, revokeAgent }, response => Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode));
        using var stillValid = await world.AgentMeAsync(agent.CredentialId, agent.Secret);
        Assert.Equal(HttpStatusCode.OK, stillValid.StatusCode);
    }

    [Fact]
    public async Task TokenCreationRequiresEnrollmentToBeEnabledForTheTenant()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var setup = await world.CreateTenantAsync(enrollmentEnabled: false);
        using var response = await setup.Admin.SendAsync(HttpMethod.Post, $"/api/tenants/{setup.Tenant.Id.Value}/agent-enrollment-tokens");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await using var context = fixture.CreatePlatformContext();
        Assert.False(await context.EnrollmentTokens.AnyAsync(row => row.TenantId == setup.Tenant.Id));

        await world.SetEnrollmentAsync(setup.Tenant, true);
        await world.CreateTokenAsync(setup);
        await world.SetEnrollmentAsync(setup.Tenant, false);
        using var disabledAgain = await setup.Admin.SendAsync(HttpMethod.Post, $"/api/tenants/{setup.Tenant.Id.Value}/agent-enrollment-tokens");
        Assert.Equal(HttpStatusCode.Forbidden, disabledAgain.StatusCode);
    }

    [Fact]
    public async Task EnablingOneTenantDoesNotEnableAnother()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var enabled = await world.CreateTenantAsync();
        var disabled = await world.CreateTenantAsync(enrollmentEnabled: false);
        await world.CreateTokenAsync(enabled);
        using var response = await disabled.Admin.SendAsync(HttpMethod.Post, $"/api/tenants/{disabled.Tenant.Id.Value}/agent-enrollment-tokens");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PlatformAdminWithoutMembershipHasNoImplicitTenantAccess()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var setup = await world.CreateTenantAsync();
        var platform = await world.SignInAsync(await world.SeedUserAsync(platformAdmin: true));
        var tenant = setup.Tenant.Id.Value;
        using var create = await platform.SendAsync(HttpMethod.Post, $"/api/tenants/{tenant}/agent-enrollment-tokens");
        using var list = await platform.GetAsync($"/api/tenants/{tenant}/agent-enrollment-tokens");
        using var agents = await platform.GetAsync($"/api/tenants/{tenant}/agents");
        using var context = await platform.GetAsync($"/api/tenants/{tenant}/context");
        Assert.All(new[] { create, list, agents, context }, response => Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode));
    }

    [Fact]
    public async Task CrossTenantAdministratorCannotManageAnotherTenantsAgents()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var tenantA = await world.CreateTenantAsync();
        var tenantB = await world.CreateTenantAsync();
        var agentOfB = await world.EnrollAsync(tenantB);
        var a = tenantA.Tenant.Id.Value;
        var b = tenantB.Tenant.Id.Value;

        using var listB = await tenantA.Admin.GetAsync($"/api/tenants/{b}/agents");
        using var revokeViaB = await tenantA.Admin.SendAsync(HttpMethod.Post, $"/api/tenants/{b}/agents/{agentOfB.AgentId}/revoke");
        using var revokeViaA = await tenantA.Admin.SendAsync(HttpMethod.Post, $"/api/tenants/{a}/agents/{agentOfB.AgentId}/revoke");
        using var createB = await tenantA.Admin.SendAsync(HttpMethod.Post, $"/api/tenants/{b}/agent-enrollment-tokens");
        Assert.Equal(HttpStatusCode.Forbidden, listB.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, revokeViaB.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, revokeViaA.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, createB.StatusCode);
        using var ownList = await tenantA.Admin.GetAsync($"/api/tenants/{a}/agents");
        Assert.Empty(AgentWorld.ReadJson(ownList).EnumerateArray());
        using var stillWorks = await world.AgentMeAsync(agentOfB.CredentialId, agentOfB.Secret);
        Assert.Equal(HttpStatusCode.OK, stillWorks.StatusCode);
        await using var context = fixture.CreatePlatformContext();
        Assert.Equal(BackupPlatform.Domain.Agents.AgentStatus.Active,
            (await context.Agents.SingleAsync(agent => agent.Id == new AgentId(agentOfB.AgentId))).Status);
    }

    [Fact]
    public async Task UserEndpointsKeepTheirAntiforgeryProtection()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var setup = await world.CreateTenantAsync();
        using var response = await setup.Admin.SendAsync(HttpMethod.Post,
            $"/api/tenants/{setup.Tenant.Id.Value}/agent-enrollment-tokens", includeAntiforgery: false);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var context = fixture.CreatePlatformContext();
        Assert.False(await context.EnrollmentTokens.AnyAsync(row => row.TenantId == setup.Tenant.Id));
    }

    [Fact]
    public async Task TenantAdministratorSeesOnlyTheirOwnAgentsWithoutSecrets()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var tenantA = await world.CreateTenantAsync();
        var tenantB = await world.CreateTenantAsync();
        var agentA = await world.EnrollAsync(tenantA);
        await world.EnrollAsync(tenantB);
        using var list = await tenantA.Admin.GetAsync($"/api/tenants/{tenantA.Tenant.Id.Value}/agents");
        var text = await list.Content.ReadAsStringAsync();
        var entry = Assert.Single(System.Text.Json.JsonDocument.Parse(text).RootElement.EnumerateArray());
        Assert.Equal(agentA.AgentId, entry.GetProperty("agentId").GetGuid());
        Assert.Equal("Active", entry.GetProperty("status").GetString());
        Assert.DoesNotContain(agentA.Secret, text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", text, StringComparison.OrdinalIgnoreCase);
    }
}
