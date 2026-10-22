using System.Net;

namespace BackupPlatform.IntegrationTests;

public sealed class BackupReportApiTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task Reports_count_terminal_outcomes_only_within_the_authorized_tenant_and_period()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var job = await scenario.CreateJobAsync();
        await scenario.StartRunAsync(job);
        await scenario.SucceedNextAsync();
        var start = scenario.Clock.GetUtcNow();
        scenario.Clock.Advance(TimeSpan.FromSeconds(1));
        await scenario.StartRunAsync(job);
        await scenario.FailNextAsync("InvalidConfiguration");
        scenario.Clock.Advance(TimeSpan.FromSeconds(1));
        using var response = await scenario.Admin.GetAsync(scenario.Root + $"/reports/backups?from={Uri.EscapeDataString(start.ToString("O"))}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = AgentWorld.ReadJson(response);
        Assert.Equal(1, report.GetProperty("succeeded").GetInt64());
        Assert.Equal(1, report.GetProperty("failed").GetInt64());
        var other = await scenario.World.CreateTenantAsync();
        using var forbidden = await other.Admin.GetAsync(scenario.Root + "/reports/backups");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using var invalid = await scenario.Admin.GetAsync(scenario.Root + "/reports/backups?from=2020-01-01T00:00:00Z");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }
}
