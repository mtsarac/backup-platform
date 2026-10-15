using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BackupPlatform.Domain.Tenancy;
using static BackupPlatform.IntegrationTests.RestoreApiScenario;

namespace BackupPlatform.IntegrationTests;

/// <summary>
/// Isolated restore verification and the cleanup it owes, through the real HTTP API: the operator starts it, the agent receives and completes it, a leftover disposable
/// database is leased to its agent from the poll, and a rollback copy is removed only by a tenant administrator who repeats its name after its guarded retention.
/// </summary>
public sealed class RestoreVerificationApiTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private const string Verification = "database-restore-verification";
    private const string Cleanup = "database-restore-cleanup";
    private const string RollbackName = "bp_old_0123456789abcdef0123456789abcdef";

    private static object VerifyBody(Guid agentId, object? target = null) =>
        new { agentId, target = target ?? new { host = "sql-target", port = 1433, integratedSecurity = true, encrypt = true, trustServerCertificate = false } };

    private static object VerifyResult(string cleanup, bool passed = true) => new
    {
        preCheck = new[] { new { checkId = "target-reachable", status = "Passed", reasonCode = "ok", message = "The target server answered." } },
        verification = new
        {
            checks = new[]
            {
                new { checkId = "database-online", status = "Passed", reasonCode = "ok", message = "The restored database is online." },
                new { checkId = "integrity", status = passed ? "Passed" : "Failed", reasonCode = passed ? "ok" : "integrity-check-failed", message = "The integrity check ran." }
            },
            userTableCount = 3, readableTableCount = 3, cleanup
        }
    };

    private static async Task<(RestoreApiScenario Scenario, AcceptedBackup Backup)> StartScenarioAsync(PostgreSqlFixture fixture)
    {
        var scenario = await CreateAsync(fixture);
        await scenario.SyncCapabilitiesAsync(scenario.Agent, "mssql.restore.verification", Capability);
        return (scenario, await scenario.AcceptBackupAsync());
    }

    private static async Task<JsonElement> StartVerificationAsync(RestoreApiScenario scenario, Guid artifactId)
    {
        using var response = await scenario.StartPreparationRawAsync(artifactId, "verifications", VerifyBody(scenario.Agent.AgentId), Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return AgentWorld.ReadJson(response);
    }

    [Fact]
    public async Task AVerificationRunsFromTheOperatorThroughTheAgentToARestoredOutcomeWithItsDatabaseDropped()
    {
        var (scenario, backup) = await StartScenarioAsync(fixture);
        await using var _ = scenario;
        var viewer = await scenario.SessionAsync(TenantRole.Viewer);
        using var denied = await scenario.StartPreparationRawAsync(backup.ArtifactId, "verifications", VerifyBody(scenario.Agent.AgentId), "viewer-key", viewer);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        var created = await StartVerificationAsync(scenario, backup.ArtifactId);
        var restoreId = created.GetProperty("id").GetGuid();
        var name = created.GetProperty("databaseName").GetString()!;
        Assert.Equal(("Verification", "NewDatabase", "Pending"), (created.GetProperty("purpose").GetString(), created.GetProperty("mode").GetString(), StatusOf(created)));
        Assert.Matches("^bp_verify_[0-9a-f]{32}$", name);
        Assert.Equal(JsonValueKind.Null, created.GetProperty("verificationOutcome").ValueKind);
        Assert.Equal(("VerificationDatabase", "Idle", name), (Property(created, "cleanup", "kind").GetString(), Property(created, "cleanup", "state").GetString(), Property(created, "cleanup", "databaseName").GetString()));

        var task = await scenario.MustPollRestoreAsync(taskType: Verification);
        Assert.Equal(restoreId, task.OperationId);
        Assert.Equal(("Verification", "NewDatabase", name), (task.Snapshot.GetProperty("purpose").GetString(), task.Snapshot.GetProperty("mode").GetString(), task.Snapshot.GetProperty("databaseName").GetString()));
        using (var started = await scenario.StartAttemptRawAsync(task)) Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        using (var stage = await scenario.ProgressRawAsync(task, "Verifying", 50)) Assert.Equal(HttpStatusCode.NoContent, stage.StatusCode);
        using (var cleaning = await scenario.ProgressRawAsync(task, "CleaningUp", null)) Assert.Equal(HttpStatusCode.NoContent, cleaning.StatusCode);
        using (var completed = await scenario.CompleteRawAsync(task, Completion(task, "Succeeded", "NotModified", result: VerifyResult("Dropped"))))
            Assert.Equal(HttpStatusCode.NoContent, completed.StatusCode);

        var finished = await scenario.RestoreAsync(restoreId);
        Assert.Equal(("Succeeded", "Restored", "NotModified", "Completed"), (StatusOf(finished), finished.GetProperty("verificationOutcome").GetString(),
            finished.GetProperty("targetState").GetString(), Property(finished, "cleanup", "state").GetString()));
        Assert.Equal("Dropped", Property(finished, "result", "verification", "cleanup").GetString());
        Assert.Equal(2, Property(finished, "result", "verification", "checks").GetArrayLength());
        Assert.False(finished.GetProperty("replicaPinned").GetBoolean());
        Assert.Null(await scenario.PollRestoreAsync(taskType: Cleanup));

        // Only verifications are listed when asked for, and ordinary restores stay as they were.
        using var verifications = await scenario.GetRawAsync($"/restores?purpose=Verification&artifactId={backup.ArtifactId}");
        Assert.Equal(restoreId, AgentWorld.ReadJson(verifications).GetProperty("items")[0].GetProperty("id").GetGuid());
        using var restores = await scenario.GetRawAsync("/restores");
        Assert.Equal(0, AgentWorld.ReadJson(restores).GetProperty("totalCount").GetInt32());
        using var unknown = await scenario.GetRawAsync("/restores?purpose=Precheck");
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    [Fact]
    public async Task AVerificationRequestCannotChooseItsDatabaseNameModeOrOverwrite()
    {
        var (scenario, backup) = await StartScenarioAsync(fixture);
        await using var _ = scenario;
        foreach (var body in new object[]
        {
            new { agentId = scenario.Agent.AgentId, target = new { host = "sql-target", integratedSecurity = true }, databaseName = "Sales" },
            new { agentId = scenario.Agent.AgentId, target = new { host = "sql-target", integratedSecurity = true }, mode = "ReplaceExistingDatabase" },
            new { agentId = scenario.Agent.AgentId, target = new { host = "sql-target", integratedSecurity = true }, confirmOverwrite = "Sales" }
        })
        {
            using var response = await scenario.StartPreparationRawAsync(backup.ArtifactId, "verifications", body, Guid.NewGuid().ToString("N"));
            // Unknown members of the request type are ignored by the binder, so none of them can reach the store: the name is always generated.
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            Assert.Matches("^bp_verify_[0-9a-f]{32}$", AgentWorld.ReadJson(response).GetProperty("databaseName").GetString()!);
        }

        // A user restore cannot take a platform name either.
        using var reserved = await scenario.StartRawAsync(backup.ArtifactId, scenario.Body("bp_verify_0123456789abcdef0123456789abcdef"), "reserved-1");
        Assert.Equal(HttpStatusCode.BadRequest, reserved.StatusCode);
        Assert.Equal("databaseName", AgentWorld.ReadJson(reserved).GetProperty("field").GetString());
    }

    [Fact]
    public async Task AnAgentWithoutTheVerificationCapabilityCannotBeAskedToVerify()
    {
        await using var scenario = await CreateAsync(fixture);
        var backup = await scenario.AcceptBackupAsync();
        using var response = await scenario.StartPreparationRawAsync(backup.ArtifactId, "verifications", VerifyBody(scenario.Agent.AgentId), "no-capability");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("agent_cannot_restore", Error(response));
    }

    private static string Error(HttpResponseMessage response) => AgentWorld.ReadJson(response).GetProperty("error").GetString()!;

    [Fact]
    public async Task ALeftoverDisposableDatabaseIsLeasedToItsAgentFromThePollAndRemovedUnderItsToken()
    {
        var (scenario, backup) = await StartScenarioAsync(fixture);
        await using var _ = scenario;
        var created = await StartVerificationAsync(scenario, backup.ArtifactId);
        var restoreId = created.GetProperty("id").GetGuid();
        var name = created.GetProperty("databaseName").GetString()!;
        var task = await scenario.MustPollRestoreAsync(taskType: Verification);
        using (var started = await scenario.StartAttemptRawAsync(task)) Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        // The integrity check failed and the drop did not finish.
        using (var completed = await scenario.CompleteRawAsync(task, Completion(task, "Failed", "NotModified", "VerificationFailed", VerifyResult("Failed", passed: false))))
            Assert.Equal(HttpStatusCode.NoContent, completed.StatusCode);

        var failed = await scenario.RestoreAsync(restoreId);
        Assert.Equal(("Failed", "Failed", "VerificationFailed", "Pending"), (StatusOf(failed), failed.GetProperty("verificationOutcome").GetString(),
            failed.GetProperty("failureCode").GetString(), Property(failed, "cleanup", "state").GetString()));
        Assert.Equal("The backup restored, but the restored database did not pass its integrity or data-read checks.", failed.GetProperty("reason").GetString());

        // The next poll carries the owed removal, with references only, and leases it once.
        using var poll = await scenario.World.PollAsync(scenario.Agent);
        var envelope = Assert.Single(AgentWorld.ReadJson(poll).GetProperty("tasks").EnumerateArray(), item => item.GetProperty("type").GetString() == Cleanup);
        var cleanup = envelope.GetProperty("restoreCleanup");
        Assert.Equal(cleanup.GetProperty("leaseToken").GetGuid().ToString(), envelope.GetProperty("id").GetString());
        Assert.Equal(("VerificationDatabase", name, "mssql"), (cleanup.GetProperty("kind").GetString(), cleanup.GetProperty("databaseName").GetString(), cleanup.GetProperty("engine").GetString()));
        Assert.Equal("sql-target", Property(cleanup, "target", "host").GetString());
        Assert.Null(await scenario.PollRestoreAsync(taskType: Cleanup));

        var token = cleanup.GetProperty("leaseToken").GetGuid();
        var path = $"/api/agent/restore-cleanups/{restoreId}/complete";
        var agent = scenario.Agent;
        using (var wrong = await scenario.World.SendAgentAsync(HttpMethod.Post, path, agent.CredentialId, agent.Secret, body: new { leaseToken = Guid.NewGuid(), outcome = "Dropped" }))
            Assert.Equal(HttpStatusCode.Conflict, wrong.StatusCode);
        using (var malformed = await scenario.World.SendAgentAsync(HttpMethod.Post, path, agent.CredentialId, agent.Secret, body: new { leaseToken = token, outcome = "Failed" }))
            Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        var sibling = await scenario.AddAgentAsync();
        using (var foreign = await scenario.World.SendAgentAsync(HttpMethod.Post, path, sibling.CredentialId, sibling.Secret, body: new { leaseToken = token, outcome = "Dropped" }))
            Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        using (var done = await scenario.World.SendAgentAsync(HttpMethod.Post, path, agent.CredentialId, agent.Secret, body: new { leaseToken = token, outcome = "Dropped" }))
            Assert.Equal(HttpStatusCode.NoContent, done.StatusCode);

        var after = await scenario.RestoreAsync(restoreId);
        Assert.Equal("Completed", Property(after, "cleanup", "state").GetString());
        Assert.Equal("Failed", after.GetProperty("verificationOutcome").GetString());
    }

    [Fact]
    public async Task OnlyOperatorsMayRetryACleanupAndOnlyWhenOneIsOwed()
    {
        var (scenario, backup) = await StartScenarioAsync(fixture);
        await using var _ = scenario;
        var viewer = await scenario.SessionAsync(TenantRole.Viewer);
        var operatorSession = await scenario.SessionAsync(TenantRole.Operator);
        var restoreId = (await StartVerificationAsync(scenario, backup.ArtifactId)).GetProperty("id").GetGuid();
        var task = await scenario.MustPollRestoreAsync(taskType: Verification);
        using (var started = await scenario.StartAttemptRawAsync(task)) Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        using (var completed = await scenario.CompleteRawAsync(task, Completion(task, "Failed", "NotModified", "InternalError", new { verification = new { checks = Array.Empty<object>(), cleanup = "Failed", cleanupReasonCode = "database-in-use" } })))
            Assert.Equal(HttpStatusCode.NoContent, completed.StatusCode);

        using var asViewer = await viewer.SendAsync(HttpMethod.Post, scenario.Root + $"/restores/{restoreId}/cleanup/retry");
        using var asOperator = await operatorSession.SendAsync(HttpMethod.Post, scenario.Root + $"/restores/{restoreId}/cleanup/retry");
        Assert.Equal(HttpStatusCode.Forbidden, asViewer.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, asOperator.StatusCode);
        Assert.Equal("Pending", Property(AgentWorld.ReadJson(asOperator), "cleanup", "state").GetString());

        // A verification that owes nothing, a missing restore and the rollback route on a verification are all refused.
        var other = await StartVerificationAsync(scenario, backup.ArtifactId);
        using var notOwed = await scenario.Admin.SendAsync(HttpMethod.Post, scenario.Root + $"/restores/{other.GetProperty("id").GetGuid()}/cleanup/retry");
        Assert.Equal(HttpStatusCode.Conflict, notOwed.StatusCode);
        using var missing = await scenario.Admin.SendAsync(HttpMethod.Post, scenario.Root + $"/restores/{Guid.NewGuid()}/cleanup/retry");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task ARollbackCopyIsKeptRemovedOnlyByAnAdministratorWhoRepeatsItsNameAfterItsRetention()
    {
        // A one-minute guard instead of the seven-day default, so the test can pass it without expiring the sessions.
        await using var scenario = await CreateAsync(fixture, configuration: new Dictionary<string, string?> { ["Restores:RollbackRetention"] = "00:01:00" });
        var backup = await scenario.AcceptBackupAsync();
        var operatorSession = await scenario.SessionAsync(TenantRole.Operator);
        var created = await scenario.StartAsync(backup.ArtifactId, scenario.Body("Sales", "ReplaceExistingDatabase", "Sales"));
        var restoreId = created.GetProperty("id").GetGuid();
        await scenario.RunToCompletionAsync(result: new { databaseState = "ONLINE", retainedDatabaseName = RollbackName });

        var kept = await scenario.RestoreAsync(restoreId);
        Assert.Equal(("RollbackCopy", "Retained", RollbackName, false), (Property(kept, "cleanup", "kind").GetString(), Property(kept, "cleanup", "state").GetString(),
            Property(kept, "cleanup", "databaseName").GetString(), Property(kept, "cleanup", "canRequest").GetBoolean()));
        Assert.Null(await scenario.PollRestoreAsync(taskType: Cleanup));

        string Path(string suffix) => scenario.Root + $"/restores/{restoreId}/{suffix}";
        using var asOperator = await operatorSession.SendAsync(HttpMethod.Post, Path("rollback-removal"), new { confirmDatabaseName = RollbackName });
        using var wrongName = await scenario.Admin.SendAsync(HttpMethod.Post, Path("rollback-removal"), new { confirmDatabaseName = "bp_old_other" });
        using var noBody = await scenario.Admin.SendAsync(HttpMethod.Post, Path("rollback-removal"), new { });
        using var tooEarly = await scenario.Admin.SendAsync(HttpMethod.Post, Path("rollback-removal"), new { confirmDatabaseName = RollbackName });
        using var retryRoute = await scenario.Admin.SendAsync(HttpMethod.Post, Path("cleanup/retry"));
        Assert.Equal(HttpStatusCode.Forbidden, asOperator.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, wrongName.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noBody.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, tooEarly.StatusCode);
        Assert.Equal("retention_not_elapsed", Error(tooEarly));
        Assert.Equal(HttpStatusCode.Conflict, retryRoute.StatusCode);

        scenario.Clock.Advance(TimeSpan.FromMinutes(2));
        using var accepted = await scenario.Admin.SendAsync(HttpMethod.Post, Path("rollback-removal"), new { confirmDatabaseName = RollbackName });
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        using var poll = await scenario.World.PollAsync(scenario.Agent);
        var cleanup = Assert.Single(AgentWorld.ReadJson(poll).GetProperty("tasks").EnumerateArray(), item => item.GetProperty("type").GetString() == Cleanup).GetProperty("restoreCleanup");
        Assert.Equal(("RollbackCopy", RollbackName), (cleanup.GetProperty("kind").GetString(), cleanup.GetProperty("databaseName").GetString()));
        using var done = await scenario.World.SendAgentAsync(HttpMethod.Post, $"/api/agent/restore-cleanups/{restoreId}/complete", scenario.Agent.CredentialId, scenario.Agent.Secret,
            body: new { leaseToken = cleanup.GetProperty("leaseToken").GetGuid(), outcome = "Dropped" });
        Assert.Equal(HttpStatusCode.NoContent, done.StatusCode);
        Assert.Equal("Completed", Property(await scenario.RestoreAsync(restoreId), "cleanup", "state").GetString());
    }
}
