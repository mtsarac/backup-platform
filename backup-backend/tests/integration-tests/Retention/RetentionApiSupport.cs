using System.Net;
using System.Text.Json;
using BackupPlatform.Api.Backups;
using BackupPlatform.Application.Backups;
using BackupPlatform.Contracts.Backups;
using Microsoft.Extensions.DependencyInjection;

namespace BackupPlatform.IntegrationTests;

/// <summary>A backup the platform accepted through the agent API: the ids to find it again and the key its replica claims.</summary>
internal sealed record AcceptedBackup(Guid JobId, Guid RunId, Guid AttemptId, Guid ArtifactId, string Key, DateTimeOffset AcceptedAt);

/// <summary>
/// Helpers for retention tests that go through the real HTTP API only: jobs with retention policies, backups completed the way an agent completes
/// them, artifact reads and the retention worker driven by hand at the scenario clock's time.
/// </summary>
internal static class RetentionApiSupport
{
    public const string ManagedFileName = "Master_20261005T053015Z_a1b2c3d4_FULL.bak";

    /// <summary>Retention enabled with operational minimums low enough for second-scale policies; sweeps and timers stay out of the way.</summary>
    public static Dictionary<string, string?> WorkerConfiguration(string? localStorageRoot = null)
    {
        var configuration = new Dictionary<string, string?>
        {
            ["Backups:Retention:Enabled"] = "true",
            ["Backups:Retention:MinimumKeepFor"] = "00:00:01",
            ["Backups:Retention:MinimumDeleteGracePeriod"] = "00:00:01",
            ["Backups:Retention:StorageSweepEnabled"] = "false",
            ["Backups:Retention:ScanInterval"] = "01:00:00",
            ["Backups:Retention:ScheduleOccurrencePruneInterval"] = "01:00:00"
        };
        if (localStorageRoot is not null) configuration["Backups:Storage:Local:RootDirectory"] = localStorageRoot;
        return configuration;
    }

    public static object Policy(bool? enabled = null, double? keepForSeconds = null, double? deleteGracePeriodSeconds = null) =>
        new { enabled, keepForSeconds, deleteGracePeriodSeconds };

    public static object JobBody(BackupApiScenario scenario, object? retention) => new
    {
        name = "Nightly full backup",
        agentId = scenario.Agent.AgentId,
        configuration = new BackupExecutionConfiguration
        {
            Database = "master", Host = "sql-server", IntegratedSecurity = true, Destinations = BackupTestDestinations.LocalBackend
        },
        retention
    };

    public static async Task<Guid> CreateJobAsync(BackupApiScenario scenario, object? retention = null)
    {
        using var response = await scenario.CreateJobRawAsync(JobBody(scenario, retention));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return AgentWorld.ReadJson(response).GetProperty("id").GetGuid();
    }

    public static Task<HttpResponseMessage> PutRetentionAsync(BackupApiScenario scenario, Guid jobId, object body, BrowserSession? session = null) =>
        (session ?? scenario.Admin).SendAsync(HttpMethod.Put, scenario.Root + $"/backup-jobs/{jobId}/retention", body);

