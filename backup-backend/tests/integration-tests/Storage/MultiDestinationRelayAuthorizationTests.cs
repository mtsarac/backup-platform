using System.Net;
using System.Net.Http.Json;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Storage;
using Microsoft.EntityFrameworkCore;
using Xunit;
using System.Text.Json;

namespace BackupPlatform.IntegrationTests.Storage;

/// <summary>
/// The destination of every direct external transfer is authorized on the SERVER against the run's snapshot, lease and the connection as it is now.
/// Driven through HTTP exactly as the agent calls it: the backend relays no payload, so the call that carries the destination, lease and key is the upload
/// intent the agent records before it writes. A refused destination answers 409; an authorized one records the intent (200) without touching a real SFTP or Google Drive.
/// </summary>
public sealed class MultiDestinationRelayAuthorizationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private sealed record Arrangement(BackupApiScenario Scenario, Guid JobId, Guid RunId, TaskClaim Claim, Guid Central, Guid Sftp, Guid Drive);

    private static async Task<Guid> AddConnectionAsync(PostgreSqlFixture fixture, TenantId tenant, string provider, string name)
    {
        var id = Guid.NewGuid();
        await using var db = fixture.CreatePlatformContext();
        var connection = new StorageConnection(id, tenant, name, provider, StorageOwnership.TenantManaged, "tenant", new Dictionary<string, string>(), false, DateTimeOffset.UtcNow);
        connection.AttachSecret($"sc-{id:N}-{provider}", [provider == "sftp" ? "password" : "refreshToken"], DateTimeOffset.UtcNow);
        db.StorageConnections.Add(connection);
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<Arrangement> ArrangeAsync(BackupApiScenario scenario, bool multi = true)
    {
        var sftp = await AddConnectionAsync(fixture, scenario.Setup.Tenant.Id, "sftp", "Relay SFTP");
        var drive = await AddConnectionAsync(fixture, scenario.Setup.Tenant.Id, "google-drive", "Relay Drive");
        var destinations = multi
            ? new[]
            {
                new BackupDestinationContract { StorageProvider = "central", StorageExecutionLocation = "Backend", IsPrimary = true },
                new BackupDestinationContract { StorageProvider = "sftp", StorageConnectionId = sftp, StorageExecutionLocation = "Backend" },
                new BackupDestinationContract { StorageProvider = "google-drive", StorageConnectionId = drive, StorageExecutionLocation = "Backend" }
            }
            : [new BackupDestinationContract { StorageProvider = "sftp", StorageConnectionId = sftp, StorageExecutionLocation = "Backend", IsPrimary = true }];
        using var created = await scenario.CreateJobRawAsync(new
        {
            name = "Replicated", agentId = scenario.Agent.AgentId,
            configuration = new BackupExecutionConfiguration
            {
                Database = "master", Host = "sql-server", IntegratedSecurity = true,
                Destinations = destinations, RequiredVerifiedReplicaCount = multi ? 2 : 1
            }
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var jobId = AgentWorld.ReadJson(created).GetProperty("id").GetGuid();
        var runId = await scenario.StartRunAsync(jobId);
        var claim = await scenario.ClaimAsync();
        await scenario.StartAttemptAsync(claim);
        var run = await scenario.RunAsync(runId);
        var central = run.GetProperty("configuration").GetProperty("destinations").EnumerateArray()
            .First(d => d.GetProperty("storageProvider").GetString() == (multi ? "central" : "sftp")).GetProperty("storageConnectionId").GetGuid();
        return new Arrangement(scenario, jobId, runId, claim, central, sftp, drive);
    }

    private static string KeyOf(Arrangement a, Guid? runId = null, Guid? attemptId = null) =>
        ManagedBackupLayout.AttemptScope(a.Scenario.Setup.Tenant.Id.Value, a.JobId, runId ?? a.RunId, attemptId ?? a.Claim.AttemptId)
        + "/" + ManagedBackupLayout.StagedFileName("master", DateTimeOffset.UtcNow, Guid.NewGuid());

    private static async Task<HttpResponseMessage> UploadAsync(Arrangement a, Guid? destination, string? key = null, Guid? lease = null, EnrolledAgent? agent = null, string? destinationHeader = null, Guid? attempt = null)
    {
        var caller = agent ?? a.Scenario.Agent;
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/agent/tasks/{attempt ?? a.Claim.AttemptId}/storage/intent")
        {
            Content = JsonContent.Create(new TaskStorageVerifyRequest(3, new string('a', 64)))
        };
        request.Headers.TryAddWithoutValidation("Authorization", AgentAuthorizationScheme.FormatHeaderValue(caller.CredentialId, caller.Secret));
        request.Headers.Add("X-Backup-Agent-Protocol", "1.0");
        request.Headers.Add(TaskStorageProtocol.LeaseHeader, (lease ?? a.Claim.LeaseToken).ToString("D"));
        request.Headers.Add(TaskStorageProtocol.KeyHeader, key ?? KeyOf(a));
        if (destinationHeader is not null) request.Headers.Add(TaskStorageProtocol.DestinationHeader, destinationHeader);
        else if (destination is { } id) request.Headers.Add(TaskStorageProtocol.DestinationHeader, id.ToString("D"));
        return await a.Scenario.World.Anonymous.SendAsync(request);
    }

    private static async Task AssertRefusedAsync(HttpResponseMessage response, string error)
    {
        using var _ = response;
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(error, AgentWorld.ReadJson(response).GetProperty("error").GetString());
        await Task.CompletedTask;
    }

    private static void AssertAuthorized(HttpResponseMessage response)
    {
        using var _ = response;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Hard_quota_intent_refuses_missing_reservations_and_oversized_artifacts_before_any_upload()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var arrangement = await ArrangeAsync(scenario, multi: false);
        var quota = new BackupPlatform.IntegrationTests.Quota.QuotaWorld(fixture);
        await quota.AssignAsync(scenario.Setup.Tenant.Id, await quota.PlanAsync(1000));
        await AssertRefusedAsync(await UploadAsync(arrangement, arrangement.Sftp), "quota_exceeded");
        await BackupPlatform.IntegrationTests.Quota.AdmissionTestKit.InsertReservationAsync(fixture, scenario.Setup.Tenant.Id,
            arrangement.RunId, 2, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(6));
        await AssertRefusedAsync(await UploadAsync(arrangement, arrangement.Sftp), "quota_exceeded");
        await using (var context = fixture.CreatePlatformContext())
        {
            await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE quota_reservations SET \"Bytes\" = 3 WHERE \"RunId\" = {arrangement.RunId}");
        }
        // At the exact bound the intent is recorded, still without contacting a provider.
        AssertAuthorized(await UploadAsync(arrangement, arrangement.Sftp));
    }

    [Fact]
    public async Task Every_direct_snapshot_destination_is_authorized_for_its_own_connection_and_only_that()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var a = await ArrangeAsync(scenario);

        AssertAuthorized(await UploadAsync(a, a.Sftp));
        AssertAuthorized(await UploadAsync(a, a.Drive));
    }

    [Fact]
    public async Task A_connection_id_the_snapshot_does_not_contain_is_refused()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var a = await ArrangeAsync(scenario);
        var sameTenantButNotInTheRun = await AddConnectionAsync(fixture, scenario.Setup.Tenant.Id, "sftp", "Not in the run");

        await AssertRefusedAsync(await UploadAsync(a, Guid.NewGuid()), "destination_not_authorized");
        await AssertRefusedAsync(await UploadAsync(a, sameTenantButNotInTheRun), "destination_not_authorized");
    }

    [Fact]
    public async Task Another_tenants_connection_is_never_a_destination()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var a = await ArrangeAsync(scenario);
        var other = await scenario.World.CreateTenantAsync();
        var foreign = await AddConnectionAsync(fixture, other.Tenant.Id, "sftp", "Foreign");

        await AssertRefusedAsync(await UploadAsync(a, foreign), "destination_not_authorized");
    }

    [Fact]
    public async Task A_destination_the_agent_writes_itself_cannot_be_used_for_a_direct_intent()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var a = await ArrangeAsync(scenario);

        await AssertRefusedAsync(await UploadAsync(a, a.Central), "destination_not_authorized");
    }

    [Fact]
    public async Task A_call_without_a_destination_is_a_bad_request_for_single_and_multi_destination_runs()
    {
        await using var multiScenario = await BackupApiScenario.CreateAsync(fixture);
        var multi = await ArrangeAsync(multiScenario);
        await using var singleScenario = await BackupApiScenario.CreateAsync(fixture);
        var single = await ArrangeAsync(singleScenario, multi: false);

        foreach (var arrangement in new[] { multi, single })
        {
            using var response = await UploadAsync(arrangement, null);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("invalid_request", AgentWorld.ReadJson(response).GetProperty("error").GetString());
            Assert.Contains("destination header", AgentWorld.ReadJson(response).GetProperty("message").GetString());
        }
        AssertAuthorized(await UploadAsync(single, single.Central));
    }

    [Fact]
    public async Task A_malformed_destination_header_is_a_bad_request()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var a = await ArrangeAsync(scenario);

        using var response = await UploadAsync(a, null, destinationHeader: "not-a-guid");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", AgentWorld.ReadJson(response).GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_stale_or_foreign_lease_is_refused_for_every_destination()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var a = await ArrangeAsync(scenario);

        await AssertRefusedAsync(await UploadAsync(a, a.Sftp, lease: Guid.NewGuid()), "lease_conflict");
        await AssertRefusedAsync(await UploadAsync(a, a.Drive, lease: Guid.NewGuid()), "lease_conflict");
        await using var db = fixture.CreatePlatformContext();
        await db.BackupRunAttempts.Where(row => row.Id == a.Claim.AttemptId).ExecuteUpdateAsync(set => set.SetProperty(row => row.LeaseExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        await AssertRefusedAsync(await UploadAsync(a, a.Sftp), "lease_conflict");
    }

    [Fact]
    public async Task Another_tenants_agent_cannot_reach_this_attempt_even_with_the_right_lease_and_destination()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var a = await ArrangeAsync(scenario);
        var other = await scenario.World.CreateTenantAsync();
        var intruder = await scenario.World.EnrollAsync(other);

        await AssertRefusedAsync(await UploadAsync(a, a.Sftp, agent: intruder), "lease_conflict");
    }

    [Fact]
    public async Task A_key_outside_the_runs_own_layout_is_refused()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var a = await ArrangeAsync(scenario);

        foreach (var key in new[] { KeyOf(a, runId: Guid.NewGuid()), KeyOf(a, attemptId: Guid.NewGuid()), "not-a-managed-key" })
        {
            using var response = await UploadAsync(a, a.Sftp, key: key);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("invalid_request", AgentWorld.ReadJson(response).GetProperty("error").GetString());
        }
    }

    [Fact]
    public async Task A_disabled_connection_takes_no_new_upload_but_the_other_destinations_are_unaffected()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var a = await ArrangeAsync(scenario);
        await using (var db = fixture.CreatePlatformContext())
        {
            await db.StorageConnections.Where(row => row.Id == a.Sftp).ExecuteUpdateAsync(set => set.SetProperty(row => row.Enabled, false));
        }

        await AssertRefusedAsync(await UploadAsync(a, a.Sftp), "destination_unavailable");
        AssertAuthorized(await UploadAsync(a, a.Drive));
    }

    // ---- completion over HTTP ----

    private static BackupTaskCompletionRequest PartialSuccess(Arrangement a, Guid? strangerConnection = null)
    {
        var request = a.Scenario.SuccessRequest(a.Claim);
        var artifact = request.Artifact!;
        var key = KeyOf(a);
        BackupTaskReplica Replica(string provider, string target, Guid connection) =>
            artifact.Replicas[0] with { StorageProvider = provider, StorageTarget = target, StorageConnectionId = connection, Key = key };
        var sftp = strangerConnection ?? a.Sftp;
        var now = DateTimeOffset.UtcNow;
        BackupTaskDestinationResult Verified(string provider, Guid connection) => new()
        {
            StorageProvider = provider, StorageConnectionId = connection, State = "Verified", Upload = "Succeeded", Verification = "Verified", Key = key, StartedAt = now.AddSeconds(-1), CompletedAt = now
        };
        return request with
        {
            Artifact = artifact with { Replicas = [Replica("central", "central", a.Central), Replica("sftp", sftp.ToString("D"), sftp)] },
            Destinations =
            [
                Verified("central", a.Central), Verified("sftp", sftp),
                new BackupTaskDestinationResult
                {
                    StorageProvider = "google-drive", StorageConnectionId = a.Drive, State = "Failed", Upload = "Failed", Verification = "NotRun", FailureCode = "StorageUnavailable",
                    StartedAt = now.AddSeconds(-1), CompletedAt = now
                }
            ]
        };
    }

    [Fact]
    public async Task A_partial_success_is_accepted_and_the_API_reports_Succeeded_and_Degraded_side_by_side()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var a = await ArrangeAsync(scenario);

        using var response = await scenario.CompleteRawAsync(a.Claim, PartialSuccess(a));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var run = await scenario.RunAsync(a.RunId);
        Assert.Equal("Succeeded", BackupApiScenario.StatusOf(run));
        var replication = run.GetProperty("replication");
        Assert.Equal("Degraded", replication.GetProperty("status").GetString());
        Assert.True(replication.GetProperty("policySatisfied").GetBoolean());
        Assert.Equal((2, 2, 3), (replication.GetProperty("verifiedReplicas").GetInt32(), replication.GetProperty("requiredVerifiedReplicas").GetInt32(), replication.GetProperty("destinationCount").GetInt32()));
        var failed = replication.GetProperty("destinations").EnumerateArray().Single(d => d.GetProperty("state").GetString() == "Failed");
        Assert.Equal("google-drive", failed.GetProperty("storageProvider").GetString());
        Assert.Equal("StorageUnavailable", failed.GetProperty("failureCode").GetString());
        var attempt = BackupApiScenario.AttemptsOf(run).Single();
        Assert.Equal("Degraded", attempt.GetProperty("replication").GetProperty("status").GetString());
        Assert.Equal("Degraded", attempt.GetProperty("artifacts")[0].GetProperty("replication").GetProperty("status").GetString());
        using var artifacts = await scenario.Admin.GetAsync($"{scenario.Root}/backup-artifacts?pageSize=10");
        var listed = AgentWorld.ReadJson(artifacts).GetProperty("items").EnumerateArray().Single();
        Assert.Equal("Degraded", listed.GetProperty("replication").GetProperty("status").GetString());
        Assert.Equal(2, listed.GetProperty("replicas").GetArrayLength());
    }

    [Fact]
    public async Task A_completion_that_names_a_destination_outside_the_snapshot_is_rejected_and_publishes_nothing()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var a = await ArrangeAsync(scenario);

        using var response = await scenario.CompleteRawAsync(a.Claim, PartialSuccess(a, strangerConnection: Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotEqual("Succeeded", BackupApiScenario.StatusOf(await scenario.RunAsync(a.RunId)));
    }

    [Fact]
    public async Task A_multi_destination_completion_that_leaves_a_destination_unaccounted_for_is_rejected()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var a = await ArrangeAsync(scenario);
        var partial = PartialSuccess(a);

        using var response = await scenario.CompleteRawAsync(a.Claim, partial with { Destinations = [.. partial.Destinations.Take(2)] });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_failed_run_still_reports_how_each_destination_failed()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var a = await ArrangeAsync(scenario);
        var now = DateTimeOffset.UtcNow;
        var failures = new[] { ("central", a.Central), ("sftp", a.Sftp), ("google-drive", a.Drive) }.Select(d => new BackupTaskDestinationResult
        {
            StorageProvider = d.Item1, StorageConnectionId = d.Item2, State = "Failed", Upload = "Failed", Verification = "NotRun", FailureCode = "StorageUnavailable",
            StartedAt = now.AddSeconds(-1), CompletedAt = now
        }).ToArray();

        using var response = await scenario.CompleteRawAsync(a.Claim, new BackupTaskCompletionRequest
        {
            LeaseToken = a.Claim.LeaseToken, Status = "Failed", FailureCode = "StorageUnavailable", Destinations = failures
        });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var run = await scenario.RunAsync(a.RunId);
        var replication = BackupApiScenario.AttemptsOf(run).Single().GetProperty("replication");
        Assert.Equal("Unavailable", replication.GetProperty("status").GetString());
        Assert.Equal(3, replication.GetProperty("destinations").EnumerateArray().Count(d => d.GetProperty("state").GetString() == "Failed"));
        Assert.NotEqual("Succeeded", BackupApiScenario.StatusOf(run));
    }
}
