using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Transport;
using BackupPlatform.AgentTests.Support;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Contracts.Backups;
using Microsoft.Extensions.Time.Testing;

namespace BackupPlatform.AgentTests;

public sealed class HttpDeleteReplicaTaskClientTests
{
    [Fact]
    public async Task Lease_and_completion_authenticate_current_identity_and_send_only_authority_and_outcome()
    {
        using var handler = new FakeHttpHandler
        {
            Respond = (request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/lease", StringComparison.Ordinal)
                ? FakeHttpHandler.Json(HttpStatusCode.OK, """{"leaseExpiresAt":"2026-01-01T00:02:00Z"}""")
                : new HttpResponseMessage(HttpStatusCode.NoContent))
        };
        using var http = new HttpClient(handler);
        var client = new HttpDeleteReplicaTaskClient(http, new AgentBackendEndpoint(new Uri("https://backup.example.test/root")), new FakeTimeProvider());
        var replica = Guid.NewGuid();
        var operation = Guid.NewGuid();
        var token = Guid.NewGuid();
        var renewed = await client.RenewAsync(TestIdentity.Create(), replica, new DeleteReplicaLeaseRequest { OperationId = operation, LeaseToken = token }, default);
        var rotated = Guid.NewGuid();
        var completed = await client.CompleteAsync(TestIdentity.Create(TestIdentity.RotatedSecret, rotated), replica,
            new DeleteReplicaCompletionRequest { OperationId = operation, LeaseToken = token, AbsenceVerified = true }, default);
        Assert.True(renewed.IsSuccess);
        Assert.True(completed.IsSuccess);
        Assert.Equal($"/root/api/agent/replica-deletions/{replica:D}/lease", handler.Requests[0].Uri.AbsolutePath);
        Assert.Equal($"/root/api/agent/replica-deletions/{replica:D}/complete", handler.Requests[1].Uri.AbsolutePath);
        Assert.Equal($"BackupAgent {rotated:N}.{TestIdentity.RotatedSecret}", handler.Requests[1].Headers["Authorization"]);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(AgentProtocol.Current.Version.ToString(), request.Headers[AgentProtocolHeader.Name]);
            using var body = JsonDocument.Parse(request.Body!);
            Assert.Equal(operation, body.RootElement.GetProperty("operationId").GetGuid());
            Assert.Equal(token, body.RootElement.GetProperty("leaseToken").GetGuid());
            Assert.False(body.RootElement.TryGetProperty("agentId", out _));
            Assert.False(body.RootElement.TryGetProperty("storageKey", out _));
            Assert.DoesNotContain(TestIdentity.Secret, request.Body!, StringComparison.Ordinal);
            Assert.DoesNotContain(TestIdentity.RotatedSecret, request.Body!, StringComparison.Ordinal);
        });
        using var completion = JsonDocument.Parse(handler.Requests[1].Body!);
        Assert.True(completion.RootElement.GetProperty("absenceVerified").GetBoolean());
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, AgentBackendFailureKind.Rejected)]
    [InlineData(HttpStatusCode.Unauthorized, AgentBackendFailureKind.Unauthorized)]
    [InlineData(HttpStatusCode.ServiceUnavailable, AgentBackendFailureKind.Transient)]
    public async Task Refused_authority_and_completion_preserve_transport_classification(HttpStatusCode status, AgentBackendFailureKind kind)
    {
        using var handler = new FakeHttpHandler { Respond = (_, _) => Task.FromResult(new HttpResponseMessage(status)) };
        using var http = new HttpClient(handler);
        var client = new HttpDeleteReplicaTaskClient(http, new AgentBackendEndpoint(new Uri("https://backup.example.test/")), new FakeTimeProvider());
        var replica = Guid.NewGuid();
        var lease = await client.RenewAsync(TestIdentity.Create(), replica, new DeleteReplicaLeaseRequest(), default);
        var completion = await client.CompleteAsync(TestIdentity.Create(), replica, new DeleteReplicaCompletionRequest(), default);
        Assert.Equal(kind, lease.Failure!.Kind);
        Assert.Equal(kind, completion.Failure!.Kind);
    }

    [Fact]
    public async Task Completion_rate_limit_preserves_retry_after()
    {
        using var handler = new FakeHttpHandler
        {
            Respond = (_, _) =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(12));
                return Task.FromResult(response);
            }
        };
        using var http = new HttpClient(handler);
        var client = new HttpDeleteReplicaTaskClient(http, new AgentBackendEndpoint(new Uri("https://backup.example.test/")), new FakeTimeProvider());
        var completion = await client.CompleteAsync(TestIdentity.Create(), Guid.NewGuid(), new DeleteReplicaCompletionRequest(), default);
        Assert.Equal(AgentBackendFailureKind.RateLimited, completion.Failure!.Kind);
        Assert.Equal(TimeSpan.FromSeconds(12), completion.Failure.RetryAfter);
    }
}
