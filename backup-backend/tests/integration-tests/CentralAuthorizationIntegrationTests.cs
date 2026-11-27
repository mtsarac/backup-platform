using System.Net;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.IntegrationTests;

public sealed class CentralAuthorizationIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task AuthorizationMatrix_UserAdmin_CanAccessOwnTenant_ForbiddenOnPlatformAndOtherTenant()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var tenantA = await world.CreateTenantAsync();
        var tenantB = await world.CreateTenantAsync();
        var aId = tenantA.Tenant.Id.Value;
        var bId = tenantB.Tenant.Id.Value;

        using var ownContext = await tenantA.Admin.GetAsync($"/api/tenants/{aId}/context");
        Assert.Equal(HttpStatusCode.OK, ownContext.StatusCode);

        using var ownAgents = await tenantA.Admin.GetAsync($"/api/tenants/{aId}/agents");
        Assert.Equal(HttpStatusCode.OK, ownAgents.StatusCode);

        using var otherContext = await tenantA.Admin.GetAsync($"/api/tenants/{bId}/context");
        Assert.Equal(HttpStatusCode.Forbidden, otherContext.StatusCode);

        using var otherAgents = await tenantA.Admin.GetAsync($"/api/tenants/{bId}/agents");
        Assert.Equal(HttpStatusCode.Forbidden, otherAgents.StatusCode);

        using var platformSummary = await tenantA.Admin.GetAsync("/api/platform/summary");
        Assert.Equal(HttpStatusCode.Forbidden, platformSummary.StatusCode);

        using var platformTenants = await tenantA.Admin.GetAsync("/api/platform/tenants");
        Assert.Equal(HttpStatusCode.Forbidden, platformTenants.StatusCode);
    }

    [Fact]
    public async Task AuthorizationMatrix_PlatformAdmin_ChoosesTenantWithoutMembership()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var tenantA = await world.CreateTenantAsync();
        var aId = tenantA.Tenant.Id.Value;

        var platformUser = await world.SeedUserAsync(platformAdmin: true);
        var platformSession = await world.SignInAsync(platformUser);

        using var platformSummary = await platformSession.GetAsync("/api/platform/summary");
        Assert.Equal(HttpStatusCode.OK, platformSummary.StatusCode);

        using var tenantContext = await platformSession.GetAsync($"/api/tenants/{aId}/context");
        Assert.Equal(HttpStatusCode.OK, tenantContext.StatusCode);

        using var tenantAgents = await platformSession.GetAsync($"/api/tenants/{aId}/agents");
        Assert.Equal(HttpStatusCode.OK, tenantAgents.StatusCode);

        using var tenants = await platformSession.GetAsync("/api/me/tenants");
        Assert.Empty(AgentWorld.ReadJson(tenants).EnumerateArray());
        await using var database = fixture.CreatePlatformContext();
        var events = await database.AuditEvents.Where(e => e.ActorUserId == platformUser.UserId && e.TenantId == tenantA.Tenant.Id && e.Action == "tenant.support_request").ToListAsync();
        Assert.Contains(events, e => e.ActorKind == BackupPlatform.Domain.Audit.AuditActorKind.PlatformAdmin && e.TargetId.Contains("/context"));
        Assert.Contains(events, e => e.ActorKind == BackupPlatform.Domain.Audit.AuditActorKind.PlatformAdmin && e.TargetId.Contains("/agents"));

        using var downloadAvail = await platformSession.GetAsync($"/api/tenants/{aId}/backup-artifacts/{Guid.NewGuid()}/download-availability");
        Assert.Equal(HttpStatusCode.Forbidden, downloadAvail.StatusCode);

        using var addMember = await platformSession.SendAsync(HttpMethod.Post, $"/api/platform/tenants/{aId}/memberships",
            new { userId = platformUser.UserId.Value });
        Assert.Equal(HttpStatusCode.BadRequest, addMember.StatusCode);
        var errorJson = AgentWorld.ReadJson(addMember);
        Assert.Equal("platform_admin_membership_not_allowed", errorJson.GetProperty("error").GetString());
    }

    [Fact]
    public async Task PlatformAdminCanCreateAndRunBackupInChosenTenantWithAudit()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var account = await scenario.World.SeedUserAsync(platformAdmin: true);
        var platform = await scenario.World.SignInAsync(account);
        using var create = await scenario.CreateJobRawAsync(BackupApiScenario.JobBody(scenario.Agent.AgentId), platform);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var job = AgentWorld.ReadJson(create).GetProperty("id").GetGuid();
        using var run = await scenario.StartRawAsync(job, Guid.NewGuid().ToString("N"), platform);
        Assert.Equal(HttpStatusCode.OK, run.StatusCode);
        await using var db = fixture.CreatePlatformContext();
        var audit = await db.AuditEvents.Where(e => e.ActorUserId == account.UserId && e.TenantId == scenario.Setup.Tenant.Id && e.Action == "tenant.support_request").ToListAsync();
        Assert.Contains(audit, e => e.ActorKind == BackupPlatform.Domain.Audit.AuditActorKind.PlatformAdmin && e.TargetId.EndsWith("/backup-jobs") && e.DetailsJson.Contains("POST"));
        Assert.Contains(audit, e => e.ActorKind == BackupPlatform.Domain.Audit.AuditActorKind.PlatformAdmin && e.TargetId.EndsWith("/runs") && e.DetailsJson.Contains("POST"));
    }

    [Theory]
    [InlineData("agents")]
    [InlineData("storage-connections")]
    public async Task CrossTenantIdProbing_ReturnsIndistinguishableResponseForForeignResourceAndNonexistentResource(string resourceRoute)
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var tenantA = await world.CreateTenantAsync();
        var tenantB = await world.CreateTenantAsync();
        var aId = tenantA.Tenant.Id.Value;

        var foreignAgent = await world.EnrollAsync(tenantB);
        var foreignId = foreignAgent.AgentId;
        var nonexistentId = Guid.NewGuid();

        using var foreignResponse = await tenantA.Admin.GetAsync($"/api/tenants/{aId}/{resourceRoute}/{foreignId}");
        using var nonexistentResponse = await tenantA.Admin.GetAsync($"/api/tenants/{aId}/{resourceRoute}/{nonexistentId}");

        Assert.Equal(nonexistentResponse.StatusCode, foreignResponse.StatusCode);
        Assert.True(foreignResponse.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task MembershipRemovalDuringActiveSession_ImmediatelyRevokesAccessOnNextRequest()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var setup = await world.CreateTenantAsync();
        var account = await world.SeedUserAsync();
        await world.GrantAsync(setup.Tenant, account);
        var session = await world.SignInAsync(account);
        var aId = setup.Tenant.Id.Value;

        using var initial = await session.GetAsync($"/api/tenants/{aId}/context");
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);

        await using (var context = fixture.CreatePlatformContext())
        {
            var membership = await context.TenantMemberships.SingleAsync(row => row.TenantId == setup.Tenant.Id && row.UserId == account.UserId);
            context.TenantMemberships.Remove(membership);
            await context.SaveChangesAsync();
        }

        using var revoked = await session.GetAsync($"/api/tenants/{aId}/context");
        Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
    }
}
