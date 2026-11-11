using System.Net;
using System.Text.Json;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.IntegrationTests;

public sealed class BackupShareApiIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static async Task<(BackupApiScenario Scenario, Guid ArtifactId)> SetupScenarioWithArtifactAsync(PostgreSqlFixture fixture)
    {
        var scenario = await BackupApiScenario.CreateAsync(fixture);
        var job = await scenario.CreateJobAsync();
        var run = await scenario.StartRunAsync(job);
        var claim = await scenario.ClaimAsync();
        await scenario.StartAttemptAsync(claim);

        using var completed = await scenario.CompleteRawAsync(claim, scenario.SuccessRequest(claim));
        Assert.Equal(HttpStatusCode.NoContent, completed.StatusCode);

        await using var context = fixture.CreatePlatformContext();
        var artifact = await context.BackupArtifacts
            .Include(a => a.Replicas)
            .SingleAsync(a => a.TenantId == scenario.Setup.Tenant.Id && a.AttemptId == claim.AttemptId);

        return (scenario, artifact.Id);
    }

    [Fact]
    public async Task CreateShare_DefaultDuration24Hours_AndAuditRecorded()
    {
        var (scenario, artifactId) = await SetupScenarioWithArtifactAsync(fixture);
        await using (scenario)
        {
            var requestBody = new { reason = "Investigate production anomaly" };
            using var response = await scenario.Admin.SendAsync(
                HttpMethod.Post,
                $"{scenario.Root}/backup-artifacts/{artifactId}/shares",
                requestBody);

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var share = AgentWorld.ReadJson(response);

            var shareId = share.GetProperty("id").GetGuid();
            Assert.NotEqual(Guid.Empty, shareId);
            Assert.Equal(scenario.Setup.Tenant.Id.Value, share.GetProperty("tenantId").GetGuid());
            Assert.Equal(artifactId, share.GetProperty("artifactId").GetGuid());
            Assert.Equal(scenario.Setup.Account.UserId.Value, share.GetProperty("createdByUserId").GetGuid());
            Assert.Equal("UserAdmin", share.GetProperty("createdByRole").GetString());
            Assert.Equal("Investigate production anomaly", share.GetProperty("reason").GetString());
            Assert.Equal("Active", share.GetProperty("status").GetString());

            var createdAt = share.GetProperty("createdAtUtc").GetDateTimeOffset();
            var expiresAt = share.GetProperty("expiresAtUtc").GetDateTimeOffset();
            Assert.Equal(TimeSpan.FromHours(24), expiresAt - createdAt);

            // Verify audit event
            await using var context = fixture.CreatePlatformContext();
            var audit = await context.AuditEvents
                .AsNoTracking()
                .SingleOrDefaultAsync(e => e.Action == AuditActions.BackupShareCreated && e.TargetId == shareId.ToString("D"));

            Assert.NotNull(audit);
            Assert.Equal(scenario.Setup.Tenant.Id, audit.TenantId);
            Assert.Equal(scenario.Setup.Account.UserId, audit.ActorUserId);
            Assert.Equal(AuditActorKind.TenantAdministrator, audit.ActorKind);

            using var details = JsonDocument.Parse(audit.DetailsJson);
            Assert.Equal(scenario.Setup.Account.UserId.ToString(), details.RootElement.GetProperty("actor").GetString());
            Assert.Equal("UserAdmin", details.RootElement.GetProperty("role").GetString());
            Assert.Equal(scenario.Setup.Tenant.Id.Value.ToString("D"), details.RootElement.GetProperty("tenant").GetString());
            Assert.Equal(artifactId.ToString("D"), details.RootElement.GetProperty("artifact").GetString());
            Assert.Equal("Investigate production anomaly", details.RootElement.GetProperty("reason").GetString());
            Assert.Equal(expiresAt.ToString("o"), details.RootElement.GetProperty("expiry").GetString());
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(73)]
    [InlineData(100)]
    public async Task CreateShare_RejectsInvalidDuration(int hours)
    {
        var (scenario, artifactId) = await SetupScenarioWithArtifactAsync(fixture);
        await using (scenario)
        {
            var requestBody = new { durationHours = hours };
            using var response = await scenario.Admin.SendAsync(
                HttpMethod.Post,
                $"{scenario.Root}/backup-artifacts/{artifactId}/shares",
                requestBody);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var body = AgentWorld.ReadJson(response);
            Assert.Equal("invalid_duration", body.GetProperty("error").GetString());
        }
    }

    [Fact]
    public async Task CreateShare_PreconditionCheck_RejectsWhenNoDownloadableReplica()
    {
        var (scenario, artifactId) = await SetupScenarioWithArtifactAsync(fixture);
        await using (scenario)
        {
            // Delete replica to invalidate precondition
            await using (var context = fixture.CreatePlatformContext())
            {
                var replica = await context.BackupReplicas.SingleAsync(r => r.ArtifactId == artifactId);
                replica.RequestDelete(scenario.Clock.GetUtcNow(), BackupDeleteReason.RetentionExpired, TimeSpan.Zero, null);
                await context.SaveChangesAsync();
            }

            using var response = await scenario.Admin.SendAsync(
                HttpMethod.Post,
                $"{scenario.Root}/backup-artifacts/{artifactId}/shares",
                new { durationHours = 48 });

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            var body = AgentWorld.ReadJson(response);
            Assert.Equal("no_downloadable_replica", body.GetProperty("error").GetString());
        }
    }

    [Fact]
    public async Task TenantIsolation_ForeignArtifactAndShareReturnNotFound()
    {
        var (scenarioA, artifactA) = await SetupScenarioWithArtifactAsync(fixture);
        await using (scenarioA)
        {
            var tenantB = await scenarioA.World.CreateTenantAsync();

            // Tenant B probes Tenant A's artifact -> 404
            using var foreignArtifactProbe = await tenantB.Admin.SendAsync(
                HttpMethod.Post,
                $"{BackupApiScenario.RootOf(tenantB)}/backup-artifacts/{artifactA}/shares",
                new { durationHours = 24 });
            Assert.Equal(HttpStatusCode.NotFound, foreignArtifactProbe.StatusCode);

            // Tenant A creates a share
            using var createShareResponse = await scenarioA.Admin.SendAsync(
                HttpMethod.Post,
                $"{scenarioA.Root}/backup-artifacts/{artifactA}/shares",
                new { durationHours = 24 });
            Assert.Equal(HttpStatusCode.Created, createShareResponse.StatusCode);
            var shareId = AgentWorld.ReadJson(createShareResponse).GetProperty("id").GetGuid();

            // Tenant B probes Tenant A's share id -> 404
            using var foreignShareProbe = await tenantB.Admin.GetAsync(
                $"{BackupApiScenario.RootOf(tenantB)}/backup-shares/{shareId}");
            Assert.Equal(HttpStatusCode.NotFound, foreignShareProbe.StatusCode);

            // Nonexistent share in Tenant B also returns 404 (indistinguishable)
            using var missingShareProbe = await tenantB.Admin.GetAsync(
                $"{BackupApiScenario.RootOf(tenantB)}/backup-shares/{Guid.NewGuid()}");
            Assert.Equal(HttpStatusCode.NotFound, missingShareProbe.StatusCode);

            // Non-member access to Tenant A's routes -> 403 Forbidden
            var nonMember = await scenarioA.SessionAsync(member: false);
            using var forbiddenList = await nonMember.GetAsync($"{scenarioA.Root}/backup-shares");
            Assert.Equal(HttpStatusCode.Forbidden, forbiddenList.StatusCode);
        }
    }

    [Fact]
    public async Task MultipleSharesPerArtifact_CoexistAndListCorrectly()
    {
        var (scenario, artifactId) = await SetupScenarioWithArtifactAsync(fixture);
        await using (scenario)
        {
            // Create share 1 (12 hours)
            using var response1 = await scenario.Admin.SendAsync(
                HttpMethod.Post,
                $"{scenario.Root}/backup-artifacts/{artifactId}/shares",
                new { durationHours = 12, reason = "Share 1" });
            Assert.Equal(HttpStatusCode.Created, response1.StatusCode);
            var shareId1 = AgentWorld.ReadJson(response1).GetProperty("id").GetGuid();

            // Create share 2 (48 hours)
            using var response2 = await scenario.Admin.SendAsync(
                HttpMethod.Post,
                $"{scenario.Root}/backup-artifacts/{artifactId}/shares",
                new { durationHours = 48, reason = "Share 2" });
            Assert.Equal(HttpStatusCode.Created, response2.StatusCode);
            var shareId2 = AgentWorld.ReadJson(response2).GetProperty("id").GetGuid();

            Assert.NotEqual(shareId1, shareId2);

            // List shares for artifact
            using var artifactSharesResponse = await scenario.Admin.GetAsync(
                $"{scenario.Root}/backup-artifacts/{artifactId}/shares");
            Assert.Equal(HttpStatusCode.OK, artifactSharesResponse.StatusCode);
            var artifactSharesDoc = AgentWorld.ReadJson(artifactSharesResponse);
            var listForArtifact = artifactSharesDoc.EnumerateArray().ToList();
            Assert.Equal(2, listForArtifact.Count);

            // List shares for tenant
            using var tenantSharesResponse = await scenario.Admin.GetAsync(
                $"{scenario.Root}/backup-shares");
            Assert.Equal(HttpStatusCode.OK, tenantSharesResponse.StatusCode);
            var tenantSharesDoc = AgentWorld.ReadJson(tenantSharesResponse);
            var listForTenant = tenantSharesDoc.EnumerateArray().ToList();
            Assert.True(listForTenant.Count >= 2);

            // Get share detail
            using var detailResponse = await scenario.Admin.GetAsync(
                $"{scenario.Root}/backup-shares/{shareId1}");
            Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
            var detailDoc = AgentWorld.ReadJson(detailResponse);
            Assert.Equal(shareId1, detailDoc.GetProperty("id").GetGuid());
            Assert.Equal("Share 1", detailDoc.GetProperty("reason").GetString());
            Assert.Equal("Active", detailDoc.GetProperty("status").GetString());
        }
    }

    [Fact]
    public async Task PlatformAdmin_CanCreateShareAcrossTenants_WithAuditMarking_AndVisibleToTenantUserAdmin()
    {
        var (scenario, artifactId) = await SetupScenarioWithArtifactAsync(fixture);
        await using (scenario)
        {
            var platformAccount = await scenario.World.SeedUserAsync(platformAdmin: true);
            var platformSession = await scenario.World.SignInAsync(platformAccount);

            // 1. Cross-tenant list API denied to UserAdmin (403)
            using var userAdminListAttempt = await scenario.Admin.GetAsync("/api/platform/backup-shares");
            Assert.Equal(HttpStatusCode.Forbidden, userAdminListAttempt.StatusCode);

            // 2. UserAdmin cannot call platform share creation (403)
            using var userAdminCreateAttempt = await scenario.Admin.SendAsync(
                HttpMethod.Post,
                "/api/platform/backup-shares",
                new
                {
                    tenantId = scenario.Setup.Tenant.Id.Value,
                    artifactId,
                    durationHours = 24,
                    reason = "Unauthorized creation attempt"
                });
            Assert.Equal(HttpStatusCode.Forbidden, userAdminCreateAttempt.StatusCode);

            // 3. PlatformAdmin creates share for arbitrary tenant without membership
            var createRequest = new
            {
                tenantId = scenario.Setup.Tenant.Id.Value,
                artifactId,
                durationHours = 24,
                reason = "Platform investigation"
            };
            using var createResponse = await platformSession.SendAsync(
                HttpMethod.Post,
                "/api/platform/backup-shares",
                createRequest);

            Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
            var createdShare = AgentWorld.ReadJson(createResponse);
            var shareId = createdShare.GetProperty("id").GetGuid();
            Assert.NotEqual(Guid.Empty, shareId);
            Assert.Equal(scenario.Setup.Tenant.Id.Value, createdShare.GetProperty("tenantId").GetGuid());
            Assert.Equal(artifactId, createdShare.GetProperty("artifactId").GetGuid());
            Assert.Equal(platformAccount.UserId.Value, createdShare.GetProperty("createdByUserId").GetGuid());
            Assert.Equal("PlatformAdmin", createdShare.GetProperty("createdByRole").GetString());
            Assert.Equal("Active", createdShare.GetProperty("status").GetString());
            Assert.Equal("Platform investigation", createdShare.GetProperty("reason").GetString());

            // 4. Distinct audit marking recorded
            await using var context = fixture.CreatePlatformContext();
            var audit = await context.AuditEvents
                .AsNoTracking()
                .SingleOrDefaultAsync(e => e.Action == AuditActions.BackupShareCreated && e.TargetId == shareId.ToString("D"));

            Assert.NotNull(audit);
            Assert.Equal(AuditActorKind.PlatformAdmin, audit.ActorKind);
            Assert.Equal(platformAccount.UserId, audit.ActorUserId);
            Assert.Equal(scenario.Setup.Tenant.Id, audit.TenantId);

            using var details = JsonDocument.Parse(audit.DetailsJson);
            Assert.Equal("PlatformAdmin", details.RootElement.GetProperty("role").GetString());
            Assert.Equal("PlatformAdmin", details.RootElement.GetProperty("actorRole").GetString());
            Assert.Equal(platformAccount.UserId.ToString(), details.RootElement.GetProperty("actor").GetString());
            Assert.Equal(scenario.Setup.Tenant.Id.Value.ToString("D"), details.RootElement.GetProperty("tenant").GetString());

            // 5. Cross-tenant list API for PlatformAdmin with tenant and status filters
            using var listAllResponse = await platformSession.GetAsync("/api/platform/backup-shares");
            Assert.Equal(HttpStatusCode.OK, listAllResponse.StatusCode);
            var allShares = AgentWorld.ReadJson(listAllResponse).EnumerateArray().ToList();
            Assert.Contains(allShares, s => s.GetProperty("id").GetGuid() == shareId);

            // Filter by tenantId
            using var listTenantResponse = await platformSession.GetAsync($"/api/platform/backup-shares?tenantId={scenario.Setup.Tenant.Id.Value}");
            Assert.Equal(HttpStatusCode.OK, listTenantResponse.StatusCode);
            var tenantShares = AgentWorld.ReadJson(listTenantResponse).EnumerateArray().ToList();
            Assert.All(tenantShares, s => Assert.Equal(scenario.Setup.Tenant.Id.Value, s.GetProperty("tenantId").GetGuid()));
            Assert.Contains(tenantShares, s => s.GetProperty("id").GetGuid() == shareId);

            // Filter by non-existent tenant returns empty
            using var emptyTenantResponse = await platformSession.GetAsync($"/api/platform/backup-shares?tenantId={Guid.NewGuid()}");
            Assert.Equal(HttpStatusCode.OK, emptyTenantResponse.StatusCode);
            Assert.Empty(AgentWorld.ReadJson(emptyTenantResponse).EnumerateArray());

            // Filter by status=Active
            using var listActiveResponse = await platformSession.GetAsync("/api/platform/backup-shares?status=Active");
            Assert.Equal(HttpStatusCode.OK, listActiveResponse.StatusCode);
            var activeShares = AgentWorld.ReadJson(listActiveResponse).EnumerateArray().ToList();
            Assert.Contains(activeShares, s => s.GetProperty("id").GetGuid() == shareId);

            // Filter by status=Expired does not contain our active share
            using var listExpiredResponse = await platformSession.GetAsync("/api/platform/backup-shares?status=Expired");
            Assert.Equal(HttpStatusCode.OK, listExpiredResponse.StatusCode);
            var expiredShares = AgentWorld.ReadJson(listExpiredResponse).EnumerateArray().ToList();
            Assert.DoesNotContain(expiredShares, s => s.GetProperty("id").GetGuid() == shareId);

            // Detail endpoint for PlatformAdmin
            using var platformDetailResponse = await platformSession.GetAsync($"/api/platform/backup-shares/{shareId}");
            Assert.Equal(HttpStatusCode.OK, platformDetailResponse.StatusCode);
            var platformDetail = AgentWorld.ReadJson(platformDetailResponse);
            Assert.Equal(shareId, platformDetail.GetProperty("id").GetGuid());
            Assert.Equal("PlatformAdmin", platformDetail.GetProperty("createdByRole").GetString());

            // 6. Tenant UserAdmin sees PlatformAdmin-created share in Paylaşımlarım
            using var tenantUserAdminListResponse = await scenario.Admin.GetAsync($"{scenario.Root}/backup-shares");
            Assert.Equal(HttpStatusCode.OK, tenantUserAdminListResponse.StatusCode);
            var visibleShares = AgentWorld.ReadJson(tenantUserAdminListResponse).EnumerateArray().ToList();
            var platformCreatedInTenantView = Assert.Single(visibleShares, s => s.GetProperty("id").GetGuid() == shareId);
            Assert.Equal("PlatformAdmin", platformCreatedInTenantView.GetProperty("createdByRole").GetString());
            Assert.Equal(platformAccount.UserId.Value, platformCreatedInTenantView.GetProperty("createdByUserId").GetGuid());
        }
    }
}
