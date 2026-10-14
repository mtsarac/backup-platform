using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
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
    [Theory]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz", false)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", true)]
    public async Task MalformedUploadsAreRejectedBeforeResolvingOrWritingStorage(string checksum, bool chunked)
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
                Destinations = [BackupTestDestinations.Relayed("google-drive", connectionId, primary: true)]
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
        request.Headers.Add(TaskStorageProtocol.DestinationHeader, connectionId.ToString("D"));
        request.Headers.Add(TaskStorageProtocol.ChecksumHeader, checksum);
        // A declared length is the only bound on an upload once the request size limit is removed: a body without one must never reach the provider.
        // (With a finite reservation the fence refuses it earlier as quota_exceeded; this run has none.)
        request.Content = chunked ? new UnknownLengthContent([1, 2, 3]) : new ByteArrayContent([1, 2, 3]);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await scenario.World.Anonymous.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", AgentWorld.ReadJson(response).GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("verify")]
    [InlineData("content")]
    public async Task LeaseLostWhileWaitingForStorageLeaseRejectsVerificationAndDeletion(string operation)
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
                Database = "master", Host = "sql-server", IntegratedSecurity = true,
                Destinations = [BackupTestDestinations.Relayed("google-drive", connectionId, primary: true)]
            }
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var jobId = AgentWorld.ReadJson(created).GetProperty("id").GetGuid();
        var runId = await scenario.StartRunAsync(jobId);
        var claim = await scenario.ClaimAsync();
        var key = ManagedBackupLayout.AttemptScope(scenario.Setup.Tenant.Id.Value, jobId, runId, claim.AttemptId)
            + "/" + ManagedBackupLayout.StagedFileName("master", DateTimeOffset.UtcNow, Guid.NewGuid());
        // Another writer holds a live transfer lease on the key, so the request must wait for it.
        var lockId = BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes($"{connectionId:N}:{key}")), 0);
        await using (var other = fixture.CreatePlatformContext())
            await other.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO backup_transfer_leases (\"LockId\",\"OwnerId\",\"ExpiresAt\") VALUES ({lockId}, {Guid.NewGuid()}, clock_timestamp() + interval '5 minutes')");
        using var request = new HttpRequestMessage(operation == "verify" ? HttpMethod.Post : HttpMethod.Delete,
            $"/api/agent/tasks/{claim.AttemptId}/storage/{operation}");
        request.Headers.TryAddWithoutValidation("Authorization", AgentAuthorizationScheme.FormatHeaderValue(scenario.Agent.CredentialId, scenario.Agent.Secret));
        request.Headers.Add("X-Backup-Agent-Protocol", "1.0");
        request.Headers.Add(TaskStorageProtocol.LeaseHeader, claim.LeaseToken.ToString("D"));
        request.Headers.Add(TaskStorageProtocol.KeyHeader, key);
        request.Headers.Add(TaskStorageProtocol.DestinationHeader, connectionId.ToString("D"));
        if (operation == "verify")
            request.Content = System.Net.Http.Json.JsonContent.Create(new TaskStorageVerifyRequest(3, new string('a', 64)));
        var pending = scenario.World.Anonymous.SendAsync(request);
        try
        {
            await Task.Delay(700);
            Assert.False(pending.IsCompleted, "The request must be waiting for the storage lease before its attempt lease is replaced.");
            await using var db = fixture.CreatePlatformContext();
            await db.BackupRunAttempts.Where(row => row.Id == claim.AttemptId)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.LeaseToken, Guid.NewGuid()));
        }
        finally
        {
            await using var cleanup = fixture.CreatePlatformContext();
            await cleanup.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM backup_transfer_leases WHERE \"LockId\" = {lockId}");
        }

        using var response = await pending;
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("lease_conflict", AgentWorld.ReadJson(response).GetProperty("error").GetString());
    }

    [Fact]
    public async Task RelayedUploadsAreNotCappedAtKestrelsDefaultRequestBodyLimit()
    {
        // Kestrel rejects bodies above ~28.6 MB unless the endpoint opts out; the relay bounds uploads by the declared length and the attempt's reservation instead.
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var upload = scenario.World.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().Single(endpoint =>
            endpoint.RoutePattern.RawText == "/api/agent/tasks/{attemptId:guid}/storage/content"
            && endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains("PUT"));
        var limit = upload.Metadata.GetMetadata<IRequestSizeLimitMetadata>();
        Assert.NotNull(limit);
        Assert.Null(limit.MaxRequestBodySize);
    }
    // A seekable StreamContent would let HttpClient send Content-Length; this one always goes out chunked.
    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
}
