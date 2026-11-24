using System.Net;

namespace BackupPlatform.IntegrationTests;

public sealed class AgentLogApiTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task RetentionRemovesOnlyExpiredReceivedLogs()
    {
        var tenant = Guid.NewGuid();
        var agent = Guid.NewGuid();
        await fixture.ExecuteAsync($"""
            INSERT INTO agent_logs ("Id", "TenantId", "AgentId", "Timestamp", "ReceivedAt", "Code", "Message", "Level")
            VALUES ('{Guid.NewGuid()}', '{tenant}', '{agent}', now(), now() - interval '31 days', 'agent.started', 'Agent started', 'Information'),
                   ('{Guid.NewGuid()}', '{tenant}', '{agent}', now(), now(), 'agent.started', 'Agent started', 'Information')
            """);
        await using var world = await AgentWorld.CreateAsync(fixture);
        for (var attempt = 0; attempt < 50; attempt++)
        {
            await using var connection = new Npgsql.NpgsqlConnection(fixture.ConnectionString);
            await connection.OpenAsync();
            await using var command = new Npgsql.NpgsqlCommand("SELECT count(*) FROM agent_logs WHERE \"TenantId\" = @tenant", connection);
            command.Parameters.AddWithValue("tenant", tenant);
            if ((long)(await command.ExecuteScalarAsync())! == 1) return;
            await Task.Delay(100);
        }
        Assert.Fail("Expired logs were not removed while the recent log was retained.");
    }

    [Fact]
    public async Task AgentLogsAreIdempotentFilteredAndTenantIsolated()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var agent = scenario.Agent;
        var job = await scenario.CreateJobAsync();
        var id = Guid.NewGuid();
        var entries = new[] { new { id, timestamp = DateTimeOffset.UtcNow, code = "backup.stage_failed", jobId = job, runId = (Guid?)null, message = "password=hunter2" } };
        using var accepted = await scenario.World.SendAgentAsync(HttpMethod.Post, "/api/agent/logs", agent.CredentialId, agent.Secret, body: entries);
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        using var replay = await scenario.World.SendAgentAsync(HttpMethod.Post, "/api/agent/logs", agent.CredentialId, agent.Secret, body: entries);
        Assert.Equal(HttpStatusCode.NoContent, replay.StatusCode);
        using var response = await scenario.Admin.GetAsync(scenario.Root + $"/agent-logs?agentId={agent.AgentId}&jobId={job}&level=Warning&text=stage");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = AgentWorld.ReadJson(response);
        Assert.Equal(id, Assert.Single(result.EnumerateArray()).GetProperty("id").GetGuid());
        Assert.DoesNotContain("hunter2", result.ToString());
        var outsider = await scenario.SessionAsync(member: false);
        using var forbidden = await outsider.GetAsync(scenario.Root + "/agent-logs");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using var unauthenticated = await scenario.World.Anonymous.GetAsync(scenario.Root + "/agent-logs");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        using var rejected = await scenario.World.SendAgentAsync(HttpMethod.Post, "/api/agent/logs", agent.CredentialId, agent.Secret, body: new[] { new { id = Guid.NewGuid(), timestamp = DateTimeOffset.UtcNow, code = "password=hunter2", jobId = job, runId = (Guid?)null } });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
    }
}
