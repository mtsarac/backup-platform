using System.Net;
using System.Text.Json;
using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Transport;
using BackupPlatform.AgentTests.Support;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Contracts.Backups;
using Microsoft.Extensions.Time.Testing;

namespace BackupPlatform.AgentTests;

public sealed class HttpReplicaRepairTaskClientTests
{
    [Fact]
    public async Task Every_call_posts_to_its_operation_route_with_the_current_identity_and_the_lease_token()
    {
        using var handler = new FakeHttpHandler
        {
            Respond = (request, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
            {
                var path when path.EndsWith("/lease", StringComparison.Ordinal) => FakeHttpHandler.Json(HttpStatusCode.OK, """{"leaseExpiresAt":"2026-01-01T00:02:00Z","cancellationRequested":true}"""),
                var path when path.EndsWith("/cleanup-check", StringComparison.Ordinal) => FakeHttpHandler.Json(HttpStatusCode.OK, """{"allowed":true}"""),
                _ => new HttpResponseMessage(HttpStatusCode.NoContent)
            })
        };
        using var http = new HttpClient(handler);
        var client = new HttpReplicaRepairTaskClient(http, new AgentBackendEndpoint(new Uri("https://backup.example.test/root")), new FakeTimeProvider());
        var identity = TestIdentity.Create();
        var operation = Guid.NewGuid();
        var token = Guid.NewGuid();

        var lease = await client.RenewAsync(identity, operation, new ReplicaRepairLeaseRequest { LeaseToken = token }, default);
        var pin = await client.PinAsync(identity, operation, new ReplicaRepairPinRequest { LeaseToken = token, SourceReplicaId = Guid.NewGuid(), TargetKey = "a/b" }, default);
        var progress = await client.ProgressAsync(identity, operation, new ReplicaRepairProgressRequest { LeaseToken = token, TransferredBytes = 7 }, default);
        var cleanup = await client.CleanupCheckAsync(identity, operation, new ReplicaRepairCleanupRequest { LeaseToken = token, TargetKey = "a/b" }, default);
        var completion = await client.CompleteAsync(identity, operation, new ReplicaRepairCompletionRequest { LeaseToken = token, Outcome = ReplicaRepairOutcomes.Cancelled }, default);

        Assert.True(lease.Value!.CancellationRequested);
        Assert.True(cleanup.Value!.Allowed);
        Assert.True(pin.IsSuccess && progress.IsSuccess && completion.IsSuccess);
        Assert.Equal(["lease", "pin", "progress", "cleanup-check", "complete"],
            handler.Requests.Select(request => request.Uri.AbsolutePath[(request.Uri.AbsolutePath.LastIndexOf('/') + 1)..]).ToArray());
        Assert.All(handler.Requests, request =>
        {
            Assert.StartsWith($"/root/api/agent/replica-repairs/{operation:D}/", request.Uri.AbsolutePath, StringComparison.Ordinal);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal($"BackupAgent {TestIdentity.CredentialId:N}.{TestIdentity.Secret}", request.Headers["Authorization"]);
            Assert.Equal(AgentProtocol.Current.Version.ToString(), request.Headers[AgentProtocolHeader.Name]);
            using var body = JsonDocument.Parse(request.Body!);
            Assert.Equal(token, body.RootElement.GetProperty("leaseToken").GetGuid());
            Assert.DoesNotContain(TestIdentity.Secret, request.Body!, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, AgentBackendFailureKind.Rejected)]
    [InlineData(HttpStatusCode.Unauthorized, AgentBackendFailureKind.Unauthorized)]
    [InlineData(HttpStatusCode.ServiceUnavailable, AgentBackendFailureKind.Transient)]
    public async Task Refused_pin_preserves_transport_classification(HttpStatusCode status, AgentBackendFailureKind kind)
    {
        using var handler = new FakeHttpHandler { Respond = (_, _) => Task.FromResult(new HttpResponseMessage(status)) };
        using var http = new HttpClient(handler);
        var client = new HttpReplicaRepairTaskClient(http, new AgentBackendEndpoint(new Uri("https://backup.example.test/")), new FakeTimeProvider());
        var pin = await client.PinAsync(TestIdentity.Create(), Guid.NewGuid(), new ReplicaRepairPinRequest(), default);
        Assert.Equal(kind, pin.Failure!.Kind);
    }
}
