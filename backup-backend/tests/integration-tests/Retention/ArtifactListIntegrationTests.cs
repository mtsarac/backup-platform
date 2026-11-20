using System.Net;
using System.Text.Json;
using BackupPlatform.Contracts.Backups;

namespace BackupPlatform.IntegrationTests;

/// <summary>
/// GET /backup-artifacts: every sort field both ways (text case-insensitive), stable paging over ties, search over job, database and stored file name
/// (the file, never the directory it sits in) combined with the filters, and the invalid-query errors. No sort column of an artifact can be empty, so
/// the shared nulls-last rule has nothing to show here; <see cref="BackupRunListIntegrationTests"/> covers it on the same helper.
/// </summary>
public sealed class ArtifactListIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    /// <summary>
    /// Four artifacts, oldest first: a1 "bravo job" / Zulu, 1024 B, x-one.bak; a2 "Alpha job" / alpha_db, 4096 B, x-two.bak (then deleted: DeletePending);
    /// a3 "charlie job" / mike, 2048 B, y-three.bak; a4 the same as a1 (ties it on job, database and size), x-four.bak.
    /// </summary>
    private sealed class Data(BackupApiScenario scenario, Dictionary<string, Guid> artifacts, Dictionary<string, Guid> jobs, Dictionary<string, string> attempts)
    {
        public Guid Job(string name) => jobs[name];
        public string Attempt(string label) => attempts[label];

        public async Task<JsonElement> GetAsync(string query, HttpStatusCode expected = HttpStatusCode.OK)
        {
            using var response = await scenario.Admin.GetAsync($"{scenario.Root}/backup-artifacts?{query}");
            Assert.Equal(expected, response.StatusCode);
            return AgentWorld.ReadJson(response);
        }

        public string[] Labels(JsonElement page) =>
            [.. page.GetProperty("items").EnumerateArray().Select(item => artifacts.Single(pair => pair.Value == item.GetProperty("id").GetGuid()).Key)];

        public async Task<string[]> OrderAsync(string query) => Labels(await GetAsync(query));
    }

    private async Task<Data> SeedAsync(BackupApiScenario scenario)
    {
        var agent = scenario.Agent;
        var jobs = new Dictionary<string, Guid>
        {
            ["bravo job"] = await JobAsync(scenario, "bravo job", "Zulu", agent),
            ["Alpha job"] = await JobAsync(scenario, "Alpha job", "alpha_db", agent),
            ["charlie job"] = await JobAsync(scenario, "charlie job", "mike", agent)
        };
        await FinishAsync(scenario, jobs["bravo job"], agent, "Zulu", 1024, "x-one.bak");
        await FinishAsync(scenario, jobs["Alpha job"], agent, "alpha_db", 4096, "x-two.bak");
        await FinishAsync(scenario, jobs["charlie job"], agent, "mike", 2048, "y-three.bak");
        await FinishAsync(scenario, jobs["bravo job"], agent, "Zulu", 1024, "x-four.bak");

        string[] files = ["x-one.bak", "x-two.bak", "y-three.bak", "x-four.bak"];
        using var listed = await scenario.Admin.GetAsync($"{scenario.Root}/backup-artifacts?pageSize=100");
        var items = AgentWorld.ReadJson(listed).GetProperty("items").EnumerateArray().ToList();
        string File(JsonElement item) => item.GetProperty("replicas")[0].GetProperty("key").GetString()!;
        var artifacts = items.ToDictionary(item => $"a{Array.IndexOf(files, File(item).Split('/')[^1]) + 1}", item => item.GetProperty("id").GetGuid());
        var attempts = items.ToDictionary(item => $"a{Array.IndexOf(files, File(item).Split('/')[^1]) + 1}", item => File(item).Split('/')[0]);
        using (await RetentionApiSupport.DeleteArtifactAsync(scenario, artifacts["a2"])) { }
        return new Data(scenario, artifacts, jobs, attempts);
    }

    private static async Task<Guid> JobAsync(BackupApiScenario scenario, string name, string database, EnrolledAgent agent)
    {
        using var response = await scenario.CreateJobRawAsync(new
        {
            name, agentId = agent.AgentId, overlapPolicy = "Queue", retryPolicy = new { maxAttempts = 1 },
            configuration = new BackupExecutionConfiguration { Database = database, Host = "sql-server", IntegratedSecurity = true, Destinations = BackupTestDestinations.LocalBackend }
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return AgentWorld.ReadJson(response).GetProperty("id").GetGuid();
    }

    /// <summary>One backup that starts a minute after the previous one and stores <paramref name="size"/> bytes as <paramref name="file"/>.</summary>
    private static async Task FinishAsync(BackupApiScenario scenario, Guid job, EnrolledAgent agent, string database, long size, string file)
    {
        scenario.Clock.Advance(TimeSpan.FromMinutes(1));
        await scenario.StartRunAsync(job);
        var claim = await scenario.ClaimAsync(agent);
        await scenario.StartAttemptAsync(claim, agent);
        var request = scenario.SuccessRequest(claim, database: database);
        request = request with { Artifact = request.Artifact! with { SizeBytes = size, Replicas = [request.Artifact.Replicas[0] with { SizeBytes = size, Key = $"{claim.AttemptId:N}/{file}" }] } };
        using var response = await scenario.CompleteRawAsync(claim, request, agent);
        Assert.True(response.StatusCode == HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());
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
        { "createdAt", "asc", [["a1"], ["a2"], ["a3"], ["a4"]] },
        { "createdAt", "desc", [["a4"], ["a3"], ["a2"], ["a1"]] },
        { "size", "asc", [["a1", "a4"], ["a3"], ["a2"]] },
        { "size", "desc", [["a2"], ["a3"], ["a1", "a4"]] },
        { "status", "asc", [["a1", "a3", "a4"], ["a2"]] },
        { "status", "desc", [["a2"], ["a1", "a3", "a4"]] },
        { "jobName", "asc", [["a2"], ["a1", "a4"], ["a3"]] },
        { "jobName", "desc", [["a3"], ["a1", "a4"], ["a2"]] },
        { "databaseName", "asc", [["a2"], ["a3"], ["a1", "a4"]] },
        { "databaseName", "desc", [["a1", "a4"], ["a3"], ["a2"]] }
    };

    [Fact]
    public async Task EverySortFieldOrdersBothWaysWithTextCaseInsensitive()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var data = await SeedAsync(scenario);

        foreach (var (field, dir, groups) in Orders.Select(row => ((string)row[0], (string)row[1], (string[][])row[2])))
            AssertGroups($"{field} {dir}", await data.OrderAsync($"sort={field}&dir={dir}"), groups);
    }

    [Fact]
    public async Task WithoutSortTheNewestBackupIsFirstAndAFieldAloneSortsInItsNaturalDirection()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var data = await SeedAsync(scenario);

        var all = await data.GetAsync("");
        Assert.Equal(["a4", "a3", "a2", "a1"], data.Labels(all));
        Assert.Equal(4, all.GetProperty("totalCount").GetInt32());
        Assert.Equal(["a1", "a2", "a3", "a4"], await data.OrderAsync("dir=asc"));
        Assert.Equal(["a4", "a3", "a2", "a1"], await data.OrderAsync("sort=createdAt"));
        AssertGroups("jobName", await data.OrderAsync("sort=jobName"), [["a2"], ["a1", "a4"], ["a3"]]);
        AssertGroups("size", await data.OrderAsync("sort=size"), [["a1", "a4"], ["a3"], ["a2"]]);
    }

    [Fact]
    public async Task APageOfTiedRowsIsStableSoPagingNeverRepeatsOrSkipsABackup()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var data = await SeedAsync(scenario);

        foreach (var sort in new[] { "size", "status", "jobName", "databaseName" })
        foreach (var dir in new[] { "asc", "desc" })
        {
            var whole = await data.OrderAsync($"sort={sort}&dir={dir}&pageSize=100");
            Assert.Equal(whole, await data.OrderAsync($"sort={sort}&dir={dir}&pageSize=100"));
            foreach (var size in new[] { 1, 2, 3 })
            {
                var paged = new List<string>();
                for (var page = 1; page <= (4 + size - 1) / size; page++) paged.AddRange(await data.OrderAsync($"sort={sort}&dir={dir}&pageSize={size}&page={page}"));
                Assert.True(whole.SequenceEqual(paged), $"{sort} {dir} pageSize {size}: [{string.Join(",", paged)}] differs from [{string.Join(",", whole)}]");
            }
        }

        var second = await data.GetAsync("sort=size&pageSize=2&page=2");
        Assert.Equal(2, second.GetProperty("page").GetInt32());
        Assert.Equal(2, second.GetProperty("pageSize").GetInt32());
        Assert.Equal(4, second.GetProperty("totalCount").GetInt32());
        var beyond = await data.GetAsync("sort=size&pageSize=2&page=9");
        Assert.Empty(beyond.GetProperty("items").EnumerateArray());
        Assert.Equal(4, beyond.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task SearchMatchesJobDatabaseAndFileNameIgnoringCaseAndCombinesWithEveryFilter()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var data = await SeedAsync(scenario);
        var a3 = Uri.EscapeDataString((await data.GetAsync("")).GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("replicas")[0].GetProperty("key").GetString()!.EndsWith("y-three.bak")).GetProperty("createdAt").GetString()!);

        Assert.Equal(["a2"], await data.OrderAsync("search=ALPHA")); // the job "Alpha job" and the database alpha_db
        Assert.Equal(["a4", "a1"], await data.OrderAsync("search=BRAVO%20JOB"));
        Assert.Equal(["a4", "a1"], await data.OrderAsync("search=zulu"));
        Assert.Equal(["a3"], await data.OrderAsync("search=%20MIKE%20")); // trimmed
        Assert.Equal(["a4", "a2", "a1"], await data.OrderAsync("search=x-")); // the file name
        Assert.Equal(["a3"], await data.OrderAsync("search=three"));
        Assert.Empty(await data.OrderAsync($"search={data.Attempt("a3")}")); // the directory of the file is not the file name
        Assert.Empty(await data.OrderAsync("search=no-such-backup"));
        Assert.Empty(await data.OrderAsync("search=%25"));
        Assert.Equal(["a2"], await data.OrderAsync("search=_")); // a literal underscore (alpha_db), not a wildcard

        Assert.Equal(["a4", "a1"], await data.OrderAsync("search=x-&state=Available"));
        Assert.Equal(["a2"], await data.OrderAsync("search=x-&state=DeletePending"));
        Assert.Empty(await data.OrderAsync("search=y-three&state=DeletePending"));
        Assert.Equal(["a4", "a1"], await data.OrderAsync($"search=x-&jobId={data.Job("bravo job")}"));
        Assert.Empty(await data.OrderAsync($"search=y-three&jobId={data.Job("bravo job")}"));
        Assert.Equal(["a4", "a3"], await data.OrderAsync($"search=bak&from={a3}&state=Available"));
        Assert.Equal(["a3", "a2", "a1"], await data.OrderAsync($"search=bak&to={a3}"));
        Assert.Equal(["a4", "a2", "a1"], await data.OrderAsync($"search=x-&agentId={scenario.Agent.AgentId}"));
        Assert.Empty(await data.OrderAsync($"search=x-&agentId={Guid.NewGuid()}"));

        var combined = await data.GetAsync("search=x-&sort=size&dir=desc&pageSize=2");
        Assert.Equal(3, combined.GetProperty("totalCount").GetInt32());
        Assert.Equal("a2", data.Labels(combined)[0]);
        Assert.Equal(2, data.Labels(combined).Length);
    }

    [Fact]
    public async Task ABackupOfAnotherTenantIsNeverFoundBySearchOrSort()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        await SeedAsync(scenario);
        var other = await scenario.World.CreateTenantAsync();

        using var response = await other.Admin.GetAsync(BackupApiScenario.RootOf(other) + "/backup-artifacts?search=bravo&sort=jobName");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, AgentWorld.ReadJson(response).GetProperty("totalCount").GetInt32());
    }

    [Theory]
    [InlineData("sort=unknown", "sort")]
    [InlineData("sort=createdat", "sort")]
    [InlineData("sort=id", "sort")]
    [InlineData("sort=size&dir=sideways", "dir")]
    [InlineData("page=0", "page")]
    [InlineData("pageSize=0", "pageSize")]
    [InlineData("pageSize=101", "pageSize")]
    public async Task AnInvalidListQueryIsABadRequestNamingTheField(string query, string field)
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);

        using var response = await scenario.Admin.GetAsync($"{scenario.Root}/backup-artifacts?{query}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = AgentWorld.ReadJson(response);
        Assert.Equal("invalid_request", body.GetProperty("error").GetString());
        Assert.Equal(field, body.GetProperty("field").GetString());
    }
}
