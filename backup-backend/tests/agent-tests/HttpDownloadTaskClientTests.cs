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
    public async Task Upload_streams_outbound_to_same_backend_with_authenticated_protocol_and_header_fence_not_browser_credentials_or_query_token()
    {
        byte[] bytes = [0, 255, 3, 8, 1];
        var session = Guid.NewGuid();
        var lease = Guid.NewGuid();
        using var handler = new InspectingHandler(async (request, cancellationToken) =>
        {
            Assert.Equal($"https://backup.example.test/root/api/agent/downloads/{session:D}/content", request.RequestUri!.ToString());
            Assert.Equal(string.Empty, request.RequestUri.Query);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal($"BackupAgent {TestIdentity.CredentialId:N}.{TestIdentity.Secret}", request.Headers.GetValues("Authorization").Single());
            Assert.Equal(AgentProtocol.Current.Version.ToString(), request.Headers.GetValues(AgentProtocolHeader.Name).Single());
            Assert.IsType<StreamContent>(request.Content);
            Assert.Equal(lease.ToString("D"), request.Content!.Headers.GetValues(DownloadTaskTypes.LeaseHeader).Single());
            Assert.Equal(bytes.LongLength, request.Content.Headers.ContentLength);
            Assert.Equal("application/octet-stream", request.Content.Headers.ContentType!.MediaType);
            Assert.Equal(bytes, await request.Content.ReadAsByteArrayAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        using var http = new HttpClient(handler);
        var client = new HttpDownloadTaskClient(http, new AgentBackendEndpoint(new Uri("https://backup.example.test/root")), new FakeTimeProvider());
        await using var content = new MemoryStream(bytes, writable: false);
        var response = await client.UploadAsync(TestIdentity.Create(), new DownloadTaskSnapshot { SessionId = session, LeaseToken = lease, SizeBytes = bytes.Length }, content, default);
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
    public async Task Upload_failure_preserves_existing_transport_classification(HttpStatusCode status, AgentBackendFailureKind expected)
    {
        using var handler = new InspectingHandler((_, _) => Task.FromResult(new HttpResponseMessage(status)));
        using var http = new HttpClient(handler);
        var client = new HttpDownloadTaskClient(http, new AgentBackendEndpoint(new Uri("https://backup.example.test/")), new FakeTimeProvider());
        await using var content = new MemoryStream([1]);
        var response = await client.UploadAsync(TestIdentity.Create(), new DownloadTaskSnapshot
        {
            SessionId = Guid.NewGuid(), LeaseToken = Guid.NewGuid(), SizeBytes = 1
        }, content, default);
        Assert.NotNull(response.Failure);
        Assert.Equal(expected, response.Failure.Kind);
    }

    private sealed class InspectingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handle(request, cancellationToken);
    }
}
