using System.Net;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.IntegrationTests;

public sealed class BackupJobIdempotencyApiTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private const string Password = "S3cretPassw0rd!-unique";

    private static Dictionary<string, string> Key(string key) => new() { ["Idempotency-Key"] = key };

    private static object LoginBody(Guid agentId, string password = Password, string name = "Nightly") => new
    {
        name, agentId,
        configuration = new BackupExecutionConfiguration
        {
            Database = "master", Host = "sql-server", IntegratedSecurity = false, DatabaseLogin = new("sa", password), Destinations = BackupTestDestinations.LocalBackend
        }
    };

    private static Task<HttpResponseMessage> Post(BackupApiScenario scenario, object body, string key, BrowserSession? session = null) =>
        (session ?? scenario.Admin).SendAsync(HttpMethod.Post, scenario.Root + "/backup-jobs", body, headers: Key(key));

    private async Task<(int Jobs, int Audits, int Secrets)> CountsAsync(BackupApiScenario scenario)
    {
        await using var context = fixture.CreatePlatformContext();
        var tenant = new TenantId(scenario.Agent.TenantId);
        var jobs = await context.BackupJobs.CountAsync(job => job.TenantId == tenant);
        var audits = await context.AuditEvents.CountAsync(e => e.TenantId == tenant && e.Action == AuditActions.BackupJobCreated);
        var prefix = $"dbc-{scenario.Agent.AgentId:N}-%";
        var secrets = await context.Database.SqlQuery<int>($"select count(*)::int as \"Value\" from platform_secrets where \"Name\" like {prefix}").SingleAsync();
        return (jobs, audits, secrets);
    }

    [Fact]
    public async Task The_same_key_and_payload_replays_the_original_job_and_the_side_effects_happen_once()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var body = LoginBody(scenario.Agent.AgentId);
        using var first = await Post(scenario, body, "key-1");
        using var again = await Post(scenario, body, "key-1");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(AgentWorld.ReadJson(first).GetProperty("id").GetGuid(), AgentWorld.ReadJson(again).GetProperty("id").GetGuid());
        Assert.Equal((1, 1, 1), await CountsAsync(scenario));
    }

    [Fact]
    public async Task The_same_key_with_another_payload_conflicts_and_creates_nothing_not_even_a_credential()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        using var first = await Post(scenario, LoginBody(scenario.Agent.AgentId), "key-2");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        foreach (var different in new[] { LoginBody(scenario.Agent.AgentId, name: "Other name"), LoginBody(scenario.Agent.AgentId, password: Password + "x") })
        {
            using var conflict = await Post(scenario, different, "key-2");
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            Assert.Equal("idempotency_conflict", AgentWorld.ReadJson(conflict).GetProperty("error").GetString());
        }
        Assert.Equal((1, 1, 1), await CountsAsync(scenario));
    }

    [Fact]
    public async Task The_key_stays_reserved_after_the_job_is_disabled()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var body = BackupApiScenario.JobBody(scenario.Agent.AgentId);
        using var first = await Post(scenario, body, "key-3");
        var id = AgentWorld.ReadJson(first).GetProperty("id").GetGuid();
        using var disabled = await scenario.Admin.SendAsync(HttpMethod.Post, scenario.Root + $"/backup-jobs/{id}/disable");
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        using var replay = await Post(scenario, body, "key-3");
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(id, AgentWorld.ReadJson(replay).GetProperty("id").GetGuid());
        using var conflict = await Post(scenario, BackupApiScenario.JobBody(scenario.Agent.AgentId, overlapPolicy: "Queue"), "key-3");
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal((1, 1, 0), await CountsAsync(scenario));
    }

    [Fact]
    public async Task The_stored_fingerprint_is_a_digest_and_no_row_holds_the_plaintext_password()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        using var created = await Post(scenario, LoginBody(scenario.Agent.AgentId), "key-4");
        var id = AgentWorld.ReadJson(created).GetProperty("id").GetGuid();
        await using var context = fixture.CreatePlatformContext();
        var job = await context.BackupJobs.AsNoTracking().SingleAsync(row => row.Id == id);
        Assert.Matches("^[0-9a-f]{64}$", job.RequestFingerprint);
        Assert.Equal("key-4", job.IdempotencyKey);
        Assert.DoesNotContain(Password, job.RequestFingerprint, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, System.Text.Json.JsonSerializer.Serialize(job.Configuration), StringComparison.Ordinal);
        var dump = await context.Database.SqlQuery<string>($"select row_to_json(j)::text as \"Value\" from backup_jobs j where \"Id\" = {id}").SingleAsync();
        Assert.DoesNotContain(Password, dump, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Parallel_identical_requests_create_exactly_one_job_one_audit_and_one_credential()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var body = LoginBody(scenario.Agent.AgentId);
        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(async _ =>
        {
            using var response = await Post(scenario, body, "key-5");
            return (response.StatusCode, Id: AgentWorld.ReadJson(response).GetProperty("id").GetGuid());
        }));
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.Created), r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Single(responses.Select(r => r.Id).Distinct());
        Assert.Equal((1, 1, 1), await CountsAsync(scenario));
    }

    [Fact]
    public async Task Parallel_requests_with_two_payloads_under_one_key_leave_one_job_and_no_stray_credential()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(async index =>
        {
            using var response = await Post(scenario, LoginBody(scenario.Agent.AgentId, password: Password + (index % 2)), "key-6");
            return response.StatusCode;
        }));
        Assert.Single(responses, status => status == HttpStatusCode.Created);
        Assert.All(responses, status => Assert.True(status is HttpStatusCode.Created or HttpStatusCode.OK or HttpStatusCode.Conflict));
        Assert.Equal((1, 1, 1), await CountsAsync(scenario));
    }

    [Fact]
    public async Task One_tenants_key_never_matches_another_tenants_job()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var other = await BackupApiScenario.CreateAsync(fixture);
        var body = BackupApiScenario.JobBody(scenario.Agent.AgentId);
        using var mine = await Post(scenario, body, "shared-key");
        using var theirs = await Post(other, BackupApiScenario.JobBody(other.Agent.AgentId), "shared-key");
        Assert.Equal(HttpStatusCode.Created, mine.StatusCode);
        Assert.Equal(HttpStatusCode.Created, theirs.StatusCode);
        Assert.NotEqual(AgentWorld.ReadJson(mine).GetProperty("id").GetGuid(), AgentWorld.ReadJson(theirs).GetProperty("id").GetGuid());
        await other.DisposeAsync();
    }

    [Fact]
    public async Task A_request_that_fails_validation_after_the_credential_was_minted_leaves_no_credential_and_the_key_free()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var bad = new { name = "x", agentId = scenario.Agent.AgentId, configuration = new BackupExecutionConfiguration
            { Database = "master", Host = "sql-server", IntegratedSecurity = false, DatabaseLogin = new("sa", Password), Destinations = BackupTestDestinations.LocalBackend },
            retryPolicy = new { maxAttempts = 9999 } };
        using var rejected = await Post(scenario, bad, "key-7");
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal((0, 0, 0), await CountsAsync(scenario));
        using var good = await Post(scenario, LoginBody(scenario.Agent.AgentId), "key-7");
        Assert.Equal(HttpStatusCode.Created, good.StatusCode);
    }

    [Fact]
    public async Task A_request_without_a_key_keeps_the_previous_behaviour_and_a_malformed_key_is_refused()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var body = BackupApiScenario.JobBody(scenario.Agent.AgentId);
        using var one = await scenario.CreateJobRawAsync(body);
        using var two = await scenario.CreateJobRawAsync(body);
        Assert.Equal(HttpStatusCode.Created, one.StatusCode);
        Assert.Equal(HttpStatusCode.Created, two.StatusCode);
        using var malformed = await Post(scenario, body, "bad key with spaces\u00e9");
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
    }
}
