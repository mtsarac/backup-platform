using System.Net;
using System.Text.Json;
using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Transport;
using BackupPlatform.AgentTests.Support;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Contracts.Downloads;
using Microsoft.Extensions.Time.Testing;

namespace BackupPlatform.AgentTests;

public sealed class HttpDownloadTaskClientTests
{
    [Fact]
    public async Task Peer_signaling_is_authenticated_fenced_JSON_and_never_artifact_content()
    {
        var session = Guid.NewGuid();
        var lease = Guid.NewGuid();
        using var handler = new InspectingHandler(async (request, cancellationToken) =>
        {
            Assert.Equal($"https://backup.example.test/root/api/agent/downloads/{session:D}/signal", request.RequestUri!.ToString());
            Assert.Equal(string.Empty, request.RequestUri.Query);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal($"BackupAgent {TestIdentity.CredentialId:N}.{TestIdentity.Secret}", request.Headers.GetValues("Authorization").Single());
            Assert.Equal(AgentProtocol.Current.Version.ToString(), request.Headers.GetValues(AgentProtocolHeader.Name).Single());
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
            using var json = JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
            Assert.Equal(lease, json.RootElement.GetProperty("leaseToken").GetGuid());
            Assert.Equal("peer-sdp", json.RootElement.GetProperty("sdp").GetString());
            Assert.False(json.RootElement.TryGetProperty("content", out _));
            return FakeHttpHandler.Json(HttpStatusCode.OK, """{"sdp":null}""");
        });
        using var http = new HttpClient(handler);
        var client = new HttpDownloadTaskClient(http, new AgentBackendEndpoint(new Uri("https://backup.example.test/root")), new FakeTimeProvider());
        var response = await client.SignalAsync(TestIdentity.Create(), session, new DownloadSignalRequest { LeaseToken = lease, Sdp = "peer-sdp" }, default);
        Assert.True(response.IsSuccess);
    }

    [Fact]
    public async Task Start_renew_and_failure_use_fenced_control_routes_and_current_rotated_credentials()
    {
        using var handler = new FakeHttpHandler();
        handler.Respond = (request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/complete", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.NoContent)
            : FakeHttpHandler.Json(HttpStatusCode.OK, """{"leaseExpiresAt":"2026-10-08T00:01:00Z","cancellationRequested":false}"""));
        using var http = new HttpClient(handler);
        var client = new HttpDownloadTaskClient(http, new AgentBackendEndpoint(new Uri("https://backup.example.test/")), new FakeTimeProvider());
        var session = Guid.NewGuid();
        var lease = Guid.NewGuid();
        Assert.True((await client.StartAsync(TestIdentity.Create(), session, new BackupTaskStartRequest { LeaseToken = lease }, default)).IsSuccess);
        Assert.True((await client.RenewAsync(TestIdentity.Create(), session, new BackupTaskLeaseRequest { LeaseToken = lease }, default)).IsSuccess);
        var rotated = TestIdentity.Create(TestIdentity.RotatedSecret, Guid.NewGuid());
        Assert.True((await client.CompleteAsync(rotated, session, new DownloadTaskCompletionRequest
        {
            LeaseToken = lease, Status = "Failed", FailureCode = DownloadAgentFailureCodes.SourceMissing
        }, default)).IsSuccess);
        Assert.Equal(new[] { "start", "lease", "complete" }, handler.Requests.Select(request => request.Uri.Segments.Last()));
        Assert.Equal(rotated.Current.ToAuthorizationHeaderValue(), handler.Requests[2].Headers["Authorization"]);
        Assert.All(handler.Requests, request =>
        {
            using var json = JsonDocument.Parse(request.Body!);
            Assert.Equal(lease, json.RootElement.GetProperty("leaseToken").GetGuid());
            Assert.False(json.RootElement.TryGetProperty("storageKey", out _));
            Assert.False(json.RootElement.TryGetProperty("tenantId", out _));
            Assert.DoesNotContain(TestIdentity.Secret, request.Body!, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, AgentBackendFailureKind.Rejected)]
    [InlineData(HttpStatusCode.Unauthorized, AgentBackendFailureKind.Unauthorized)]
    [InlineData(HttpStatusCode.ServiceUnavailable, AgentBackendFailureKind.Transient)]
    public async Task Signaling_failure_preserves_transport_classification(HttpStatusCode status, AgentBackendFailureKind expected)
    {
        using var handler = new InspectingHandler((_, _) => Task.FromResult(new HttpResponseMessage(status)));
        using var http = new HttpClient(handler);
        var client = new HttpDownloadTaskClient(http, new AgentBackendEndpoint(new Uri("https://backup.example.test/")), new FakeTimeProvider());
        var response = await client.SignalAsync(TestIdentity.Create(), Guid.NewGuid(),
            new DownloadSignalRequest { LeaseToken = Guid.NewGuid() }, default);
        Assert.NotNull(response.Failure);
        Assert.Equal(expected, response.Failure.Kind);
    }

    private sealed class InspectingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handle(request, cancellationToken);
    }
}
