using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using BackupPlatform.Contracts.Downloads;
using BackupPlatform.Domain.Downloads;

namespace BackupPlatform.IntegrationTests;

public sealed class DownloadAgentApiTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    internal const string Sdp = "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\nm=application 9 UDP/DTLS/SCTP webrtc-datachannel\r\na=fingerprint:sha-256 AA:BB\r\n";

    internal static async Task MakeOnlineAsync(DownloadApiScenario scenario)
    {
        using var heartbeat = await scenario.World.HeartbeatAsync(scenario.Backup.Agent);
        Assert.Equal(HttpStatusCode.OK, heartbeat.StatusCode);
        using var capability = await scenario.World.SyncCapabilitiesAsync(scenario.Backup.Agent,
            AgentWorld.CapabilitySnapshot(AgentWorld.CapabilityReport(DownloadTaskTypes.Capability)));
        Assert.Equal(HttpStatusCode.OK, capability.StatusCode);
    }

    internal static async Task<JsonElement> ClaimAsync(DownloadApiScenario scenario)
    {
        using var response = await scenario.World.PollAsync(scenario.Backup.Agent);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.Single(AgentWorld.ReadJson(response).GetProperty("tasks").EnumerateArray(),
            task => task.GetProperty("type").GetString() == DownloadTaskTypes.BackupDownload).GetProperty("download").Clone();
    }

    private static Task<HttpResponseMessage> AgentAsync(DownloadApiScenario scenario, Guid sessionId, string operation, object body, EnrolledAgent? caller = null)
    {
        var agent = caller ?? scenario.Backup.Agent;
        return scenario.World.SendAgentAsync(HttpMethod.Post, $"/api/agent/downloads/{sessionId}/{operation}", agent.CredentialId, agent.Secret, body: body);
    }

    [Fact]
    public async Task DirectSessionUsesSingleBrowserOfferFencedAgentAnswerAndMetadataCompletionWithoutBackendPayload()
    {
        await using var scenario = await DownloadApiScenario.CreateAsync(fixture, agentLocal: true);
        await MakeOnlineAsync(scenario);
        var created = await scenario.CreateSessionAsync();
        Assert.True(created.GetProperty("direct").GetBoolean());
        var id = created.GetProperty("sessionId").GetGuid();
        var url = created.GetProperty("downloadUrl").GetString()!;
        using var offer = await scenario.Admin.SendAsync(HttpMethod.Post, url, new { sdp = Sdp });
        Assert.Equal(HttpStatusCode.OK, offer.StatusCode);
        using var replayOffer = await scenario.Admin.SendAsync(HttpMethod.Post, url, new { sdp = Sdp });
        Assert.Equal(HttpStatusCode.Conflict, replayOffer.StatusCode);
        var task = await ClaimAsync(scenario);
        var token = task.GetProperty("leaseToken").GetGuid();
        var sibling = await scenario.World.EnrollAsync(scenario.Backup.Backup.Setup);
        using var wrongAgent = await AgentAsync(scenario, id, "signal", new { leaseToken = token }, sibling);
        Assert.Equal(HttpStatusCode.Conflict, wrongAgent.StatusCode);
        using var wrongLease = await AgentAsync(scenario, id, "signal", new { leaseToken = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Conflict, wrongLease.StatusCode);
        using var fetched = await AgentAsync(scenario, id, "signal", new { leaseToken = token });
        Assert.Equal(Sdp, AgentWorld.ReadJson(fetched).GetProperty("sdp").GetString());
        using var answer = await AgentAsync(scenario, id, "signal", new { leaseToken = token, sdp = Sdp });
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        using var browser = await scenario.Admin.GetAsync(url);
        Assert.Equal("application/json", browser.Content.Headers.ContentType!.MediaType);
        Assert.Equal(Sdp, AgentWorld.ReadJson(browser).GetProperty("sdp").GetString());
        using var replayAnswer = await AgentAsync(scenario, id, "signal", new { leaseToken = token, sdp = Sdp });
        Assert.Equal(HttpStatusCode.Conflict, replayAnswer.StatusCode);
        using var badHash = await AgentAsync(scenario, id, "complete", new { leaseToken = token, status = "Completed", transferredBytes = scenario.Bytes.Length, checksum = new string('a', 64) });
        Assert.Equal(HttpStatusCode.Conflict, badHash.StatusCode);
        using var completed = await AgentAsync(scenario, id, "complete", new { leaseToken = token, status = "Completed", transferredBytes = scenario.Bytes.Length, checksum = scenario.Checksum });
        Assert.Equal(HttpStatusCode.NoContent, completed.StatusCode);
        Assert.Equal(DownloadState.Completed, (await scenario.Store().GetAsync(scenario.Backup.Backup.Setup.Tenant.Id, id, default))!.State);
        using var replay = await scenario.Admin.GetAsync(url);
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        using var upload = new HttpRequestMessage(HttpMethod.Post, $"/api/agent/downloads/{id}/content");
        upload.Headers.Authorization = new AuthenticationHeaderValue("BackupAgent", $"{scenario.Backup.Agent.CredentialId:N}.{scenario.Backup.Agent.Secret}");
        upload.Headers.Add("X-Backup-Agent-Protocol", "1.0");
        upload.Content = new ByteArrayContent(scenario.Bytes);
        using var refused = await scenario.World.Anonymous.SendAsync(upload);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(DownloadAgentFailureCodes.DirectPathUnavailable, AgentWorld.ReadJson(refused).GetProperty("error").GetString());
    }

    [Fact]
    public async Task RawSessionCannotUnwrapKeysAndExpiredSessionCannotSignal()
    {
        await using var scenario = await DownloadApiScenario.CreateAsync(fixture, agentLocal: true);
        await MakeOnlineAsync(scenario);
        var created = await scenario.CreateSessionAsync();
        var id = created.GetProperty("sessionId").GetGuid();
        var url = created.GetProperty("downloadUrl").GetString()!;
        using var offer = await scenario.Admin.SendAsync(HttpMethod.Post, url, new { sdp = Sdp });
        Assert.Equal(HttpStatusCode.OK, offer.StatusCode);
        var task = await ClaimAsync(scenario);
        var token = task.GetProperty("leaseToken").GetGuid();
        using var key = await AgentAsync(scenario, id, "artifact-key", new { leaseToken = token });
        Assert.Equal(HttpStatusCode.Conflict, key.StatusCode);
        scenario.Backup.Clock.Advance(TimeSpan.FromMinutes(16));
        using var expired = await AgentAsync(scenario, id, "signal", new { leaseToken = token, sdp = Sdp });
        Assert.Equal(HttpStatusCode.Conflict, expired.StatusCode);
        using var browser = await scenario.Admin.GetAsync(url);
        Assert.Equal(HttpStatusCode.Conflict, browser.StatusCode);
    }

    [Theory]
    [InlineData("not SDP")]
    [InlineData(Sdp + "a=candidate:1 1 UDP 1 127.0.0.1 10000 typ relay\r\n")]
    public async Task InvalidOrRelayedSignalingNeverBecomesClaimable(string sdp)
    {
        await using var scenario = await DownloadApiScenario.CreateAsync(fixture, agentLocal: true);
        await MakeOnlineAsync(scenario);
        var created = await scenario.CreateSessionAsync();
        using var offer = await scenario.Admin.SendAsync(HttpMethod.Post, created.GetProperty("downloadUrl").GetString()!, new { sdp });
        Assert.Equal(HttpStatusCode.BadRequest, offer.StatusCode);
        using var poll = await scenario.World.PollAsync(scenario.Backup.Agent);
        Assert.DoesNotContain(AgentWorld.ReadJson(poll).GetProperty("tasks").EnumerateArray(), item => item.GetProperty("type").GetString() == DownloadTaskTypes.BackupDownload);
    }

    [Fact]
    public async Task OfflineAgentIsAnExplicitDirectPathUnavailableError()
    {
        await using var scenario = await DownloadApiScenario.CreateAsync(fixture, agentLocal: true);
        await MakeOnlineAsync(scenario);
        scenario.Backup.Clock.Advance(TimeSpan.FromMinutes(5));
        using var response = await scenario.CreateRawAsync();
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(DownloadAgentFailureCodes.DirectPathUnavailable, AgentWorld.ReadJson(response).GetProperty("error").GetString());
    }
}
