using System.Net;

namespace BackupPlatform.IntegrationTests;

/// <summary>A member of tenant B reaches nothing of tenant A: not through A's route, and not by naming A's ids under B's route.</summary>
public sealed class CrossTenantIsolationApiTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task MemberOfAnotherTenantCannotReadOrChangeJobsRunsAndAgents()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var jobId = await scenario.CreateJobAsync();
        var runId = await scenario.StartRunAsync(jobId);
        var other = await scenario.World.CreateTenantAsync();
        var theirs = BackupApiScenario.RootOf(other);
        var mine = scenario.Root;
        var agentId = scenario.Agent.AgentId;

        // Another tenant's route: membership is refused before any handler runs.
        foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
        {
            (HttpMethod.Get, $"{mine}/backup-jobs/{jobId}", null),
            (HttpMethod.Get, $"{mine}/backup-jobs", null),
            (HttpMethod.Get, $"{mine}/backup-runs/{runId}", null),
            (HttpMethod.Get, $"{mine}/agents/{agentId}", null),
            (HttpMethod.Get, $"{mine}/backup-artifacts", null),
            (HttpMethod.Get, $"{mine}/restores", null),
            (HttpMethod.Get, $"{mine}/backup-shares", null),
            (HttpMethod.Put, $"{mine}/backup-jobs/{jobId}", BackupApiScenario.JobBody(agentId)),
            (HttpMethod.Post, $"{mine}/backup-runs/{runId}/cancel", null),
            (HttpMethod.Post, $"{mine}/agents/{agentId}/revoke", null),
        })
        {
            using var response = await other.Admin.SendAsync(method, path, body);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        // Own route with the other tenant's ids: the rows do not exist for this tenant.
        foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
        {
            (HttpMethod.Get, $"{theirs}/backup-jobs/{jobId}", null),
            (HttpMethod.Get, $"{theirs}/backup-runs/{runId}", null),
            (HttpMethod.Get, $"{theirs}/agents/{agentId}", null),
            (HttpMethod.Put, $"{theirs}/backup-jobs/{jobId}", BackupApiScenario.JobBody(agentId)),
            (HttpMethod.Post, $"{theirs}/backup-runs/{runId}/cancel", null),
            (HttpMethod.Post, $"{theirs}/agents/{agentId}/revoke", null),
        })
        {
            using var response = await other.Admin.SendAsync(method, path, body);
            Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict or HttpStatusCode.BadRequest,
                $"{method} {path} answered {(int)response.StatusCode}");
        }

        using (var jobs = await other.Admin.GetAsync($"{theirs}/backup-jobs"))
            Assert.Empty(AgentWorld.ReadJson(jobs).EnumerateArray());
        using (var own = await scenario.Admin.GetAsync($"{mine}/backup-jobs/{jobId}"))
            Assert.Equal(HttpStatusCode.OK, own.StatusCode);
    }
}
