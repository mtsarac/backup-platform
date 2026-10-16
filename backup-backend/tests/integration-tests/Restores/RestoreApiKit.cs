using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BackupPlatform.Api.Restores;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Domain.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BackupPlatform.IntegrationTests;

/// <summary>What an agent receives from a poll for a restore: enough to call back for that attempt.</summary>
internal sealed record RestoreTask(Guid OperationId, Guid AttemptId, Guid LeaseToken, JsonElement Snapshot);

/// <summary>
/// A tenant with an administrator and one enrolled agent on a real API host, driven only through HTTP: the browser side starts, reads and cancels restores,
/// the agent side polls for them and reports back with its own credentials. Backups are accepted the way an agent completes them, so every artifact and replica
/// is produced by production code.
/// </summary>
internal sealed class RestoreApiScenario : IAsyncDisposable
{
    public const string Capability = "mssql.restore.database";

    private RestoreApiScenario(BackupApiScenario backup) => Backup = backup;

    public BackupApiScenario Backup { get; }
    public AgentWorld World => Backup.World;
    public EnrolledAgent Agent => Backup.Agent;
    public BrowserSession Admin => Backup.Admin;
    public string Root => Backup.Root;
    public MutableClock Clock => Backup.Clock;
    public Guid TenantId => Backup.Setup.Tenant.Id.Value;

    public static async Task<RestoreApiScenario> CreateAsync(PostgreSqlFixture database, bool restoreCapable = true, IReadOnlyDictionary<string, string?>? configuration = null)
    {
        var scenario = new RestoreApiScenario(await BackupApiScenario.CreateAsync(database, configuration));
        if (restoreCapable) await scenario.SyncCapabilityAsync(scenario.Agent);
        return scenario;
    }

    public ValueTask DisposeAsync() => Backup.DisposeAsync();

    // A snapshot sync replaces the agent's capability rows, and every backup run needs this one.
    private static AgentCapabilityReport MultiDestination => AgentWorld.CapabilityReport(BackupTaskProtocol.MultiDestinationCapability);

