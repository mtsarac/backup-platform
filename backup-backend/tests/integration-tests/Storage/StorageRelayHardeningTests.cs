using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using BackupPlatform.Agents;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Domain.Storage;
using Xunit;

namespace BackupPlatform.IntegrationTests.Storage;

public sealed class StorageRelayHardeningTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task NonHexChecksumIsRejectedBeforeResolvingOrWritingStorage()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var connectionId = Guid.NewGuid();
        await using (var db = fixture.CreatePlatformContext())
        {
            var connection = new StorageConnection(connectionId, scenario.Setup.Tenant.Id, "Relay validation", "google-drive",
                StorageOwnership.TenantManaged, "tenant", new Dictionary<string, string> { ["folderId"] = "test-folder" }, false, DateTimeOffset.UtcNow);
            connection.AttachSecret($"sc-{connectionId:N}-validation", ["refreshToken"], DateTimeOffset.UtcNow);
            db.StorageConnections.Add(connection);
            await db.SaveChangesAsync();
        }
        using var created = await scenario.CreateJobRawAsync(new
        {
            name = "Relay boundary", agentId = scenario.Agent.AgentId,
            configuration = new BackupExecutionConfiguration
            {
                Database = "master", Host = "sql-server", IntegratedSecurity = true,
                StorageProvider = "google-drive",
                StorageConnectionId = connectionId, StorageExecutionLocation = "Backend"
            }
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var jobId = AgentWorld.ReadJson(created).GetProperty("id").GetGuid();
        var runId = await scenario.StartRunAsync(jobId);
        var claim = await scenario.ClaimAsync();
        var key = ManagedBackupLayout.AttemptScope(scenario.Setup.Tenant.Id.Value, jobId, runId, claim.AttemptId)
            + "/" + ManagedBackupLayout.StagedFileName("master", DateTimeOffset.UtcNow, Guid.NewGuid());
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/agent/tasks/{claim.AttemptId}/storage/content");
        request.Headers.TryAddWithoutValidation("Authorization", AgentAuthorizationScheme.FormatHeaderValue(scenario.Agent.CredentialId, scenario.Agent.Secret));
        request.Headers.Add("X-Backup-Agent-Protocol", "1.0");
        request.Headers.Add(TaskStorageProtocol.LeaseHeader, claim.LeaseToken.ToString("D"));
        request.Headers.Add(TaskStorageProtocol.KeyHeader, key);
        request.Headers.Add(TaskStorageProtocol.ChecksumHeader, new string('z', 64));
        request.Content = new ByteArrayContent([1, 2, 3]);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await scenario.World.Anonymous.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", AgentWorld.ReadJson(response).GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("verify")]
    [InlineData("content")]
    public async Task LeaseLostWhileWaitingForStorageLockRejectsVerificationAndDeletion(string operation)
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var connectionId = Guid.NewGuid();
        await using (var db = fixture.CreatePlatformContext())
        {
            var connection = new StorageConnection(connectionId, scenario.Setup.Tenant.Id, "Lease fence", "google-drive",
                StorageOwnership.TenantManaged, "tenant", new Dictionary<string, string>(), false, DateTimeOffset.UtcNow);
            connection.AttachSecret($"sc-{connectionId:N}-fence", ["refreshToken"], DateTimeOffset.UtcNow);
            db.StorageConnections.Add(connection);
            await db.SaveChangesAsync();
        }
        using var created = await scenario.CreateJobRawAsync(new
        {
            name = "Lease fence", agentId = scenario.Agent.AgentId,
            configuration = new BackupExecutionConfiguration
            {
                Database = "master", Host = "sql-server", IntegratedSecurity = true, StorageProvider = "google-drive",
                StorageConnectionId = connectionId, StorageExecutionLocation = "Backend"
            }
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var jobId = AgentWorld.ReadJson(created).GetProperty("id").GetGuid();
        var runId = await scenario.StartRunAsync(jobId);
        var claim = await scenario.ClaimAsync();
        var key = ManagedBackupLayout.AttemptScope(scenario.Setup.Tenant.Id.Value, jobId, runId, claim.AttemptId)
            + "/" + ManagedBackupLayout.StagedFileName("master", DateTimeOffset.UtcNow, Guid.NewGuid());
        var lockId = BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes($"{connectionId:N}:{key}")), 0);
        await using var held = new NpgsqlConnection(fixture.ConnectionString);
        await held.OpenAsync();
        await using (var acquire = new NpgsqlCommand("SELECT pg_advisory_lock(@id)", held))
        {
            acquire.Parameters.AddWithValue("id", lockId);
            await acquire.ExecuteNonQueryAsync();
        }
        using var request = new HttpRequestMessage(operation == "verify" ? HttpMethod.Post : HttpMethod.Delete,
            $"/api/agent/tasks/{claim.AttemptId}/storage/{operation}");
        request.Headers.TryAddWithoutValidation("Authorization", AgentAuthorizationScheme.FormatHeaderValue(scenario.Agent.CredentialId, scenario.Agent.Secret));
        request.Headers.Add("X-Backup-Agent-Protocol", "1.0");
        request.Headers.Add(TaskStorageProtocol.LeaseHeader, claim.LeaseToken.ToString("D"));
        request.Headers.Add(TaskStorageProtocol.KeyHeader, key);
        if (operation == "verify")
            request.Content = System.Net.Http.Json.JsonContent.Create(new TaskStorageVerifyRequest(3, new string('a', 64)));
        var pending = scenario.World.Anonymous.SendAsync(request);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var waiting = new NpgsqlCommand(
                "SELECT EXISTS(SELECT 1 FROM pg_locks WHERE locktype='advisory' AND NOT granted AND classid::bigint=@high AND objid::bigint=@low)", held);
            waiting.Parameters.AddWithValue("high", (long)(uint)((ulong)lockId >> 32));
            waiting.Parameters.AddWithValue("low", (long)(uint)lockId);
            while (!Equals(true, await waiting.ExecuteScalarAsync(deadline.Token)))
            {
                Assert.False(pending.IsCompleted, "The request must reach the storage lock before its lease is replaced.");
            }
            await using var db = fixture.CreatePlatformContext();
            await db.BackupRunAttempts.Where(row => row.Id == claim.AttemptId)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.LeaseToken, Guid.NewGuid()));
        }
        finally
        {
            await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock(@id)", held);
            release.Parameters.AddWithValue("id", lockId);
            await release.ExecuteNonQueryAsync();
        }
        using var response = await pending;
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("lease_conflict", AgentWorld.ReadJson(response).GetProperty("error").GetString());
    }
}
