using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Domain.Agents;
using BackupPlatform.Domain.Capabilities;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.IntegrationTests;

public sealed class AgentCapabilitySyncIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static readonly AgentCapabilitySyncRequest Full = AgentWorld.CapabilitySnapshot(
        AgentWorld.CapabilityReport("mssql.backup.full", "Supported", new Dictionary<string, string> { ["scope"] = "agent" }),
        AgentWorld.CapabilityReport("mssql.backup.checksum", "Supported", new Dictionary<string, string> { ["scope"] = "agent" }),
        AgentWorld.CapabilityReport("mssql.backup.log", "Unsupported"));

    private static readonly AgentCapabilitySyncRequest Reduced = AgentWorld.CapabilitySnapshot(
        AgentWorld.CapabilityReport("mssql.backup.full", "Unknown"),
        AgentWorld.CapabilityReport("mssql.backup.differential", "Unsupported"));

    private static async Task<bool> SyncRequestedAsync(AgentWorld world, EnrolledAgent agent, string? revision)
    {
        using var response = await world.HeartbeatAsync(agent, AgentWorld.Heartbeat(capabilityRevision: revision));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return AgentWorld.ReadJson(response).GetProperty("capabilitySyncRequested").GetBoolean();
    }

    private static async Task<JsonResult> PutAsync(AgentWorld world, EnrolledAgent agent, object body, string? protocol = "1.0")
    {
        using var response = await world.SyncCapabilitiesAsync(agent, body, protocol);
        var text = await response.Content.ReadAsStringAsync();
        return new JsonResult(response.StatusCode, text, text.Length == 0 ? default : AgentWorld.ReadJson(response));
    }

    private sealed record JsonResult(HttpStatusCode Status, string Text, System.Text.Json.JsonElement Json);

    [Fact]
    public async Task AnUnknownRevisionRequestsASyncAndTheSyncClearsTheRequestUntilTheRevisionChanges()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var agent = await world.EnrollAsync(await world.CreateTenantAsync());

        Assert.True(await SyncRequestedAsync(world, agent, Full.Revision));

        var sync = await PutAsync(world, agent, Full);
        Assert.Equal(HttpStatusCode.OK, sync.Status);
        Assert.Equal(Full.Revision, sync.Json.GetProperty("revision").GetString());
        Assert.True(sync.Json.GetProperty("changed").GetBoolean());

        Assert.False(await SyncRequestedAsync(world, agent, Full.Revision));
        Assert.False(await SyncRequestedAsync(world, agent, Full.Revision));
        // The agent's capabilities changed: a different revision asks again, and syncing it clears the request again.
        Assert.True(await SyncRequestedAsync(world, agent, Reduced.Revision));
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(world, agent, Reduced)).Status);
        Assert.False(await SyncRequestedAsync(world, agent, Reduced.Revision));
    }

    [Fact]
    public async Task AnAgentThatAdvertisesNoRevisionIsNeverAskedToSync()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var agent = await world.EnrollAsync(await world.CreateTenantAsync());

        Assert.False(await SyncRequestedAsync(world, agent, null));
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(world, agent, Full)).Status);
        Assert.False(await SyncRequestedAsync(world, agent, null));
    }

    [Fact]
    public async Task AChangedRevisionReplacesTheWholeSnapshotAndRefreshesTheServerTimestamp()
    {
        var clock = new MutableClock();
        await using var world = await AgentWorld.CreateAsync(fixture, clock: clock);
        var agent = await world.EnrollAsync(await world.CreateTenantAsync());
        var firstTime = clock.GetUtcNow();
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(world, agent, Full)).Status);
        var first = await world.CapabilityRowsOfAsync(agent.AgentId);
        Assert.Equal(["mssql.backup.checksum", "mssql.backup.full", "mssql.backup.log"], first.Select(row => row.Identifier));
        Assert.All(first, row => Assert.Equal((Full.Revision, firstTime), (row.Revision, row.ReportedAt)));
        Assert.Equal([CapabilitySupport.Supported, CapabilitySupport.Supported, CapabilitySupport.Unsupported], first.Select(row => row.Support));
        Assert.Null(first[2].MetadataJson);
        Assert.Contains("\"scope\"", first[0].MetadataJson, StringComparison.Ordinal);

        clock.Advance(TimeSpan.FromMinutes(5));
        var replaced = await PutAsync(world, agent, Reduced);

        Assert.True(replaced.Json.GetProperty("changed").GetBoolean());
        var rows = await world.CapabilityRowsOfAsync(agent.AgentId);
        Assert.Equal(["mssql.backup.differential", "mssql.backup.full"], rows.Select(row => row.Identifier));
        Assert.Equal([CapabilitySupport.Unsupported, CapabilitySupport.Unknown], rows.Select(row => row.Support));
        Assert.All(rows, row => Assert.Equal((Reduced.Revision, clock.GetUtcNow()), (row.Revision, row.ReportedAt)));
    }

    [Fact]
    public async Task ASyncWithTheStoredRevisionIsIdempotentAndWritesNothing()
    {
        var clock = new MutableClock();
        await using var world = await AgentWorld.CreateAsync(fixture, clock: clock);
        var agent = await world.EnrollAsync(await world.CreateTenantAsync());
        var storedAt = clock.GetUtcNow();
        Assert.True((await PutAsync(world, agent, Full)).Json.GetProperty("changed").GetBoolean());
        var before = await world.CapabilityRowsOfAsync(agent.AgentId);

        clock.Advance(TimeSpan.FromMinutes(10));
        var repeated = await PutAsync(world, agent, Full);

        Assert.Equal(HttpStatusCode.OK, repeated.Status);
        Assert.False(repeated.Json.GetProperty("changed").GetBoolean());
        Assert.Equal(Full.Revision, repeated.Json.GetProperty("revision").GetString());
        var after = await world.CapabilityRowsOfAsync(agent.AgentId);
        Assert.Equal(before.Select(row => (row.Identifier, row.Support, row.MetadataJson, row.Revision)),
            after.Select(row => (row.Identifier, row.Support, row.MetadataJson, row.Revision)));
        Assert.All(after, row => Assert.Equal(storedAt, row.ReportedAt));
    }

    [Fact]
    public async Task ConcurrentDuplicateSyncsStoreOneSnapshotAndExactlyOneReportsAChange()
    {
        await using var world = await AgentWorld.CreateAsync(fixture, limits: new BackupPlatform.Api.Security.AgentRateLimitOptions { HeartbeatPerAgent = 1000 });
        var agent = await world.EnrollAsync(await world.CreateTenantAsync());

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => PutAsync(world, agent, Full)));

        Assert.All(results, result => Assert.Equal(HttpStatusCode.OK, result.Status));
        Assert.Single(results, result => result.Json.GetProperty("changed").GetBoolean());
        var rows = await world.CapabilityRowsOfAsync(agent.AgentId);
        Assert.Equal(3, rows.Count);
        Assert.All(rows, row => Assert.Equal(Full.Revision, row.Revision));
    }

    [Fact]
    public async Task ConcurrentSyncsOfDifferentRevisionsLeaveExactlyOneConsistentSnapshot()
    {
        await using var world = await AgentWorld.CreateAsync(fixture, limits: new BackupPlatform.Api.Security.AgentRateLimitOptions { HeartbeatPerAgent = 1000 });
        var agent = await world.EnrollAsync(await world.CreateTenantAsync());

        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(index => PutAsync(world, agent, index % 2 == 0 ? Full : Reduced)));

        Assert.All(results, result => Assert.Equal(HttpStatusCode.OK, result.Status));
        var rows = await world.CapabilityRowsOfAsync(agent.AgentId);
        var winner = Assert.Single(rows.Select(row => row.Revision).Distinct());
        var expected = winner == Full.Revision ? Full : Reduced;
        Assert.Equal(expected.Revision, winner);
        Assert.Equal(expected.Capabilities.Select(capability => capability.Identifier).Order(StringComparer.Ordinal), rows.Select(row => row.Identifier));
        Assert.Single(rows.Select(row => row.ReportedAt).Distinct());
    }

    [Fact]
    public async Task ARevisionThatDoesNotMatchTheCapabilitiesIsRejectedAndTheStoredSnapshotSurvives()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var agent = await world.EnrollAsync(await world.CreateTenantAsync());
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(world, agent, Full)).Status);

        var wrong = await PutAsync(world, agent, Reduced with { Revision = Full.Revision });
        var forged = await PutAsync(world, agent, Reduced with { Revision = "c1-" + new string('a', 48) });
        var blank = await PutAsync(world, agent, Reduced with { Revision = "" });

        foreach (var result in new[] { wrong, forged, blank })
        {
            Assert.Equal(HttpStatusCode.BadRequest, result.Status);
            Assert.Equal("invalid_request", result.Json.GetProperty("error").GetString());
            Assert.Contains("Revision", result.Json.GetProperty("message").GetString(), StringComparison.Ordinal);
        }
        Assert.All(await world.CapabilityRowsOfAsync(agent.AgentId), row => Assert.Equal(Full.Revision, row.Revision));
    }

    public static TheoryData<string, string, string> InvalidSnapshots() => new()
    {
        { "Capabilities", "empty set", "{\"revision\":\"x\",\"capabilities\":[]}" },
        { "Capabilities", "null set", "{\"revision\":\"x\",\"capabilities\":null}" },
        { "Capabilities", "null entry", "{\"revision\":\"x\",\"capabilities\":[null]}" },
        { "Capabilities.Identifier", "bad grammar", "{\"revision\":\"x\",\"capabilities\":[{\"identifier\":\"Secret-Value\",\"support\":\"Supported\"}]}" },
        { "Capabilities.Identifier", "missing", "{\"revision\":\"x\",\"capabilities\":[{\"support\":\"Supported\"}]}" },
        { "Capabilities.Identifier", "duplicate", "{\"revision\":\"x\",\"capabilities\":[{\"identifier\":\"mssql.a\",\"support\":\"Supported\"},{\"identifier\":\"mssql.a\",\"support\":\"Unsupported\"}]}" },
        { "Capabilities.Support", "numeric text", "{\"revision\":\"x\",\"capabilities\":[{\"identifier\":\"mssql.a\",\"support\":\"1\"}]}" },
        { "Capabilities.Support", "unknown name", "{\"revision\":\"x\",\"capabilities\":[{\"identifier\":\"mssql.a\",\"support\":\"Maybe\"}]}" },
        { "Capabilities.Support", "wrong case", "{\"revision\":\"x\",\"capabilities\":[{\"identifier\":\"mssql.a\",\"support\":\"supported\"}]}" },
        { "Capabilities.Metadata", "null value", "{\"revision\":\"x\",\"capabilities\":[{\"identifier\":\"mssql.a\",\"support\":\"Supported\",\"metadata\":{\"k\":null}}]}" },
        { "Capabilities.Metadata", "blank value", "{\"revision\":\"x\",\"capabilities\":[{\"identifier\":\"mssql.a\",\"support\":\"Supported\",\"metadata\":{\"k\":\"  \"}}]}" },
        { "Capabilities.Metadata", "control character", "{\"revision\":\"x\",\"capabilities\":[{\"identifier\":\"mssql.a\",\"support\":\"Supported\",\"metadata\":{\"k\":\"a\\u0000b\"}}]}" },
        { "Capabilities.Metadata", "oversized key", "{\"revision\":\"x\",\"capabilities\":[{\"identifier\":\"mssql.a\",\"support\":\"Supported\",\"metadata\":{\"" + new string('k', 65) + "\":\"v\"}}]}" },
        { "Capabilities.Metadata", "oversized value", "{\"revision\":\"x\",\"capabilities\":[{\"identifier\":\"mssql.a\",\"support\":\"Supported\",\"metadata\":{\"k\":\"" + new string('v', 257) + "\"}}]}" },
        { "Capabilities.Metadata", "too many entries", "{\"revision\":\"x\",\"capabilities\":[{\"identifier\":\"mssql.a\",\"support\":\"Supported\",\"metadata\":{" + string.Join(',', Enumerable.Range(0, 9).Select(i => $"\"k{i}\":\"v\"")) + "}}]}" },
        { "Capabilities", "too many capabilities", "{\"revision\":\"x\",\"capabilities\":[" + string.Join(',', Enumerable.Range(0, 129).Select(i => $"{{\"identifier\":\"mssql.c{i}\",\"support\":\"Supported\"}}")) + "]}" }
    };

    [Theory]
    [MemberData(nameof(InvalidSnapshots))]
    public async Task InvalidSnapshotsAreRejectedNamingOnlyTheFieldAndTheStoredSnapshotSurvives(string field, string reason, string json)
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var agent = await world.EnrollAsync(await world.CreateTenantAsync());
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(world, agent, Full)).Status);

        using var response = await SendRawAsync(world, agent, json);

        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, reason);
        var error = AgentWorld.ReadJson(response);
        Assert.Equal("invalid_request", error.GetProperty("error").GetString());
        var message = error.GetProperty("message").GetString()!;
        Assert.Contains($"Invalid field: {field}.", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret-Value", message, StringComparison.Ordinal);
        var rows = await world.CapabilityRowsOfAsync(agent.AgentId);
        Assert.Equal(3, rows.Count);
        Assert.All(rows, row => Assert.Equal(Full.Revision, row.Revision));
    }

    [Theory]
    [InlineData("{\"revision\":\"x\",\"capabilities\":[{\"identifier\":\"mssql.a\",\"support\":1}]}")]
    [InlineData("{\"revision\":\"x\",\"capabilities\":\"not-a-list\"}")]
    [InlineData("not json")]
    public async Task MalformedBodiesAreRejectedWithoutStoringAnything(string json)
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var agent = await world.EnrollAsync(await world.CreateTenantAsync());

        using var response = await SendRawAsync(world, agent, json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await world.CapabilityRowsOfAsync(agent.AgentId));
    }

    private static Task<HttpResponseMessage> SendRawAsync(AgentWorld world, EnrolledAgent agent, string json)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, "/api/agent/capabilities")
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("Authorization", AgentAuthorizationScheme.FormatHeaderValue(agent.CredentialId, agent.Secret));
        request.Headers.TryAddWithoutValidation(AgentProtocolHeader.Name, "1.0");
        return world.Anonymous.SendAsync(request);
    }

    [Fact]
    public async Task TheBodyAndQueryCannotChooseTheAgentOrTenantTheSnapshotIsStoredFor()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var tenantA = await world.CreateTenantAsync();
        var tenantB = await world.CreateTenantAsync();
        var a = await world.EnrollAsync(tenantA);
        var b = await world.EnrollAsync(tenantB);

        using var response = await world.SendAgentAsync(HttpMethod.Put, $"/api/agent/capabilities?agentId={b.AgentId}&tenantId={b.TenantId}",
            a.CredentialId, a.Secret, "1.0", new
            {
                revision = Full.Revision,
                capabilities = Full.Capabilities,
                agentId = b.AgentId,
                tenantId = b.TenantId
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, (await world.CapabilityRowsOfAsync(a.AgentId)).Count);
        Assert.Empty(await world.CapabilityRowsOfAsync(b.AgentId));
        await using var context = fixture.CreatePlatformContext();
        Assert.All(await context.AgentCapabilities.AsNoTracking().Where(row => row.AgentId == new AgentId(a.AgentId)).ToListAsync(),
            row => Assert.Equal(new TenantId(a.TenantId), row.TenantId));
    }

    [Fact]
    public async Task AnAgentOfAnotherTenantNeverOverwritesOrReadsTheSnapshotOfAnAgentInThisTenant()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var tenantA = await world.CreateTenantAsync();
        var tenantB = await world.CreateTenantAsync();
        var a = await world.EnrollAsync(tenantA);
        var b = await world.EnrollAsync(tenantB);
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(world, a, Full)).Status);

        var other = await PutAsync(world, b, Reduced);
        // The same snapshot revision in two tenants is stored twice, independently.
        var same = await PutAsync(world, b, Full);

        Assert.True(other.Json.GetProperty("changed").GetBoolean());
        Assert.True(same.Json.GetProperty("changed").GetBoolean());
        Assert.Equal(3, (await world.CapabilityRowsOfAsync(a.AgentId)).Count);
        Assert.Equal(3, (await world.CapabilityRowsOfAsync(b.AgentId)).Count);
        Assert.False(await SyncRequestedAsync(world, a, Full.Revision));
        Assert.True(await SyncRequestedAsync(world, a, Reduced.Revision));

        var viewA = await world.AgentViewAsync(tenantA, a.AgentId);
        Assert.Equal(Full.Revision, viewA.GetProperty("capabilitiesRevision").GetString());
        using var list = await tenantA.Admin.GetAsync($"/api/tenants/{tenantA.Tenant.Id.Value}/agents");
        var entry = Assert.Single(AgentWorld.ReadJson(list).EnumerateArray());
        Assert.Equal(a.AgentId, entry.GetProperty("agentId").GetGuid());
        using var foreign = await tenantA.Admin.GetAsync($"/api/tenants/{tenantA.Tenant.Id.Value}/agents/{b.AgentId}");
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        using var foreignTenant = await tenantA.Admin.GetAsync($"/api/tenants/{tenantB.Tenant.Id.Value}/agents");
        Assert.Equal(HttpStatusCode.Forbidden, foreignTenant.StatusCode);
    }

    [Fact]
    public async Task TheStoreRejectsAnIdentityWhoseTenantDoesNotOwnTheAgent()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var a = await world.EnrollAsync(await world.CreateTenantAsync());
        var b = await world.EnrollAsync(await world.CreateTenantAsync());
        using var scope = world.Factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<BackupPlatform.Agents.Capabilities.IAgentCapabilityStore>();
        var capabilities = Full.Capabilities.Select(report => new Capability(new CapabilityIdentifier(report.Identifier), CapabilitySupport.Supported)).ToList();

        var crossTenant = new BackupPlatform.Application.Agents.AgentIdentity(new AgentId(a.AgentId), new TenantId(b.TenantId), a.CredentialId, 1);
        var outcome = await store.ReplaceSnapshotAsync(crossTenant, Full.Revision, capabilities, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(BackupPlatform.Agents.Capabilities.ReplaceSnapshotOutcome.AgentNotFound, outcome);
        Assert.Null(await store.GetRevisionAsync(crossTenant, CancellationToken.None));
        Assert.Empty(await world.CapabilityRowsOfAsync(a.AgentId));
    }

    [Fact]
    public async Task HeartbeatsCarryOnlyTheRevisionAndNeverStoreACapabilityList()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var agent = await world.EnrollAsync(await world.CreateTenantAsync());

        using var response = await world.HeartbeatAsync(agent, new
        {
            agentVersion = "1.2.3",
            platform = "linux",
            capabilityRevision = Full.Revision,
            capabilities = Full.Capabilities
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(AgentWorld.ReadJson(response).GetProperty("capabilitySyncRequested").GetBoolean());
        Assert.Empty(await world.CapabilityRowsOfAsync(agent.AgentId));
        Assert.DoesNotContain(typeof(AgentHeartbeatRequest).GetProperties(), property => property.PropertyType != typeof(string)
            && typeof(System.Collections.IEnumerable).IsAssignableFrom(property.PropertyType));
    }

    [Fact]
    public async Task AgentsWithoutASnapshotShowNoCapabilitiesAndTheListReturnsEachAgentsOwnSnapshot()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var setup = await world.CreateTenantAsync();
        var synced = await world.EnrollAsync(setup);
        var silent = await world.EnrollAsync(setup);
        var metadata = new Dictionary<string, string> { ["note"] = "İstanbul-ğüşiöç \"quoted\"" };
        var snapshot = AgentWorld.CapabilitySnapshot(AgentWorld.CapabilityReport("mssql.backup.full", "Supported", metadata));
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(world, synced, snapshot)).Status);

        using var list = await setup.Admin.GetAsync($"/api/tenants/{setup.Tenant.Id.Value}/agents");

        var entries = AgentWorld.ReadJson(list).EnumerateArray().ToDictionary(entry => entry.GetProperty("agentId").GetGuid());
        var withSnapshot = entries[synced.AgentId];
        Assert.Equal(snapshot.Revision, withSnapshot.GetProperty("capabilitiesRevision").GetString());
        var capability = Assert.Single(withSnapshot.GetProperty("capabilities").EnumerateArray());
        Assert.Equal("mssql.backup.full", capability.GetProperty("identifier").GetString());
        Assert.Equal("Supported", capability.GetProperty("support").GetString());
        Assert.Equal(metadata["note"], capability.GetProperty("metadata").GetProperty("note").GetString());
        var without = entries[silent.AgentId];
        Assert.Equal(System.Text.Json.JsonValueKind.Null, without.GetProperty("capabilitiesRevision").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, without.GetProperty("capabilitiesReportedAt").ValueKind);
        Assert.Empty(without.GetProperty("capabilities").EnumerateArray());
    }

    [Fact]
    public async Task RevokedAndDisabledAgentsCannotSyncAndNothingIsStored()
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
            Assert.Equal(HttpStatusCode.Unauthorized, (await PutAsync(world, agent, Full)).Status);
            Assert.Empty(await world.CapabilityRowsOfAsync(agent.AgentId));
        }
    }

    [Fact]
    public async Task TheRouteRequiresAnAgentCredentialAndTheProtocolHeaderAndRejectsBrowsers()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var setup = await world.CreateTenantAsync();
        var agent = await world.EnrollAsync(setup);

        using var anonymous = await world.Anonymous.PutAsJsonAsync("/api/agent/capabilities", Full);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var browser = await setup.Admin.SendAsync(HttpMethod.Put, "/api/agent/capabilities", Full);
        Assert.Equal(HttpStatusCode.Unauthorized, browser.StatusCode);
        var missingProtocol = await PutAsync(world, agent, Full, protocol: null);
        Assert.Equal(HttpStatusCode.BadRequest, missingProtocol.Status);
        Assert.Equal("protocol_missing", missingProtocol.Json.GetProperty("error").GetString());
        var incompatible = await PutAsync(world, agent, Full, protocol: "2.0");
        Assert.Equal("protocol_incompatible", incompatible.Json.GetProperty("error").GetString());
        Assert.Empty(await world.CapabilityRowsOfAsync(agent.AgentId));
    }

    [Fact]
    public async Task OversizedSyncBodiesAreRejectedOverRealSockets()
    {
        await using var world = await AgentWorld.CreateAsync(fixture, kestrel: true);
        var agent = await world.EnrollAsync(await world.CreateTenantAsync());

        var tooLarge = await PutAsync(world, agent, new { revision = Full.Revision, capabilities = Full.Capabilities, padding = new string('p', 40_000) });
        var normal = await PutAsync(world, agent, Full);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.Status);
        Assert.Equal(HttpStatusCode.OK, normal.Status);
    }

    [Fact]
    public async Task ExcessiveSyncsFromOneAgentAreRateLimitedWhileOtherAgentsAreUnaffected()
    {
        await using var world = await AgentWorld.CreateAsync(fixture, limits: new BackupPlatform.Api.Security.AgentRateLimitOptions { HeartbeatPerAgent = 2 });
        var setup = await world.CreateTenantAsync();
        var noisy = await world.EnrollAsync(setup);
        var quiet = await world.EnrollAsync(setup);

        Assert.Equal(HttpStatusCode.OK, (await PutAsync(world, noisy, Full)).Status);
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(world, noisy, Full)).Status);
        var limited = await PutAsync(world, noisy, Full);
        var other = await PutAsync(world, quiet, Full);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.Status);
        Assert.Equal(HttpStatusCode.OK, other.Status);
    }
}
