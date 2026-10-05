namespace BackupPlatform.IntegrationTests;

/// <summary>The limits are backend configuration (the "Backups" section). These tests prove the running API host applies it to what agents receive from a poll.</summary>
public sealed class BackupConcurrencyLimitApiTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ConfiguredAgentLimitDecidesHowManyTasksOneAgentIsHandedOverHttp(int limit)
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture, Settings(("AgentMaxConcurrentBackups", limit.ToString())));
        var job = await scenario.CreateJobAsync(overlapPolicy: "Queue");
        var runs = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            runs.Add(await scenario.StartRunAsync(job, $"run-{i}"));
            scenario.Clock.Advance(TimeSpan.FromSeconds(1)); // runs created in the same instant are ordered by id, not by arrival
        }

        var granted = new List<TaskClaim>();
        for (var poll = 0; poll < 4; poll++)
            if (await scenario.PollAsync() is { } claim) granted.Add(claim);

        Assert.Equal(runs.Take(limit), granted.Select(claim => claim.RunId)); // oldest first, never more than the limit
        using var failed = await scenario.FailRawAsync(granted[0], "PreCheckFailed");
        Assert.Equal(System.Net.HttpStatusCode.NoContent, failed.StatusCode);
        Assert.Equal(runs[limit], (await scenario.ClaimAsync()).RunId); // the freed slot goes to the next run in line
    }

    [Fact]
    public async Task ConfiguredTenantLimitStopsOneTenantsAgentsFromRunningMoreBackupsThanAllowed()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture, Settings(("AgentMaxConcurrentBackups", "5"), ("TenantMaxConcurrentBackups", "1")));
        var second = await scenario.World.EnrollAsync(scenario.Setup);
        await scenario.StartRunAsync(await scenario.CreateJobAsync());
        var secondRun = await scenario.StartRunAsync(await scenario.CreateJobAsync(agent: second));

        var first = await scenario.ClaimAsync();
        Assert.Null(await scenario.PollAsync(second)); // different agent, same tenant, tenant already at its limit
        using var failed = await scenario.FailRawAsync(first, "PreCheckFailed");
        Assert.Equal(System.Net.HttpStatusCode.NoContent, failed.StatusCode);

        Assert.Equal(secondRun, (await scenario.ClaimAsync(second)).RunId);
    }

    [Theory]
    [InlineData("AgentMaxConcurrentBackups", "0")]
    [InlineData("TenantMaxConcurrentBackups", "0")]
    [InlineData("GlobalMaxConcurrentBackups", "-1")]
    [InlineData("RetryScanInterval", "00:00:00.100")]
    public void NonsensicalLimitsStopTheHostFromStartingInsteadOfRunningUnbounded(string option, string value)
    {
        using var factory = new AuthApiFactory(fixture.ConnectionString, configuration: Settings((option, value)));

        var failure = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(failure);
        Assert.Contains(option, failure.ToString()); // the operator is told which setting is wrong
    }

    private static Dictionary<string, string?> Settings(params (string Option, string Value)[] options) =>
        options.ToDictionary(option => "Backups:" + option.Option, option => (string?)option.Value);
}
