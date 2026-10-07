using System.Net;
using System.Text.Json;
using BackupPlatform.Domain.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using static BackupPlatform.IntegrationTests.RestoreApiScenario;

namespace BackupPlatform.IntegrationTests;

/// <summary>
/// Restores through the real HTTP API: the browser starts, lists, reads and cancels them, the target agent polls for them and reports back. Everything the platform
/// stores or answers is read back through the API; only the backups' preconditions that cannot be produced through it (an unverified replica, a disabled agent) are
/// written to the database directly.
/// </summary>
public sealed class RestoreApiTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static string Error(HttpResponseMessage response) => AgentWorld.ReadJson(response).GetProperty("error").GetString()!;

    [Fact]
    public async Task ARestoreGoesFromRequestThroughTheAgentToItsSuccessfulResult()
    {
        await using var scenario = await CreateAsync(fixture);
        var backup = await scenario.AcceptBackupAsync();
        var artifact = await scenario.ArtifactAsync(backup.ArtifactId);
        var replicaId = RetentionApiSupport.ReplicaOf(artifact).GetProperty("id").GetGuid();

        var created = await scenario.StartAsync(backup.ArtifactId, scenario.Body("Sales_Restored", options: new { terminateActiveConnections = false, verifyChecksum = true }));
        var restoreId = created.GetProperty("id").GetGuid();

        Assert.Equal("Pending", StatusOf(created));
        Assert.Equal(backup.ArtifactId, created.GetProperty("artifactId").GetGuid());
        Assert.Equal(replicaId, created.GetProperty("replicaId").GetGuid());
        Assert.Equal(scenario.Agent.AgentId, created.GetProperty("agentId").GetGuid());
        Assert.Equal("NewDatabase", created.GetProperty("mode").GetString());
        Assert.Equal("Forbidden", created.GetProperty("overwritePolicy").GetString());
        Assert.Equal("CentralStorage", created.GetProperty("sourceAccess").GetString());
        Assert.Equal("Sales_Restored", created.GetProperty("databaseName").GetString());
        Assert.Equal("sql-target", Property(created, "target", "host").GetString());
        Assert.True(Property(created, "options", "verifyBackup").GetBoolean());
        Assert.True(created.GetProperty("replicaPinned").GetBoolean());
        Assert.Equal(JsonValueKind.Null, created.GetProperty("targetState").ValueKind);

        // The agent's next poll hands the restore out, with references only.
        var task = await scenario.MustPollRestoreAsync();
        Assert.Equal(restoreId, task.OperationId);
        var snapshot = task.Snapshot;
        Assert.Equal(backup.Key, Property(snapshot, "source", "storageKey").GetString());
        Assert.Equal("central", Property(snapshot, "source", "storageProvider").GetString());
        Assert.Equal("CentralStorage", Property(snapshot, "source", "access").GetString());
        Assert.Equal(1024, Property(snapshot, "source", "sizeBytes").GetInt64());
        Assert.Equal(new string('b', 64), Property(snapshot, "source", "checksum").GetString());
        Assert.Equal("sql-target", Property(snapshot, "target", "host").GetString());
        Assert.Equal("Sales_Restored", snapshot.GetProperty("databaseName").GetString());
        Assert.Equal("NewDatabase", snapshot.GetProperty("mode").GetString());
        Assert.Equal("default", snapshot.GetProperty("stagingReference").GetString());
        Assert.False(snapshot.GetProperty("cancellationRequested").GetBoolean());
        Assert.Equal(scenario.TenantId, snapshot.GetProperty("tenantId").GetGuid());
        Assert.Equal("Leased", StatusOf(await scenario.RestoreAsync(restoreId)));
        Assert.Null(await scenario.PollRestoreAsync());

        using (var started = await scenario.StartAttemptRawAsync(task))
        {
            Assert.Equal(HttpStatusCode.OK, started.StatusCode);
            var lease = AgentWorld.ReadJson(started);
            Assert.False(lease.GetProperty("cancellationRequested").GetBoolean());
            Assert.True(lease.GetProperty("leaseExpiresAt").GetDateTimeOffset() > scenario.Clock.GetUtcNow());
        }
        using (var progress = await scenario.ProgressRawAsync(task, "Restoring", 55.5)) Assert.Equal(HttpStatusCode.NoContent, progress.StatusCode);
        var running = await scenario.RestoreAsync(restoreId);
        Assert.Equal("Running", StatusOf(running));
        Assert.Equal("Restoring", Property(running, "progress", "stage").GetString());
        Assert.Equal(55.5, Property(running, "progress", "percent").GetDouble());
        using (var renewed = await scenario.RenewRawAsync(task)) Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);

        using (var completed = await scenario.CompleteRawAsync(task, Completion(task, result: SampleResult()))) Assert.Equal(HttpStatusCode.NoContent, completed.StatusCode);

        var done = await scenario.RestoreAsync(restoreId);
        Assert.Equal("Succeeded", StatusOf(done));
        Assert.Equal("Online", done.GetProperty("targetState").GetString());
        Assert.Equal(JsonValueKind.Null, done.GetProperty("failureCode").ValueKind);
        Assert.Equal(JsonValueKind.Null, done.GetProperty("reason").ValueKind);
        Assert.Equal("The restored database is online.", done.GetProperty("targetStateSummary").GetString());
        Assert.False(done.GetProperty("replicaPinned").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, done.GetProperty("completedAt").ValueKind);
        Assert.Equal("ONLINE", Property(done, "result", "databaseState").GetString());
        Assert.True(Property(done, "result", "checksumVerified").GetBoolean());
        Assert.Equal("Passed", Property(done, "result", "preCheck")[0].GetProperty("status").GetString());
        Assert.Equal("/var/opt/mssql/data/Restored.mdf", Property(done, "result", "fileMappings")[0].GetProperty("targetPhysicalName").GetString());
        var attempt = Assert.Single(done.GetProperty("attempts").EnumerateArray());
        Assert.Equal("Succeeded", attempt.GetProperty("status").GetString());
        Assert.Null(await scenario.PollRestoreAsync());
        Assert.Equal("Available", (await scenario.ArtifactAsync(backup.ArtifactId)).GetProperty("state").GetString());
    }

    [Fact]
    public async Task ARestoreOfAnAgentLocalBackupIsReadFromTheAgentsOwnDisk()
    {
        await using var scenario = await CreateAsync(fixture);
        var backup = await scenario.AcceptBackupAsync(agentLocal: true);

        var created = await scenario.StartAsync(backup.ArtifactId);

        Assert.Equal("AgentLocal", created.GetProperty("sourceAccess").GetString());
        var task = await scenario.MustPollRestoreAsync();
        Assert.Equal("AgentLocal", Property(task.Snapshot, "source", "access").GetString());
        Assert.Equal("Agent", Property(task.Snapshot, "source", "storageExecutionLocation").GetString());
        Assert.Equal("local", Property(task.Snapshot, "source", "storageProvider").GetString());
    }

    [Fact]
    public async Task AReplaceRestoreCarriesTheConfirmedOverwriteAndTheChosenOptionsToTheAgent()
    {
        await using var scenario = await CreateAsync(fixture);
        var backup = await scenario.AcceptBackupAsync();

        var created = await scenario.StartAsync(backup.ArtifactId, scenario.Body("Existing", mode: "ReplaceExistingDatabase", confirmOverwrite: "Existing",
            options: new { terminateActiveConnections = true, verifyChecksum = false, verifyBackup = false }));

        Assert.Equal("ReplaceExistingDatabase", created.GetProperty("mode").GetString());
        Assert.Equal("ReplaceExisting", created.GetProperty("overwritePolicy").GetString());
        var options = (await scenario.MustPollRestoreAsync()).Snapshot.GetProperty("options");
        Assert.True(options.GetProperty("terminateActiveConnections").GetBoolean());
        Assert.False(options.GetProperty("verifyChecksum").GetBoolean());
        Assert.False(options.GetProperty("verifyBackup").GetBoolean());
    }

    [Fact]
    public async Task TheSameIdempotencyKeyAnswersWithTheOriginalRestoreAndADifferentRequestUnderItIsAConflict()
    {
        await using var scenario = await CreateAsync(fixture);
        var backup = await scenario.AcceptBackupAsync();
        var created = await scenario.StartAsync(backup.ArtifactId, scenario.Body("Sales"), "idem-1");

        // The same request again (an omitted option spelled out with its default is still the same request).
        using var replay = await scenario.StartRawAsync(backup.ArtifactId, scenario.Body("Sales", mode: "NewDatabase", options: new { verifyChecksum = true }), "idem-1");
        using var conflict = await scenario.StartRawAsync(backup.ArtifactId, scenario.Body("Other"), "idem-1");

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(created.GetProperty("id").GetGuid(), AgentWorld.ReadJson(replay).GetProperty("id").GetGuid());
        Assert.Equal($"{scenario.Root}/restores/{created.GetProperty("id").GetGuid()}", replay.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("idempotency_conflict", Error(conflict));
        using var list = await scenario.GetRawAsync("/restores");
        Assert.Equal(1, AgentWorld.ReadJson(list).GetProperty("totalCount").GetInt32());
        // Only the one restore was ever offered to the agent.
        Assert.NotNull(await scenario.PollRestoreAsync());
    }

    [Fact]
    public async Task AReplayStillAnswersWithTheOriginalRestoreOnceItHasEnded()
    {
        await using var scenario = await CreateAsync(fixture);
        var backup = await scenario.AcceptBackupAsync();
        var created = await scenario.StartAsync(backup.ArtifactId, scenario.Body(), "idem-1");
        await scenario.RunToCompletionAsync();

        using var replay = await scenario.StartRawAsync(backup.ArtifactId, scenario.Body(), "idem-1");

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var view = AgentWorld.ReadJson(replay);
        Assert.Equal(created.GetProperty("id").GetGuid(), view.GetProperty("id").GetGuid());
        Assert.Equal("Succeeded", StatusOf(view));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("scheduled-1")]
    [InlineData("has space")]
    public async Task AValidIdempotencyKeyIsRequired(string? key)
    {
        await using var scenario = await CreateAsync(fixture);
        var backup = await scenario.AcceptBackupAsync();

        using var response = await scenario.StartRawAsync(backup.ArtifactId, scenario.Body(), key);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", Error(response));
        Assert.Equal("Idempotency-Key", AgentWorld.ReadJson(response).GetProperty("field").GetString());
        using var list = await scenario.GetRawAsync("/restores");
        Assert.Equal(0, AgentWorld.ReadJson(list).GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task OnlyOperatorsAndAboveMayStartOrCancelWhileViewersMayRead()
    {
        await using var scenario = await CreateAsync(fixture);
        var backup = await scenario.AcceptBackupAsync();
        var viewer = await scenario.SessionAsync(TenantRole.Viewer);
        var operatorSession = await scenario.SessionAsync(TenantRole.Operator);
        var created = await scenario.StartAsync(backup.ArtifactId);
        var restoreId = created.GetProperty("id").GetGuid();

        using var viewerStart = await scenario.StartRawAsync(backup.ArtifactId, scenario.Body(), "viewer-key", viewer);
        using var viewerCancel = await scenario.CancelRawAsync(restoreId, viewer);
        using var viewerList = await scenario.GetRawAsync("/restores", viewer);
        using var viewerGet = await scenario.GetRawAsync($"/restores/{restoreId}", viewer);
        using var operatorStart = await scenario.StartRawAsync(backup.ArtifactId, scenario.Body("OperatorDb"), "operator-key", operatorSession);
        using var adminStart = await scenario.StartRawAsync(backup.ArtifactId, scenario.Body("AdminDb"), "admin-key");
        using var operatorCancel = await scenario.CancelRawAsync(restoreId, operatorSession);

        Assert.Equal(HttpStatusCode.Forbidden, viewerStart.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, viewerCancel.StatusCode);
        Assert.Equal(HttpStatusCode.OK, viewerList.StatusCode);
        Assert.Equal(HttpStatusCode.OK, viewerGet.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, operatorStart.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, adminStart.StatusCode);
        Assert.Equal(HttpStatusCode.OK, operatorCancel.StatusCode);
        Assert.Equal("Cancelled", StatusOf(AgentWorld.ReadJson(operatorCancel)));
    }

    [Fact]
    public async Task AnonymousCallersAreUnauthorized()
    {
        await using var scenario = await CreateAsync(fixture);
        var backup = await scenario.AcceptBackupAsync();
        var created = await scenario.StartAsync(backup.ArtifactId);

        using var list = await scenario.World.Anonymous.GetAsync(scenario.Root + "/restores");
        using var get = await scenario.World.Anonymous.GetAsync(scenario.Root + $"/restores/{created.GetProperty("id").GetGuid()}");

        Assert.Equal(HttpStatusCode.Unauthorized, list.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, get.StatusCode);
    }

    [Fact]
    public async Task AnotherTenantNeverSeesStartsOrCancelsARestore()
    {
        await using var scenario = await CreateAsync(fixture);
        var backup = await scenario.AcceptBackupAsync();
        var created = await scenario.StartAsync(backup.ArtifactId);
        var restoreId = created.GetProperty("id").GetGuid();
        var other = await scenario.World.CreateTenantAsync();
        var otherAgent = await scenario.World.EnrollAsync(other);
        var otherRoot = BackupApiScenario.RootOf(other);
        using var sync = await scenario.World.SyncCapabilitiesAsync(otherAgent, AgentWorld.CapabilitySnapshot(AgentWorld.CapabilityReport(Capability)));
        Assert.Equal(HttpStatusCode.OK, sync.StatusCode);

        using var start = await scenario.StartRawAsync(backup.ArtifactId, Body(otherAgent.AgentId), "foreign-key", other.Admin, otherRoot);
        using var get = await other.Admin.GetAsync($"{otherRoot}/restores/{restoreId}");
        using var cancel = await scenario.CancelRawAsync(restoreId, other.Admin, otherRoot);
        using var list = await other.Admin.GetAsync($"{otherRoot}/restores");
        using var foreignAgent = await scenario.StartRawAsync(backup.ArtifactId, Body(otherAgent.AgentId), "foreign-agent-key");

        Assert.Equal(HttpStatusCode.NotFound, start.StatusCode);
        Assert.Equal("artifact_not_found", Error(start));
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, cancel.StatusCode);
        Assert.Equal(0, AgentWorld.ReadJson(list).GetProperty("totalCount").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, foreignAgent.StatusCode);
        Assert.Equal("agent_not_found", Error(foreignAgent));
        Assert.Equal("Pending", StatusOf(await scenario.RestoreAsync(restoreId)));
    }

    [Fact]
    public async Task UnknownIdentifiersAreNotFound()
    {
        await using var scenario = await CreateAsync(fixture);
        var backup = await scenario.AcceptBackupAsync();

        using var artifact = await scenario.StartRawAsync(Guid.NewGuid(), scenario.Body(), "k1");
        using var agent = await scenario.StartRawAsync(backup.ArtifactId, Body(Guid.NewGuid()), "k2");
        using var get = await scenario.GetRawAsync($"/restores/{Guid.NewGuid()}");
        using var cancel = await scenario.CancelRawAsync(Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, artifact.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, agent.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, cancel.StatusCode);
    }

    [Theory]
    [InlineData("bad-host", "target.host", "invalid_value")]
    [InlineData("bad-port", "target.port", "invalid_value")]
    [InlineData("no-credential", "target.databaseCredentialReference", "invalid_value")]
    [InlineData("secret-as-reference", "target.databaseCredentialReference", "invalid_value")]
    [InlineData("system-database", "databaseName", "invalid_value")]
    [InlineData("empty-database", "databaseName", "invalid_value")]
    [InlineData("bad-mode", "mode", "invalid_value")]
    [InlineData("numeric-mode", "mode", "invalid_value")]
    [InlineData("replace-without-confirmation", "confirmOverwrite", "confirmation_required")]
    [InlineData("replace-wrong-confirmation", "confirmOverwrite", "confirmation_mismatch")]
    [InlineData("confirm-new", "confirmOverwrite", "not_applicable")]
    [InlineData("empty-agent", "agentId", "invalid_value")]
    [InlineData("empty-replica", "replicaId", "invalid_value")]
    public async Task AnInvalidRequestIsRefusedWithTheFieldAndAStableCodeAndNothingIsCreated(string scenarioName, string field, string code)
    {
        await using var scenario = await CreateAsync(fixture);
        var backup = await scenario.AcceptBackupAsync();
        var agent = scenario.Agent.AgentId;
        object body = scenarioName switch
        {
            "bad-host" => Body(agent, target: new { host = "bad host!", integratedSecurity = true }),
            "bad-port" => Body(agent, target: new { host = "sql-target", port = 70000, integratedSecurity = true }),
            "no-credential" => Body(agent, target: new { host = "sql-target", integratedSecurity = false }),
            "secret-as-reference" => Body(agent, target: new { host = "sql-target", integratedSecurity = false, databaseCredentialReference = "p@ss:word/with spaces" }),
            "system-database" => Body(agent, "master"),
            "empty-database" => Body(agent, ""),
            "bad-mode" => Body(agent, mode: "Overwrite"),
            "numeric-mode" => Body(agent, mode: "2"),
            "replace-without-confirmation" => Body(agent, mode: "ReplaceExistingDatabase"),
            "replace-wrong-confirmation" => Body(agent, "Existing", "ReplaceExistingDatabase", "existing"),
            "confirm-new" => Body(agent, confirmOverwrite: "Restored"),
            "empty-agent" => Body(Guid.Empty),
            _ => Body(agent, replicaId: Guid.Empty)
        };

        using var response = await scenario.StartRawAsync(backup.ArtifactId, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = AgentWorld.ReadJson(response);
        Assert.Equal("invalid_request", json.GetProperty("error").GetString());
        Assert.Equal(field, json.GetProperty("field").GetString());
        Assert.Equal(code, json.GetProperty("code").GetString());
        Assert.DoesNotContain("p@ss", json.GetRawText());
        using var list = await scenario.GetRawAsync("/restores");
        Assert.Equal(0, AgentWorld.ReadJson(list).GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task AnAgentThatNeverReportedTheRestoreCapabilityCannotRestore()
    {
        await using var scenario = await CreateAsync(fixture, restoreCapable: false);
        var backup = await scenario.AcceptBackupAsync();

        using var response = await scenario.StartRawAsync(backup.ArtifactId, scenario.Body());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var json = AgentWorld.ReadJson(response);
        Assert.Equal("agent_cannot_restore", json.GetProperty("error").GetString());
        Assert.Equal("capability_not_supported", json.GetProperty("reason").GetString());
        await scenario.SyncCapabilityAsync(scenario.Agent, "Unsupported");
        using var unsupported = await scenario.StartRawAsync(backup.ArtifactId, scenario.Body(), "key-2");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unsupported.StatusCode);
        await scenario.SyncCapabilityAsync(scenario.Agent);
        using var supported = await scenario.StartRawAsync(backup.ArtifactId, scenario.Body(), "key-3");
        Assert.Equal(HttpStatusCode.Accepted, supported.StatusCode);
    }

    [Fact]
    public async Task ADisabledAgentCannotRestore()
    {
        await using var scenario = await CreateAsync(fixture);
        var backup = await scenario.AcceptBackupAsync();
        await scenario.ExecuteAsync("UPDATE agents SET \"Status\" = 3 WHERE \"Id\" = @agent", ("agent", scenario.Agent.AgentId));

        using var response = await scenario.StartRawAsync(backup.ArtifactId, scenario.Body());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var json = AgentWorld.ReadJson(response);
        Assert.Equal("agent_cannot_restore", json.GetProperty("error").GetString());
        Assert.Equal("agent_not_active", json.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task AnAgentLocalBackupOfAnotherAgentIsRefusedWithAnExplicitTransferMessage()
    {
        await using var scenario = await CreateAsync(fixture);
        var backup = await scenario.AcceptBackupAsync(agentLocal: true);
        var sibling = await scenario.AddAgentAsync();

        using var automatic = await scenario.StartRawAsync(backup.ArtifactId, scenario.Body(agent: sibling), "k1");
        var replicaId = RetentionApiSupport.ReplicaOf(await scenario.ArtifactAsync(backup.ArtifactId)).GetProperty("id").GetGuid();
        using var named = await scenario.StartRawAsync(backup.ArtifactId, scenario.Body(replicaId: replicaId, agent: sibling), "k2");

        foreach (var response in new[] { automatic, named })
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            var json = AgentWorld.ReadJson(response);
            Assert.Equal("agent_transfer_required", json.GetProperty("error").GetString());
            var message = json.GetProperty("message").GetString()!;
            Assert.Contains("another agent", message);
            Assert.Contains("not supported", message);
        }
        Assert.Null(await scenario.PollRestoreAsync(sibling));
        // Its owner can restore it.
        Assert.Equal(HttpStatusCode.Accepted, (await scenario.StartRawAsync(backup.ArtifactId, scenario.Body(), "k3")).StatusCode);
    }

    [Fact]
    public async Task ABackendHeldFileOfTheLocalProviderIsNotReachableByAnyAgent()
    {
        await using var scenario = await CreateAsync(fixture);
        var backup = await scenario.AcceptBackupAsync(backendLocal: true);

        using var response = await scenario.StartRawAsync(backup.ArtifactId, scenario.Body());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("no_accessible_replica", Error(response));
    }

    [Fact]
    public async Task AReplicaThatIsNotVerifiedIsRefused()
    {
        await using var scenario = await CreateAsync(fixture);
        var backup = await scenario.AcceptBackupAsync();
        await scenario.ExecuteAsync("UPDATE backup_replicas SET \"VerificationState\" = 'Unverified', \"VerifiedAt\" = NULL WHERE \"ArtifactId\" = @artifact", ("artifact", backup.ArtifactId));

        using var response = await scenario.StartRawAsync(backup.ArtifactId, scenario.Body());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("replica_not_verified", Error(response));
    }

    [Fact]
    public async Task AReplicaOfAnotherBackupIsNotFound()
    {
        await using var scenario = await CreateAsync(fixture);
        var backup = await scenario.AcceptBackupAsync();
        var other = await scenario.AcceptBackupAsync();
        var otherReplica = RetentionApiSupport.ReplicaOf(await scenario.ArtifactAsync(other.ArtifactId)).GetProperty("id").GetGuid();

        using var response = await scenario.StartRawAsync(backup.ArtifactId, scenario.Body(replicaId: otherReplica));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("replica_not_found", Error(response));
    }

    [Fact]
    public async Task ASoftDeletedBackupCanBeRestoredInsideItsGracePeriodButNotAfterIt()
    {
        await using var scenario = await CreateAsync(fixture);
        var backup = await scenario.AcceptBackupAsync();
        using (var deleted = await RetentionApiSupport.DeleteArtifactAsync(scenario.Backup, backup.ArtifactId)) Assert.Equal(HttpStatusCode.Accepted, deleted.StatusCode);

        using var inside = await scenario.StartRawAsync(backup.ArtifactId, scenario.Body("InsideGrace"), "inside");
        scenario.Clock.Advance(TimeSpan.FromHours(25));
        var admin = await scenario.SessionAsync(TenantRole.Admin);
        using var after = await scenario.StartRawAsync(backup.ArtifactId, scenario.Body("AfterGrace"), "after", admin);

        Assert.Equal(HttpStatusCode.Accepted, inside.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, after.StatusCode);
        Assert.Equal("replica_not_restorable", Error(after));
    }

    [Fact]
    public async Task CancellingAPendingRestoreEndsItAtOnceAndTheAgentIsNeverOfferedIt()
    {
        await using var scenario = await CreateAsync(fixture);
        var backup = await scenario.AcceptBackupAsync();
        var created = await scenario.StartAsync(backup.ArtifactId);
        var restoreId = created.GetProperty("id").GetGuid();

        var cancelled = await scenario.CancelAsync(restoreId);
        var again = await scenario.CancelAsync(restoreId);

        Assert.Equal("Cancelled", StatusOf(cancelled));
        Assert.Equal("Cancelled", cancelled.GetProperty("failureCode").GetString());
        Assert.Equal("The restore was cancelled.", cancelled.GetProperty("reason").GetString());
        Assert.Equal("NotModified", cancelled.GetProperty("targetState").GetString());
        Assert.Equal("The target server was not modified.", cancelled.GetProperty("targetStateSummary").GetString());
        Assert.False(cancelled.GetProperty("replicaPinned").GetBoolean());
        Assert.Equal(cancelled.GetProperty("completedAt").GetDateTimeOffset(), again.GetProperty("completedAt").GetDateTimeOffset());
        Assert.Null(await scenario.PollRestoreAsync());
    }

    [Fact]
    public async Task CancellingARunningRestoreIsDeliveredOnLeaseRenewalAndTheAgentReportsWhatItLeftBehind()
    {
        await using var scenario = await CreateAsync(fixture);
        var backup = await scenario.AcceptBackupAsync();
        var created = await scenario.StartAsync(backup.ArtifactId, scenario.Body("Existing", mode: "ReplaceExistingDatabase", confirmOverwrite: "Existing"));
        var restoreId = created.GetProperty("id").GetGuid();
        var task = await scenario.MustPollRestoreAsync();
        using (var started = await scenario.StartAttemptRawAsync(task)) Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        using (var progress = await scenario.ProgressRawAsync(task, "Restoring", 30)) Assert.Equal(HttpStatusCode.NoContent, progress.StatusCode);
        using (var before = await scenario.RenewRawAsync(task)) Assert.False(AgentWorld.ReadJson(before).GetProperty("cancellationRequested").GetBoolean());

        var flagged = await scenario.CancelAsync(restoreId);

        Assert.Equal("Running", StatusOf(flagged));
        Assert.NotEqual(JsonValueKind.Null, flagged.GetProperty("cancellationRequestedAt").ValueKind);
        Assert.True(flagged.GetProperty("replicaPinned").GetBoolean());
        using (var renewed = await scenario.RenewRawAsync(task))
        {
            Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);
            Assert.True(AgentWorld.ReadJson(renewed).GetProperty("cancellationRequested").GetBoolean());
        }
        var result = new { databaseState = "RESTORING", targetStateNote = "The restore was cancelled while running; database Existing was left in RESTORING state and is not usable." };
        using (var completed = await scenario.CompleteRawAsync(task, Completion(task, "Cancelled", "PartiallyRestored", result: result))) Assert.Equal(HttpStatusCode.NoContent, completed.StatusCode);

        var done = await scenario.RestoreAsync(restoreId);
        Assert.Equal("Cancelled", StatusOf(done));
        Assert.Equal("PartiallyRestored", done.GetProperty("targetState").GetString());
        Assert.Contains("RESTORING", done.GetProperty("targetStateSummary").GetString());
        Assert.Equal("RESTORING", Property(done, "result", "databaseState").GetString());
        Assert.Contains("not usable", Property(done, "result", "targetStateNote").GetString());
        Assert.False(done.GetProperty("replicaPinned").GetBoolean());
    }

    [Fact]
    public async Task AFailedRestoreShowsAFixedReasonAndTheTargetStateTheAgentReported()
    {
        await using var scenario = await CreateAsync(fixture);
        var backup = await scenario.AcceptBackupAsync();
        var created = await scenario.StartAsync(backup.ArtifactId);

        await scenario.RunToCompletionAsync("Failed", "NotModified", "ChecksumMismatch");

        var done = await scenario.RestoreAsync(created.GetProperty("id").GetGuid());
        Assert.Equal("Failed", StatusOf(done));
        Assert.Equal("ChecksumMismatch", done.GetProperty("failureCode").GetString());
        Assert.Equal("The backup file does not match its recorded checksum; nothing was restored.", done.GetProperty("reason").GetString());
        Assert.Equal("NotModified", done.GetProperty("targetState").GetString());
        Assert.Equal("ChecksumMismatch", done.GetProperty("attempts")[0].GetProperty("failureCode").GetString());
    }

    [Fact]
    public async Task ARestoreHoldsTheAgentSoItsBackupWaitsAndTheOtherWayRound()
    {
        await using var scenario = await CreateAsync(fixture);
        var backup = await scenario.AcceptBackupAsync();
        var jobId = await scenario.Backup.CreateJobAsync();
        var created = await scenario.StartAsync(backup.ArtifactId);

        // A backup queued behind the restore: one poll hands out the restore only.
        var runId = await scenario.Backup.StartRunAsync(jobId);
        var task = await scenario.MustPollRestoreAsync();
        Assert.Null(await scenario.Backup.PollAsync());
        Assert.Equal("Pending", StatusOf(await scenario.Backup.RunAsync(runId)));

        using (var started = await scenario.StartAttemptRawAsync(task)) Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        using (var completed = await scenario.CompleteRawAsync(task, Completion(task))) Assert.Equal(HttpStatusCode.NoContent, completed.StatusCode);
        var claim = await scenario.Backup.ClaimAsync();
        Assert.Equal(runId, claim.RunId);

        // Now the backup holds the agent: a restore requested meanwhile waits for it.
        var second = await scenario.StartAsync(backup.ArtifactId, scenario.Body("Second"));
        Assert.Null(await scenario.PollRestoreAsync());
        using var cancelled = await scenario.Backup.CancelRawAsync(runId);
        await scenario.Backup.StartAttemptAsync(claim);
        using (var done = await scenario.Backup.CompleteRawAsync(claim, new BackupPlatform.Contracts.Backups.BackupTaskCompletionRequest { LeaseToken = claim.LeaseToken, Status = "Cancelled" }))
            Assert.Equal(HttpStatusCode.NoContent, done.StatusCode);
        Assert.Equal(second.GetProperty("id").GetGuid(), (await scenario.MustPollRestoreAsync()).OperationId);
        Assert.Equal("Succeeded", StatusOf(await scenario.RestoreAsync(created.GetProperty("id").GetGuid())));
    }

    [Fact]
    public async Task TheListFiltersByStatusArtifactAndAgentAndIsPaged()
    {
        await using var scenario = await CreateAsync(fixture);
        var first = await scenario.AcceptBackupAsync();
        var second = await scenario.AcceptBackupAsync();
        var sibling = await scenario.AddAgentAsync();
        var one = (await scenario.StartAsync(first.ArtifactId, scenario.Body("One"))).GetProperty("id").GetGuid();
        scenario.Clock.Advance(TimeSpan.FromSeconds(1));
        var two = (await scenario.StartAsync(second.ArtifactId, scenario.Body("Two"))).GetProperty("id").GetGuid();
        scenario.Clock.Advance(TimeSpan.FromSeconds(1));
        var three = (await scenario.StartAsync(first.ArtifactId, scenario.Body("Three", agent: sibling))).GetProperty("id").GetGuid();
        await scenario.CancelAsync(two);

        static Guid[] Ids(JsonElement page) => page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ToArray();
        async Task<JsonElement> Page(string query)
        {
            using var response = await scenario.GetRawAsync("/restores" + query);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return AgentWorld.ReadJson(response);
        }

        var all = await Page("");
        Assert.Equal([three, two, one], Ids(all));
        Assert.Equal(3, all.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, all.GetProperty("page").GetInt32());
        Assert.Equal(25, all.GetProperty("pageSize").GetInt32());
        Assert.Equal([three, one], Ids(await Page("?status=pending")));
        Assert.Equal([two], Ids(await Page("?status=Cancelled")));
        Assert.Equal([three, one], Ids(await Page($"?artifactId={first.ArtifactId}")));
        Assert.Equal([three], Ids(await Page($"?agentId={sibling.AgentId}")));
        Assert.Equal([one], Ids(await Page("?page=2&pageSize=2")));
        Assert.Empty(Ids(await Page("?page=9&pageSize=2")));
    }

    [Fact]
    public async Task TheListLeavesTheResultOutAndTheDetailViewHasIt()
    {
        await using var scenario = await CreateAsync(fixture);
        var backup = await scenario.AcceptBackupAsync();
        var restoreId = (await scenario.StartAsync(backup.ArtifactId)).GetProperty("id").GetGuid();
        await scenario.RunToCompletionAsync(result: SampleResult());

        using var response = await scenario.GetRawAsync("/restores");

        var listed = AgentWorld.ReadJson(response).GetProperty("items")[0];
        Assert.Equal(restoreId, listed.GetProperty("id").GetGuid());
        Assert.Equal("Succeeded", StatusOf(listed));
        Assert.Equal(JsonValueKind.Null, listed.GetProperty("result").ValueKind);
        Assert.Equal("ONLINE", Property(await scenario.RestoreAsync(restoreId), "result", "databaseState").GetString());
    }

    [Theory]
    [InlineData("?status=Exploded")]
    [InlineData("?status=1")]
    [InlineData("?page=0")]
    [InlineData("?pageSize=101")]
    [InlineData("?pageSize=0")]
    public async Task AnInvalidListQueryIsABadRequest(string query)
    {
        await using var scenario = await CreateAsync(fixture);

        using var response = await scenario.GetRawAsync("/restores" + query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", Error(response));
    }

    [Fact]
    public async Task ALostAgentEndsTheRestoreAgentLostWithAnUnknownTargetAndTheSweepNeverTouchesALiveOne()
    {
        await using var scenario = await CreateAsync(fixture);
        var backup = await scenario.AcceptBackupAsync();
        var lost = (await scenario.StartAsync(backup.ArtifactId, scenario.Body("Lost"))).GetProperty("id").GetGuid();
        var task = await scenario.MustPollRestoreAsync();
        using (var started = await scenario.StartAttemptRawAsync(task)) Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        using (var progress = await scenario.ProgressRawAsync(task, "Restoring", 5)) Assert.Equal(HttpStatusCode.NoContent, progress.StatusCode);

        scenario.Clock.Advance(TimeSpan.FromSeconds(60));
        await scenario.SweepAsync();
        // The sweep is global (other tests' leftovers expire too), so what is asserted is this restore: a lease with time left is never touched.
        Assert.Equal("Running", StatusOf(await scenario.RestoreAsync(lost, await scenario.SessionAsync(TenantRole.Admin))));
        scenario.Clock.Advance(TimeSpan.FromSeconds(70));
        await scenario.SweepAsync();
        var view = await scenario.RestoreAsync(lost, await scenario.SessionAsync(TenantRole.Admin));
        Assert.Equal("AgentLost", StatusOf(view));
        Assert.Equal("AgentLost", view.GetProperty("failureCode").GetString());
        Assert.Equal("The agent lease expired before the restore completed.", view.GetProperty("reason").GetString());
        Assert.Equal("Unknown", view.GetProperty("targetState").GetString());
        Assert.False(view.GetProperty("replicaPinned").GetBoolean());
        // The lost agent's late report is a conflict, never a resurrection.
        using var late = await scenario.CompleteRawAsync(task, Completion(task));
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
    }

    [Fact]
    public async Task ARestoreNobodyClaimsIsWithdrawnAfterTheUnclaimedExpiry()
    {
        await using var scenario = await CreateAsync(fixture);
        var backup = await scenario.AcceptBackupAsync();
        var restoreId = (await scenario.StartAsync(backup.ArtifactId)).GetProperty("id").GetGuid();

        scenario.Clock.Advance(TimeSpan.FromMinutes(59));
        await scenario.SweepAsync();
        var admin = await scenario.SessionAsync(TenantRole.Admin);
        Assert.Equal("Pending", StatusOf(await scenario.RestoreAsync(restoreId, admin)));
        scenario.Clock.Advance(TimeSpan.FromMinutes(2));
        var swept = await scenario.SweepAsync();

        Assert.True(swept.UnclaimedWithdrawn >= 1);
        var view = await scenario.RestoreAsync(restoreId, await scenario.SessionAsync(TenantRole.Admin));
        Assert.Equal("Cancelled", StatusOf(view));
        Assert.Equal("NotClaimed", view.GetProperty("failureCode").GetString());
        Assert.Equal("NotModified", view.GetProperty("targetState").GetString());
        Assert.False(view.GetProperty("replicaPinned").GetBoolean());
        Assert.Null(await scenario.PollRestoreAsync());
    }

    [Fact]
    public async Task TheStaleRestoreDetectorRunsAsAHostedServiceOfTheApi()
    {
        await using var scenario = await CreateAsync(fixture);

        Assert.Single(scenario.World.Factory.Services.GetServices<IHostedService>().OfType<BackupPlatform.Api.Restores.StaleRestoreDetector>());
    }
}
