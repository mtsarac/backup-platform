using System.Net;
using System.Text.Json;
using BackupPlatform.Contracts.Discovery;
using BackupPlatform.Domain.Tenancy;

namespace BackupPlatform.IntegrationTests;

/// <summary>Heartbeat directive, claim/report fencing, inventory merge, tenant isolation and role checks of database discovery, through real HTTP.</summary>
public sealed class DiscoveryApiIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private const string ScansPath = "/api/agent/discovery/scans";

    private static async Task<string?> ScanReasonAsync(AgentWorld world, EnrolledAgent agent)
    {
        using var response = await world.HeartbeatAsync(agent);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var value = AgentWorld.ReadJson(response).GetProperty("discoveryScanReason");
        return value.ValueKind == JsonValueKind.Null ? null : value.GetString();
    }

    private static async Task DeclareDiscoveryAsync(AgentWorld world, EnrolledAgent agent)
    {
        using var response = await world.SyncCapabilitiesAsync(agent, AgentWorld.CapabilitySnapshot(AgentWorld.CapabilityReport(DiscoveryProtocol.Capability)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<JsonElement?> ClaimAsync(AgentWorld world, EnrolledAgent agent)
    {
        using var response = await world.SendAgentAsync(HttpMethod.Post, ScansPath, agent.CredentialId, agent.Secret);
        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return AgentWorld.ReadJson(response);
    }

    private static Task<HttpResponseMessage> ReportAsync(AgentWorld world, EnrolledAgent agent, Guid scanId, object report) =>
        world.SendAgentAsync(HttpMethod.Put, $"{ScansPath}/{scanId}", agent.CredentialId, agent.Secret, body: report);

    private static AgentDiscoveryReport SqlServerReport(string access = DiscoveryAccess.CredentialsRequired, IReadOnlyList<AgentDiscoveredDatabase>? databases = null) => new()
    {
        Outcome = DiscoveryOutcomes.Completed,
        Sources = [new AgentDiscoverySourceStatus { Source = DiscoverySources.Service, Status = DiscoverySourceStates.Scanned }],
        Instances =
        [
            new AgentDiscoveredInstance
            {
                InstanceKey = "mssql|service|default", Engine = DiscoveryEngines.MsSql, Source = DiscoverySources.Service, DisplayName = "SQL Server (default)",
                Port = 1433, Version = "16.0.1000", Reachability = DiscoveryReachability.Reachable, Access = access, Databases = databases
            }
        ]
    };

    private static string Root(TenantSetup setup) => $"/api/tenants/{setup.Tenant.Id.Value}/discovery";

    private static async Task<JsonElement> InstancesAsync(TenantSetup setup, BrowserSession? session = null)
    {
        using var response = await (session ?? setup.Admin).GetAsync(Root(setup) + "/instances");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return AgentWorld.ReadJson(response).GetProperty("items");
    }

    [Fact]
    public async Task A_discovery_capable_agent_gets_one_exclusive_initial_scan_and_its_report_fills_the_inventory_without_listing_databases()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var setup = await world.CreateTenantAsync();
        var agent = await world.EnrollAsync(setup);

        Assert.Null(await ScanReasonAsync(world, agent));
        await DeclareDiscoveryAsync(world, agent);
        Assert.Equal(DiscoveryScanReasons.Initial, await ScanReasonAsync(world, agent));

        var plan = (await ClaimAsync(world, agent))!.Value;
        Assert.Equal(DiscoveryScanReasons.Initial, plan.GetProperty("reason").GetString());
        Assert.Null(await ClaimAsync(world, agent));
        Assert.Null(await ScanReasonAsync(world, agent));

        using var report = await ReportAsync(world, agent, plan.GetProperty("scanId").GetGuid(), SqlServerReport());
        Assert.Equal(HttpStatusCode.NoContent, report.StatusCode);

        var item = Assert.Single((await InstancesAsync(setup)).EnumerateArray());
        Assert.Equal("CredentialsRequired", item.GetProperty("access").GetString());
        Assert.Equal(0, item.GetProperty("databaseCounts").GetProperty("total").GetInt32());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("databasesCheckedAt").ValueKind);
        Assert.Equal("Discovered", item.GetProperty("lastChange").GetProperty("kind").GetString());
    }

    [Fact]
    public async Task A_report_for_a_stale_scan_is_a_conflict_and_a_malformed_report_a_bad_request()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var setup = await world.CreateTenantAsync();
        var agent = await world.EnrollAsync(setup);
        await DeclareDiscoveryAsync(world, agent);
        await ScanReasonAsync(world, agent);
        var scanId = (await ClaimAsync(world, agent))!.Value.GetProperty("scanId").GetGuid();

        using var unknown = await ReportAsync(world, agent, Guid.NewGuid(), SqlServerReport());
        Assert.Equal(HttpStatusCode.Conflict, unknown.StatusCode);

        using var malformed = await ReportAsync(world, agent, scanId, SqlServerReport() with { Outcome = "Done" });
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);

        // The malformed report did not consume the claim.
        using var accepted = await ReportAsync(world, agent, scanId, SqlServerReport());
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        using var replay = await ReportAsync(world, agent, scanId, SqlServerReport());
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
    }

    [Fact]
    public async Task Tenants_never_see_each_others_inventory_and_every_mutation_needs_its_role()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var setup = await world.CreateTenantAsync();
        var other = await world.CreateTenantAsync();
        var agent = await world.EnrollAsync(setup);
        await DeclareDiscoveryAsync(world, agent);
        await ScanReasonAsync(world, agent);
        var plan = (await ClaimAsync(world, agent))!.Value;
        (await ReportAsync(world, agent, plan.GetProperty("scanId").GetGuid(), SqlServerReport())).Dispose();
        var instanceId = (await InstancesAsync(setup)).EnumerateArray().Single().GetProperty("id").GetGuid();

        Assert.Empty((await InstancesAsync(other)).EnumerateArray());
        using (var foreignRead = await other.Admin.GetAsync($"/api/tenants/{other.Tenant.Id.Value}/discovery/instances/{instanceId}"))
            Assert.Equal(HttpStatusCode.NotFound, foreignRead.StatusCode);
        using (var foreignBind = await other.Admin.SendAsync(HttpMethod.Put, $"/api/tenants/{other.Tenant.Id.Value}/discovery/instances/{instanceId}/credential",
            new { credentialReference = "x", integratedSecurity = false, encrypt = true, trustServerCertificate = false }))
            Assert.Equal(HttpStatusCode.NotFound, foreignBind.StatusCode);

        var viewerAccount = await world.SeedUserAsync();
        await world.GrantAsync(setup.Tenant, viewerAccount, TenantRole.Viewer);
        var viewer = await world.SignInAsync(viewerAccount);
        Assert.Single((await InstancesAsync(setup, viewer)).EnumerateArray());
        using (var rescan = await viewer.SendAsync(HttpMethod.Post, $"{Root(setup)}/agents/{agent.AgentId}/rescan"))
            Assert.Equal(HttpStatusCode.Forbidden, rescan.StatusCode);
        using (var bind = await viewer.SendAsync(HttpMethod.Put, $"{Root(setup)}/instances/{instanceId}/credential", new { credentialReference = "x", integratedSecurity = false }))
            Assert.Equal(HttpStatusCode.Forbidden, bind.StatusCode);

        var operatorAccount = await world.SeedUserAsync();
        await world.GrantAsync(setup.Tenant, operatorAccount, TenantRole.Operator);
        var operatorSession = await world.SignInAsync(operatorAccount);
        using (var settings = await operatorSession.SendAsync(HttpMethod.Put, $"{Root(setup)}/agents/{agent.AgentId}/settings", new { enabled = true, scanIntervalMinutes = 60, includeDocker = true }))
            Assert.Equal(HttpStatusCode.Forbidden, settings.StatusCode);
        using (var adminSettings = await setup.Admin.SendAsync(HttpMethod.Put, $"{Root(setup)}/agents/{agent.AgentId}/settings", new { enabled = true, scanIntervalMinutes = 60, includeDocker = false }))
        {
            Assert.Equal(HttpStatusCode.OK, adminSettings.StatusCode);
            Assert.Equal(60, AgentWorld.ReadJson(adminSettings).GetProperty("scanIntervalMinutes").GetInt32());
        }
        using (var outOfRange = await setup.Admin.SendAsync(HttpMethod.Put, $"{Root(setup)}/agents/{agent.AgentId}/settings", new { enabled = true, scanIntervalMinutes = 1, includeDocker = true }))
            Assert.Equal(HttpStatusCode.BadRequest, outOfRange.StatusCode);
    }

    [Fact]
    public async Task A_bound_credential_is_verified_by_a_targeted_scan_whose_plan_carries_only_the_reference()
    {
        var clock = new MutableClock();
        await using var world = await AgentWorld.CreateAsync(fixture, clock: clock);
        var setup = await world.CreateTenantAsync();
        var agent = await world.EnrollAsync(setup);
        await DeclareDiscoveryAsync(world, agent);
        await ScanReasonAsync(world, agent);
        var plan = (await ClaimAsync(world, agent))!.Value;
        (await ReportAsync(world, agent, plan.GetProperty("scanId").GetGuid(), SqlServerReport())).Dispose();
        var instanceId = (await InstancesAsync(setup)).EnumerateArray().Single().GetProperty("id").GetGuid();

        using (var early = await setup.Admin.SendAsync(HttpMethod.Post, $"{Root(setup)}/instances/{instanceId}/verify"))
            Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);

        using (var bind = await setup.Admin.SendAsync(HttpMethod.Put, $"{Root(setup)}/instances/{instanceId}/credential",
            new { credentialReference = "sql-main", integratedSecurity = false, encrypt = true, trustServerCertificate = true }))
        {
            Assert.Equal(HttpStatusCode.OK, bind.StatusCode);
            Assert.True(AgentWorld.ReadJson(bind).GetProperty("credential").GetProperty("bound").GetBoolean());
        }

        clock.Advance(TimeSpan.FromMinutes(1));
        using (var verify = await setup.Admin.SendAsync(HttpMethod.Post, $"{Root(setup)}/instances/{instanceId}/verify"))
            Assert.Equal(HttpStatusCode.Accepted, verify.StatusCode);
        using (var tooSoon = await setup.Admin.SendAsync(HttpMethod.Post, $"{Root(setup)}/agents/{agent.AgentId}/rescan"))
            Assert.Equal(HttpStatusCode.TooManyRequests, tooSoon.StatusCode);

        Assert.Equal(DiscoveryScanReasons.Manual, await ScanReasonAsync(world, agent));
        var manual = (await ClaimAsync(world, agent))!.Value;
        Assert.Equal("mssql|service|default", manual.GetProperty("onlyInstanceKey").GetString());
        var binding = Assert.Single(manual.GetProperty("bindings").EnumerateArray());
        Assert.Equal("sql-main", binding.GetProperty("credentialReference").GetString());
        Assert.DoesNotContain("password", manual.GetRawText(), StringComparison.OrdinalIgnoreCase);

        var databases = new List<AgentDiscoveredDatabase>
        {
            new() { Name = "master", IsSystem = true, State = DiscoveryDatabaseStates.Online, FullBackup = DiscoverySupportNames.Unsupported },
            new() { Name = "Sales", State = DiscoveryDatabaseStates.Online, FullBackup = DiscoverySupportNames.Supported }
        };
        (await ReportAsync(world, agent, manual.GetProperty("scanId").GetGuid(), SqlServerReport(DiscoveryAccess.Authenticated, databases))).Dispose();

        using var detail = await setup.Admin.GetAsync($"{Root(setup)}/instances/{instanceId}");
        var json = AgentWorld.ReadJson(detail);
        Assert.Equal(2, json.GetProperty("databases").GetArrayLength());
        Assert.Equal(1, json.GetProperty("databaseCounts").GetProperty("backupEligible").GetInt32());
        Assert.Equal("Authenticated", json.GetProperty("access").GetString());
    }
}