    /// <summary>The agent reports (as a capability sync does) that it can restore.</summary>
    public async Task SyncCapabilityAsync(EnrolledAgent agent, string support = "Supported")
    {
        using var response = await World.SyncCapabilitiesAsync(agent, AgentWorld.CapabilitySnapshot(AgentWorld.CapabilityReport(Capability, support), MultiDestination));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    public async Task SyncCapabilitiesAsync(EnrolledAgent agent, params string[] capabilities)
    {
        using var response = await World.SyncCapabilitiesAsync(agent,
            AgentWorld.CapabilitySnapshot(capabilities.Select(capability => AgentWorld.CapabilityReport(capability)).Append(MultiDestination).ToArray()));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>An enrolled sibling agent of the same tenant, optionally able to restore.</summary>
    public async Task<EnrolledAgent> AddAgentAsync(bool restoreCapable = true)
    {
        var agent = await World.DeclareBackupCapabilityAsync(await World.EnrollAsync(Backup.Setup));
        if (restoreCapable) await SyncCapabilityAsync(agent);
        return agent;
    }

    // ---- backups ----

    /// <summary>
    /// A backup accepted through the API: in central storage (any agent can read it), or on <paramref name="owner"/>'s own disk when <paramref name="agentLocal"/>.
    /// <paramref name="backendLocal"/> reports the replica as a backend-held file of the local provider, which no agent can reach.
    /// </summary>
    public async Task<AcceptedBackup> AcceptBackupAsync(bool agentLocal = false, bool backendLocal = false, EnrolledAgent? owner = null, string engine = "mssql")
    {
        owner ??= Agent;
        var provider = agentLocal || backendLocal ? "local" : "central";
        var location = agentLocal ? "Agent" : "Backend";
        var body = new
        {
            name = "Nightly full backup",
            agentId = owner.AgentId,
            configuration = new BackupExecutionConfiguration
            {
                Engine = engine, Database = "master", Host = "sql-server", IntegratedSecurity = engine == "mssql", DatabaseCredentialReference = engine == "postgres" ? "pg-credential" : null,
                CopyOnly = engine == "mssql", Destinations = [agentLocal || backendLocal ? BackupTestDestinations.Local(location: location) : BackupTestDestinations.Central()]
            }
        };
        using var created = await Backup.CreateJobRawAsync(body);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var jobId = AgentWorld.ReadJson(created).GetProperty("id").GetGuid();
        var runId = await Backup.StartRunAsync(jobId);
        var claim = await Backup.ClaimAsync(owner);
        Assert.Equal(runId, claim.RunId);
        await Backup.StartAttemptAsync(claim);
        var request = Backup.SuccessRequest(claim);
        var replica = request.Artifact!.Replicas[0] with { StorageProvider = provider, StorageTarget = agentLocal || backendLocal ? "local" : "central", Key = $"{claim.AttemptId:N}/backup.bak" };
        request = request with { Artifact = request.Artifact with { Replicas = [replica] } };
        if (engine == "postgres")
            request = request with { Artifact = request.Artifact with
            {
                SourceMetadataJson = JsonSerializer.Serialize(new { engine, databaseName = "master", backupType = "Full",
                    backupStartedAt = Clock.GetUtcNow().AddSeconds(-10), backupFinishedAt = Clock.GetUtcNow().AddSeconds(-5),
                    backupVerification = new { state = "Passed", method = "pg_restore-full-read" } }),
                AppliedOptionsJson = """{"copyOnly":false,"checksum":true,"compression":true}"""
            } };
        var acceptedAt = Clock.GetUtcNow();
        using var completed = await Backup.CompleteRawAsync(claim, request);
        Assert.Equal(HttpStatusCode.NoContent, completed.StatusCode);
        var artifact = RetentionApiSupport.ArtifactOf(await Backup.RunAsync(runId));
        return new AcceptedBackup(jobId, runId, claim.AttemptId, artifact.GetProperty("id").GetGuid(), replica.Key, acceptedAt);
    }

    public Task<JsonElement> ArtifactAsync(Guid artifactId) => RetentionApiSupport.ArtifactAsync(Backup, artifactId);

    // ---- the browser side ----

    public static object Body(Guid agentId, string databaseName = "Restored", string? mode = null, string? confirmOverwrite = null, Guid? replicaId = null,
        object? options = null, object? target = null, Guid? precheckId = null) => new
        {
            agentId,
            replicaId,
            target = target ?? new { host = "sql-target", port = 1433, integratedSecurity = true, encrypt = true, trustServerCertificate = false },
            databaseName,
            mode,
            confirmOverwrite,
            options,
            precheckId
        };

    public object Body(string databaseName = "Restored", string? mode = null, string? confirmOverwrite = null, Guid? replicaId = null, object? options = null, EnrolledAgent? agent = null) =>
        Body((agent ?? Agent).AgentId, databaseName, mode, confirmOverwrite, replicaId, options);

    /// <summary>POST restore with a CSRF token and (unless <paramref name="key"/> is null) an Idempotency-Key.</summary>
    public async Task<HttpResponseMessage> StartRawAsync(Guid artifactId, object? body, string? key = "key-1", BrowserSession? session = null, string? root = null)
    {
        var browser = session ?? Admin;
        using var csrf = await browser.Client.GetAsync("/api/auth/antiforgery");
        using var request = new HttpRequestMessage(HttpMethod.Post, (root ?? Root) + $"/backup-artifacts/{artifactId}/restore");
        request.Headers.Add("X-CSRF-TOKEN", AgentWorld.ReadJson(csrf).GetProperty("requestToken").GetString());
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        request.Content = JsonContent.Create(body);
        return await browser.Client.SendAsync(request);
    }

    /// <summary>Starts a restore, expects 202 Accepted with a Location, and returns the view.</summary>
    public async Task<JsonElement> StartAsync(Guid artifactId, object? body = null, string? key = null)
    {
        using var response = await StartRawAsync(artifactId, body ?? Body(), key ?? Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var view = AgentWorld.ReadJson(response);
        Assert.Equal($"{Root}/restores/{view.GetProperty("id").GetGuid()}", response.Headers.Location!.OriginalString);
        return view;
    }

    public async Task<HttpResponseMessage> StartPreparationRawAsync(Guid artifactId, string purpose, object body, string? key = "prep-1",
        BrowserSession? session = null, string? root = null)
    {
        var browser = session ?? Admin;
        using var csrf = await browser.Client.GetAsync("/api/auth/antiforgery");
        using var request = new HttpRequestMessage(HttpMethod.Post, (root ?? Root) + $"/backup-artifacts/{artifactId}/restore-{purpose}");
        request.Headers.Add("X-CSRF-TOKEN", AgentWorld.ReadJson(csrf).GetProperty("requestToken").GetString());
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        request.Content = JsonContent.Create(body);
        return await browser.Client.SendAsync(request);
    }

    public async Task<JsonElement> StartPreparationAsync(Guid artifactId, string purpose = "precheck", object? body = null, string? key = null)
    {
        using var response = await StartPreparationRawAsync(artifactId, purpose, body ?? Body(), key ?? Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var view = AgentWorld.ReadJson(response);
        Assert.Equal($"{Root}/restores/{view.GetProperty("id").GetGuid()}", response.Headers.Location!.OriginalString);
        return view;
    }

    public async Task<JsonElement> RestoreAsync(Guid restoreId, BrowserSession? session = null)
    {
        using var response = await (session ?? Admin).GetAsync(Root + $"/restores/{restoreId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return AgentWorld.ReadJson(response);
    }

    public Task<HttpResponseMessage> GetRawAsync(string path, BrowserSession? session = null) => (session ?? Admin).GetAsync(Root + path);

    public Task<HttpResponseMessage> CancelRawAsync(Guid restoreId, BrowserSession? session = null, string? root = null) =>
        (session ?? Admin).SendAsync(HttpMethod.Post, (root ?? Root) + $"/restores/{restoreId}/cancel");

    public async Task<JsonElement> CancelAsync(Guid restoreId)
    {
        using var response = await CancelRawAsync(restoreId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return AgentWorld.ReadJson(response);
    }

    public static string StatusOf(JsonElement view) => view.GetProperty("status").GetString()!;

    // ---- the agent side ----

    /// <summary>One poll; the restore task if the poll carried one. Fails if it carried anything else besides a deletion.</summary>
    public async Task<RestoreTask?> PollRestoreAsync(EnrolledAgent? agent = null, string taskType = "database-restore")
    {
        using var response = await World.PollAsync(agent ?? Agent);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tasks = AgentWorld.ReadJson(response).GetProperty("tasks").EnumerateArray().ToList();
        var restore = tasks.Where(task => task.GetProperty("type").GetString() == taskType).ToList();
        if (restore.Count == 0) return null;
        var envelope = Assert.Single(restore);
        var snapshot = envelope.GetProperty("restore");
        Assert.Equal(snapshot.GetProperty("attemptId").GetGuid().ToString(), envelope.GetProperty("id").GetString());
        return new RestoreTask(snapshot.GetProperty("operationId").GetGuid(), snapshot.GetProperty("attemptId").GetGuid(), snapshot.GetProperty("leaseToken").GetGuid(), snapshot);
    }

    public async Task<RestoreTask> MustPollRestoreAsync(EnrolledAgent? agent = null, string taskType = "database-restore") =>
        await PollRestoreAsync(agent, taskType) ?? throw new Xunit.Sdk.XunitException($"The agent was expected to receive {taskType} but the poll carried none.");

    public Task<HttpResponseMessage> AgentCallAsync(Guid attemptId, string action, object body, EnrolledAgent? agent = null)
    {
        var caller = agent ?? Agent;
        return World.SendAgentAsync(HttpMethod.Post, $"/api/agent/restores/{attemptId}/{action}", caller.CredentialId, caller.Secret, body: body);
    }

    public Task<HttpResponseMessage> StartAttemptRawAsync(RestoreTask task, Guid? token = null) => AgentCallAsync(task.AttemptId, "start",
        new BackupTaskStartRequest { LeaseToken = token ?? task.LeaseToken, SoftwareVersion = "1.2.3", ProtocolVersion = "1.0", Platform = "linux" });

    public Task<HttpResponseMessage> RenewRawAsync(RestoreTask task, Guid? token = null) =>
        AgentCallAsync(task.AttemptId, "lease", new BackupTaskLeaseRequest { LeaseToken = token ?? task.LeaseToken });

    public Task<HttpResponseMessage> ProgressRawAsync(RestoreTask task, string stage = "Restoring", double? percent = 10, Guid? token = null) =>
        AgentCallAsync(task.AttemptId, "progress", new BackupTaskProgressRequest { LeaseToken = token ?? task.LeaseToken, Stage = stage, Percent = percent });

    public Task<HttpResponseMessage> CompleteRawAsync(RestoreTask task, object body, EnrolledAgent? agent = null) => AgentCallAsync(task.AttemptId, "complete", body, agent);

    public static object Completion(RestoreTask task, string status = "Succeeded", string targetState = "Online", string? failureCode = null, object? result = null, Guid? token = null) => new
    {
        leaseToken = token ?? task.LeaseToken,
        status,
        failureCode,
        targetState,
        result
    };

    public static object SampleResult() => new
    {
        preCheck = new[] { new { checkId = "target-reachable", status = "Passed", reasonCode = "ok", message = "The target server answered." } },
        warnings = new[] { new { code = "version-newer-build", message = "The backup comes from a newer build." } },
        sourceBackup = new { databaseName = "master", backupType = "Database", position = 1, backupSizeBytes = 1024, hasChecksums = true },
        targetServer = new { serverName = "sql-target", version = "17.0.1000.7" },
        fileMappings = new[] { new { logicalName = "master", fileType = "D", targetPhysicalName = "/var/opt/mssql/data/Restored.mdf", sizeBytes = 1024 } },
        checksumVerified = true,
        backupVerified = true,
        databaseState = "ONLINE"
    };

    /// <summary>Claims, starts and completes the next restore the way a healthy agent does; returns the task.</summary>
    public async Task<RestoreTask> RunToCompletionAsync(string status = "Succeeded", string targetState = "Online", string? failureCode = null, object? result = null, EnrolledAgent? agent = null)
    {
        var task = await MustPollRestoreAsync(agent);
        using (var started = await StartAttemptRawAsync(task)) Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        using (var completed = await CompleteRawAsync(task, Completion(task, status, targetState, failureCode, result), agent)) Assert.Equal(HttpStatusCode.NoContent, completed.StatusCode);
        return task;
    }

    // ---- sweeps and raw rows ----

    public Task<RestoreSweepResult> SweepAsync() => World.Factory.Services.GetRequiredService<StaleRestoreDetector>().SweepAsync(CancellationToken.None);

    public async Task<int> ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(World.Database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return await command.ExecuteNonQueryAsync();
    }

    /// <summary>A session of <paramref name="role"/> whose cookie is valid at the scenario clock's current time (a test that moved the clock by hours outlives older sessions).</summary>
    public Task<BrowserSession> SessionAsync(TenantRole role) => Backup.SessionAsync(role);

    public static JsonElement Property(JsonElement element, params string[] path)
    {
        foreach (var name in path) element = element.GetProperty(name);
        return element;
    }
}
