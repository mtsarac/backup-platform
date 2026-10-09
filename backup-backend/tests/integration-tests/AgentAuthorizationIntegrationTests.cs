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
        using var status = await session.GetAsync($"/api/tenants/{tenant}/agent-enrollment-tokens/{tokenId}");
        using var revokeToken = await session.SendAsync(HttpMethod.Delete, $"/api/tenants/{tenant}/agent-enrollment-tokens/{tokenId}");
        using var agents = await session.GetAsync($"/api/tenants/{tenant}/agents");
        using var revokeAgent = await session.SendAsync(HttpMethod.Post, $"/api/tenants/{tenant}/agents/{agent.AgentId}/revoke");
        Assert.Equal(HttpStatusCode.OK, agents.StatusCode);
        Assert.All(new[] { create, list, status, revokeToken, revokeAgent }, response => Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode));
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
        Assert.Equal("enrollment_disabled", AgentWorld.ReadJson(response).GetProperty("error").GetString());
        await using var context = fixture.CreatePlatformContext();
        Assert.False(await context.EnrollmentTokens.AnyAsync(row => row.TenantId == setup.Tenant.Id));

        await world.SetEnrollmentAsync(setup.Tenant, true);
        await world.CreateTokenAsync(setup);
        await world.SetEnrollmentAsync(setup.Tenant, false);
        using var disabledAgain = await setup.Admin.SendAsync(HttpMethod.Post, $"/api/tenants/{setup.Tenant.Id.Value}/agent-enrollment-tokens");
        Assert.Equal(HttpStatusCode.Forbidden, disabledAgain.StatusCode);
        Assert.Equal("enrollment_disabled", AgentWorld.ReadJson(disabledAgain).GetProperty("error").GetString());
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
        using var status = await platform.GetAsync($"/api/tenants/{tenant}/agent-enrollment-tokens/{Guid.NewGuid()}");
        using var agents = await platform.GetAsync($"/api/tenants/{tenant}/agents");
        using var context = await platform.GetAsync($"/api/tenants/{tenant}/context");
        using var detail = await platform.GetAsync($"/api/tenants/{tenant}/agents/{Guid.NewGuid()}");
        using var policy = await platform.GetAsync($"/api/tenants/{tenant}/agent-enrollment");
        using var changePolicy = await platform.SendAsync(HttpMethod.Put, $"/api/tenants/{tenant}/agent-enrollment", new { enabled = false });
        Assert.All(new[] { create, list, status, agents, context, detail, policy, changePolicy }, response => Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode));
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

    [Theory]
    [InlineData(TenantRole.Owner)]
    [InlineData(TenantRole.Admin)]
    public async Task TenantEnrollmentPolicyIsAdminScopedAndIsolated(TenantRole role)
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var own = await world.CreateTenantAsync(role, enrollmentEnabled: false);
        var other = await world.CreateTenantAsync(enrollmentEnabled: false);
        var path = $"/api/tenants/{own.Tenant.Id.Value}/agent-enrollment";
        using var initial = await own.Admin.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        Assert.False(AgentWorld.ReadJson(initial).GetProperty("enabled").GetBoolean());
        using var enabled = await own.Admin.SendAsync(HttpMethod.Put, path, new { enabled = true });
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        Assert.True(AgentWorld.ReadJson(enabled).GetProperty("enabled").GetBoolean());
        using var repeat = await own.Admin.SendAsync(HttpMethod.Put, path, new { enabled = true });
        Assert.False(AgentWorld.ReadJson(repeat).GetProperty("changed").GetBoolean());
        using var foreignRead = await own.Admin.GetAsync($"/api/tenants/{other.Tenant.Id.Value}/agent-enrollment");
        using var foreignWrite = await own.Admin.SendAsync(HttpMethod.Put,
            $"/api/tenants/{other.Tenant.Id.Value}/agent-enrollment", new { enabled = true });
        Assert.Equal(HttpStatusCode.Forbidden, foreignRead.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, foreignWrite.StatusCode);
        using var otherRead = await other.Admin.GetAsync($"/api/tenants/{other.Tenant.Id.Value}/agent-enrollment");
        Assert.False(AgentWorld.ReadJson(otherRead).GetProperty("enabled").GetBoolean());
        using var noCsrf = await own.Admin.SendAsync(HttpMethod.Put, path, new { enabled = false }, includeAntiforgery: false);
        Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
    }

    [Theory]
    [InlineData(TenantRole.Operator)]
    [InlineData(TenantRole.Viewer)]
    public async Task SafeAgentDetailsAreReadableButEnrollmentPolicyIsNot(TenantRole role)
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var setup = await world.CreateTenantAsync();
        var agent = await world.EnrollAsync(setup);
        using (await world.HeartbeatAsync(agent)) { }
        var snapshot = AgentWorld.CapabilitySnapshot(
            AgentWorld.CapabilityReport("mssql.backup.full", "Supported", new Dictionary<string, string> { ["scope"] = "agent" }),
            AgentWorld.CapabilityReport("mssql.backup.log", "Unsupported"));
        using (var sync = await world.SyncCapabilitiesAsync(agent, snapshot))
        {
            Assert.Equal(HttpStatusCode.OK, sync.StatusCode);
        }
        var account = await world.SeedUserAsync();
        await world.GrantAsync(setup.Tenant, account, role);
        var session = await world.SignInAsync(account);
        var tenantPath = $"/api/tenants/{setup.Tenant.Id.Value}";
        using var detail = await session.GetAsync($"{tenantPath}/agents/{agent.AgentId}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        var json = AgentWorld.ReadJson(detail);
        Assert.Equal(agent.AgentId, json.GetProperty("agentId").GetGuid());
        Assert.Equal("Active", json.GetProperty("status").GetString());
        Assert.Equal("online", json.GetProperty("connectivity").GetString());
        Assert.Equal("test-os", json.GetProperty("osDescription").GetString());
        Assert.Equal("10.0.0", json.GetProperty("runtimeVersion").GetString());
        Assert.Equal(world.Clock.GetUtcNow(), json.GetProperty("enrolledAt").GetDateTimeOffset());
        // The safe view returns exactly the persisted snapshot the agent synced.
        Assert.Equal(snapshot.Revision, json.GetProperty("capabilitiesRevision").GetString());
        Assert.Equal(world.Clock.GetUtcNow(), json.GetProperty("capabilitiesReportedAt").GetDateTimeOffset());
        var capabilities = json.GetProperty("capabilities").EnumerateArray().ToList();
        Assert.Equal(["mssql.backup.full", "mssql.backup.log"], capabilities.Select(c => c.GetProperty("identifier").GetString()));
        Assert.Equal(["Supported", "Unsupported"], capabilities.Select(c => c.GetProperty("support").GetString()));
        Assert.Equal("agent", capabilities[0].GetProperty("metadata").GetProperty("scope").GetString());
        Assert.Empty(capabilities[1].GetProperty("metadata").EnumerateObject());
        using var list = await session.GetAsync($"{tenantPath}/agents");
        var listEntry = Assert.Single(AgentWorld.ReadJson(list).EnumerateArray());
        Assert.Equal(json.GetProperty("capabilities").GetRawText(), listEntry.GetProperty("capabilities").GetRawText());
        var text = await detail.Content.ReadAsStringAsync();
        foreach (var forbidden in new[] { agent.Secret, agent.CredentialId.ToString(), "credentialGeneration", "secretHash", "lease", "installationId", "private-path", "not-for-the-browser" })
        {
            Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
        }
        using var missing = await session.GetAsync($"{tenantPath}/agents/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var other = await world.CreateTenantAsync();
        var foreign = await world.EnrollAsync(other);
        using var foreignViaOwn = await session.GetAsync($"{tenantPath}/agents/{foreign.AgentId}");
        using var foreignViaOther = await session.GetAsync($"/api/tenants/{other.Tenant.Id.Value}/agents/{foreign.AgentId}");
        Assert.Equal(HttpStatusCode.NotFound, foreignViaOwn.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, foreignViaOther.StatusCode);
        using var policyRead = await session.GetAsync($"{tenantPath}/agent-enrollment");
        using var policyWrite = await session.SendAsync(HttpMethod.Put, $"{tenantPath}/agent-enrollment", new { enabled = false });
        Assert.Equal(HttpStatusCode.Forbidden, policyRead.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, policyWrite.StatusCode);
        using var anonymous = await world.Anonymous.GetAsync($"{tenantPath}/agents/{agent.AgentId}");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Theory]
    [InlineData(TenantRole.Owner, "Administrator")]
    [InlineData(TenantRole.Admin, "Administrator")]
    [InlineData(TenantRole.Operator, "Operator")]
    [InlineData(TenantRole.Viewer, "Viewer")]
    public async Task UserMembershipPreservesAuthorizationClassAndReportsActualRole(TenantRole role, string authorizationRole)
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var tenant = await world.AddTenantAsync();
        var account = await world.SeedUserAsync(platformAdmin: true);
        await world.GrantAsync(tenant, account, role);
        var session = await world.SignInAsync(account);
        using var me = await session.GetAsync("/api/me");
        Assert.True(AgentWorld.ReadJson(me).GetProperty("isPlatformAdmin").GetBoolean());
        using var memberships = await session.GetAsync("/api/me/tenants");
        var membership = Assert.Single(AgentWorld.ReadJson(memberships).EnumerateArray());
        Assert.Equal(authorizationRole, membership.GetProperty("role").GetString());
        Assert.Equal(role.ToString(), membership.GetProperty("membershipRole").GetString());
        using var context = await session.GetAsync($"/api/tenants/{tenant.Id.Value}/context");
        Assert.Equal(authorizationRole, AgentWorld.ReadJson(context).GetProperty("role").GetString());
        Assert.Equal(role.ToString(), AgentWorld.ReadJson(context).GetProperty("membershipRole").GetString());
        var ordinary = await world.SignInAsync(await world.SeedUserAsync());
        using var ordinaryMe = await ordinary.GetAsync("/api/me");
        Assert.False(AgentWorld.ReadJson(ordinaryMe).GetProperty("isPlatformAdmin").GetBoolean());
    }
}
