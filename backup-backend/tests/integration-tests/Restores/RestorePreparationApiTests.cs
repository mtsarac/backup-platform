using System.Net;
using System.Text.Json;
using BackupPlatform.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using static BackupPlatform.IntegrationTests.RestoreApiScenario;

namespace BackupPlatform.IntegrationTests;

public sealed class RestorePreparationApiTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private const string PrecheckCapability = "mssql.restore.precheck";
    private const string DiscoveryCapability = "mssql.restore.discovery";

    private async Task<RestoreApiScenario> CreatePreparationScenarioAsync()
    {
        var scenario = await CreateAsync(fixture);
        await scenario.SyncCapabilitiesAsync(scenario.Agent, Capability, PrecheckCapability, DiscoveryCapability);
        return scenario;
    }

    private static object DiscoveryBody(RestoreApiScenario scenario) => new
    {
        agentId = scenario.Agent.AgentId,
        target = new { host = "sql-target", port = 1433, integratedSecurity = true, encrypt = true, trustServerCertificate = false }
    };

    [Fact]
    public async Task SuccessfulPrecheckIsReadonlyPinsItsSourceAndNeverAppearsInExecutionHistory()
    {
        await using var scenario = await CreatePreparationScenarioAsync();
        var backup = await scenario.AcceptBackupAsync();
        var view = await scenario.StartPreparationAsync(backup.ArtifactId);
        var id = view.GetProperty("id").GetGuid();
        var replica = view.GetProperty("replicaId").GetGuid();
        var world = new RestoreWorld(fixture);
        Assert.Equal("Precheck", view.GetProperty("purpose").GetString());
        Assert.True(view.GetProperty("replicaPinned").GetBoolean());
        Assert.Equal(1, await world.ActivePinsAsync(replica));
        var task = await scenario.MustPollRestoreAsync(taskType: "database-restore-precheck");
        Assert.Equal(id, task.OperationId);
        Assert.Equal("Precheck", task.Snapshot.GetProperty("purpose").GetString());
        using (var started = await scenario.StartAttemptRawAsync(task)) Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        Assert.Equal(1, await world.ActivePinsAsync(replica));
        using (var completed = await scenario.CompleteRawAsync(task, Completion(task, targetState: "NotModified", result: SampleResult())))
            Assert.Equal(HttpStatusCode.NoContent, completed.StatusCode);
        var done = await scenario.RestoreAsync(id);
        Assert.Equal("Succeeded", StatusOf(done));
        Assert.Equal("NotModified", done.GetProperty("targetState").GetString());
        Assert.Equal("Passed", Property(done, "result", "preCheck")[0].GetProperty("status").GetString());
        Assert.True(Property(done, "result", "backupVerified").GetBoolean());
        Assert.False(done.GetProperty("replicaPinned").GetBoolean());
        Assert.Equal(0, await world.ActivePinsAsync(replica));
        using var history = await scenario.GetRawAsync("/restores");
        Assert.Equal(0, AgentWorld.ReadJson(history).GetProperty("totalCount").GetInt32());
        var execution = await scenario.StartAsync(backup.ArtifactId);
        Assert.Equal("Execution", execution.GetProperty("purpose").GetString());
        using var listed = await scenario.GetRawAsync("/restores");
        Assert.Equal(execution.GetProperty("id").GetGuid(), Assert.Single(AgentWorld.ReadJson(listed).GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
    }

    [Theory]
    [InlineData("precheck", PrecheckCapability, "database-restore-precheck")]
    [InlineData("discovery", DiscoveryCapability, "database-restore-discovery")]
    public async Task LegacyAgentCannotCreateOrClaimPreparationUntilItAdvertisesTheSpecificCapability(string purpose, string capability, string taskType)
    {
        await using var scenario = await CreateAsync(fixture);
        var backup = await scenario.AcceptBackupAsync();
        var body = purpose == "discovery" ? DiscoveryBody(scenario) : scenario.Body();
        using var rejected = await scenario.StartPreparationRawAsync(backup.ArtifactId, purpose, body);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
        await scenario.SyncCapabilitiesAsync(scenario.Agent, Capability, capability);
        var created = await scenario.StartPreparationAsync(backup.ArtifactId, purpose, body);
        await scenario.SyncCapabilityAsync(scenario.Agent);
        using (var poll = await scenario.World.PollAsync(scenario.Agent))
        {
            Assert.Equal(HttpStatusCode.OK, poll.StatusCode);
            Assert.Empty(AgentWorld.ReadJson(poll).GetProperty("tasks").EnumerateArray());
        }
        Assert.Equal("Pending", StatusOf(await scenario.RestoreAsync(created.GetProperty("id").GetGuid())));
        await scenario.SyncCapabilitiesAsync(scenario.Agent, Capability, capability);
        Assert.Equal(created.GetProperty("id").GetGuid(), (await scenario.MustPollRestoreAsync(taskType: taskType)).OperationId);
    }

    [Theory]
    [InlineData("precheck")]
    [InlineData("discovery")]
    public async Task PreparationIdempotencyReplaysConflictsAndIsSeparateFromExecution(string purpose)
    {
        await using var scenario = await CreatePreparationScenarioAsync();
        var backup = await scenario.AcceptBackupAsync();
        var body = purpose == "discovery" ? DiscoveryBody(scenario) : scenario.Body();
        using var missing = await scenario.StartPreparationRawAsync(backup.ArtifactId, purpose, body, key: null);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        var created = await scenario.StartPreparationAsync(backup.ArtifactId, purpose, body, "shared-key");
        using var replay = await scenario.StartPreparationRawAsync(backup.ArtifactId, purpose, body, "shared-key");
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(created.GetProperty("id").GetGuid(), AgentWorld.ReadJson(replay).GetProperty("id").GetGuid());
        using var conflict = await scenario.StartPreparationRawAsync(backup.ArtifactId, purpose,
            Body(scenario.Agent.AgentId, target: new { host = "different", integratedSecurity = true }), "shared-key");
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var execution = await scenario.StartAsync(backup.ArtifactId, key: "shared-key");
        Assert.NotEqual(created.GetProperty("id").GetGuid(), execution.GetProperty("id").GetGuid());
        var otherPurpose = purpose == "precheck" ? "discovery" : "precheck";
        var other = await scenario.StartPreparationAsync(backup.ArtifactId, otherPurpose,
            otherPurpose == "discovery" ? DiscoveryBody(scenario) : scenario.Body(), "shared-key");
        Assert.NotEqual(created.GetProperty("id").GetGuid(), other.GetProperty("id").GetGuid());
    }

    [Theory]
    [InlineData("precheck")]
    [InlineData("discovery")]
    public async Task ViewerAndPlatformAdministratorWithoutMembershipCannotStartOrCancelPreparation(string purpose)
    {
        await using var scenario = await CreatePreparationScenarioAsync();
        var backup = await scenario.AcceptBackupAsync();
        var body = purpose == "discovery" ? DiscoveryBody(scenario) : scenario.Body();
        var created = await scenario.StartPreparationAsync(backup.ArtifactId, purpose, body);
        var id = created.GetProperty("id").GetGuid();
        var viewer = await scenario.SessionAsync(TenantRole.Viewer);
        var platform = await scenario.World.SignInAsync(await scenario.World.SeedUserAsync(platformAdmin: true));
        foreach (var session in new[] { viewer, platform })
        {
            using var start = await scenario.StartPreparationRawAsync(backup.ArtifactId, purpose, body, session: session);
            using var cancel = await scenario.CancelRawAsync(id, session);
            Assert.Equal(HttpStatusCode.Forbidden, start.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, cancel.StatusCode);
        }
        using var platformDetail = await scenario.GetRawAsync($"/restores/{id}", platform);
        Assert.Equal(HttpStatusCode.Forbidden, platformDetail.StatusCode);
        Assert.Equal("Pending", StatusOf(await scenario.RestoreAsync(id, viewer)));
    }

    [Theory]
    [InlineData("precheck")]
    [InlineData("discovery")]
    public async Task OtherTenantCannotUseArtifactOrReadOrCancelPreparation(string purpose)
    {
        await using var scenario = await CreatePreparationScenarioAsync();
        var backup = await scenario.AcceptBackupAsync();
        var created = await scenario.StartPreparationAsync(backup.ArtifactId, purpose, purpose == "discovery" ? DiscoveryBody(scenario) : scenario.Body());
        var id = created.GetProperty("id").GetGuid();
        var other = await scenario.World.CreateTenantAsync();
        var agent = await scenario.World.EnrollAsync(other);
        await scenario.SyncCapabilitiesAsync(agent, Capability, PrecheckCapability, DiscoveryCapability);
        var root = BackupApiScenario.RootOf(other);
        using var start = await scenario.StartPreparationRawAsync(backup.ArtifactId, purpose, Body(agent.AgentId), session: other.Admin, root: root);
        using var detail = await other.Admin.GetAsync($"{root}/restores/{id}");
        using var cancel = await scenario.CancelRawAsync(id, other.Admin, root);
        Assert.Equal(HttpStatusCode.NotFound, start.StatusCode);
        Assert.Equal("artifact_not_found", AgentWorld.ReadJson(start).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, cancel.StatusCode);
        Assert.Equal("Pending", StatusOf(await scenario.RestoreAsync(id)));
    }

    [Fact]
    public async Task DiscoveryTransportsTheRealInstanceAndDatabaseResultWithoutModifyingTheTargetOrHistory()
    {
        await using var scenario = await CreatePreparationScenarioAsync();
        var backup = await scenario.AcceptBackupAsync();
        var created = await scenario.StartPreparationAsync(backup.ArtifactId, "discovery", DiscoveryBody(scenario));
        var id = created.GetProperty("id").GetGuid();
        Assert.Equal("Discovery", created.GetProperty("purpose").GetString());
        var task = await scenario.MustPollRestoreAsync(taskType: "database-restore-discovery");
        Assert.Equal("Discovery", task.Snapshot.GetProperty("purpose").GetString());
        Assert.Equal("", task.Snapshot.GetProperty("databaseName").GetString());
        using (var started = await scenario.StartAttemptRawAsync(task)) Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        var result = new
        {
            discovery = new
            {
                instance = new { engine = "mssql", serverName = "sql-target", instanceName = "MSSQLSERVER", version = "17.0.1000.7", edition = "Developer Edition", hostPlatform = "Linux" },
                databases = new[] { new { name = "master", isSystem = true, availability = "ONLINE" }, new { name = "Sales", isSystem = false, availability = "ONLINE" } }
            }
        };
        using (var completed = await scenario.CompleteRawAsync(task, Completion(task, targetState: "NotModified", result: result)))
            Assert.Equal(HttpStatusCode.NoContent, completed.StatusCode);
        var done = await scenario.RestoreAsync(id);
        Assert.Equal("Succeeded", StatusOf(done));
        Assert.Equal("NotModified", done.GetProperty("targetState").GetString());
        Assert.Equal("Developer Edition", Property(done, "result", "discovery", "instance", "edition").GetString());
        Assert.Equal("Sales", Property(done, "result", "discovery", "databases")[1].GetProperty("name").GetString());
        Assert.False(Property(done, "result", "discovery", "databases")[1].GetProperty("isSystem").GetBoolean());
        Assert.False(done.GetProperty("replicaPinned").GetBoolean());
        using var history = await scenario.GetRawAsync("/restores");
        Assert.Empty(AgentWorld.ReadJson(history).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task CancellingPendingPreparationReleasesItsPinAndAuditsOnlyOnce()
    {
        await using var scenario = await CreatePreparationScenarioAsync();
        var backup = await scenario.AcceptBackupAsync();
        var created = await scenario.StartPreparationAsync(backup.ArtifactId);
        var id = created.GetProperty("id").GetGuid();
        await scenario.CancelAsync(id);
        await scenario.CancelAsync(id);
        Assert.Equal(0, await new RestoreWorld(fixture).ActivePinsAsync(created.GetProperty("replicaId").GetGuid()));
        Assert.Equal("NotModified", (await scenario.RestoreAsync(id)).GetProperty("targetState").GetString());
        await using var context = fixture.CreatePlatformContext();
        Assert.Equal(1, await context.AuditEvents.CountAsync(audit => audit.TargetId == id.ToString() && audit.Action == "restore.cancel_requested"));
    }
}
