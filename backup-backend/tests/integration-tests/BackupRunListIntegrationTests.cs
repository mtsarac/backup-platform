using System.Net;
using System.Text.Json;
using BackupPlatform.Contracts.Backups;

namespace BackupPlatform.IntegrationTests;

/// <summary>GET /backup-runs and /backup-jobs/{id}/runs: every sort field both ways, nulls last, stable paging over ties, search with the filters.</summary>
public sealed class BackupRunListIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    /// <summary>
    /// Five runs, oldest first (each starts one minute after the previous one):
    /// r1 "bravo job" on alpha-host, 30 s, 1024 B; r2 "Alpha job" on Charlie-host, 10 s, 4096 B; r3 "charlie job" on alpha-host, failed after 20 s;
    /// r4 the same as r1 (it ties r1 on every sortable column but its dates); r5 "Alpha job" on Charlie-host, queued and never started.
    /// </summary>
    private sealed class Data(BackupApiScenario scenario, Dictionary<string, Guid> runs, Guid bravoJob, EnrolledAgent alpha)
    {
        public BackupApiScenario Scenario { get; } = scenario;
        public Guid Bravo { get; } = bravoJob;
        public EnrolledAgent Alpha { get; } = alpha;
        public Guid R(string label) => runs[label];

        public async Task<JsonElement> GetAsync(string query, string path = "/backup-runs", HttpStatusCode expected = HttpStatusCode.OK)
        {
            using var response = await Scenario.Admin.GetAsync($"{Scenario.Root}{path}?{query}");
            Assert.Equal(expected, response.StatusCode);
            return AgentWorld.ReadJson(response);
        }

        public string[] Labels(JsonElement page) =>
            [.. page.GetProperty("items").EnumerateArray().Select(item => runs.Single(pair => pair.Value == item.GetProperty("id").GetGuid()).Key)];

        public async Task<string[]> OrderAsync(string query, string path = "/backup-runs") => Labels(await GetAsync(query, path));
    }

    private async Task<Data> SeedAsync(BackupApiScenario scenario)
    {
        var alpha = scenario.Agent;
        var charlie = await scenario.World.DeclareBackupCapabilityAsync(await scenario.World.EnrollAsync(scenario.Setup));
        await fixture.ExecuteAsync($"UPDATE agents SET \"HostName\" = 'alpha-host' WHERE \"Id\" = '{alpha.AgentId}'");
        await fixture.ExecuteAsync($"UPDATE agents SET \"HostName\" = 'Charlie-host' WHERE \"Id\" = '{charlie.AgentId}'");
        var bravoJob = await JobAsync(scenario, "bravo job", alpha);
        var alphaJob = await JobAsync(scenario, "Alpha job", charlie);
        var charlieJob = await JobAsync(scenario, "charlie job", alpha);
        var runs = new Dictionary<string, Guid>
        {
            ["r1"] = await FinishAsync(scenario, bravoJob, alpha, 30, 1024),
            ["r2"] = await FinishAsync(scenario, alphaJob, charlie, 10, 4096),
            ["r3"] = await FinishAsync(scenario, charlieJob, alpha, 20, null),
            ["r4"] = await FinishAsync(scenario, bravoJob, alpha, 30, 1024)
        };
        scenario.Clock.Advance(TimeSpan.FromMinutes(1));
        runs["r5"] = await scenario.StartRunAsync(alphaJob);
        return new Data(scenario, runs, bravoJob, alpha);
    }

    private static async Task<Guid> JobAsync(BackupApiScenario scenario, string name, EnrolledAgent agent)
    {
        using var response = await scenario.CreateJobRawAsync(new
        {
            name, agentId = agent.AgentId, overlapPolicy = "Queue", retryPolicy = new { maxAttempts = 1 },
            configuration = new BackupExecutionConfiguration { Database = "master", Host = "sql-server", IntegratedSecurity = true, Destinations = BackupTestDestinations.LocalBackend }
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return AgentWorld.ReadJson(response).GetProperty("id").GetGuid();
    }

    /// <summary>One run that starts a minute after the previous one, lasts <paramref name="seconds"/> and succeeds with <paramref name="size"/> bytes (null: fails).</summary>
    private static async Task<Guid> FinishAsync(BackupApiScenario scenario, Guid job, EnrolledAgent agent, int seconds, long? size)
    {
        scenario.Clock.Advance(TimeSpan.FromMinutes(1));
        var run = await scenario.StartRunAsync(job);
        var claim = await scenario.ClaimAsync(agent);
        await scenario.StartAttemptAsync(claim, agent);
        scenario.Clock.Advance(TimeSpan.FromSeconds(seconds));
        var request = scenario.SuccessRequest(claim);
        request = size is { } bytes
            ? request with { Artifact = request.Artifact! with { SizeBytes = bytes, Replicas = [request.Artifact.Replicas[0] with { SizeBytes = bytes }] } }
            : new BackupTaskCompletionRequest { LeaseToken = claim.LeaseToken, Status = "Failed", FailureCode = "InvalidConfiguration" };
        using var response = await scenario.CompleteRawAsync(claim, request, agent);
        Assert.True(response.StatusCode == HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());
        return run;
    }

    // Each inner array is a group of rows that tie on the sorted column (their relative order is by id), so it is compared as a set.
    private static void AssertGroups(string what, string[] actual, string[][] groups)
    {
        var index = 0;
        foreach (var group in groups)
        {
            var slice = actual.Skip(index).Take(group.Length).Order().ToArray();
            Assert.True(group.Order().SequenceEqual(slice), $"{what}: expected [{string.Join(",", group.Order())}] at position {index}, got [{string.Join(",", slice)}] in [{string.Join(",", actual)}]");
            index += group.Length;
        }
        Assert.Equal(index, actual.Length);
    }

    public static TheoryData<string, string, string[][]> Orders => new()
    {
        { "createdAt", "asc", [["r1"], ["r2"], ["r3"], ["r4"], ["r5"]] },
        { "createdAt", "desc", [["r5"], ["r4"], ["r3"], ["r2"], ["r1"]] },
        { "startedAt", "asc", [["r1"], ["r2"], ["r3"], ["r4"], ["r5"]] },
        { "startedAt", "desc", [["r4"], ["r3"], ["r2"], ["r1"], ["r5"]] },
        // The Turkish labels: Başarılı (r1, r2, r4), Başarısız (r3), Sırada bekliyor (r5).
        { "status", "asc", [["r1", "r2", "r4"], ["r3"], ["r5"]] },
        { "status", "desc", [["r5"], ["r3"], ["r1", "r2", "r4"]] },
        { "jobName", "asc", [["r2", "r5"], ["r1", "r4"], ["r3"]] },
        { "jobName", "desc", [["r3"], ["r1", "r4"], ["r2", "r5"]] },
        { "computer", "asc", [["r1", "r3", "r4"], ["r2", "r5"]] },
        { "computer", "desc", [["r2", "r5"], ["r1", "r3", "r4"]] },
        { "duration", "asc", [["r2"], ["r3"], ["r1", "r4"], ["r5"]] },
        { "duration", "desc", [["r1", "r4"], ["r3"], ["r2"], ["r5"]] },
        { "size", "asc", [["r1", "r4"], ["r2"], ["r3", "r5"]] },
        { "size", "desc", [["r2"], ["r1", "r4"], ["r3", "r5"]] }
    };

    [Fact]
    public async Task EverySortFieldOrdersBothWaysWithTextCaseInsensitiveAndMissingValuesLast()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var data = await SeedAsync(scenario);

        foreach (var (field, dir, groups) in Orders.Select(row => ((string)row[0], (string)row[1], (string[][])row[2])))
        {
            AssertGroups($"{field} {dir}", await data.OrderAsync($"sort={field}&dir={dir}"), groups);
            // The job-scoped list sorts the same way: bravo job holds r1 and r4 only.
            var scoped = groups.Select(group => group.Where(label => label is "r1" or "r4").ToArray()).Where(group => group.Length > 0).ToArray();
            AssertGroups($"job {field} {dir}", await data.OrderAsync($"sort={field}&dir={dir}", $"/backup-jobs/{data.Bravo}/runs"), scoped);
        }
    }

    [Fact]
    public async Task WithoutSortTheNewestRunIsFirstAndADirectionAloneKeepsTheDefaultField()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var data = await SeedAsync(scenario);

        var all = await data.GetAsync("");
        Assert.Equal(["r5", "r4", "r3", "r2", "r1"], data.Labels(all));
        Assert.Equal(5, all.GetProperty("totalCount").GetInt32());
        Assert.Equal(["r1", "r2", "r3", "r4", "r5"], await data.OrderAsync("dir=asc"));
        // A field alone sorts in the direction that suits it: dates newest first, text and numbers ascending.
        Assert.Equal(["r4", "r3", "r2", "r1", "r5"], await data.OrderAsync("sort=startedAt"));
        AssertGroups("jobName", await data.OrderAsync("sort=jobName"), [["r2", "r5"], ["r1", "r4"], ["r3"]]);
        AssertGroups("size", await data.OrderAsync("sort=size"), [["r1", "r4"], ["r2"], ["r3", "r5"]]);
    }

    [Fact]
    public async Task APageOfTiedRowsIsStableSoPagingNeverRepeatsOrSkipsARun()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var data = await SeedAsync(scenario);

        foreach (var sort in new[] { "duration", "size", "status", "jobName", "computer" })
        foreach (var dir in new[] { "asc", "desc" })
        {
            var whole = await data.OrderAsync($"sort={sort}&dir={dir}&pageSize=100");
            Assert.Equal(whole, await data.OrderAsync($"sort={sort}&dir={dir}&pageSize=100"));
            foreach (var size in new[] { 1, 2, 3 })
            {
                var paged = new List<string>();
                for (var page = 1; page <= (5 + size - 1) / size; page++) paged.AddRange(await data.OrderAsync($"sort={sort}&dir={dir}&pageSize={size}&page={page}"));
                Assert.True(whole.SequenceEqual(paged), $"{sort} {dir} pageSize {size}: [{string.Join(",", paged)}] differs from [{string.Join(",", whole)}]");
            }
        }

        var second = await data.GetAsync("sort=duration&pageSize=2&page=2");
        Assert.Equal(2, second.GetProperty("page").GetInt32());
        Assert.Equal(2, second.GetProperty("pageSize").GetInt32());
        Assert.Equal(5, second.GetProperty("totalCount").GetInt32());
        var beyond = await data.GetAsync("sort=duration&pageSize=2&page=9");
        Assert.Empty(beyond.GetProperty("items").EnumerateArray());
        Assert.Equal(5, beyond.GetProperty("totalCount").GetInt32());
    }

    [Theory]
    [InlineData("2147483648")]
    [InlineData("99999999999999999999999999")]
    public async Task AnAbsurdPageIsClampedNotRefused(string page)
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var data = await SeedAsync(scenario);

        var result = await data.GetAsync($"page={page}");

        Assert.Equal(5, result.GetProperty("totalCount").GetInt32());
        Assert.Empty(result.GetProperty("items").EnumerateArray());
        Assert.Equal(1_000_000, result.GetProperty("page").GetInt32());
    }

    [Fact]
    public async Task SearchMatchesJobComputerAndDatabaseIgnoringCaseAndCombinesWithEveryFilter()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var data = await SeedAsync(scenario);
        var r3Created = Uri.EscapeDataString((await scenario.RunAsync(data.R("r3"))).GetProperty("createdAt").GetString()!);

        Assert.Equal(["r5", "r2"], await data.OrderAsync("search=ALPHA%20JOB"));
        Assert.Equal(["r5", "r3", "r2"], await data.OrderAsync("search=charlie"));
        Assert.Equal(["r5", "r4", "r3", "r2", "r1"], await data.OrderAsync("search=MASTER"));
        Assert.Equal(["r5", "r4", "r3", "r2", "r1"], await data.OrderAsync("search=%20alpha%20")); // trimmed; the job Alpha job and the computer alpha-host
        Assert.Empty(await data.OrderAsync("search=no-such-run"));
        Assert.Empty(await data.OrderAsync("search=%25"));
        Assert.Empty(await data.OrderAsync("search=_"));

        Assert.Equal(["r2"], await data.OrderAsync("search=alpha%20job&status=Succeeded"));
        Assert.Equal(["r5"], await data.OrderAsync("search=alpha%20job&status=Pending"));
        Assert.Equal(["r3"], await data.OrderAsync("search=charlie&status=Failed"));
        Assert.Equal(["r3"], await data.OrderAsync($"search=charlie&agentId={data.Alpha.AgentId}"));
        Assert.Equal(["r4", "r1"], await data.OrderAsync($"search=bravo&jobId={data.Bravo}"));
        Assert.Empty(await data.OrderAsync($"search=charlie&jobId={data.Bravo}"));
        Assert.Equal(["r5", "r3"], await data.OrderAsync($"search=charlie&from={r3Created}"));
        Assert.Equal(["r3", "r2"], await data.OrderAsync($"search=charlie&to={r3Created}"));

        var combined = await data.GetAsync("search=charlie&sort=jobName&dir=desc&pageSize=2");
        Assert.Equal(3, combined.GetProperty("totalCount").GetInt32());
        Assert.Equal("r3", data.Labels(combined)[0]);
        Assert.Equal(["r4", "r1"], await data.OrderAsync("search=bravo&sort=startedAt", $"/backup-jobs/{data.Bravo}/runs"));
    }

    // Every string of a group is the same search text (the frontend foldSearch test and TextFoldTests use these groups).
    [Theory]
    [InlineData("INDIGO", "İNDİGO", "indigo", "ındıgo")]
    [InlineData("ŞİFRELİ", "ŞİFRELI", "sifreli", "SIFRELI")]
    [InlineData("çağrı", "ÇAĞRI", "cagri", "CAGRI")]
    public async Task SearchFoldsCaseAndTurkishLettersOfTheJobComputerAndDatabase(params string[] terms)
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var data = await SeedAsync(scenario);
        var jobId = await JobAsync(scenario, $"{terms[0]} job", data.Alpha);
        var run = await scenario.StartRunAsync(jobId);

        foreach (var term in terms) Assert.Equal([run], (await data.GetAsync("search=" + Uri.EscapeDataString(term + " job"))).GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ToArray());
    }

    [Fact]
    public async Task ARunOfAnotherTenantIsNeverFoundBySearchOrSort()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        await SeedAsync(scenario);
        var other = await scenario.World.CreateTenantAsync();

        using var response = await other.Admin.GetAsync(BackupApiScenario.RootOf(other) + "/backup-runs?search=bravo&sort=jobName");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, AgentWorld.ReadJson(response).GetProperty("totalCount").GetInt32());
    }

    [Theory]
    [InlineData("sort=unknown", "sort")]
    [InlineData("sort=createdat", "sort")]
    [InlineData("sort=id", "sort")]
    [InlineData("sort=startedAt&dir=sideways", "dir")]
    [InlineData("page=0", "page")]
    [InlineData("pageSize=0", "pageSize")]
    [InlineData("pageSize=101", "pageSize")]
    public async Task AnInvalidListQueryIsABadRequestNamingTheField(string query, string field)
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var job = await scenario.CreateJobAsync();

        foreach (var path in new[] { "/backup-runs", $"/backup-jobs/{job}/runs" })
        {
            using var response = await scenario.Admin.GetAsync($"{scenario.Root}{path}?{query}");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var body = AgentWorld.ReadJson(response);
            Assert.Equal("invalid_request", body.GetProperty("error").GetString());
            Assert.Equal(field, body.GetProperty("field").GetString());
        }
    }
}
