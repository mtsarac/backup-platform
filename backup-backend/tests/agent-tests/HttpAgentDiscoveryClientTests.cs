using System.Net;
using System.Text.Json;
using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Transport;
using BackupPlatform.AgentTests.Support;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Contracts.Discovery;
using Microsoft.Extensions.Time.Testing;

namespace BackupPlatform.AgentTests;

public sealed class HttpAgentDiscoveryClientTests
{
    private static readonly Guid ScanId = Guid.Parse("55555555-5555-5555-5555-555555555555");

    private static HttpAgentDiscoveryClient Create(FakeHttpHandler handler) =>
        new(new HttpClient(handler), new AgentBackendEndpoint(new Uri("https://backup.example.test/root")), new FakeTimeProvider());

    [Fact]
    public async Task A_claim_is_posted_with_the_credential_and_protocol_header_and_the_plan_is_read()
    {
        using var handler = new FakeHttpHandler();
        handler.Respond = (_, _) => Task.FromResult(FakeHttpHandler.Json(HttpStatusCode.OK, $$"""
            {"scanId":"{{ScanId}}","reason":"manual","includeDocker":false,"timeoutSeconds":90,"onlyInstanceKey":"mssql|service|default",
             "bindings":[{"instanceKey":"mssql|service|default","engine":"mssql","credentialReference":"sa-ref","integratedSecurity":false,"encrypt":true,"trustServerCertificate":true}]}
            """));
        var client = Create(handler);

        var result = await client.ClaimAsync(TestIdentity.Create(), default);

        Assert.True(result.IsSuccess);
        var plan = result.Value!;
        Assert.Equal(ScanId, plan.ScanId);
        Assert.Equal(DiscoveryScanReasons.Manual, plan.Reason);
        Assert.False(plan.IncludeDocker);
        Assert.Equal(90, plan.TimeoutSeconds);
        Assert.Equal("mssql|service|default", plan.OnlyInstanceKey);
        var binding = Assert.Single(plan.Bindings);
        Assert.Equal("sa-ref", binding.CredentialReference);
        Assert.True(binding.TrustServerCertificate);
        var recorded = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, recorded.Method);
        Assert.Equal("/root/api/agent/discovery/scans", recorded.Uri.AbsolutePath);
        Assert.Equal(AgentProtocol.Current.Version.ToString(), recorded.Headers[AgentProtocolHeader.Name]);
        Assert.StartsWith("BackupAgent ", recorded.Headers["Authorization"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_content_is_a_success_without_a_plan()
    {
        using var handler = new FakeHttpHandler();
        handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));

        var result = await Create(handler).ClaimAsync(TestIdentity.Create(), default);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task An_unreadable_plan_is_a_transient_failure()
    {
        using var handler = new FakeHttpHandler();
        handler.Respond = (_, _) => Task.FromResult(FakeHttpHandler.Json(HttpStatusCode.OK, "not json"));

        var result = await Create(handler).ClaimAsync(TestIdentity.Create(), default);

        Assert.False(result.IsSuccess);
        Assert.Equal(AgentBackendFailureKind.Transient, result.Failure.Kind);
    }

    [Fact]
    public async Task A_report_is_put_to_the_scan_route_with_the_contract_shape_and_no_identity_or_secret()
    {
        using var handler = new FakeHttpHandler();
        handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        var report = new AgentDiscoveryReport
        {
            Outcome = DiscoveryOutcomes.Partial,
            FailureCode = "scan_timeout",
            Sources = [new AgentDiscoverySourceStatus { Source = DiscoverySources.Docker, Status = DiscoverySourceStates.Unavailable, ReasonCode = "docker_not_found" }],
            Instances =
            [
                new AgentDiscoveredInstance
                {
                    InstanceKey = "mssql|service|default",
                    Port = 1433,
                    Access = DiscoveryAccess.Authenticated,
                    FullBackup = DiscoverySupportNames.Supported,
                    Databases = [new AgentDiscoveredDatabase { Name = "Sales", State = DiscoveryDatabaseStates.Online, FullBackup = DiscoverySupportNames.Supported }],
                },
            ],
        };

        var result = await Create(handler).ReportAsync(TestIdentity.Create(), ScanId, report, default);

        Assert.True(result.IsSuccess);
        var recorded = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, recorded.Method);
        Assert.Equal($"/root/api/agent/discovery/scans/{ScanId}", recorded.Uri.AbsolutePath);
        Assert.Equal(AgentProtocol.Current.Version.ToString(), recorded.Headers[AgentProtocolHeader.Name]);
        using var body = JsonDocument.Parse(recorded.Body!);
        Assert.Equal("Partial", body.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("docker_not_found", body.RootElement.GetProperty("sources")[0].GetProperty("reasonCode").GetString());
        var instance = body.RootElement.GetProperty("instances")[0];
        Assert.Equal("mssql|service|default", instance.GetProperty("instanceKey").GetString());
        Assert.Equal(1433, instance.GetProperty("port").GetInt32());
        Assert.Equal("Sales", instance.GetProperty("databases")[0].GetProperty("name").GetString());
        Assert.False(body.RootElement.TryGetProperty("agentId", out _));
        Assert.False(body.RootElement.TryGetProperty("tenantId", out _));
        Assert.DoesNotContain(TestIdentity.Secret, recorded.Body!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_report_needs_a_scan_id()
    {
        using var handler = new FakeHttpHandler();

        await Assert.ThrowsAsync<ArgumentException>(() => Create(handler).ReportAsync(TestIdentity.Create(), Guid.Empty, new AgentDiscoveryReport(), default));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, AgentBackendFailureKind.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests, AgentBackendFailureKind.RateLimited)]
    [InlineData(HttpStatusCode.ServiceUnavailable, AgentBackendFailureKind.Transient)]
    [InlineData(HttpStatusCode.Conflict, AgentBackendFailureKind.Rejected)]
    public async Task Refusals_keep_the_shared_transport_classification_for_both_calls(HttpStatusCode status, AgentBackendFailureKind expected)
    {
        using var handler = new FakeHttpHandler();
        handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(status));
        var client = Create(handler);

        var claim = await client.ClaimAsync(TestIdentity.Create(), default);
        var report = await client.ReportAsync(TestIdentity.Create(), ScanId, new AgentDiscoveryReport(), default);

        Assert.Equal(expected, claim.Failure!.Kind);
        Assert.Equal(expected, report.Failure!.Kind);
        Assert.DoesNotContain(TestIdentity.Secret, claim.Failure.Description + report.Failure.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_superseded_report_is_a_409_rejection_with_the_status_code()
    {
        using var handler = new FakeHttpHandler();
        handler.Respond = (_, _) => Task.FromResult(FakeHttpHandler.Json(HttpStatusCode.Conflict, """{"error":"scan_superseded"}"""));

        var result = await Create(handler).ReportAsync(TestIdentity.Create(), ScanId, new AgentDiscoveryReport(), default);

        Assert.Equal(AgentBackendFailureKind.Rejected, result.Failure!.Kind);
        Assert.Equal(409, result.Failure.StatusCode);
    }
}