    public static async Task<JsonElement> JobAsync(BackupApiScenario scenario, Guid jobId)
    {
        using var response = await scenario.Admin.GetAsync(scenario.Root + $"/backup-jobs/{jobId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return AgentWorld.ReadJson(response);
    }

    /// <summary>
    /// Starts a run of <paramref name="jobId"/> and completes it as an agent would, reporting one replica of local/default under the key
    /// <paramref name="keyOf"/> returns. <paramref name="beforeComplete"/> runs just before the completion reaches the backend (stage the file there).
    /// The completion is accepted at whatever the scenario clock then says, which is what retention must be computed from.
    /// </summary>
    public static async Task<AcceptedBackup> AcceptBackupAsync(
        BackupApiScenario scenario, Guid jobId, Func<TaskClaim, string>? keyOf = null, Action<string>? beforeComplete = null, TimeSpan? agentWorkTakes = null)
    {
        var runId = await scenario.StartRunAsync(jobId);
        var claim = await scenario.ClaimAsync();
        Assert.Equal(runId, claim.RunId);
        await scenario.StartAttemptAsync(claim);
        var key = keyOf?.Invoke(claim) ?? $"{claim.AttemptId:N}/backup.bak";
        var request = scenario.SuccessRequest(claim);
        var reported = request.Artifact!;
        request = request with { Artifact = reported with { Replicas = [reported.Replicas[0] with { Key = key }] } };
        beforeComplete?.Invoke(key);
        // The agent built its report before the work finished; the backend accepts it later, and only its own clock counts.
        if (agentWorkTakes is { } takes) scenario.Clock.Advance(takes);
        var acceptedAt = scenario.Clock.GetUtcNow();
        using var response = await scenario.CompleteRawAsync(claim, request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var artifact = ArtifactOf(await scenario.RunAsync(runId));
        return new AcceptedBackup(jobId, runId, claim.AttemptId, artifact.GetProperty("id").GetGuid(), key, acceptedAt);
    }

    /// <summary>The managed-layout key an agent writes for this attempt of <paramref name="scenario"/>'s tenant.</summary>
    public static string ManagedKey(BackupApiScenario scenario, Guid jobId, TaskClaim claim) =>
        $"{ManagedBackupLayout.AttemptScope(scenario.Setup.Tenant.Id.Value, jobId, claim.RunId, claim.AttemptId)}/{ManagedFileName}";

    public static JsonElement ArtifactOf(JsonElement run) =>
        run.GetProperty("attempts").EnumerateArray().SelectMany(attempt => attempt.GetProperty("artifacts").EnumerateArray()).Single();

    public static JsonElement ReplicaOf(JsonElement artifact) => Assert.Single(artifact.GetProperty("replicas").EnumerateArray());

    /// <summary>
    /// Reads an artifact. The scenario clock also drives the browser session's lifetime, so a test that moves it by days outlives the admin
    /// session it started with; the read then signs in again instead of failing on an expired cookie.
    /// </summary>
    public static async Task<JsonElement> ArtifactAsync(BackupApiScenario scenario, Guid artifactId, BrowserSession? session = null)
    {
        var path = scenario.Root + $"/backup-artifacts/{artifactId}";
        var response = await (session ?? scenario.Admin).GetAsync(path);
        if (response.StatusCode == HttpStatusCode.Unauthorized && session is null)
        {
            response.Dispose();
            response = await (await scenario.SessionAsync(BackupPlatform.Domain.Tenancy.TenantRole.Admin)).GetAsync(path);
        }
        using (response)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return AgentWorld.ReadJson(response);
        }
    }

    public static Task<HttpResponseMessage> DeleteArtifactAsync(BackupApiScenario scenario, Guid artifactId, BrowserSession? session = null, string? root = null) =>
        (session ?? scenario.Admin).SendAsync(HttpMethod.Delete, (root ?? scenario.Root) + $"/backup-artifacts/{artifactId}");

    public static Task<HttpResponseMessage> CancelDeleteAsync(BackupApiScenario scenario, Guid artifactId, BrowserSession? session = null, string? root = null) =>
        (session ?? scenario.Admin).SendAsync(HttpMethod.Post, (root ?? scenario.Root) + $"/backup-artifacts/{artifactId}/cancel-delete");

    public static bool IsNull(JsonElement element, string property) =>
        !element.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null;

    // ---- the worker, driven without timers ----

    public static BackupRetentionWorker WorkerOf(BackupApiScenario scenario) => scenario.World.Factory.Services.GetRequiredService<BackupRetentionWorker>();

    public static Task<RetentionCycleResult> RunRetentionAsync(BackupApiScenario scenario) => WorkerOf(scenario).RunRetentionOnceAsync(CancellationToken.None);
}
