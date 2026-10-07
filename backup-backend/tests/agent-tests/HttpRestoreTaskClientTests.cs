using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Transport;
using BackupPlatform.AgentTests.Support;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Contracts.Restores;
using Microsoft.Extensions.Time.Testing;

namespace BackupPlatform.AgentTests;

public sealed class HttpRestoreTaskClientTests
{
    [Fact]
    public async Task Restore_mutations_use_the_restore_routes_authenticate_the_current_identity_and_carry_the_lease_without_identity_in_the_body()
    {
        using var handler = new FakeHttpHandler();
        handler.Respond = (request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/start", StringComparison.Ordinal)
            || request.RequestUri.AbsolutePath.EndsWith("/lease", StringComparison.Ordinal)
                ? FakeHttpHandler.Json(HttpStatusCode.OK, """{"leaseExpiresAt":"2026-01-01T00:02:00Z","cancellationRequested":true}""")
                : new HttpResponseMessage(HttpStatusCode.NoContent));
        using var http = new HttpClient(handler);
        var client = new HttpRestoreTaskClient(http, new AgentBackendEndpoint(new Uri("https://backup.example.test/root")), new FakeTimeProvider());
        var attempt = Guid.NewGuid();
        var token = Guid.NewGuid();
        var identity = TestIdentity.Create();

        var started = await client.StartAsync(identity, attempt, new BackupTaskStartRequest { LeaseToken = token, SoftwareVersion = "1.2.3", ProtocolVersion = "1.0", Platform = "linux" }, default);
        var renewed = await client.RenewAsync(identity, attempt, new BackupTaskLeaseRequest { LeaseToken = token }, default);
        var progress = await client.ReportProgressAsync(identity, attempt, new BackupTaskProgressRequest { LeaseToken = token, Stage = "Restoring", Percent = 50 }, default);
        var rotated = Guid.NewGuid();
        var complete = await client.CompleteAsync(TestIdentity.Create(TestIdentity.RotatedSecret, rotated), attempt, new RestoreTaskCompletionRequest
        {
            LeaseToken = token, Status = "Cancelled", FailureCode = "Cancelled", TargetState = "PartiallyRestored",
            Result = new RestoreTaskResult { DatabaseState = "RESTORING", TargetStateNote = "Left restoring." }
        }, default);

        Assert.True(started.IsSuccess);
        Assert.True(renewed.Value!.CancellationRequested);
        Assert.True(progress.IsSuccess);
        Assert.True(complete.IsSuccess);
        var requests = handler.Requests;
        Assert.Equal($"https://backup.example.test/root/api/agent/restores/{attempt:D}/start", requests[0].Uri.ToString());
        Assert.Equal($"/root/api/agent/restores/{attempt:D}/lease", requests[1].Uri.AbsolutePath);
        Assert.Equal($"/root/api/agent/restores/{attempt:D}/progress", requests[2].Uri.AbsolutePath);
        Assert.Equal($"/root/api/agent/restores/{attempt:D}/complete", requests[3].Uri.AbsolutePath);
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

        using var completion = JsonDocument.Parse(requests[3].Body!);
        Assert.Equal("Cancelled", completion.RootElement.GetProperty("status").GetString());
        Assert.Equal("PartiallyRestored", completion.RootElement.GetProperty("targetState").GetString());
        Assert.Equal("RESTORING", completion.RootElement.GetProperty("result").GetProperty("databaseState").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, AgentBackendFailureKind.Rejected)]
    [InlineData(HttpStatusCode.NotFound, AgentBackendFailureKind.Rejected)]
    [InlineData(HttpStatusCode.Unauthorized, AgentBackendFailureKind.Unauthorized)]
    [InlineData(HttpStatusCode.ServiceUnavailable, AgentBackendFailureKind.Transient)]
    public async Task Failed_restore_mutations_preserve_the_transport_classification(HttpStatusCode status, AgentBackendFailureKind expected)
    {
        using var handler = new FakeHttpHandler { Respond = (_, _) => Task.FromResult(new HttpResponseMessage(status)) };
        using var http = new HttpClient(handler);
        var client = new HttpRestoreTaskClient(http, new AgentBackendEndpoint(new Uri("https://backup.example.test/")), new FakeTimeProvider());
        var response = await client.CompleteAsync(TestIdentity.Create(), Guid.NewGuid(),
            new RestoreTaskCompletionRequest { LeaseToken = Guid.NewGuid(), Status = "Failed", FailureCode = "InternalError", TargetState = "NotModified" }, default);
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
        var client = new HttpRestoreTaskClient(http, new AgentBackendEndpoint(new Uri("https://backup.example.test/")), new FakeTimeProvider());
        var renewal = await client.RenewAsync(TestIdentity.Create(), Guid.NewGuid(), new BackupTaskLeaseRequest { LeaseToken = Guid.NewGuid() }, default);
        Assert.Equal(AgentBackendFailureKind.ProtocolIncompatible, renewal.Failure!.Kind);
        Assert.Equal("upgrade_agent", renewal.Failure.Error!.Action);
        handler.Respond = (_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(12));
            return Task.FromResult(response);
        };
        var completion = await client.CompleteAsync(TestIdentity.Create(), Guid.NewGuid(),
            new RestoreTaskCompletionRequest { LeaseToken = Guid.NewGuid(), Status = "Cancelled", TargetState = "NotModified" }, default);
        Assert.Equal(AgentBackendFailureKind.RateLimited, completion.Failure!.Kind);
        Assert.Equal(TimeSpan.FromSeconds(12), completion.Failure.RetryAfter);
    }

    [Fact]
    public async Task An_empty_attempt_identifier_is_refused_before_anything_is_sent()
    {
        using var handler = new FakeHttpHandler();
        using var http = new HttpClient(handler);
        var client = new HttpRestoreTaskClient(http, new AgentBackendEndpoint(new Uri("https://backup.example.test/")), new FakeTimeProvider());
        await Assert.ThrowsAsync<ArgumentException>(() => client.RenewAsync(TestIdentity.Create(), Guid.Empty, new BackupTaskLeaseRequest { LeaseToken = Guid.NewGuid() }, default));
        Assert.Empty(handler.Requests);
    }
}
