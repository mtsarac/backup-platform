using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Domain.Backups;
using BackupPlatform.StorageProviders.Abstractions.Transfer;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace BackupPlatform.IntegrationTests.Backup;

/// <summary>
/// Real HTTPS API, real published Agent.Host (policy call, governor, paced file stream, local storage) and real SQL Server: a job limit set through the API
/// paces the agent's upload, and the stored artifact is still byte-identical to what SQL Server wrote.
/// </summary>
public sealed class MsSqlPacedUploadAgentSmokeTests(PostgreSqlFixture database, ITestOutputHelper output) : IClassFixture<PostgreSqlFixture>
{
    private const long Limit = 250_000;

    [Fact]
    public async Task A_job_limit_set_through_the_api_paces_the_real_agents_upload_and_every_checksum_still_matches()
    {
        await using var sql = new OrchestrationSqlSource(rows: 150);
        await sql.InitializeAsync();
        await using var world = await AgentWorld.CreateAsync(database, clock: new MutableClock(live: true), kestrel: true);
        var tenant = await world.CreateTenantAsync();
        await world.EntitleAsync(tenant.Tenant);
        // The fixture declares what the host's capability sync will also declare, so admission does not depend on the sync having run yet.
        var agent = await world.DeclareBackupCapabilityAsync(await world.EnrollAsync(tenant));
        var root = $"/api/tenants/{tenant.Tenant.Id.Value}";
        await using var host = await AgentHostSmokeProcess.StartAsync(world, agent, output.WriteLine);

        var unlimited = await RunAsync(tenant, agent, host, sql, root, limit: null);
        var limited = await RunAsync(tenant, agent, host, sql, root, limit: Limit);

        var capacity = TransferRateLimiter.Capacity(Limit);
        var floor = TimeSpan.FromSeconds((limited.Size - capacity) / Limit);
        output.WriteLine("artifact {0} bytes; unlimited upload {1:F2} s; limited to {2} B/s ({3:F1} Mbit/s): {4:F2} s (the ceiling allows no less than {5:F2} s)",
            limited.Size, unlimited.Upload.TotalSeconds, Limit, Limit * 8 / 1e6, limited.Upload.TotalSeconds, floor.TotalSeconds);
        Assert.True(limited.Upload >= floor, $"{limited.Size} bytes were uploaded in {limited.Upload}, faster than {Limit} B/s allows ({floor})");
        Assert.True(unlimited.Upload < floor / 2, $"the unlimited upload ({unlimited.Upload}) should be far quicker than the limited one");
        Assert.True(limited.Upload < floor * 3, $"{limited.Upload} is far slower than the ceiling");
    }

    private async Task<(long Size, TimeSpan Upload)> RunAsync(TenantSetup tenant, EnrolledAgent agent, AgentHostSmokeProcess host, OrchestrationSqlSource sql, string root, long? limit)
    {
        using var created = await tenant.Admin.SendAsync(HttpMethod.Post, root + "/backup-jobs",
            new { name = "Paced real backup", agentId = agent.AgentId, configuration = MsSqlManualAgentSmokeTests.Configuration(sql, sql.DatabaseName) });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var jobId = AgentWorld.ReadJson(created).GetProperty("id").GetGuid();
        if (limit is not null)
        {
            using var set = await tenant.Admin.SendAsync(HttpMethod.Put, root + $"/upload-bandwidth/jobs/{jobId}", new { bytesPerSecond = limit });
            Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        }
        using var csrf = await tenant.Admin.GetAsync("/api/auth/antiforgery");
        using var start = new HttpRequestMessage(HttpMethod.Post, root + $"/backup-jobs/{jobId}/runs");
        start.Headers.Add("X-CSRF-TOKEN", AgentWorld.ReadJson(csrf).GetProperty("requestToken").GetString());
        start.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        using var started = await tenant.Admin.Client.SendAsync(start);
        Assert.True(started.StatusCode == HttpStatusCode.OK, $"{(int)started.StatusCode}: {await started.Content.ReadAsStringAsync()}");
        var runId = AgentWorld.ReadJson(started).GetProperty("id").GetGuid();

        JsonElement detail;
        using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3)))
            while (true)
            {
                host.AssertAlive();
                using var response = await tenant.Admin.GetAsync(root + $"/backup-runs/{runId}");
                detail = AgentWorld.ReadJson(response);
                if (detail.GetProperty("status").GetString() is "Succeeded" or "Failed" or "Cancelled" or "AgentLost") break;
                await Task.Delay(100, timeout.Token);
            }
        Assert.Equal("Succeeded", detail.GetProperty("status").GetString());
        var artifact = Assert.Single(Assert.Single(detail.GetProperty("attempts").EnumerateArray()).GetProperty("artifacts").EnumerateArray());
        var replica = Assert.Single(artifact.GetProperty("replicas").EnumerateArray());
        Assert.Equal("Verified", replica.GetProperty("verificationState").GetString());
        var physical = Path.Combine(host.StorageDirectory, replica.GetProperty("key").GetString()!.Replace('/', Path.DirectorySeparatorChar));
        await using (var stored = File.OpenRead(physical))
        {
            var checksum = Convert.ToHexStringLower(await SHA256.HashDataAsync(stored));
            Assert.Equal(checksum, artifact.GetProperty("artifactChecksum").GetString());
            Assert.Equal(checksum, replica.GetProperty("checksum").GetString());
            Assert.Equal(stored.Length, artifact.GetProperty("sizeBytes").GetInt64());
        }
        await using var context = database.CreatePlatformContext();
        var outcome = await context.BackupDestinationOutcomes.AsNoTracking().SingleAsync(row => row.RunId == runId);
        Assert.Equal(BackupDestinationStates.Verified, outcome.State);
        return (artifact.GetProperty("sizeBytes").GetInt64(), outcome.CompletedAt - outcome.StartedAt);
    }
}
