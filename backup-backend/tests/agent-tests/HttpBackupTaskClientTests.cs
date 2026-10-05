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

public sealed class HttpBackupTaskClientTests
{
    [Fact]
    public async Task Task_mutations_authenticate_current_identity_and_carry_the_lease_without_identity_in_body()
    {
        using var handler = new FakeHttpHandler();
        handler.Respond = (request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/start", StringComparison.Ordinal)
            || request.RequestUri.AbsolutePath.EndsWith("/lease", StringComparison.Ordinal)
                ? FakeHttpHandler.Json(HttpStatusCode.OK, """{"leaseExpiresAt":"2026-01-01T00:02:00Z","cancellationRequested":true}""")
                : new HttpResponseMessage(HttpStatusCode.NoContent));
        using var http = new HttpClient(handler);
        var client = new HttpBackupTaskClient(http, new AgentBackendEndpoint(new Uri("https://backup.example.test/root")), new FakeTimeProvider());
        var attempt = Guid.NewGuid();
        var token = Guid.NewGuid();
        var identity = TestIdentity.Create();
        var started = await client.StartAsync(identity, attempt, new BackupTaskStartRequest { LeaseToken = token, SoftwareVersion = "1.2.3", ProtocolVersion = "1.0", Platform = "linux" }, default);
        var renewed = await client.RenewAsync(identity, attempt, new BackupTaskLeaseRequest { LeaseToken = token }, default);
        var progress = await client.ReportProgressAsync(identity, attempt, new BackupTaskProgressRequest { LeaseToken = token, Stage = "BackingUp", Percent = 50 }, default);
        var rotated = Guid.NewGuid();
        var complete = await client.CompleteAsync(TestIdentity.Create(TestIdentity.RotatedSecret, rotated), attempt,
            new BackupTaskCompletionRequest { LeaseToken = token, Status = "Cancelled", FailureCode = "Cancelled" }, default);
        Assert.True(started.IsSuccess);
        Assert.True(renewed.Value!.CancellationRequested);
        Assert.True(progress.IsSuccess);
        Assert.True(complete.IsSuccess);
        var requests = handler.Requests;
        Assert.Equal($"https://backup.example.test/root/api/agent/tasks/{attempt:D}/start", requests[0].Uri.ToString());
        Assert.Equal($"/root/api/agent/tasks/{attempt:D}/lease", requests[1].Uri.AbsolutePath);
        Assert.Equal($"/root/api/agent/tasks/{attempt:D}/progress", requests[2].Uri.AbsolutePath);
        Assert.Equal($"/root/api/agent/tasks/{attempt:D}/complete", requests[3].Uri.AbsolutePath);
        Assert.Equal($"BackupAgent {rotated:N}.{TestIdentity.RotatedSecret}", requests[3].Headers["Authorization"]);
        Assert.All(requests, request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(AgentProtocol.Current.Version.ToString(), request.Headers[AgentProtocolHeader.Name]);
            using var body = JsonDocument.Parse(request.Body!);
            Assert.Equal(token, body.RootElement.GetProperty("leaseToken").GetGuid());
            Assert.False(body.RootElement.TryGetProperty("agentId", out _));
            Assert.DoesNotContain(TestIdentity.Secret, request.Body!, StringComparison.Ordinal);
            Assert.DoesNotContain(TestIdentity.RotatedSecret, request.Body!, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, AgentBackendFailureKind.Rejected)]
    [InlineData(HttpStatusCode.Unauthorized, AgentBackendFailureKind.Unauthorized)]
    [InlineData(HttpStatusCode.ServiceUnavailable, AgentBackendFailureKind.Transient)]
    public async Task Failed_task_mutations_preserve_transport_classification(HttpStatusCode status, AgentBackendFailureKind expected)
    {
        using var handler = new FakeHttpHandler { Respond = (_, _) => Task.FromResult(new HttpResponseMessage(status)) };
        using var http = new HttpClient(handler);
        var client = new HttpBackupTaskClient(http, new AgentBackendEndpoint(new Uri("https://backup.example.test/")), new FakeTimeProvider());
        var response = await client.CompleteAsync(TestIdentity.Create(), Guid.NewGuid(), new BackupTaskCompletionRequest { LeaseToken = Guid.NewGuid(), Status = "Cancelled" }, default);
        Assert.False(response.IsSuccess);
        Assert.Equal(expected, response.Failure.Kind);
        Assert.Equal((int)status, response.Failure.StatusCode);
    }

    [Fact]
    public async Task Renewal_protocol_refusal_and_completion_rate_limit_are_not_treated_as_success()
    {
        using var handler = new FakeHttpHandler();
        handler.Respond = (_, _) => Task.FromResult(FakeHttpHandler.Json(HttpStatusCode.BadRequest,
            """{"error":"protocol_incompatible","action":"upgrade_agent"}"""));
        using var http = new HttpClient(handler);
        var client = new HttpBackupTaskClient(http, new AgentBackendEndpoint(new Uri("https://backup.example.test/")), new FakeTimeProvider());
        var renewal = await client.RenewAsync(TestIdentity.Create(), Guid.NewGuid(), new BackupTaskLeaseRequest { LeaseToken = Guid.NewGuid() }, default);
        Assert.Equal(AgentBackendFailureKind.ProtocolIncompatible, renewal.Failure!.Kind);
        Assert.Equal("upgrade_agent", renewal.Failure.Error!.Action);
        handler.Respond = (_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(12));
            return Task.FromResult(response);
        };
        var completion = await client.CompleteAsync(TestIdentity.Create(), Guid.NewGuid(), new BackupTaskCompletionRequest { LeaseToken = Guid.NewGuid(), Status = "Cancelled" }, default);
        Assert.Equal(AgentBackendFailureKind.RateLimited, completion.Failure!.Kind);
        Assert.Equal(TimeSpan.FromSeconds(12), completion.Failure.RetryAfter);
    }
}
