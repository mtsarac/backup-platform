using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using BackupPlatform.Contracts.Downloads;
using BackupPlatform.Domain.Downloads;

namespace BackupPlatform.IntegrationTests;

public sealed class DownloadAgentApiTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static async Task MakeOnlineAsync(DownloadApiScenario scenario)
    {
        using var heartbeat = await scenario.World.HeartbeatAsync(scenario.Backup.Agent);
        Assert.Equal(HttpStatusCode.OK, heartbeat.StatusCode);
        using var capability = await scenario.World.SyncCapabilitiesAsync(scenario.Backup.Agent,
            AgentWorld.CapabilitySnapshot(AgentWorld.CapabilityReport(DownloadTaskTypes.Capability)));
        Assert.Equal(HttpStatusCode.OK, capability.StatusCode);
    }

    private static async Task<JsonElement> ClaimAsync(DownloadApiScenario scenario)
    {
        using var response = await scenario.World.PollAsync(scenario.Backup.Agent);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.Single(AgentWorld.ReadJson(response).GetProperty("tasks").EnumerateArray(),
            task => task.GetProperty("type").GetString() == DownloadTaskTypes.BackupDownload).GetProperty("download").Clone();
    }

    private static async Task<HttpResponseMessage> UploadAsync(DownloadApiScenario scenario, Guid sessionId, Guid leaseToken, byte[] bytes, EnrolledAgent? agent = null)
    {
        agent ??= scenario.Backup.Agent;
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/agent/downloads/{sessionId}/content");
        request.Headers.Authorization = new AuthenticationHeaderValue("BackupAgent", $"{agent.CredentialId:N}.{agent.Secret}");
        request.Headers.Add("X-Backup-Agent-Protocol", "1.0");
        request.Headers.Add(DownloadTaskTypes.LeaseHeader, leaseToken.ToString());
        request.Content = new ByteArrayContent(bytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return await scenario.World.Anonymous.SendAsync(request);
    }

    [Fact]
    public async Task LocalPreparationRejectsWrongAgentAndLeaseThenStreamsIndependentlyVerifiedBytes()
    {
        await using var scenario = await DownloadApiScenario.CreateAsync(fixture, agentLocal: true);
        await MakeOnlineAsync(scenario);
        var created = await scenario.CreateSessionAsync();
        var sessionId = created.GetProperty("sessionId").GetGuid();
        Assert.Equal("Preparing", (await scenario.StatusAsync(sessionId)).GetProperty("state").GetString());
        var task = await ClaimAsync(scenario);
        var token = task.GetProperty("leaseToken").GetGuid();
        Assert.Equal(scenario.Accepted.ArtifactId, task.GetProperty("artifactId").GetGuid());
        Assert.Equal(scenario.ReplicaId, task.GetProperty("replicaId").GetGuid());
        var sibling = await scenario.World.EnrollAsync(scenario.Backup.Backup.Setup);
        using var wrongAgent = await UploadAsync(scenario, sessionId, token, scenario.Bytes, sibling);
        Assert.Equal(HttpStatusCode.Conflict, wrongAgent.StatusCode);
        using var wrongLease = await UploadAsync(scenario, sessionId, Guid.NewGuid(), scenario.Bytes);
        Assert.Equal(HttpStatusCode.Conflict, wrongLease.StatusCode);
        Assert.Equal("Preparing", (await scenario.StatusAsync(sessionId)).GetProperty("state").GetString());
        using var upload = await UploadAsync(scenario, sessionId, token, scenario.Bytes);
        Assert.Equal(HttpStatusCode.NoContent, upload.StatusCode);
        Assert.Equal("Ready", (await scenario.StatusAsync(sessionId)).GetProperty("state").GetString());
        using var replay = await UploadAsync(scenario, sessionId, token, scenario.Bytes);
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        using var content = await scenario.Admin.GetAsync(created.GetProperty("downloadUrl").GetString()!);
        Assert.Equal(HttpStatusCode.OK, content.StatusCode);
        Assert.Equal(scenario.Bytes, await content.Content.ReadAsByteArrayAsync());
        var completed = (await scenario.Store().GetAsync(scenario.Backup.Backup.Setup.Tenant.Id, sessionId, default))!;
        Assert.Equal(DownloadState.Completed, completed.State);
        Assert.NotNull(completed.ReleasedAt);
    }

    [Fact]
    public async Task CorruptOutboundPayloadNeverBecomesReadyAndReleasesPin()
    {
        await using var scenario = await DownloadApiScenario.CreateAsync(fixture, agentLocal: true);
        await MakeOnlineAsync(scenario);
        var created = await scenario.CreateSessionAsync();
        var sessionId = created.GetProperty("sessionId").GetGuid();
        var task = await ClaimAsync(scenario);
        var corrupt = scenario.Bytes.ToArray();
        corrupt[0] ^= 0xff;
        using var upload = await UploadAsync(scenario, sessionId, task.GetProperty("leaseToken").GetGuid(), corrupt);
        Assert.Equal(HttpStatusCode.Conflict, upload.StatusCode);
        var failed = (await scenario.Store().GetAsync(scenario.Backup.Backup.Setup.Tenant.Id, sessionId, default))!;
        Assert.Equal(DownloadState.Failed, failed.State);
        Assert.Equal(DownloadAgentFailureCodes.IntegrityMismatch, failed.FailureCode);
        Assert.NotNull(failed.ReleasedAt);
        using var content = await scenario.Admin.GetAsync(created.GetProperty("downloadUrl").GetString()!);
        Assert.Equal(HttpStatusCode.Conflict, content.StatusCode);
    }

    [Fact]
    public async Task OfflineAgentIsAnExplicitAvailabilityAndPreparationError()
    {
        await using var scenario = await DownloadApiScenario.CreateAsync(fixture, agentLocal: true);
        await MakeOnlineAsync(scenario);
        scenario.Backup.Clock.Advance(TimeSpan.FromMinutes(5));
        using var availability = await scenario.Admin.GetAsync(scenario.ArtifactPath + "/download-availability");
        var detail = AgentWorld.ReadJson(availability);
        Assert.False(detail.GetProperty("available").GetBoolean());
        Assert.Equal(DownloadAgentFailureCodes.AgentUnavailable, detail.GetProperty("reason").GetString());
        using var request = await scenario.CreateRawAsync();
        Assert.Equal(HttpStatusCode.Conflict, request.StatusCode);
        Assert.Equal(DownloadAgentFailureCodes.AgentUnavailable, AgentWorld.ReadJson(request).GetProperty("error").GetString());
    }

    [Fact]
    public async Task ClaimedLocalDownloadContinuesThroughSoftDeleteWhileNewDownloadsAreDenied()
    {
        await using var scenario = await DownloadApiScenario.CreateAsync(fixture, agentLocal: true);
        await MakeOnlineAsync(scenario);
        var created = await scenario.CreateSessionAsync();
        var sessionId = created.GetProperty("sessionId").GetGuid();
        var task = await ClaimAsync(scenario);
        await new RetentionWorld(fixture).Store().RequestDeleteAsync(scenario.Backup.Backup.Setup.Tenant.Id,
            scenario.Accepted.ArtifactId, null, scenario.Backup.Clock.GetUtcNow(), default);
        using var fresh = await scenario.CreateRawAsync();
        Assert.Equal(HttpStatusCode.Conflict, fresh.StatusCode);
        using var upload = await UploadAsync(scenario, sessionId, task.GetProperty("leaseToken").GetGuid(), scenario.Bytes);
        Assert.Equal(HttpStatusCode.NoContent, upload.StatusCode);
        using var content = await scenario.Admin.GetAsync(created.GetProperty("downloadUrl").GetString()!);
        Assert.Equal(HttpStatusCode.OK, content.StatusCode);
        Assert.Equal(scenario.Bytes, await content.Content.ReadAsByteArrayAsync());
    }
}
