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
}
