using System.Net;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Domain.Tenancy;

namespace BackupPlatform.IntegrationTests;

public sealed class BackupUiApiTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task OperatorJobEditChangesFutureRunsWithoutChangingExistingSnapshots()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var job = await scenario.CreateJobAsync(overlapPolicy: "Queue");
        var first = await scenario.StartRunAsync(job);
        var session = await scenario.SessionAsync(TenantRole.Operator);
        var body = new { name = "Updated job", agentId = scenario.Agent.AgentId,
            configuration = new BackupExecutionConfiguration { Database = "updated-database", Host = "sql-server", IntegratedSecurity = true },
            retryPolicy = new { maxAttempts = 5 }, overlapPolicy = "Queue" };
        using var edited = await session.SendAsync(HttpMethod.Put, scenario.Root + $"/backup-jobs/{job}", body);
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.Equal("Updated job", AgentWorld.ReadJson(edited).GetProperty("name").GetString());
        var second = await scenario.StartRunAsync(job);
        Assert.Equal("master", (await scenario.RunAsync(first)).GetProperty("configuration").GetProperty("database").GetString());
        var future = await scenario.RunAsync(second);
        Assert.Equal("updated-database", future.GetProperty("configuration").GetProperty("database").GetString());
        Assert.Equal(5, future.GetProperty("retry").GetProperty("maxAttempts").GetInt32());
        Assert.Equal("Updated job", future.GetProperty("jobName").GetString());
        using var list = await session.GetAsync(scenario.Root + "/backup-jobs");
        var listed = Assert.Single(AgentWorld.ReadJson(list).EnumerateArray());
        Assert.Equal(job, listed.GetProperty("id").GetGuid());
        Assert.NotEqual(System.Text.Json.JsonValueKind.Null, listed.GetProperty("agentName").ValueKind);
        Assert.NotEqual(System.Text.Json.JsonValueKind.Null, listed.GetProperty("lastRun").ValueKind);
    }

    [Fact]
    public async Task ViewerReadsAreSafeAndEditsRequireTenantOperatorAndAntiforgery()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var job = await scenario.CreateJobAsync();
        var viewer = await scenario.SessionAsync(TenantRole.Viewer);
        var body = BackupApiScenario.JobBody(scenario.Agent.AgentId);
        using var forbidden = await viewer.SendAsync(HttpMethod.Put, scenario.Root + $"/backup-jobs/{job}", body);
        using var csrf = await scenario.Admin.SendAsync(HttpMethod.Put, scenario.Root + $"/backup-jobs/{job}", body, includeAntiforgery: false);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, csrf.StatusCode);
        foreach (var resource in new[] { "/backup-jobs", "/dashboard", "/backup-runs?page=1" })
        {
            using var response = await viewer.GetAsync(scenario.Root + resource);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain(scenario.Agent.Secret, json);
            Assert.DoesNotContain("leaseToken", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("idempotencyKey", json, StringComparison.OrdinalIgnoreCase);
        }
        var other = await scenario.World.CreateTenantAsync();
        using var hidden = await other.Admin.SendAsync(HttpMethod.Put, BackupApiScenario.RootOf(other) + $"/backup-jobs/{job}", body);
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        using var crossAgent = await scenario.Admin.SendAsync(HttpMethod.Put, scenario.Root + $"/backup-jobs/{job}", BackupApiScenario.JobBody((await scenario.World.EnrollAsync(other)).AgentId));
        Assert.Equal(HttpStatusCode.NotFound, crossAgent.StatusCode);
    }

    [Fact]
    public async Task JobValidationReturnsSafeFieldWithoutEchoingInput()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var job = await scenario.CreateJobAsync();
        var hostile = "password-secret/../../outside";
        var body = new { name = "Invalid", agentId = scenario.Agent.AgentId,
            configuration = new BackupExecutionConfiguration { Database = "master", Host = "sql-server", IntegratedSecurity = true, StoragePrefix = hostile } };
        using var response = await scenario.Admin.SendAsync(HttpMethod.Put, scenario.Root + $"/backup-jobs/{job}", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("configuration.storagePrefix", AgentWorld.ReadJson(response).GetProperty("field").GetString());
        Assert.Equal("invalid_value", AgentWorld.ReadJson(response).GetProperty("code").GetString());
        Assert.DoesNotContain(hostile, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task HistoryTraversesBeyondOneHundredWithCombinedFiltersAndTenantIsolation()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var job = await scenario.CreateJobAsync(overlapPolicy: "Queue");
        var created = scenario.Clock.GetUtcNow();
        for (var index = 0; index < 105; index++) await scenario.StartRunAsync(job, $"history-{index}");
        var other = await scenario.World.CreateTenantAsync();
        using var isolated = await other.Admin.GetAsync(BackupApiScenario.RootOf(other) + "/backup-runs?page=1&pageSize=100");
        Assert.Equal(0, AgentWorld.ReadJson(isolated).GetProperty("totalCount").GetInt64());
        var instant = Uri.EscapeDataString(created.ToString("O"));
        var filter = $"&jobId={job}&agentId={scenario.Agent.AgentId}&status=Pending&trigger=Manual&from={instant}&to={instant}";
        var found = new HashSet<Guid>();
        for (var page = 1; page <= 3; page++)
        {
            using var response = await scenario.Admin.GetAsync(scenario.Root + $"/backup-runs?page={page}&pageSize=50" + filter);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = AgentWorld.ReadJson(response);
            Assert.Equal(105, json.GetProperty("totalCount").GetInt64());
            Assert.Equal(page, json.GetProperty("page").GetInt32());
            foreach (var item in json.GetProperty("items").EnumerateArray()) Assert.True(found.Add(item.GetProperty("id").GetGuid()));
        }
        Assert.Equal(105, found.Count);
        using var filteredEmpty = await scenario.Admin.GetAsync(scenario.Root + "/backup-runs?page=1&status=Failed");
        Assert.Equal(0, AgentWorld.ReadJson(filteredEmpty).GetProperty("totalCount").GetInt64());
        using var invalid = await scenario.Admin.GetAsync(scenario.Root + "/backup-runs?page=0");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var numericStatus = await scenario.Admin.GetAsync(scenario.Root + "/backup-runs?status=1");
        Assert.Equal(HttpStatusCode.BadRequest, numericStatus.StatusCode);
    }

    [Fact]
    public async Task DashboardCountsEntireTenantAndRetryProvenanceIsDurable()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var job = await scenario.CreateJobAsync(overlapPolicy: "Queue");
        var run = await scenario.StartRunAsync(job);
        await scenario.FailNextAsync("StorageUnavailable");
        using var retry = await scenario.RetryRawAsync(run);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal("ManualRetry", (await scenario.RunAsync(run)).GetProperty("attempts")[1].GetProperty("trigger").GetString());
        using var history = await scenario.Admin.GetAsync(scenario.Root + "/backup-runs?trigger=Retry&page=1");
        Assert.Equal(1, AgentWorld.ReadJson(history).GetProperty("totalCount").GetInt64());
        for (var index = 0; index < 10; index++) await scenario.StartRunAsync(job);
        using var dashboard = await scenario.Admin.GetAsync(scenario.Root + "/dashboard");
        Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
        var data = AgentWorld.ReadJson(dashboard);
        Assert.Equal(11, data.GetProperty("runs").GetProperty("active").GetInt32());
        Assert.Equal(8, data.GetProperty("recentRuns").GetArrayLength());
        Assert.Equal(1, data.GetProperty("jobs").GetProperty("total").GetInt32());
        Assert.Equal(1, data.GetProperty("agents").GetProperty("total").GetInt32());
        foreach (var name in new[] { "running", "retryPending", "cancellationRequested", "storageFailedLast24Hours" })
            Assert.True(data.GetProperty("runs").TryGetProperty(name, out _));
    }

    [Fact]
    public async Task CooperativeCancellationHasDistinctReadStatusAndFilterUntilAgentAcknowledges()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var job = await scenario.CreateJobAsync();
        var run = await scenario.StartRunAsync(job);
        var claim = await scenario.ClaimAsync();
        await scenario.StartAttemptAsync(claim);
        using var cancel = await scenario.CancelRawAsync(run);
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        Assert.Equal("CancellationRequested", AgentWorld.ReadJson(cancel).GetProperty("status").GetString());
        using var filtered = await scenario.Admin.GetAsync(scenario.Root + "/backup-runs?status=CancellationRequested&page=1");
        Assert.Equal(1, AgentWorld.ReadJson(filtered).GetProperty("totalCount").GetInt64());
        using var running = await scenario.Admin.GetAsync(scenario.Root + "/backup-runs?status=Running&page=1");
        Assert.Equal(0, AgentWorld.ReadJson(running).GetProperty("totalCount").GetInt64());
        using var completed = await scenario.FailRawAsync(claim, "DatabaseUnavailable");
        Assert.Equal(HttpStatusCode.NoContent, completed.StatusCode);
        Assert.Equal("Cancelled", (await scenario.RunAsync(run)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task AutomaticRetryAttemptUsesStoredProvenance()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var job = await scenario.CreateJobAsync();
        var run = await scenario.StartRunAsync(job);
        await scenario.FailNextAsync("DatabaseUnavailable");
        scenario.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, await scenario.ScanRetriesAsync());
        Assert.Equal("AutomaticRetry", (await scenario.RunAsync(run)).GetProperty("attempts")[1].GetProperty("trigger").GetString());
    }
}
