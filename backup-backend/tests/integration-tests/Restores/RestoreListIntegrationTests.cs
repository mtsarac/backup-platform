using System.Net;
using System.Text.Json;
using static BackupPlatform.IntegrationTests.RestoreApiScenario;

namespace BackupPlatform.IntegrationTests;

/// <summary>GET /restores: every sort field both ways, a verification sorted by the database it verified, stable paging over ties, search with the filters.</summary>
public sealed class RestoreListIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    /// <summary>
    /// Five restores, oldest first. Target / source (database, else job) / computer / status:
    /// q1 Delta / sales / alpha-host / Succeeded; q2 alpha / Marketing / Charlie-host / Cancelled; q3 Charlie / sales / alpha-host / Pending;
    /// q4 bravo / "Charlie job" (its backup has no database) / Charlie-host / Pending; v1 a verification of the Marketing backup on alpha-host, Pending,
    /// whose own database is the generated bp_verify_ copy and which the list therefore titles "Marketing".
    /// </summary>
    private sealed class Data(RestoreApiScenario scenario, Dictionary<string, Guid> restores, AcceptedBackup sales, EnrolledAgent charlie)
    {
        public RestoreApiScenario Scenario { get; } = scenario;
        public Dictionary<string, Guid> Restores { get; } = restores;
        public AcceptedBackup Sales { get; } = sales;
        public EnrolledAgent Charlie { get; } = charlie;

        public async Task<JsonElement> GetAsync(string query, HttpStatusCode expected = HttpStatusCode.OK)
        {
            using var response = await Scenario.GetRawAsync("/restores?" + query);
            Assert.Equal(expected, response.StatusCode);
            return AgentWorld.ReadJson(response);
        }

        public string[] Labels(JsonElement page) =>
            [.. page.GetProperty("items").EnumerateArray().Select(item => Restores.Single(pair => pair.Value == item.GetProperty("id").GetGuid()).Key)];

        /// <summary>The labels of the restores a query lists, verifications included.</summary>
        public async Task<string[]> OrderAsync(string query) => Labels(await GetAsync("purpose=All&" + query));
    }

    private async Task<Data> SeedAsync(RestoreApiScenario scenario)
    {
        var alpha = scenario.Agent;
        await scenario.SyncCapabilitiesAsync(alpha, "mssql.restore.verification", Capability);
        var charlie = await scenario.AddAgentAsync();
        await fixture.ExecuteAsync($"UPDATE agents SET \"HostName\" = 'alpha-host' WHERE \"Id\" = '{alpha.AgentId}'");
        await fixture.ExecuteAsync($"UPDATE agents SET \"HostName\" = 'Charlie-host' WHERE \"Id\" = '{charlie.AgentId}'");
        var sales = await scenario.AcceptBackupAsync(jobName: "Alpha job", database: "sales");
        var marketing = await scenario.AcceptBackupAsync(jobName: "Bravo job", database: "Marketing");
        var noDatabase = await scenario.AcceptBackupAsync(jobName: "Charlie job", database: "temporary");
        await fixture.ExecuteAsync($"UPDATE backup_runs SET \"Configuration\" = jsonb_set(\"Configuration\", '{{Database}}', '\"\"') WHERE \"Id\" = '{noDatabase.RunId}'");

        var restores = new Dictionary<string, Guid>();
        async Task<Guid> StartAsync(AcceptedBackup backup, string database, EnrolledAgent? agent = null)
        {
            scenario.Clock.Advance(TimeSpan.FromMinutes(1));
            return (await scenario.StartAsync(backup.ArtifactId, scenario.Body(database, agent: agent))).GetProperty("id").GetGuid();
        }
        restores["q1"] = await StartAsync(sales, "Delta");
        await scenario.RunToCompletionAsync();
        restores["q2"] = await StartAsync(marketing, "alpha", charlie);
        await scenario.CancelAsync(restores["q2"]);
        restores["q3"] = await StartAsync(sales, "Charlie");
        restores["q4"] = await StartAsync(noDatabase, "bravo", charlie);
        scenario.Clock.Advance(TimeSpan.FromMinutes(1));
        var target = new { host = "sql-target", port = 1433, integratedSecurity = true, encrypt = true, trustServerCertificate = false };
        restores["v1"] = (await scenario.StartPreparationAsync(marketing.ArtifactId, "verifications", new { agentId = alpha.AgentId, target })).GetProperty("id").GetGuid();
        return new Data(scenario, restores, sales, charlie);
    }

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
        { "createdAt", "asc", [["q1"], ["q2"], ["q3"], ["q4"], ["v1"]] },
        { "createdAt", "desc", [["v1"], ["q4"], ["q3"], ["q2"], ["q1"]] },
        // The Turkish status labels in order: Başarılı (q1), İptal edildi (q2), Sırada bekliyor (q3, q4, v1).
        { "status", "asc", [["q1"], ["q2"], ["q3", "q4", "v1"]] },
        { "status", "desc", [["q3", "q4", "v1"], ["q2"], ["q1"]] },
        // The target is what the list shows: the verification "Marketing", not its bp_verify_ database, which would sort second.
        { "target", "asc", [["q2"], ["q4"], ["q3"], ["q1"], ["v1"]] },
        { "target", "desc", [["v1"], ["q1"], ["q3"], ["q4"], ["q2"]] },
        { "source", "asc", [["q4"], ["q2", "v1"], ["q1", "q3"]] },
        { "source", "desc", [["q1", "q3"], ["q2", "v1"], ["q4"]] },
        { "computer", "asc", [["q1", "q3", "v1"], ["q2", "q4"]] },
        { "computer", "desc", [["q2", "q4"], ["q1", "q3", "v1"]] }
    };

    [Fact]
    public async Task EverySortFieldOrdersBothWaysWithTextCaseInsensitive()
    {
        await using var scenario = await CreateAsync(fixture);
        var data = await SeedAsync(scenario);

        foreach (var (field, dir, groups) in Orders.Select(row => ((string)row[0], (string)row[1], (string[][])row[2])))
            AssertGroups($"{field} {dir}", await data.OrderAsync($"sort={field}&dir={dir}"), groups);
    }

    [Fact]
    public async Task AVerificationIsSortedAndFoundByTheDatabaseItVerifiedNeverByItsGeneratedName()
    {
        await using var scenario = await CreateAsync(fixture);
        var data = await SeedAsync(scenario);

        Assert.StartsWith("bp_verify_", (await scenario.RestoreAsync(data.Restores["v1"])).GetProperty("databaseName").GetString());
        Assert.Equal(["v1", "q2"], await data.OrderAsync("search=marketing&sort=target&dir=desc"));
        Assert.Empty(await data.OrderAsync("search=bp_verify"));
        Assert.Empty(await data.OrderAsync("search=verify_"));
        Assert.Equal(["v1"], data.Labels(await data.GetAsync("purpose=Verification&search=MARKETING")));
        Assert.Equal(["v1"], data.Labels(await data.GetAsync("purpose=Verification&sort=target")));
    }

    [Theory]
    [InlineData("doğrulama")]
    [InlineData("DOĞRULAMA")]
    [InlineData("dogrulama")]
    [InlineData("DOGRULAMA")]
    [InlineData("ğrul")]
    [InlineData("deneme")]
    [InlineData("DENEME")]
    [InlineData("den")]
    public async Task VerificationRestoresAreFoundByTheWordsOfTheirVisibleLabel(string term)
    {
        await using var scenario = await CreateAsync(fixture);
        var data = await SeedAsync(scenario);

        Assert.Equal(["v1"], await data.OrderAsync("search=" + Uri.EscapeDataString(term)));
        Assert.Empty(data.Labels(await data.GetAsync("search=" + Uri.EscapeDataString(term))));
    }

    [Theory]
    [InlineData("doğrulama x")]
    [InlineData("yedek")]
    [InlineData("denemeler")]
    public async Task OnlyTermsInsideTheLabelWordsMatchByLabel(string term)
    {
        await using var scenario = await CreateAsync(fixture);
        var data = await SeedAsync(scenario);

        Assert.Empty(await data.OrderAsync("search=" + Uri.EscapeDataString(term)));
    }

    [Theory]
    [InlineData("MARKETİNG")]
    [InlineData("marketing")]
    [InlineData("MARKETING")]
    [InlineData("ALPHA-HOST")]
    [InlineData("CHARLİE-HOST")]
    [InlineData("charlıe")]
    public async Task SearchFoldsCaseAndTurkishLetters(string term)
    {
        await using var scenario = await CreateAsync(fixture);
        var data = await SeedAsync(scenario);

        Assert.NotEmpty(await data.OrderAsync("search=" + Uri.EscapeDataString(term)));
        Assert.Equal(await data.OrderAsync("search=" + Uri.EscapeDataString(term.ToLowerInvariant().Replace("ı", "i"))), await data.OrderAsync("search=" + Uri.EscapeDataString(term)));
    }

    [Fact]
    public async Task WithoutSortTheNewestRestoreIsFirstAndAFieldAloneUsesItsDefaultDirection()
    {
        await using var scenario = await CreateAsync(fixture);
        var data = await SeedAsync(scenario);

        var all = await data.GetAsync("purpose=All");
        Assert.Equal(["v1", "q4", "q3", "q2", "q1"], data.Labels(all));
        Assert.Equal(5, all.GetProperty("totalCount").GetInt32());
        Assert.Equal(["q1", "q2", "q3", "q4", "v1"], await data.OrderAsync("dir=asc"));
        Assert.Equal(["v1", "q4", "q3", "q2", "q1"], await data.OrderAsync("sort=createdAt"));
        Assert.Equal(["q2", "q4", "q3", "q1", "v1"], await data.OrderAsync("sort=target"));
        // The default list holds executions only: the verification is left out.
        Assert.Equal(["q4", "q3", "q2", "q1"], data.Labels(await data.GetAsync("")));
        Assert.Equal(["q1", "q2", "q3", "q4"], data.Labels(await data.GetAsync("dir=asc")));
    }

    [Fact]
    public async Task APageOfTiedRowsIsStableSoPagingNeverRepeatsOrSkipsARestore()
    {
        await using var scenario = await CreateAsync(fixture);
        var data = await SeedAsync(scenario);

        foreach (var sort in new[] { "status", "source", "computer", "target" })
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

        var beyond = await data.GetAsync("purpose=All&sort=status&pageSize=2&page=9");
        Assert.Empty(beyond.GetProperty("items").EnumerateArray());
        Assert.Equal(5, beyond.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task SearchMatchesTargetSourceAndComputerIgnoringCaseAndCombinesWithEveryFilter()
    {
        await using var scenario = await CreateAsync(fixture);
        var data = await SeedAsync(scenario);

        Assert.Equal(["q3", "q1"], await data.OrderAsync("search=ALPHA%20JOB"));
        Assert.Equal(["v1", "q2"], await data.OrderAsync("search=marketing"));
        Assert.Equal(["q4", "q3", "q2"], await data.OrderAsync("search=%20charlie%20"));
        Assert.Equal(["q2"], await data.OrderAsync("search=alpha&status=Cancelled"));
        Assert.Empty(await data.OrderAsync("search=no-such-restore"));
        Assert.Empty(await data.OrderAsync("search=%25"));
        Assert.Empty(await data.OrderAsync("search=_"));

        Assert.Equal(["q4", "q3"], await data.OrderAsync("search=charlie&status=Pending"));
        Assert.Equal(["q4", "q2"], await data.OrderAsync($"search=charlie&agentId={data.Charlie.AgentId}"));
        Assert.Equal(["q2"], await data.OrderAsync($"search=charlie&agentId={data.Charlie.AgentId}&status=Cancelled"));
        Assert.Equal(["q3"], await data.OrderAsync($"search=charlie&artifactId={data.Sales.ArtifactId}"));
        Assert.Equal(["q2"], data.Labels(await data.GetAsync("search=marketing")));
        Assert.Equal(["v1"], data.Labels(await data.GetAsync("search=marketing&purpose=Verification")));

        var combined = await data.GetAsync("purpose=All&search=charlie&sort=target&dir=desc&pageSize=2");
        Assert.Equal(3, combined.GetProperty("totalCount").GetInt32());
        Assert.Equal(["q3", "q4"], data.Labels(combined));
        Assert.Equal(["q2"], data.Labels(await data.GetAsync("purpose=All&search=charlie&sort=target&dir=desc&pageSize=2&page=2")));
    }

    [Fact]
    public async Task ARestoreOfAnotherTenantIsNeverFoundBySearchOrSort()
    {
        await using var scenario = await CreateAsync(fixture);
        await SeedAsync(scenario);
        var other = await scenario.World.CreateTenantAsync();

        using var response = await other.Admin.GetAsync(BackupApiScenario.RootOf(other) + "/restores?purpose=All&search=alpha&sort=target");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, AgentWorld.ReadJson(response).GetProperty("totalCount").GetInt32());
    }

    [Theory]
    [InlineData("sort=unknown", "sort")]
    [InlineData("sort=databaseName", "sort")]
    [InlineData("sort=target&dir=sideways", "dir")]
    [InlineData("page=0", "page")]
    [InlineData("pageSize=0", "pageSize")]
    [InlineData("pageSize=101", "pageSize")]
    public async Task AnInvalidListQueryIsABadRequestNamingTheField(string query, string field)
    {
        await using var scenario = await CreateAsync(fixture);

        using var response = await scenario.GetRawAsync("/restores?" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = AgentWorld.ReadJson(response);
        Assert.Equal("invalid_request", body.GetProperty("error").GetString());
        Assert.Equal(field, body.GetProperty("field").GetString());
    }
}
