using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using BackupPlatform.Agents;
using BackupPlatform.Agents.Capabilities;
using BackupPlatform.Domain.Agents;
using BackupPlatform.Domain.Identity;
using BackupPlatform.SecretProviders.Abstractions;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Domain.Storage;
using Xunit;
using BackupPlatform.Domain.Backups;

namespace BackupPlatform.IntegrationTests.Storage;

public sealed class StorageRelayHardeningTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    [Theory]
    [InlineData("PUT")]
    [InlineData("GET")]
    public async Task RetiredRelayEndpointsFailClosedWithoutReadingBodyOrResolvingStorage(string method)
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        using var request = new HttpRequestMessage(new HttpMethod(method), $"/api/agent/tasks/{Guid.NewGuid()}/storage/content");
        request.Headers.TryAddWithoutValidation("Authorization", AgentAuthorizationScheme.FormatHeaderValue(scenario.Agent.CredentialId, scenario.Agent.Secret));
        request.Headers.Add("X-Backup-Agent-Protocol", "1.0");
        if (method == "PUT")
        {
            request.Content = new ByteArrayContent([1, 2, 3]);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        }
        using var response = await scenario.World.Anonymous.SendAsync(request);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("agent_direct_required", AgentWorld.ReadJson(response).GetProperty("error").GetString());
    }

    /// <summary>The fixture agent declares direct storage; an agent that never did is the one the backend must not relay for.</summary>
    private async Task RemoveDirectCapabilityAsync(BackupApiScenario scenario)
    {
        await using var db = fixture.CreatePlatformContext();
        await db.AgentCapabilities.Where(row => row.AgentId == new AgentId(scenario.Agent.AgentId) && row.Identifier == TaskStorageProtocol.DirectCapability).ExecuteDeleteAsync();
    }

    [Fact]
    public async Task RetiredRestoreRelayEndpointsFailClosed()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/agent/restore-tasks/{Guid.NewGuid()}/storage/content");
        request.Headers.TryAddWithoutValidation("Authorization", AgentAuthorizationScheme.FormatHeaderValue(scenario.Agent.CredentialId, scenario.Agent.Secret));
        request.Headers.Add("X-Backup-Agent-Protocol", "1.0");
        using var response = await scenario.World.Anonymous.SendAsync(request);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("agent_direct_required", AgentWorld.ReadJson(response).GetProperty("error").GetString());
    }


    [Fact]
    public async Task AccessEndpoint_RejectsWhenDestinationIsRelayedExecutionLocation()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        await RemoveDirectCapabilityAsync(scenario);
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
        using var response = await scenario.StartRawAsync(jobId, "key");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var denial = AgentWorld.ReadJson(response);
        Assert.Equal("admission_denied", denial.GetProperty("error").GetString());
        Assert.Equal("agent_direct_storage_unsupported", denial.GetProperty("code").GetString());
    }
    [Fact]
    public async Task AccessEndpoint_RejectsWhenMissingHeaders()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/agent/tasks/{Guid.NewGuid()}/storage/access");
        request.Headers.TryAddWithoutValidation("Authorization", AgentAuthorizationScheme.FormatHeaderValue(scenario.Agent.CredentialId, scenario.Agent.Secret));
        request.Headers.Add("X-Backup-Agent-Protocol", "1.0");

        using var response = await scenario.World.Anonymous.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", AgentWorld.ReadJson(response).GetProperty("error").GetString());
    }

    [Fact]
    public async Task AccessEndpoint_RejectsRestoreWhenSourceIsRelayedExecutionLocation()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/agent/restore-tasks/{Guid.NewGuid()}/storage/access");
        request.Headers.TryAddWithoutValidation("Authorization", AgentAuthorizationScheme.FormatHeaderValue(scenario.Agent.CredentialId, scenario.Agent.Secret));
        request.Headers.Add("X-Backup-Agent-Protocol", "1.0");
        request.Headers.Add(TaskStorageProtocol.LeaseHeader, Guid.NewGuid().ToString("D"));
        request.Headers.Add(TaskStorageProtocol.RestoreHeader, Guid.NewGuid().ToString("D"));

        using var response = await scenario.World.Anonymous.SendAsync(request);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("lease_conflict", AgentWorld.ReadJson(response).GetProperty("error").GetString());
    }

    [Fact]
    public async Task AccessEndpoint_RejectsDirectWhenJobConfigurationRequestsDirectWithoutBackendAdmission()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        await RemoveDirectCapabilityAsync(scenario);
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
            name = "Direct untrusted", agentId = scenario.Agent.AgentId,
            configuration = new BackupExecutionConfiguration
            {
                Database = "master", Host = "sql-server", IntegratedSecurity = true,
                Destinations = [new BackupDestinationContract { StorageProvider = "google-drive", StorageConnectionId = connectionId, StorageExecutionLocation = "Backend", IsPrimary = true, Direct = true }]
            }
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var jobId = AgentWorld.ReadJson(created).GetProperty("id").GetGuid();
        using var response = await scenario.StartRawAsync(jobId, "key");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var denial = AgentWorld.ReadJson(response);
        Assert.Equal("admission_denied", denial.GetProperty("error").GetString());
        Assert.Equal("agent_direct_storage_unsupported", denial.GetProperty("code").GetString());
    }
    [Fact]
    public async Task AccessEndpoint_SucceedsForAdmittedDirectDestination_WhenAgentSupportsDirectStorage()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var connectionId = Guid.NewGuid();
        await using (var db = fixture.CreatePlatformContext())
        {
            var connection = new StorageConnection(connectionId, scenario.Setup.Tenant.Id, "Direct SFTP", "sftp",
                StorageOwnership.TenantManaged, "tenant", new Dictionary<string, string>
                {
                    ["host"] = "sftp.example.internal",
                    ["port"] = "22",
                    ["username"] = "backup-user",
                    ["authenticationType"] = "password",
                    ["remoteBaseDirectory"] = "/backups",
                    ["hostKeyFingerprint"] = "SHA256:dGVzdC1maW5nZXJwcmludC1mb3ItZGlyZWN0"
                }, false, DateTimeOffset.UtcNow);
            connection.AttachSecret($"sc-{connectionId:N}-sftp", ["password"], DateTimeOffset.UtcNow);
            db.StorageConnections.Add(connection);

            await db.SaveChangesAsync();
        }

        var secrets = scenario.World.Factory.Services.GetRequiredService<ISecretStore>();
        await secrets.SetAsync(new SecretReference($"sc-{connectionId:N}-sftp"),
            new SecretValue("{\"password\":\"sftp-direct-pass\"}"), default);

        using var created = await scenario.CreateJobRawAsync(new
        {
            name = "Direct admitted", agentId = scenario.Agent.AgentId,
            configuration = new BackupExecutionConfiguration
            {
                Database = "master", Host = "sql-server", IntegratedSecurity = true,
                Destinations = [new BackupDestinationContract { StorageProvider = "sftp", StorageConnectionId = connectionId, StorageExecutionLocation = "Backend", IsPrimary = true }]
            }
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var jobId = AgentWorld.ReadJson(created).GetProperty("id").GetGuid();
        await scenario.StartRunAsync(jobId);
        var claim = await scenario.ClaimAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/agent/tasks/{claim.AttemptId}/storage/access");
        request.Headers.TryAddWithoutValidation("Authorization", AgentAuthorizationScheme.FormatHeaderValue(scenario.Agent.CredentialId, scenario.Agent.Secret));
        request.Headers.Add("X-Backup-Agent-Protocol", "1.0");
        request.Headers.Add(TaskStorageProtocol.LeaseHeader, claim.LeaseToken.ToString("D"));
        request.Headers.Add(TaskStorageProtocol.DestinationHeader, connectionId.ToString("D"));

        using var response = await scenario.World.Anonymous.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var body = AgentWorld.ReadJson(response);
        Assert.Equal("sftp", body.GetProperty("providerType").GetString());
        Assert.Equal("sftp.example.internal", body.GetProperty("host").GetString());
        Assert.Equal(22, body.GetProperty("port").GetInt32());
        Assert.Equal("backup-user", body.GetProperty("username").GetString());
        Assert.Equal("sftp-direct-pass", body.GetProperty("password").GetString());
    }

    [Fact]
    public async Task AccessEndpoint_SucceedsForDirectSftpWithPrivateKeyAuthentication()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var connectionId = Guid.NewGuid();
        await using (var db = fixture.CreatePlatformContext())
        {
            var connection = new StorageConnection(connectionId, scenario.Setup.Tenant.Id, "Direct Key SFTP", "sftp",
                StorageOwnership.TenantManaged, "tenant", new Dictionary<string, string>
                {
                    ["host"] = "sftp.key.internal",
                    ["port"] = "2222",
                    ["username"] = "key-user",
                    ["authenticationType"] = "private-key",
                    ["remoteBaseDirectory"] = "/backups",
                    ["hostKeyFingerprint"] = "SHA256:dGVzdC1maW5nZXJwcmludC1mb3Ita2V5"
                }, false, DateTimeOffset.UtcNow);
            connection.AttachSecret($"sc-{connectionId:N}-sftp", ["privateKey"], DateTimeOffset.UtcNow);
            db.StorageConnections.Add(connection);

            await db.SaveChangesAsync();
        }

        var secrets = scenario.World.Factory.Services.GetRequiredService<ISecretStore>();
        await secrets.SetAsync(new SecretReference($"sc-{connectionId:N}-sftp"),
            new SecretValue("{\"privateKey\":\"test-private-key-data\"}"), default);

        using var created = await scenario.CreateJobRawAsync(new
        {
            name = "Direct key admitted", agentId = scenario.Agent.AgentId,
            configuration = new BackupExecutionConfiguration
            {
                Database = "master", Host = "sql-server", IntegratedSecurity = true,
                Destinations = [new BackupDestinationContract { StorageProvider = "sftp", StorageConnectionId = connectionId, StorageExecutionLocation = "Backend", IsPrimary = true }]
            }
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var jobId = AgentWorld.ReadJson(created).GetProperty("id").GetGuid();
        await scenario.StartRunAsync(jobId);
        var claim = await scenario.ClaimAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/agent/tasks/{claim.AttemptId}/storage/access");
        request.Headers.TryAddWithoutValidation("Authorization", AgentAuthorizationScheme.FormatHeaderValue(scenario.Agent.CredentialId, scenario.Agent.Secret));
        request.Headers.Add("X-Backup-Agent-Protocol", "1.0");
        request.Headers.Add(TaskStorageProtocol.LeaseHeader, claim.LeaseToken.ToString("D"));
        request.Headers.Add(TaskStorageProtocol.DestinationHeader, connectionId.ToString("D"));

        using var response = await scenario.World.Anonymous.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var body = AgentWorld.ReadJson(response);
        Assert.Equal("sftp", body.GetProperty("providerType").GetString());
        Assert.Equal("sftp.key.internal", body.GetProperty("host").GetString());
        Assert.Equal(2222, body.GetProperty("port").GetInt32());
        Assert.Equal("key-user", body.GetProperty("username").GetString());
        Assert.Equal("private-key", body.GetProperty("authenticationType").GetString());
        Assert.Equal("test-private-key-data", body.GetProperty("privateKey").GetString());
    }

    [Fact]
    public async Task CatalogEndpoint_RejectsSaveWithForeignAttemptKey()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var connectionId = Guid.NewGuid();
        await using (var db = fixture.CreatePlatformContext())
        {
            var connection = new StorageConnection(connectionId, scenario.Setup.Tenant.Id, "Direct Drive", "google-drive",
                StorageOwnership.TenantManaged, "tenant", new Dictionary<string, string>
                {
                    ["folderId"] = "fld-1"
                }, false, DateTimeOffset.UtcNow);
            connection.AttachSecret($"sc-{connectionId:N}-drive", ["refreshToken"], DateTimeOffset.UtcNow);
            db.StorageConnections.Add(connection);

            await db.SaveChangesAsync();
        }

        using var created = await scenario.CreateJobRawAsync(new
        {
            name = "Drive catalog test", agentId = scenario.Agent.AgentId,
            configuration = new BackupExecutionConfiguration
            {
                Database = "master", Host = "sql-server", IntegratedSecurity = true,
                Destinations = [new BackupDestinationContract { StorageProvider = "google-drive", StorageConnectionId = connectionId, StorageExecutionLocation = "Backend", IsPrimary = true }]
            }
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var jobId = AgentWorld.ReadJson(created).GetProperty("id").GetGuid();
        await scenario.StartRunAsync(jobId);
        var claim = await scenario.ClaimAsync();

        var foreignKey = $"{scenario.Setup.Tenant.Id:N}/{jobId:N}/{Guid.NewGuid():N}/{Guid.NewGuid():N}/data.bak";
        using var saveReq = new HttpRequestMessage(HttpMethod.Post, $"/api/agent/tasks/{claim.AttemptId}/storage/catalog");
        saveReq.Headers.TryAddWithoutValidation("Authorization", AgentAuthorizationScheme.FormatHeaderValue(scenario.Agent.CredentialId, scenario.Agent.Secret));
        saveReq.Headers.Add("X-Backup-Agent-Protocol", "1.0");
        saveReq.Headers.Add(TaskStorageProtocol.LeaseHeader, claim.LeaseToken.ToString("D"));
        saveReq.Headers.Add(TaskStorageProtocol.DestinationHeader, connectionId.ToString("D"));
        saveReq.Content = JsonContent.Create(new TaskStorageCatalogSaveRequest(
            foreignKey,
            new TaskStorageCatalogRecord("file-id-1", "folder-1", "data.bak", 100, "abc", "def")));

        using var saveResp = await scenario.World.Anonymous.SendAsync(saveReq);
        Assert.Equal(HttpStatusCode.Conflict, saveResp.StatusCode);
        var body = AgentWorld.ReadJson(saveResp);
        Assert.Equal("invalid_key", body.GetProperty("error").GetString());

        foreach (var key in new[] { foreignKey, $"{Guid.NewGuid():N}/{jobId:N}/{Guid.NewGuid():N}/{claim.AttemptId:N}/data.bak" })
        {
            using var getReq = new HttpRequestMessage(HttpMethod.Get, $"/api/agent/tasks/{claim.AttemptId}/storage/catalog?key={Uri.EscapeDataString(key)}");
            getReq.Headers.TryAddWithoutValidation("Authorization", AgentAuthorizationScheme.FormatHeaderValue(scenario.Agent.CredentialId, scenario.Agent.Secret));
            getReq.Headers.Add("X-Backup-Agent-Protocol", "1.0");
            getReq.Headers.Add(TaskStorageProtocol.LeaseHeader, claim.LeaseToken.ToString("D"));
            getReq.Headers.Add(TaskStorageProtocol.DestinationHeader, connectionId.ToString("D"));
            using var getResp = await scenario.World.Anonymous.SendAsync(getReq);
            Assert.Equal(HttpStatusCode.Conflict, getResp.StatusCode);
            Assert.Equal("invalid_key", AgentWorld.ReadJson(getResp).GetProperty("error").GetString());
        }
    }

    [Fact]
    public async Task CatalogEndpoint_DeleteRouteDoesNotExistOnRestore()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var endpoints = scenario.World.Factory.Services.GetRequiredService<EndpointDataSource>();
        var hasDeleteCatalogOnRestore = endpoints.Endpoints
            .OfType<RouteEndpoint>()
            .Any(e => e.RoutePattern.RawText?.Contains("restore-tasks/{attemptId:guid}/storage/catalog", StringComparison.OrdinalIgnoreCase) == true
                && e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains("DELETE") == true);
        Assert.False(hasDeleteCatalogOnRestore);
    }
    // A seekable StreamContent would let HttpClient send Content-Length; this one always goes out chunked.
    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
}
