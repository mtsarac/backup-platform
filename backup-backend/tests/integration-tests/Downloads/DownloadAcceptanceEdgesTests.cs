using System.Net;
using System.Text.Json;
using BackupPlatform.Api.Downloads;
using BackupPlatform.Domain.Downloads;
using BackupPlatform.Infrastructure.Persistence;
using BackupPlatform.IntegrationTests.Quota;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BackupPlatform.IntegrationTests;

public sealed class DownloadAcceptanceEdgesTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task OverQuotaAllowsExistingDownloadWithoutCreatingArtifactsOrChangingUsage()
    {
        await using var scenario = await DownloadApiScenario.CreateAsync(fixture);
        var quota = new QuotaWorld(fixture);
        var tenant = scenario.Backup.Backup.Setup.Tenant.Id;
        await quota.AssignAsync(tenant, await quota.PlanAsync(1));
        using var beforeResponse = await scenario.Admin.GetAsync(scenario.Root + "/storage-quota");
        var before = AgentWorld.ReadJson(beforeResponse);
        Assert.Equal("OverQuota", before.GetProperty("state").GetString());
        Assert.False(before.GetProperty("newBackupsAllowed").GetBoolean());
        var sourceBefore = await quota.SourceUsageAsync(tenant);
        await using var context = fixture.CreatePlatformContext();
        var artifactsBefore = await context.BackupArtifacts.CountAsync(artifact => artifact.TenantId == tenant);
        var session = await scenario.CreateSessionAsync();
        using var response = await scenario.Admin.GetAsync(session.GetProperty("downloadUrl").GetString()!);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(scenario.Bytes, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(sourceBefore, await quota.SourceUsageAsync(tenant));
        Assert.Equal(artifactsBefore, await context.BackupArtifacts.CountAsync(artifact => artifact.TenantId == tenant));
        using var afterResponse = await scenario.Admin.GetAsync(scenario.Root + "/storage-quota");
        var after = AgentWorld.ReadJson(afterResponse);
        Assert.Equal(before.GetProperty("usedBytes").GetInt64(), after.GetProperty("usedBytes").GetInt64());
        Assert.Equal(before.GetProperty("reservedBytes").GetInt64(), after.GetProperty("reservedBytes").GetInt64());
    }

    [Fact]
    public async Task MissingPhysicalReplicaCannotAuthorizeNewDownload()
    {
        await using var scenario = await DownloadApiScenario.CreateAsync(fixture);
        File.Delete(scenario.FilePath);
        using var availability = await scenario.Admin.GetAsync(scenario.ArtifactPath + "/download-availability");
        Assert.False(AgentWorld.ReadJson(availability).GetProperty("available").GetBoolean());
        using var request = await scenario.CreateRawAsync();
        Assert.Equal(HttpStatusCode.Conflict, request.StatusCode);
    }

    [Fact]
    public async Task VerificationFailedReplicaCannotAuthorizeNewDownload()
    {
        await using var scenario = await DownloadApiScenario.CreateAsync(fixture);
        await using var context = fixture.CreatePlatformContext();
        await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE backup_replicas SET \"VerificationState\" = 'Failed', \"VerifiedAt\" = NULL WHERE \"Id\" = {scenario.ReplicaId}");
        using var request = await scenario.CreateRawAsync();
        Assert.Equal(HttpStatusCode.Conflict, request.StatusCode);
        using var availability = await scenario.Admin.GetAsync(scenario.ArtifactPath + "/download-availability");
        Assert.False(AgentWorld.ReadJson(availability).GetProperty("available").GetBoolean());
    }

    [Fact]
    public async Task DeletedReplicaRemainsHistoryButCannotAuthorizeDownload()
    {
        await using var scenario = await DownloadApiScenario.CreateAsync(fixture);
        var retention = new RetentionWorld(fixture).Store();
        var now = scenario.Backup.Clock.GetUtcNow();
        await retention.RequestDeleteAsync(scenario.Backup.Backup.Setup.Tenant.Id, scenario.Accepted.ArtifactId, null, now, default);
        var deletion = Assert.Single(await retention.ClaimDueDeletionsAsync("download-edge", now.AddDays(2), TimeSpan.FromMinutes(2), 100, default), claim => claim.ReplicaId == scenario.ReplicaId);
        File.Delete(scenario.FilePath);
        Assert.True(await retention.CompleteDeletionAsync(deletion.ReplicaId, deletion.LeaseToken, now.AddDays(2), default));
        using var history = await scenario.Admin.GetAsync(scenario.ArtifactPath);
        Assert.Equal("Deleted", AgentWorld.ReadJson(history).GetProperty("state").GetString());
        using var request = await scenario.CreateRawAsync();
        Assert.Equal(HttpStatusCode.Conflict, request.StatusCode);
    }

    [Fact]
    public async Task ConcurrentSessionsDownloadSameReplicaWithIndependentCompletedAudits()
    {
        await using var scenario = await DownloadApiScenario.CreateAsync(fixture);
        var first = await scenario.CreateSessionAsync();
        var second = await scenario.CreateSessionAsync();
        Assert.NotEqual(first.GetProperty("sessionId").GetGuid(), second.GetProperty("sessionId").GetGuid());
        async Task DownloadAsync(JsonElement session)
        {
            using var response = await scenario.Admin.GetAsync(session.GetProperty("downloadUrl").GetString()!);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(scenario.Bytes, await response.Content.ReadAsByteArrayAsync());
            var persisted = (await scenario.Store().GetAsync(scenario.Backup.Backup.Setup.Tenant.Id, session.GetProperty("sessionId").GetGuid(), default))!;
            Assert.Equal(DownloadState.Completed, persisted.State);
            Assert.Equal(scenario.Bytes.Length, persisted.TransferredBytes);
            Assert.NotNull(persisted.ReleasedAt);
        }
        await Task.WhenAll(DownloadAsync(first), DownloadAsync(second));
    }

    [Fact]
    public async Task ClientDisconnectBeforeFinalFlushRecordsCancellationAndReleasesPin()
    {
        await using var scenario = await DownloadApiScenario.CreateAsync(fixture);
        var prepared = await scenario.CreateSessionAsync();
        var id = prepared.GetProperty("sessionId").GetGuid();
        var session = (await scenario.Store().GetAsync(scenario.Backup.Backup.Setup.Tenant.Id, id, default))!;
        using var disconnected = new CancellationTokenSource();
        using var body = new DisconnectOnFlushStream(disconnected);
        var http = new DefaultHttpContext { RequestAborted = disconnected.Token };
        http.Response.Body = body;
        using var scope = scenario.World.Factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<DownloadStreamer>().StreamAsync(http, session);
        var persisted = (await scenario.Store().GetAsync(session.TenantId, id, default))!;
        Assert.Equal(DownloadState.Cancelled, persisted.State);
        Assert.Equal("download_cancelled", persisted.FailureCode);
        Assert.Equal(scenario.Bytes.Length, persisted.TransferredBytes);
        Assert.NotNull(persisted.ReleasedAt);
        Assert.False(persisted.IsPinned(scenario.Backup.Clock.GetUtcNow()));
        await using var context = fixture.CreatePlatformContext();
        var audit = await context.AuditEvents.SingleAsync(entry => entry.TargetId == id.ToString() && entry.Action == "download.cancelled");
        using var details = JsonDocument.Parse(audit.DetailsJson);
        Assert.Equal(scenario.Bytes.Length, details.RootElement.GetProperty("transferredBytes").GetInt64());
        Assert.Equal("Cancelled", details.RootElement.GetProperty("result").GetString());
    }

    private sealed class DisconnectOnFlushStream(CancellationTokenSource disconnected) : MemoryStream
    {
        private bool _disconnected;
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            if (_disconnected) return Task.CompletedTask;
            _disconnected = true;
            disconnected.Cancel();
            return Task.FromCanceled(disconnected.Token);
        }
    }
}
