using System.Net;
using System.Text.Json;
using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Transport;
using BackupPlatform.AgentTests.Support;
using BackupPlatform.Contracts.Agents;
using Microsoft.Extensions.Time.Testing;

namespace BackupPlatform.AgentTests;

public sealed class HttpAgentCapabilityClientTests
{
    private static HttpAgentCapabilityClient Create(FakeHttpHandler handler) =>
        new(new HttpClient(handler), new AgentBackendEndpoint(new Uri("https://backup.example.test/root")), new FakeTimeProvider());

    [Fact]
    public async Task The_snapshot_is_put_to_the_capabilities_route_with_the_credential_and_without_identity_in_the_body()
    {
        using var handler = new FakeHttpHandler();
        handler.Respond = (_, _) => Task.FromResult(FakeHttpHandler.Json(HttpStatusCode.OK, """{"revision":"c1-abc","changed":true}"""));
        var client = Create(handler);
        var request = new AgentCapabilitySyncRequest
        {
            Revision = "c1-abc",
            Capabilities = [new AgentCapabilityReport { Identifier = "mssql.backup.full", Support = "Supported", Metadata = new Dictionary<string, string> { ["scope"] = "agent" } }]
        };

        var result = await client.SyncAsync(TestIdentity.Create(), request, default);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.Changed);
        var recorded = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, recorded.Method);
        Assert.Equal("/root/api/agent/capabilities", recorded.Uri.AbsolutePath);
        Assert.Equal(AgentProtocol.Current.Version.ToString(), recorded.Headers[AgentProtocolHeader.Name]);
        Assert.StartsWith("BackupAgent ", recorded.Headers["Authorization"], StringComparison.Ordinal);
        using var body = JsonDocument.Parse(recorded.Body!);
        Assert.Equal("c1-abc", body.RootElement.GetProperty("revision").GetString());
        var capability = Assert.Single(body.RootElement.GetProperty("capabilities").EnumerateArray());
        Assert.Equal("mssql.backup.full", capability.GetProperty("identifier").GetString());
        Assert.Equal("Supported", capability.GetProperty("support").GetString());
        Assert.Equal("agent", capability.GetProperty("metadata").GetProperty("scope").GetString());
        Assert.False(body.RootElement.TryGetProperty("agentId", out _));
        Assert.False(body.RootElement.TryGetProperty("tenantId", out _));
        Assert.DoesNotContain(TestIdentity.Secret, recorded.Body!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, AgentBackendFailureKind.Unauthorized)]
    [InlineData(HttpStatusCode.ServiceUnavailable, AgentBackendFailureKind.Transient)]
    [InlineData(HttpStatusCode.Conflict, AgentBackendFailureKind.Rejected)]
    public async Task Refusals_keep_the_shared_transport_classification(HttpStatusCode status, AgentBackendFailureKind expected)
    {
        using var handler = new FakeHttpHandler();
        handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(status));
        var client = Create(handler);

        var result = await client.SyncAsync(TestIdentity.Create(), new AgentCapabilitySyncRequest { Revision = "c1-abc" }, default);

        Assert.False(result.IsSuccess);
        Assert.Equal(expected, result.Failure.Kind);
        Assert.DoesNotContain(TestIdentity.Secret, result.Failure.Description, StringComparison.Ordinal);
    }
}
