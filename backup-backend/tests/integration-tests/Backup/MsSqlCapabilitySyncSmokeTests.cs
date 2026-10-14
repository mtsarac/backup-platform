using System.Diagnostics;
using System.Net;
using Xunit.Abstractions;

namespace BackupPlatform.IntegrationTests.Backup;

/// <summary>
/// A run is admitted only once the agent has reported that it can execute it (fail-closed). A freshly started real host reports its capabilities after its first
/// heartbeat; until then a run start is refused with the explicit admission code, and it succeeds on its own shortly after, without any fixture help.
/// </summary>
public sealed class MsSqlCapabilitySyncSmokeTests(PostgreSqlFixture database, ITestOutputHelper output) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task A_run_is_refused_until_the_real_host_has_synced_its_capabilities_and_then_admitted_without_intervention()
    {
        await using var sql = new OrchestrationSqlSource(rows: 20);
        await sql.InitializeAsync();
        await using var world = await AgentWorld.CreateAsync(database, clock: new MutableClock(live: true), kestrel: true);
        var tenant = await world.CreateTenantAsync();
        await world.EntitleAsync(tenant.Tenant);
        var agent = await world.EnrollAsync(tenant);
        var root = $"/api/tenants/{tenant.Tenant.Id.Value}";
        using var created = await tenant.Admin.SendAsync(HttpMethod.Post, root + "/backup-jobs",
            new { name = "Capability sync", agentId = agent.AgentId, configuration = MsSqlManualAgentSmokeTests.Configuration(sql, sql.DatabaseName) });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var jobId = AgentWorld.ReadJson(created).GetProperty("id").GetGuid();

        async Task<(HttpStatusCode Status, string Code)> TryStartAsync()
        {
            using var csrf = await tenant.Admin.GetAsync("/api/auth/antiforgery");
            using var request = new HttpRequestMessage(HttpMethod.Post, root + $"/backup-jobs/{jobId}/runs");
            request.Headers.Add("X-CSRF-TOKEN", AgentWorld.ReadJson(csrf).GetProperty("requestToken").GetString());
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
            using var response = await tenant.Admin.Client.SendAsync(request);
            var body = AgentWorld.ReadJson(response);
            return (response.StatusCode, body.TryGetProperty("code", out var code) ? code.GetString()! : "");
        }

        Assert.Equal((HttpStatusCode.Conflict, "agent_multi_destination_unsupported"), await TryStartAsync());

        await using var host = await AgentHostSmokeProcess.StartAsync(world, agent, output.WriteLine);
        var watch = Stopwatch.StartNew();
        (HttpStatusCode Status, string Code) outcome;
        do
        {
            host.AssertAlive();
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(45), "the host did not sync its capabilities in time");
            await Task.Delay(250);
            outcome = await TryStartAsync();
        }
        while (outcome.Status != HttpStatusCode.OK);

        output.WriteLine("run admitted {0:F1} s after the host started (real capability sync, no fixture)", watch.Elapsed.TotalSeconds);
    }
}
