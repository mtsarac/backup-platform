using System.Net;
using BackupPlatform.Agents;
using BackupPlatform.Agents.Connectivity;
using BackupPlatform.Contracts.Protocol;
using BackupPlatform.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BackupPlatform.IntegrationTests;

public sealed class AgentConnectivityIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task AnAgentIsUnknownThenOnlineAfterAHeartbeatThenOfflineAfterTheTimeoutThenOnlineAgain()
    {
        var clock = new MutableClock();
        await using var world = await AgentWorld.CreateAsync(fixture, clock: clock);
        var setup = await world.CreateTenantAsync();
        var agent = await world.EnrollAsync(setup);

        var unknown = await world.AgentViewAsync(setup, agent.AgentId);
        Assert.Equal("unknown", unknown.GetProperty("connectivity").GetString());
        Assert.Equal("Unknown", unknown.GetProperty("operationalStatus").GetString());
        Assert.Equal("Active", unknown.GetProperty("status").GetString());

        using var heartbeat = await world.HeartbeatAsync(agent);
        Assert.Equal(HttpStatusCode.OK, heartbeat.StatusCode);
        var online = await world.AgentViewAsync(setup, agent.AgentId);
        Assert.Equal("online", online.GetProperty("connectivity").GetString());
        Assert.Equal("Online", online.GetProperty("operationalStatus").GetString());
        Assert.Equal(clock.GetUtcNow(), online.GetProperty("lastSeenAt").GetDateTimeOffset());
        Assert.Equal("1.2.3", online.GetProperty("agentVersion").GetString());

        clock.Advance(TimeSpan.FromSeconds(119));
        Assert.Equal("online", (await world.AgentViewAsync(setup, agent.AgentId)).GetProperty("connectivity").GetString());
        clock.Advance(TimeSpan.FromSeconds(1));
        var offline = await world.AgentViewAsync(setup, agent.AgentId);
        Assert.Equal("offline", offline.GetProperty("connectivity").GetString());
        Assert.Equal("Offline", offline.GetProperty("operationalStatus").GetString());
        Assert.Equal("Active", offline.GetProperty("status").GetString());

        using var again = await world.HeartbeatAsync(agent);
        Assert.Equal("online", (await world.AgentViewAsync(setup, agent.AgentId)).GetProperty("connectivity").GetString());
    }

    [Fact]
    public async Task GoingOfflineCostsNoWritesBecauseTheStateIsDerived()
    {
        var clock = new MutableClock();
        await using var world = await AgentWorld.CreateAsync(fixture, clock: clock);
        var setup = await world.CreateTenantAsync();
        var agent = await world.EnrollAsync(setup);
        using var heartbeat = await world.HeartbeatAsync(agent);
        var before = await world.ConnectivityOfAsync(agent.AgentId);
        clock.Advance(TimeSpan.FromHours(6));
        Assert.Equal("offline", (await world.AgentViewAsync(setup, agent.AgentId)).GetProperty("connectivity").GetString());
        var after = await world.ConnectivityOfAsync(agent.AgentId);
        Assert.Equal(before!.LastSeenAt, after!.LastSeenAt);
        Assert.Equal(before.LastHeartbeatAt, after.LastHeartbeatAt);
    }

    [Fact]
    public async Task LifecycleOutranksConnectivityForRevokedAndDisabledAgents()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var setup = await world.CreateTenantAsync();
        var revoked = await world.EnrollAsync(setup);
        var disabled = await world.EnrollAsync(setup);
        var healthy = await world.EnrollAsync(setup);
        foreach (var agent in new[] { revoked, disabled, healthy })
        {
            using var response = await world.HeartbeatAsync(agent);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        using var revoke = await setup.Admin.SendAsync(HttpMethod.Post, $"/api/tenants/{setup.Tenant.Id.Value}/agents/{revoked.AgentId}/revoke");
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        await using (var context = fixture.CreatePlatformContext())
        {
            (await context.Agents.SingleAsync(row => row.Id == new AgentId(disabled.AgentId))).Disable();
            await context.SaveChangesAsync();
        }

        // Each was seen moments ago, so liveness alone would say Online; the lifecycle state must win.
        var revokedView = await world.AgentViewAsync(setup, revoked.AgentId);
        var disabledView = await world.AgentViewAsync(setup, disabled.AgentId);
        Assert.Equal("online", revokedView.GetProperty("connectivity").GetString());
        Assert.Equal("Revoked", revokedView.GetProperty("operationalStatus").GetString());
        Assert.Equal("online", disabledView.GetProperty("connectivity").GetString());
        Assert.Equal("Disabled", disabledView.GetProperty("operationalStatus").GetString());
        Assert.Equal("Online", (await world.AgentViewAsync(setup, healthy.AgentId)).GetProperty("operationalStatus").GetString());
    }

    [Fact]
    public async Task RevokedAgentsAreListedOnlyWhenAskedForAndStayReadableById()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var setup = await world.CreateTenantAsync();
        var revoked = await world.EnrollAsync(setup);
        var healthy = await world.EnrollAsync(setup);
        using var revoke = await setup.Admin.SendAsync(HttpMethod.Post, $"/api/tenants/{setup.Tenant.Id.Value}/agents/{revoked.AgentId}/revoke");
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        var root = $"/api/tenants/{setup.Tenant.Id.Value}/agents";

        async Task<Guid[]> ListedAsync(string query)
        {
            using var response = await setup.Admin.GetAsync(root + query);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return [.. AgentWorld.ReadJson(response).EnumerateArray().Select(agent => agent.GetProperty("agentId").GetGuid())];
        }

        Assert.Equal([healthy.AgentId], await ListedAsync(""));
        Assert.Equal([healthy.AgentId], await ListedAsync("?includeRevoked=false"));
        Assert.Equal(new[] { revoked.AgentId, healthy.AgentId }.Order(), (await ListedAsync("?includeRevoked=true")).Order());
        Assert.Equal("Revoked", (await world.AgentViewAsync(setup, revoked.AgentId)).GetProperty("operationalStatus").GetString());
    }

    [Fact]
    public async Task SummaryAndFiltersCountOnlineOfflineUnknownAndLifecycleBucketsPerTenant()
    {
        var clock = new MutableClock();
        await using var world = await AgentWorld.CreateAsync(fixture, clock: clock);
        var setup = await world.CreateTenantAsync();
        var other = await world.CreateTenantAsync();
        var stale = await world.EnrollAsync(setup);
        using (await world.HeartbeatAsync(stale)) { }
        clock.Advance(TimeSpan.FromMinutes(5));
        var fresh = await world.EnrollAsync(setup);
        var silent = await world.EnrollAsync(setup);
        var revoked = await world.EnrollAsync(setup);
        using (await world.HeartbeatAsync(fresh)) { }
        using (await setup.Admin.SendAsync(HttpMethod.Post, $"/api/tenants/{setup.Tenant.Id.Value}/agents/{revoked.AgentId}/revoke")) { }
        var foreign = await world.EnrollAsync(other);
        using (await world.HeartbeatAsync(foreign)) { }

        using var summary = await setup.Admin.GetAsync($"/api/tenants/{setup.Tenant.Id.Value}/agents/connectivity-summary");
        var counts = AgentWorld.ReadJson(summary);
        Assert.Equal((1, 1, 1, 0, 1, 4),
            (counts.GetProperty("online").GetInt32(), counts.GetProperty("offline").GetInt32(), counts.GetProperty("unknown").GetInt32(),
             counts.GetProperty("disabled").GetInt32(), counts.GetProperty("revoked").GetInt32(), counts.GetProperty("total").GetInt32()));

        async Task<Guid[]> Ids(string filter)
        {
            using var response = await setup.Admin.GetAsync($"/api/tenants/{setup.Tenant.Id.Value}/agents?includeRevoked=true&connectivity={filter}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return AgentWorld.ReadJson(response).EnumerateArray().Select(entry => entry.GetProperty("agentId").GetGuid()).Order().ToArray();
        }
        Assert.Equal([fresh.AgentId], await Ids("online"));
        Assert.Equal([stale.AgentId], await Ids("offline"));
        Assert.Equal(new[] { silent.AgentId, revoked.AgentId }.Order().ToArray(), await Ids("unknown"));
        using var bad = await setup.Admin.GetAsync($"/api/tenants/{setup.Tenant.Id.Value}/agents?connectivity=all");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        using var otherSummary = await other.Admin.GetAsync($"/api/tenants/{other.Tenant.Id.Value}/agents/connectivity-summary");
        Assert.Equal(1, AgentWorld.ReadJson(otherSummary).GetProperty("online").GetInt32());
        using var crossTenant = await other.Admin.GetAsync($"/api/tenants/{setup.Tenant.Id.Value}/agents/connectivity-summary");
        Assert.Equal(HttpStatusCode.Forbidden, crossTenant.StatusCode);
    }

    [Fact]
    public async Task ConnectivityViewsRequireTenantMembershipButAllowViewers()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var setup = await world.CreateTenantAsync();
        var viewer = await world.SeedUserAsync();
        await world.GrantAsync(setup.Tenant, viewer, BackupPlatform.Domain.Tenancy.TenantRole.Viewer);
        var session = await world.SignInAsync(viewer);
        using var list = await session.GetAsync($"/api/tenants/{setup.Tenant.Id.Value}/agents");
        using var summary = await session.GetAsync($"/api/tenants/{setup.Tenant.Id.Value}/agents/connectivity-summary");
        using var anonymous = await world.Anonymous.GetAsync($"/api/tenants/{setup.Tenant.Id.Value}/agents/connectivity-summary");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal(HttpStatusCode.OK, summary.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task StateIsComputedFromPersistedLastSeenAcrossABackendRestart()
    {
        var firstClock = new MutableClock();
        TenantSetup setup;
        EnrolledAgent agent;
        await using (var first = await AgentWorld.CreateAsync(fixture, clock: firstClock))
        {
            setup = await first.CreateTenantAsync();
            agent = await first.EnrollAsync(setup);
            using var heartbeat = await first.HeartbeatAsync(agent);
            Assert.Equal(HttpStatusCode.OK, heartbeat.StatusCode);
        }

        await using var restartedSoon = await AgentWorld.CreateAsync(fixture, clock: new MutableClock(firstClock.GetUtcNow().AddSeconds(30)));
        var soon = await restartedSoon.SignInAsync(setup.Account);
        using var soonList = await soon.GetAsync($"/api/tenants/{setup.Tenant.Id.Value}/agents");
        Assert.Equal("online", AgentWorld.ReadJson(soonList).EnumerateArray().Single().GetProperty("connectivity").GetString());

        await using var restartedLate = await AgentWorld.CreateAsync(fixture, clock: new MutableClock(firstClock.GetUtcNow().AddMinutes(10)));
        var late = await restartedLate.SignInAsync(setup.Account);
        using var lateList = await late.GetAsync($"/api/tenants/{setup.Tenant.Id.Value}/agents");
        Assert.Equal("offline", AgentWorld.ReadJson(lateList).EnumerateArray().Single().GetProperty("connectivity").GetString());
        using var resumed = await restartedLate.HeartbeatAsync(agent);
        Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);
        using var afterResume = await late.GetAsync($"/api/tenants/{setup.Tenant.Id.Value}/agents");
        Assert.Equal("online", AgentWorld.ReadJson(afterResume).EnumerateArray().Single().GetProperty("connectivity").GetString());
    }

    [Fact]
    public async Task ABackendThatNoLongerSupportsTheReportedProtocolShowsVersionMismatch()
    {
        var clock = new MutableClock();
        TenantSetup setup;
        EnrolledAgent agent;
        await using (var first = await AgentWorld.CreateAsync(fixture, clock: clock))
        {
            setup = await first.CreateTenantAsync();
            agent = await first.EnrollAsync(setup);
            using var heartbeat = await first.HeartbeatAsync(agent);
        }
        var upgraded = new AgentSecurityOptions
        {
            Protocol = new ProtocolNegotiationContract(new ProtocolVersion(2, 0), new ProtocolVersionRange(new ProtocolVersion(2, 0), new ProtocolVersion(2, 0)))
        };
        await using var second = await AgentWorld.CreateAsync(fixture, upgraded, clock: new MutableClock(clock.GetUtcNow().AddSeconds(10)));
        var admin = await second.SignInAsync(setup.Account);
        using var list = await admin.GetAsync($"/api/tenants/{setup.Tenant.Id.Value}/agents");
        var view = AgentWorld.ReadJson(list).EnumerateArray().Single();
        Assert.Equal("online", view.GetProperty("connectivity").GetString());
        Assert.Equal("VersionMismatch", view.GetProperty("operationalStatus").GetString());
        Assert.Equal("1.0", view.GetProperty("protocolVersion").GetString());
    }

    [Fact]
    public async Task PollsAndConnectsAreASignOfLifeButAreWriteCoalescedWhileHeartbeatsAlwaysWrite()
    {
        var clock = new MutableClock();
        await using var world = await AgentWorld.CreateAsync(fixture, clock: clock);
        var agent = await world.EnrollAsync(await world.CreateTenantAsync());

        using (await world.PollAsync(agent)) { }
        var first = (await world.ConnectivityOfAsync(agent.AgentId))!;
        Assert.Equal(clock.GetUtcNow(), first.LastSeenAt);
        Assert.Null(first.LastHeartbeatAt);
        Assert.Null(first.ReportedAgentVersion);

        clock.Advance(TimeSpan.FromSeconds(5));
        using (await world.PollAsync(agent)) { }
        Assert.Equal(first.LastSeenAt, (await world.ConnectivityOfAsync(agent.AgentId))!.LastSeenAt);

        clock.Advance(TimeSpan.FromSeconds(6));
        using (await world.PollAsync(agent)) { }
        Assert.Equal(clock.GetUtcNow(), (await world.ConnectivityOfAsync(agent.AgentId))!.LastSeenAt);

        clock.Advance(TimeSpan.FromSeconds(1));
        using (await world.HeartbeatAsync(agent)) { }
        var heartbeat = (await world.ConnectivityOfAsync(agent.AgentId))!;
        Assert.Equal(clock.GetUtcNow(), heartbeat.LastSeenAt);
        Assert.Equal(clock.GetUtcNow(), heartbeat.LastHeartbeatAt);
        Assert.Equal("1.2.3", heartbeat.ReportedAgentVersion);
    }

    [Fact]
    public async Task CapabilityRevisionChangesAreRecordedWithoutSendingCapabilities()
    {
        var clock = new MutableClock();
        await using var world = await AgentWorld.CreateAsync(fixture, clock: clock);
        var agent = await world.EnrollAsync(await world.CreateTenantAsync());
        using (await world.HeartbeatAsync(agent, AgentWorld.Heartbeat(capabilityRevision: "r1"))) { }
        Assert.Equal("r1", (await world.ConnectivityOfAsync(agent.AgentId))!.CapabilityRevision);
        clock.Advance(TimeSpan.FromSeconds(30));
        using (await world.HeartbeatAsync(agent, AgentWorld.Heartbeat(capabilityRevision: "r2"))) { }
        Assert.Equal("r2", (await world.ConnectivityOfAsync(agent.AgentId))!.CapabilityRevision);
        clock.Advance(TimeSpan.FromSeconds(30));
        using (await world.HeartbeatAsync(agent, AgentWorld.Heartbeat())) { }
        Assert.Null((await world.ConnectivityOfAsync(agent.AgentId))!.CapabilityRevision);
        // Recording a revision never stores capabilities: only an explicit sync does.
        Assert.Empty(await world.CapabilityRowsOfAsync(agent.AgentId));
    }

    [Fact]
    public async Task LifecycleLookupReportsEachKnownAgentAndOmitsUnknownOnes()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var setup = await world.CreateTenantAsync();
        var active = await world.EnrollAsync(setup);
        var revoked = await world.EnrollAsync(setup);
        using (await setup.Admin.SendAsync(HttpMethod.Post, $"/api/tenants/{setup.Tenant.Id.Value}/agents/{revoked.AgentId}/revoke")) { }
        using var scope = world.Factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentConnectivityStore>();
        var unknown = AgentId.New();
        var result = await store.GetLifecycleAsync([new AgentId(active.AgentId), new AgentId(revoked.AgentId), unknown], CancellationToken.None);
        Assert.Equal(BackupPlatform.Domain.Agents.AgentStatus.Active, result[new AgentId(active.AgentId)]);
        Assert.Equal(BackupPlatform.Domain.Agents.AgentStatus.Revoked, result[new AgentId(revoked.AgentId)]);
        Assert.False(result.ContainsKey(unknown));
        Assert.Empty(await store.GetLifecycleAsync([], CancellationToken.None));
    }

    [Fact]
    public async Task ConcurrentHeartbeatsFromOneAgentLeaveOneConsistentRow()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var agent = await world.EnrollAsync(await world.CreateTenantAsync());
        var statuses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(async () =>
        {
            using var response = await world.HeartbeatAsync(agent);
            return response.StatusCode;
        })));
        Assert.All(statuses, status => Assert.Equal(HttpStatusCode.OK, status));
        await using var context = fixture.CreatePlatformContext();
        Assert.Equal(1, await context.AgentConnectivity.CountAsync(row => row.AgentId == new AgentId(agent.AgentId)));
    }
}
