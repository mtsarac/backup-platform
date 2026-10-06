using System.Net;
using System.Net.Http.Json;
using BackupPlatform.Agents.Connectivity;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Domain.Agents;
using BackupPlatform.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BackupPlatform.IntegrationTests;

public sealed class AgentHeartbeatIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task ValidHeartbeatIsRecordedAndAnswersWithServerTimeAndCadence()
    {
        var clock = new MutableClock(new DateTimeOffset(2031, 5, 6, 7, 8, 9, TimeSpan.Zero));
        await using var world = await AgentWorld.CreateAsync(fixture, clock: clock);
        var agent = await world.EnrollAsync(await world.CreateTenantAsync());

        using var response = await world.HeartbeatAsync(agent, AgentWorld.Heartbeat("2.3.4", "caps-1"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var json = AgentWorld.ReadJson(response);
        Assert.Equal(clock.GetUtcNow(), json.GetProperty("serverTime").GetDateTimeOffset());
        Assert.Equal(30, json.GetProperty("heartbeatIntervalSeconds").GetInt32());
        Assert.Equal(120, json.GetProperty("offlineTimeoutSeconds").GetInt32());
        Assert.Equal("1.0", json.GetProperty("protocol").GetProperty("current").GetString());
        // The revision "caps-1" is unknown to the backend, so it asks for a capability sync.
        Assert.True(json.GetProperty("capabilitySyncRequested").GetBoolean());

        var row = await world.ConnectivityOfAsync(agent.AgentId);
        Assert.NotNull(row);
        Assert.Equal(clock.GetUtcNow(), row.LastSeenAt);
        Assert.Equal(clock.GetUtcNow(), row.LastHeartbeatAt);
        Assert.Equal("2.3.4", row.ReportedAgentVersion);
        Assert.Equal((1, 0), (row.ReportedProtocolMajor, row.ReportedProtocolMinor));
        Assert.Equal(AgentPlatform.Linux, row.ReportedPlatform);
        Assert.Equal("test-os", row.ReportedOsDescription);
        Assert.Equal("10.0.0", row.ReportedRuntimeVersion);
        Assert.Equal("caps-1", row.CapabilityRevision);
    }

    [Fact]
    public async Task TheServerClockDecidesWhenAnAgentWasSeenAndAnAgentClaimOfOnlineIsIgnored()
    {
        var clock = new MutableClock(new DateTimeOffset(2031, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await using var world = await AgentWorld.CreateAsync(fixture, clock: clock);
        var agent = await world.EnrollAsync(await world.CreateTenantAsync());
        using var response = await world.HeartbeatAsync(agent, new
        {
            agentVersion = "1.0.0",
            platform = "linux",
            online = true,
            status = "online",
            lastSeenAt = "2001-01-01T00:00:00Z",
            serverTime = "2099-12-31T23:59:59Z",
            timestamp = 1
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(clock.GetUtcNow(), (await world.ConnectivityOfAsync(agent.AgentId))!.LastSeenAt);
    }

    [Fact]
    public async Task HeartbeatsAreIdempotentAndNeverMoveTimeBackward()
    {
        var clock = new MutableClock();
        await using var world = await AgentWorld.CreateAsync(fixture, clock: clock);
        var agent = await world.EnrollAsync(await world.CreateTenantAsync());
        using var first = await world.HeartbeatAsync(agent);
        clock.Advance(TimeSpan.FromSeconds(30));
        using var replay = await world.HeartbeatAsync(agent);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var row = await world.ConnectivityOfAsync(agent.AgentId);
        Assert.Equal(clock.GetUtcNow(), row!.LastSeenAt);
        await using var context = fixture.CreatePlatformContext();
        Assert.Equal(1, await context.AgentConnectivity.CountAsync(candidate => candidate.AgentId == new AgentId(agent.AgentId)));

        // A delayed or reordered write that carries an older time can neither rewind timestamps nor overwrite newer metadata.
        using var scope = world.Factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentConnectivityStore>();
        await store.RecordHeartbeatAsync(new HeartbeatWrite(new AgentId(agent.AgentId), new TenantId(agent.TenantId),
            clock.GetUtcNow().AddMinutes(-10), "0.0.1", 1, 0, AgentPlatform.Linux, "stale", "stale", "stale"), CancellationToken.None);
        var after = await world.ConnectivityOfAsync(agent.AgentId);
        Assert.Equal(clock.GetUtcNow(), after!.LastSeenAt);
        Assert.Equal(clock.GetUtcNow(), after.LastHeartbeatAt);
        Assert.Equal("1.2.3", after.ReportedAgentVersion);
        Assert.Equal("test-os", after.ReportedOsDescription);
    }

    [Fact]
    public async Task AgentsCannotRecordHeartbeatsForEachOtherByNamingAnotherIdentity()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var setupA = await world.CreateTenantAsync();
        var setupB = await world.CreateTenantAsync();
        var a = await world.EnrollAsync(setupA);
        var b = await world.EnrollAsync(setupB);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/agent/heartbeat?agentId={b.AgentId}&tenantId={b.TenantId}");
        request.Headers.TryAddWithoutValidation("Authorization", AgentAuthorizationScheme.FormatHeaderValue(a.CredentialId, a.Secret));
        request.Headers.TryAddWithoutValidation(AgentProtocolHeader.Name, "1.0");
        request.Headers.TryAddWithoutValidation("X-Agent-Id", b.AgentId.ToString());
        request.Headers.TryAddWithoutValidation("X-Tenant-Id", b.TenantId.ToString());
        request.Content = JsonContent.Create(new { agentId = b.AgentId, tenantId = b.TenantId, agentVersion = "1.0.0", platform = "linux" });
        using var response = await world.Anonymous.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(await world.ConnectivityOfAsync(a.AgentId));
        Assert.Null(await world.ConnectivityOfAsync(b.AgentId));
        // A's credential with B's secret, or B's credential id with A's secret, authenticates as neither.
        using var mixed = await world.HeartbeatWithCredentialAsync(a.CredentialId, b.Secret);
        Assert.Equal(HttpStatusCode.Unauthorized, mixed.StatusCode);
        Assert.Null(await world.ConnectivityOfAsync(b.AgentId));
    }

    [Fact]
    public async Task BrowserSessionsAndMissingCredentialsCannotReachConnectivityEndpoints()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var setup = await world.CreateTenantAsync();
        var agent = await world.EnrollAsync(setup);
        using var cookieHeartbeat = await setup.Admin.SendAsync(HttpMethod.Post, "/api/agent/heartbeat", AgentWorld.Heartbeat());
        using var cookiePoll = await setup.Admin.GetAsync("/api/agent/tasks");
        using var anonymousHeartbeat = await world.Anonymous.PostAsJsonAsync("/api/agent/heartbeat", AgentWorld.Heartbeat());
        using var anonymousPoll = await world.Anonymous.GetAsync("/api/agent/tasks");
        Assert.All(new[] { cookieHeartbeat, cookiePoll, anonymousHeartbeat, anonymousPoll },
            response => Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode));
        Assert.Null(await world.ConnectivityOfAsync(agent.AgentId));
    }

    [Fact]
    public async Task RevokedAndDisabledAgentsAreRejectedOnEveryConnectivityEndpoint()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var setup = await world.CreateTenantAsync();
        var revoked = await world.EnrollAsync(setup);
        var disabled = await world.EnrollAsync(setup);
        using var revoke = await setup.Admin.SendAsync(HttpMethod.Post, $"/api/tenants/{setup.Tenant.Id.Value}/agents/{revoked.AgentId}/revoke");
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        await using (var context = fixture.CreatePlatformContext())
        {
            var row = await context.Agents.SingleAsync(candidate => candidate.Id == new AgentId(disabled.AgentId));
            row.Disable();
            await context.SaveChangesAsync();
        }
        foreach (var agent in new[] { revoked, disabled })
        {
            using var heartbeat = await world.HeartbeatAsync(agent);
            using var poll = await world.PollAsync(agent);
            Assert.Equal(HttpStatusCode.Unauthorized, heartbeat.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, poll.StatusCode);
            Assert.Null(await world.ConnectivityOfAsync(agent.AgentId));
        }
    }

    [Theory]
    [InlineData("", "linux", "AgentVersion")]
    [InlineData("not-a-version", "linux", "AgentVersion")]
    [InlineData("1.0.0", "plan9", "Platform")]
    [InlineData("1.0.0", "", "Platform")]
    public async Task InvalidHeartbeatBodiesAreRejectedWithoutBeingRecorded(string version, string platform, string field)
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var agent = await world.EnrollAsync(await world.CreateTenantAsync());
        using var response = await world.HeartbeatAsync(agent, new { agentVersion = version, platform });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = AgentWorld.ReadJson(response);
        Assert.Equal("invalid_request", error.GetProperty("error").GetString());
        Assert.Contains(field, error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Null(await world.ConnectivityOfAsync(agent.AgentId));
    }

    [Fact]
    public async Task MalformedOrOversizedCapabilityAndRuntimeFieldsAreRejected()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var agent = await world.EnrollAsync(await world.CreateTenantAsync());
        foreach (var body in new object[]
        {
            new { agentVersion = "1.0.0", platform = "linux", capabilityRevision = new string('x', 65) },
            new { agentVersion = "1.0.0", platform = "linux", capabilityRevision = "has space" },
            new { agentVersion = "1.0.0", platform = "linux", runtime = new { osDescription = new string('o', 257) } },
            new { agentVersion = "1.0.0", platform = "linux", runtime = new { runtimeVersion = "bad\u0007bell" } }
        })
        {
            using var response = await world.HeartbeatAsync(agent, body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        Assert.Null(await world.ConnectivityOfAsync(agent.AgentId));
    }

    [Fact]
    public async Task PollReturnsAnEmptyTaskCollectionAServerCursorAndTheRecommendedInterval()
    {
        await using var world = await AgentWorld.CreateAsync(fixture,
            connectivity: new AgentConnectivityOptions { PollInterval = TimeSpan.FromSeconds(45) });
        var agent = await world.EnrollAsync(await world.CreateTenantAsync());
        using var response = await world.PollAsync(agent);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = AgentWorld.ReadJson(response);
        Assert.Empty(json.GetProperty("tasks").EnumerateArray());
        Assert.Equal("0", json.GetProperty("cursor").GetString());
        Assert.Equal(45, json.GetProperty("nextPollAfterSeconds").GetInt32());
        using var echoed = await world.PollAsync(agent, cursor: "0");
        Assert.Equal(HttpStatusCode.OK, echoed.StatusCode);
        using var invalid = await world.PollAsync(agent, cursor: "bad%20cursor!");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task ProtocolMustBeDeclaredAndInsideTheWindowForHeartbeatAndPolling()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var agent = await world.EnrollAsync(await world.CreateTenantAsync());

        using var missing = await world.HeartbeatAsync(agent, protocol: null);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal("protocol_missing", AgentWorld.ReadJson(missing).GetProperty("error").GetString());

        using var malformed = await world.PollAsync(agent, protocol: "banana");
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal("invalid_request", AgentWorld.ReadJson(malformed).GetProperty("error").GetString());

        foreach (var response in new[] { await world.HeartbeatAsync(agent, protocol: "2.0"), await world.PollAsync(agent, protocol: "2.0") })
        {
            using var _ = response;
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var error = AgentWorld.ReadJson(response);
            Assert.Equal("protocol_incompatible", error.GetProperty("error").GetString());
            Assert.Equal("wait_for_server_upgrade", error.GetProperty("action").GetString());
            var window = error.GetProperty("supportedProtocol");
            Assert.Equal("1.0", window.GetProperty("minimumSupported").GetString());
            Assert.Equal("1.0", window.GetProperty("maximumSupported").GetString());
        }
        Assert.Null(await world.ConnectivityOfAsync(agent.AgentId));

        using var supported = await world.HeartbeatAsync(agent, protocol: "1.0");
        Assert.Equal(HttpStatusCode.OK, supported.StatusCode);
    }

    [Fact]
    public async Task AnAgentThatIsTooOldForTheBackendIsToldToUpgradeNotToRetry()
    {
        var options = new BackupPlatform.Agents.AgentSecurityOptions
        {
            Protocol = new BackupPlatform.Contracts.Protocol.ProtocolNegotiationContract(
                new BackupPlatform.Contracts.Protocol.ProtocolVersion(2, 0),
                new BackupPlatform.Contracts.Protocol.ProtocolVersionRange(
                    new BackupPlatform.Contracts.Protocol.ProtocolVersion(2, 0), new BackupPlatform.Contracts.Protocol.ProtocolVersion(2, 0)))
        };
        await using var world = await AgentWorld.CreateAsync(fixture, options);
        var agent = await world.EnrollAsync(await world.CreateTenantAsync(), protocolVersion: "2.0");
        using var response = await world.HeartbeatAsync(agent, protocol: "1.0");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("upgrade_agent", AgentWorld.ReadJson(response).GetProperty("action").GetString());
    }

    [Fact]
    public async Task ConnectivityResponsesDoNotLeakTheSecretOrAnotherTenantsData()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var agent = await world.EnrollAsync(await world.CreateTenantAsync());
        using var heartbeat = await world.HeartbeatAsync(agent);
        using var poll = await world.PollAsync(agent);
        using var rejected = await world.HeartbeatAsync(agent, protocol: "9.9");
        foreach (var response in new[] { heartbeat, poll, rejected })
        {
            Assert.DoesNotContain(agent.Secret, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            Assert.DoesNotContain(agent.Secret, response.Headers.ToString(), StringComparison.Ordinal);
        }
    }
}
