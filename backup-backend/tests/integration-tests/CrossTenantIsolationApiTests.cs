using System.Net;
using System.Text.Json;
using BackupPlatform.Domain.Backups;
using Xunit;

namespace BackupPlatform.IntegrationTests;

/// <summary>
/// Tenant A owns a job, a run, an artifact, a share, a restore and an agent; a member of tenant B (an administrator of tenant B, with a valid session)
/// tries to read and change every one of them, through tenant A's routes (not a member: 403) and through tenant B's own routes with A's ids (not found: 404).
/// Afterwards tenant A's data is exactly as it was.
/// </summary>
public sealed class CrossTenantIsolationApiTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private sealed record Owned(Guid Job, Guid Run, Guid Artifact, Guid Share, Guid Restore, Guid Agent);

    private static async Task<Owned> SeedAsync(RestoreApiScenario a)
    {
        var accepted = await a.AcceptBackupAsync();
        var restore = await a.StartAsync(accepted.ArtifactId);
        using var share = await a.Admin.SendAsync(HttpMethod.Post, $"{a.Root}/backup-artifacts/{accepted.ArtifactId}/shares", new { durationHours = 24 });
        Assert.Equal(HttpStatusCode.Created, share.StatusCode);
        return new Owned(accepted.JobId, accepted.RunId, accepted.ArtifactId, AgentWorld.ReadJson(share).GetProperty("id").GetGuid(),
            restore.GetProperty("id").GetGuid(), a.Agent.AgentId);
    }

    private static IEnumerable<string> Items(JsonElement body) =>
        (body.ValueKind == JsonValueKind.Array ? body : body.GetProperty("items")).EnumerateArray().Select(item => item.GetProperty("id").GetGuid().ToString());

    [Fact]
    public async Task AMemberOfAnotherTenantCannotReadAnyTenantResource()
    {
        await using var a = await RestoreApiScenario.CreateAsync(fixture);
        var owned = await SeedAsync(a);
        var b = await a.World.CreateTenantAsync();
        var bRoot = BackupApiScenario.RootOf(b);

        string[] reads =
        [
            $"/backup-jobs/{owned.Job}", $"/backup-jobs/{owned.Job}/runs", $"/backup-jobs/{owned.Job}/schedule/occurrences", $"/backup-runs/{owned.Run}",
            $"/backup-artifacts/{owned.Artifact}", $"/backup-shares/{owned.Share}", $"/restores/{owned.Restore}",
            $"/agents/{owned.Agent}"
        ];
        foreach (var path in reads)
        {
            using var own = await b.Admin.GetAsync(bRoot + path);
            Assert.True(own.StatusCode == HttpStatusCode.NotFound, $"GET {bRoot + path} answered {(int)own.StatusCode}, expected 404.");
            using var foreign = await b.Admin.GetAsync(a.Root + path);
            Assert.True(foreign.StatusCode == HttpStatusCode.Forbidden, $"GET {a.Root + path} answered {(int)foreign.StatusCode}, expected 403.");
        }

        // Listing the shares of a foreign artifact is a filtered list, not a lookup: it answers with nothing.
        using var foreignShares = await b.Admin.GetAsync($"{bRoot}/backup-artifacts/{owned.Artifact}/shares");
        Assert.Empty(Items(AgentWorld.ReadJson(foreignShares)));

        // Lists of the intruder's own tenant never contain the owner's rows.
        foreach (var list in new[] { "/backup-jobs", "/backup-runs", "/backup-artifacts", "/backup-shares", "/restores", "/agents" })
        {
            using var response = await b.Admin.GetAsync(bRoot + list);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var ids = Items(AgentWorld.ReadJson(response)).ToHashSet();
            Assert.DoesNotContain(owned.Job.ToString(), ids);
            Assert.DoesNotContain(owned.Run.ToString(), ids);
            Assert.DoesNotContain(owned.Artifact.ToString(), ids);
            Assert.DoesNotContain(owned.Share.ToString(), ids);
            Assert.DoesNotContain(owned.Restore.ToString(), ids);
            Assert.DoesNotContain(owned.Agent.ToString(), ids);
        }
    }

    [Fact]
    public async Task AMemberOfAnotherTenantCannotChangeAnyTenantResource()
    {
        await using var a = await RestoreApiScenario.CreateAsync(fixture);
        var owned = await SeedAsync(a);
        var b = await a.World.CreateTenantAsync();
        var bRoot = BackupApiScenario.RootOf(b);

        (HttpMethod Method, string Path, object? Body)[] writes =
        [
            (HttpMethod.Put, $"/backup-jobs/{owned.Job}", BackupApiScenario.JobBody(owned.Agent)),
            (HttpMethod.Delete, $"/backup-jobs/{owned.Job}", null),
            (HttpMethod.Post, $"/backup-jobs/{owned.Job}/disable", null),
            (HttpMethod.Post, $"/backup-jobs/{owned.Job}/runs", null),
            (HttpMethod.Post, $"/backup-runs/{owned.Run}/cancel", null),
            (HttpMethod.Post, $"/backup-runs/{owned.Run}/retry", null),
            (HttpMethod.Post, $"/backup-runs/{owned.Run}/pause", null),
            (HttpMethod.Delete, $"/backup-artifacts/{owned.Artifact}", null),
            (HttpMethod.Post, $"/backup-artifacts/{owned.Artifact}/shares", new { durationHours = 24 }),
            (HttpMethod.Post, $"/backup-shares/{owned.Share}/extend", new { additionalHours = 1 }),
            (HttpMethod.Post, $"/backup-shares/{owned.Share}/revoke", new { reason = "intruder" }),
            (HttpMethod.Post, $"/restores/{owned.Restore}/cancel", null),
            (HttpMethod.Post, $"/agents/{owned.Agent}/revoke", null)
        ];
        foreach (var (method, path, body) in writes)
        {
            var headers = new[] { KeyValuePair.Create("Idempotency-Key", Guid.NewGuid().ToString("N")) };
            using var own = await b.Admin.SendAsync(method, bRoot + path, body, headers: headers);
            Assert.True(own.StatusCode == HttpStatusCode.NotFound, $"{method} {bRoot + path} answered {(int)own.StatusCode}, expected 404.");
            using var foreign = await b.Admin.SendAsync(method, a.Root + path, body, headers: headers);
            Assert.True(foreign.StatusCode == HttpStatusCode.Forbidden, $"{method} {a.Root + path} answered {(int)foreign.StatusCode}, expected 403.");
        }

        // The owner's data is untouched.
        using var job = await a.Admin.GetAsync($"{a.Root}/backup-jobs/{owned.Job}");
        var jobView = AgentWorld.ReadJson(job);
        Assert.True(jobView.GetProperty("enabled").GetBoolean());
        Assert.False(jobView.TryGetProperty("archivedAt", out var archived) && archived.ValueKind != JsonValueKind.Null);
        using var run = await a.Admin.GetAsync($"{a.Root}/backup-runs/{owned.Run}");
        Assert.Equal("Succeeded", AgentWorld.ReadJson(run).GetProperty("status").GetString());
        using var share = await a.Admin.GetAsync($"{a.Root}/backup-shares/{owned.Share}");
        Assert.NotEqual("Revoked", AgentWorld.ReadJson(share).GetProperty("status").GetString());
        using var restore = await a.Admin.GetAsync($"{a.Root}/restores/{owned.Restore}");
        Assert.NotEqual("Cancelled", AgentWorld.ReadJson(restore).GetProperty("status").GetString());
        using var agent = await a.Admin.GetAsync($"{a.Root}/agents/{owned.Agent}");
        Assert.Equal(HttpStatusCode.OK, agent.StatusCode);
        Assert.NotEqual("Revoked", AgentWorld.ReadJson(agent).GetProperty("status").GetString());
        await using var db = fixture.CreatePlatformContext();
        Assert.False((await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(db.BackupJobs, row => row.Id == owned.Job)).IsArchived);
    }

    [Fact]
    public async Task AnAgentOfAnotherTenantCannotCallTheTasksOfAnotherTenant()
    {
        await using var a = await RestoreApiScenario.CreateAsync(fixture);
        var jobId = await a.Backup.CreateJobAsync();
        await a.Backup.StartRunAsync(jobId);
        var claim = await a.Backup.ClaimAsync(a.Agent);
        var b = await a.World.CreateTenantAsync();
        await a.World.EntitleAsync(b.Tenant);
        var intruder = await a.World.DeclareBackupCapabilityAsync(await a.World.EnrollAsync(b));

        foreach (var action in new[] { "start", "lease", "progress" })
        {
            using var response = await a.Backup.AgentCallAsync(claim, action, new { leaseToken = claim.LeaseToken, stage = "BackingUp", percent = 1 }, intruder);
            Assert.True(!response.IsSuccessStatusCode, $"{action} by another tenant's agent answered {(int)response.StatusCode}.");
        }

        // The legitimate agent still owns the attempt: nothing the intruder did moved it.
        await a.Backup.StartAttemptAsync(claim);
    }
}
