using System.Net;
using BackupPlatform.Domain.Downloads;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BackupPlatform.IntegrationTests;

public sealed class RemoveArtifactAccessMigrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task UpgradePreservesSupportDownloads()
    {
        await using var scenario = await SupportScenario.CreateAsync(fixture);
        var supportId = SupportScenario.IdOf(await scenario.OpenAsync([SupportScenario.View, SupportScenario.Download]));
        var path = SupportScenario.Path(supportId, $"/artifacts/{scenario.ArtifactId}/downloads");
        var (status, created) = await scenario.PostAsync(path, new { replicaId = scenario.Source.ReplicaId });
        Assert.Equal(HttpStatusCode.OK, status);
        var downloadId = created.GetProperty("sessionId").GetGuid();
        var url = created.GetProperty("downloadUrl").GetString()!;
        await using (var context = fixture.CreatePlatformContext())
        {
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync("20261106140706_JobCredentialClaims");
            await migrator.MigrateAsync();
        }
        var session = await scenario.Source.Store().GetAsync(scenario.Tenant.Id, downloadId, default);
        Assert.Equal(DownloadState.Ready, session!.State);
        Assert.Equal(supportId, session.SupportSessionId);
        using var content = await scenario.Admin.Client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, content.StatusCode);
        Assert.Equal(scenario.Source.Bytes, await content.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task UpgradeRevokesLegacyBreakGlassSessionsWithoutDisturbingTenantDownloads()
    {
        await using var scenario = await DownloadApiScenario.CreateAsync(fixture, agentLocal: true);
        await scenario.EncryptAsync();
        await DownloadAgentApiTests.MakeOnlineAsync(scenario);
        using var prepared = await scenario.Admin.SendAsync(HttpMethod.Post, scenario.ArtifactPath + "/downloads", new { decrypt = true });
        Assert.Equal(HttpStatusCode.OK, prepared.StatusCode);
        var download = AgentWorld.ReadJson(prepared);
        var normalId = download.GetProperty("sessionId").GetGuid();
        var url = download.GetProperty("downloadUrl").GetString()!;
        using var offer = await scenario.Admin.SendAsync(HttpMethod.Post, url, new { sdp = DownloadAgentApiTests.Sdp });
        Assert.Equal(HttpStatusCode.OK, offer.StatusCode);
        var task = await DownloadAgentApiTests.ClaimAsync(scenario);
        var token = task.GetProperty("leaseToken").GetGuid();
        using var answer = await AgentAsync(normalId, "signal", new { leaseToken = token, sdp = DownloadAgentApiTests.Sdp });
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);

        var streamingId = Guid.NewGuid();
        var preparingId = Guid.NewGuid();
        var readyId = Guid.NewGuid();
        var completedId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        await using (var context = fixture.CreatePlatformContext())
        {
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync("20261106140706_JobCredentialClaims");
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO artifact_break_glass_requests ("Id", "TenantId", "ArtifactId", "RequestedBy", "Reason", "Status", "CreatedAt", "ExpiresAt")
                SELECT {requestId}, "TenantId", "ArtifactId", "UserId", 'Migration regression', 'Pending', "CreatedAt", "ExpiresAt"
                FROM download_sessions WHERE "Id" = {normalId};
                INSERT INTO download_sessions ("Id", "TenantId", "ArtifactId", "ReplicaId", "UserId", "SupportSessionId", "Decrypted", "BreakGlassRequestId", "AgentId", "StorageProvider", "StorageTarget", "Key", "Checksum", "SizeBytes", "Filename", "CreatedAt", "ExpiresAt", "State", "LeaseOwner", "LeaseExpiresAt", "TransferredBytes", "FailureCode", "ReleasedAt")
                SELECT v.id, s."TenantId", s."ArtifactId", s."ReplicaId", s."UserId", NULL, TRUE, {requestId}, s."AgentId", s."StorageProvider", s."StorageTarget", s."Key", s."Checksum", s."SizeBytes", s."Filename", s."CreatedAt", s."ExpiresAt", v.state,
                    s."LeaseOwner", s."LeaseExpiresAt", s."TransferredBytes", s."FailureCode", CASE WHEN v.state = 'Completed' THEN s."CreatedAt" ELSE NULL END
                FROM download_sessions s CROSS JOIN (VALUES ({streamingId}, 'Streaming'), ({preparingId}, 'Preparing'), ({readyId}, 'Ready'), ({completedId}, 'Completed')) AS v(id, state)
                WHERE s."Id" = {normalId}
                """);
            using var beforeKey = await AgentAsync(streamingId, "artifact-key", new { leaseToken = token });
            Assert.Equal(HttpStatusCode.OK, beforeKey.StatusCode);
            await migrator.MigrateAsync();
        }

        foreach (var id in new[] { streamingId, preparingId, readyId })
        {
            var session = await scenario.Store().GetAsync(scenario.Backup.Backup.Setup.Tenant.Id, id, default);
            Assert.NotNull(session);
            Assert.Equal(DownloadState.Cancelled, session.State);
            Assert.NotNull(session.ReleasedAt);
            Assert.Null(session.LeaseOwner);
            Assert.Null(session.LeaseExpiresAt);
            using var key = await AgentAsync(id, "artifact-key", new { leaseToken = token });
            using var renew = await AgentAsync(id, "lease", new { leaseToken = token });
            using var signal = await AgentAsync(id, "signal", new { leaseToken = token });
            using var content = await scenario.Admin.GetAsync(url.Replace(normalId.ToString(), id.ToString(), StringComparison.OrdinalIgnoreCase));
            Assert.Equal(HttpStatusCode.Conflict, key.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, renew.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, signal.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, content.StatusCode);
        }
        var completed = await scenario.Store().GetAsync(scenario.Backup.Backup.Setup.Tenant.Id, completedId, default);
        Assert.Equal(DownloadState.Completed, completed!.State);
        using var completedKey = await AgentAsync(completedId, "artifact-key", new { leaseToken = token });
        Assert.Equal(HttpStatusCode.Conflict, completedKey.StatusCode);
        var normal = await scenario.Store().GetAsync(scenario.Backup.Backup.Setup.Tenant.Id, normalId, default);
        Assert.Equal(DownloadState.Streaming, normal!.State);
        Assert.Equal(token.ToString("D"), normal.LeaseOwner);
        using var normalKey = await AgentAsync(normalId, "artifact-key", new { leaseToken = token });
        using var normalRenew = await AgentAsync(normalId, "lease", new { leaseToken = token });
        using var normalContent = await scenario.Admin.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, normalKey.StatusCode);
        Assert.Equal(HttpStatusCode.OK, normalRenew.StatusCode);
        Assert.Equal(HttpStatusCode.OK, normalContent.StatusCode);

        Task<HttpResponseMessage> AgentAsync(Guid id, string operation, object body) =>
            scenario.World.SendAgentAsync(HttpMethod.Post, $"/api/agent/downloads/{id}/{operation}", scenario.Backup.Agent.CredentialId, scenario.Backup.Agent.Secret, body: body);
    }
}
