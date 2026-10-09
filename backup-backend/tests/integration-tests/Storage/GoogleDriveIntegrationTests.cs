using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using BackupPlatform.Api.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Storage;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Infrastructure.Persistence;
using BackupPlatform.IntegrationTests.Quota;
using BackupPlatform.Storage;
using BackupPlatform.StorageProviders.Abstractions;
using BackupPlatform.StorageProviders.GoogleDrive;
using BackupPlatform.Application.Backups;
using BackupPlatform.SecretProviders.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace BackupPlatform.IntegrationTests.Storage;

public sealed class GoogleDriveIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static readonly ContentChecksum ProbeChecksum = ContentChecksum.Sha256(SHA256.HashData([1, 2, 3, 4]));

    [Fact]
    public async Task GoogleDrive_OAuthLifecycle_AndConnectionTest_EndToEnd()
    {
        var fakeHandler = new FakeGoogleDriveServerHandler();
        var fakeHttp = new HttpClient(fakeHandler);

        var tenantId = TenantId.New();
        var settings = new Dictionary<string, string?>
        {
            ["GoogleDrive:ClientId"] = "test_client_id",
            ["GoogleDrive:ClientSecret"] = "test_client_secret",
            ["GoogleDrive:RedirectUri"] = "https://backup.example.test/api/platform/google-drive/oauth/callback"
        };
        var world = await AgentWorld.CreateAsync(fixture, configuration: settings, configureServices: services =>
        {
            services.AddSingleton(fakeHttp);
        });
        var tenant = new Tenant(tenantId, "Google Drive Tenant");
        await using (var db = fixture.CreatePlatformContext())
        {
            db.Tenants.Add(tenant);
            await db.SaveChangesAsync();
        }
        await world.EntitleAsync(tenant);

        var user = await world.SeedUserAsync();
        await world.GrantAsync(tenant, user, TenantRole.Admin);
        var session = await world.SignInAsync(user);

        // 1. Create Google Drive storage connection (initially without secret)
        var createResponse = await session.SendAsync(HttpMethod.Post, $"/api/tenants/{tenantId.Value}/storage-connections", new
        {
            providerType = "google-drive",
            name = "My Cloud Drive"
        });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var connectionId = Guid.Parse(created.GetProperty("id").GetString()!);
        Assert.False(created.GetProperty("hasSecret").GetBoolean());

        // 2. Request OAuth authorization URL
        var authUrlResponse = await session.SendAsync(
            HttpMethod.Post, $"/api/tenants/{tenantId.Value}/storage-connections/{connectionId}/google-drive/authorize-url", new { });
        Assert.Equal(HttpStatusCode.OK, authUrlResponse.StatusCode);
        var authUrlObj = await authUrlResponse.Content.ReadFromJsonAsync<JsonElement>();
        var authUrl = authUrlObj.GetProperty("authorizationUrl").GetString()!;

        Assert.Contains("accounts.google.com/o/oauth2/v2/auth", authUrl);
        Assert.Contains("scope=https%3A%2F%2Fwww.googleapis.com%2Fauth%2Fdrive.file", authUrl);
        Assert.Contains("code_challenge=", authUrl);

        var uri = new Uri(authUrl);
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var state = query["state"]!;

        // 3. Process OAuth callback via public endpoint (as Google redirects)
        var callbackClient = world.Factory.CreateClient();
        var callbackResp = await callbackClient.GetAsync(
            $"/api/platform/google-drive/oauth/callback?code=fake_auth_code&state={state}");
        Assert.Equal(HttpStatusCode.OK, callbackResp.StatusCode);
        var html = await callbackResp.Content.ReadAsStringAsync();
        Assert.True(html.Contains("başarılı"), $"Expected success but got: {html}");

        // 4. Verify connection now has credentials and account metadata
        var getConnResp = await session.GetAsync($"/api/tenants/{tenantId.Value}/storage-connections/{connectionId}");
        var connText = await getConnResp.Content.ReadAsStringAsync();
        var connObj = JsonSerializer.Deserialize<JsonElement>(connText);
        Assert.True(connObj.GetProperty("hasSecret").GetBoolean(), $"Connection was: {connText}");
        var config = connObj.GetProperty("configuration");
        Assert.Equal("testuser@gmail.com", config.GetProperty("accountEmail").GetString());
        Assert.Equal("false", config.GetProperty("reauthorizationRequired").GetString());

        // 5. Single-use state verification: repeating the callback with the same state fails
        var replayResp = await callbackClient.GetAsync(
            $"/api/platform/google-drive/oauth/callback?code=fake_auth_code&state={state}");
        Assert.Equal(HttpStatusCode.OK, replayResp.StatusCode);
        var replayHtml = await replayResp.Content.ReadAsStringAsync();
        Assert.Contains("başarısız", replayHtml);

        // Nothing from the query string is reflected into the page (no script injection through `error`).
        var xss = await callbackClient.GetAsync("/api/platform/google-drive/oauth/callback?error=%27%3C%2Fscript%3E%3Cscript%3Ealert(1)%3C%2Fscript%3E&state=x");
        Assert.DoesNotContain("alert(1)", await xss.Content.ReadAsStringAsync());

        // 6. Test connection via API
        var testResp = await session.SendAsync(
            HttpMethod.Post, $"/api/tenants/{tenantId.Value}/storage-connections/{connectionId}/test", new { });
        Assert.Equal(HttpStatusCode.OK, testResp.StatusCode);
        var testResult = await testResp.Content.ReadFromJsonAsync<JsonElement>();
        var testObj = testResult.GetProperty("test");
        Assert.True(testObj.GetProperty("status").GetString() == "Succeeded", $"Test failed: {testObj}");
        Assert.Equal("ok", testObj.GetProperty("code").GetString());

        // 7. Disconnect connection
        var disconnectResp = await session.SendAsync(
            HttpMethod.Post, $"/api/tenants/{tenantId.Value}/storage-connections/{connectionId}/google-drive/disconnect", new { });
        Assert.Equal(HttpStatusCode.OK, disconnectResp.StatusCode);

        // Verify secret detached and reauthorizationRequired is set
        var disconnectedGetResp = await session.GetAsync($"/api/tenants/{tenantId.Value}/storage-connections/{connectionId}");
        var disconnectedObj = await disconnectedGetResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(disconnectedObj.GetProperty("hasSecret").GetBoolean());
        Assert.Equal("true", disconnectedObj.GetProperty("configuration").GetProperty("reauthorizationRequired").GetString());
    }

    [Theory]
    [InlineData("http://localhost:5173", "}, \"http://localhost:5173\");")]
    [InlineData("http://localhost:5173/path", "}, window.location.origin);")]
    [InlineData("*", "}, window.location.origin);")]
    [InlineData("", "}, window.location.origin);")]
    public async Task GoogleDrive_OAuthCallback_TargetsOnlyAValidConfiguredFrontendOrigin(string configured, string expected)
    {
        var world = await AgentWorld.CreateAsync(fixture, configuration: new Dictionary<string, string?>
        {
            ["GoogleDrive:ClientId"] = "test_client_id",
            ["GoogleDrive:ClientSecret"] = "test_client_secret",
            ["GoogleDrive:RedirectUri"] = "https://backup.example.test/api/platform/google-drive/oauth/callback",
            ["GoogleDrive:FrontendOrigin"] = configured
        });

        var response = await world.Factory.CreateClient().GetAsync("/api/platform/google-drive/oauth/callback?error=access_denied&state=x");

        Assert.Contains(expected, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task GoogleDrive_QuotaAccounting_AndTenantIsolation()
    {
        var tenantA = TenantId.New();
        var tenantB = TenantId.New();

        var world = await AgentWorld.CreateAsync(fixture, configuration: new Dictionary<string, string?>
        {
            ["GoogleDrive:ClientId"] = "test_client_id",
            ["GoogleDrive:ClientSecret"] = "test_client_secret",
            ["GoogleDrive:RedirectUri"] = "https://backup.example.test/api/platform/google-drive/oauth/callback"
        });

        var tA = new Tenant(tenantA, "Tenant A");
        var tB = new Tenant(tenantB, "Tenant B");
        await using (var db = fixture.CreatePlatformContext())
        {
            db.Tenants.AddRange(tA, tB);
            await db.SaveChangesAsync();
        }
        await world.EntitleAsync(tA);
        await world.EntitleAsync(tB);

        var userA = await world.SeedUserAsync();
        await world.GrantAsync(tA, userA, TenantRole.Admin);
        var sessionA = await world.SignInAsync(userA);

        var userB = await world.SeedUserAsync();
        await world.GrantAsync(tB, userB, TenantRole.Admin);
        var sessionB = await world.SignInAsync(userB);

        // Tenant A creates Google Drive connection
        var createA = await sessionA.SendAsync(HttpMethod.Post, $"/api/tenants/{tenantA.Value}/storage-connections", new
        {
            providerType = "google-drive",
            name = "Drive A"
        });
        Assert.Equal(HttpStatusCode.Created, createA.StatusCode);
        var connA = await createA.Content.ReadFromJsonAsync<JsonElement>();
        var connIdA = Guid.Parse(connA.GetProperty("id").GetString()!);

        // Tenant B cannot access Tenant A's connection (Tenant Isolation)

        // Another tenant's admin can neither start OAuth for nor disconnect this connection.
        var crossAuthorize = await sessionB.SendAsync(HttpMethod.Post, $"/api/tenants/{tenantB.Value}/storage-connections/{connIdA}/google-drive/authorize-url", new { });
        Assert.Equal(HttpStatusCode.Conflict, crossAuthorize.StatusCode);
        var crossDisconnect = await sessionB.SendAsync(HttpMethod.Post, $"/api/tenants/{tenantB.Value}/storage-connections/{connIdA}/google-drive/disconnect", new { });
        Assert.Equal(HttpStatusCode.NotFound, crossDisconnect.StatusCode);
        var crossTenantGet = await sessionB.GetAsync($"/api/tenants/{tenantB.Value}/storage-connections/{connIdA}");
        Assert.Equal(HttpStatusCode.NotFound, crossTenantGet.StatusCode);

        // Quota check: Google Drive connection is assigned and counted in TenantStorageUsage projection
        await using (var db = fixture.CreatePlatformContext())
        {
            var connection = await db.StorageConnections.SingleAsync(c => c.Id == connIdA);
            Assert.Equal(StorageProviderTypes.GoogleDrive, connection.ProviderType);
            Assert.Equal(StorageOwnership.TenantManaged, connection.Ownership);
            Assert.Equal("tenant", connection.QuotaScope);
        }
    }

    [Fact]
    public async Task GoogleDrive_Resolver_Put_Verify_Download_AndRetention_Lifecycle()
    {
        var fakeHandler = new FakeGoogleDriveServerHandler();
        var fakeHttp = new HttpClient(fakeHandler);
        var tenantId = TenantId.New();
        var connectionId = Guid.NewGuid();

        var settings = new Dictionary<string, string?>
        {
            ["GoogleDrive:ClientId"] = "test_client_id",
            ["GoogleDrive:ClientSecret"] = "test_client_secret",
            ["GoogleDrive:RedirectUri"] = "https://backup.example.test/api/platform/google-drive/oauth/callback"
        };
        var world = await AgentWorld.CreateAsync(fixture, configuration: settings, configureServices: services =>
        {
            services.AddSingleton(fakeHttp);
        });

        var tenant = new Tenant(tenantId, "Resolver Tenant");
        await using (var db = fixture.CreatePlatformContext())
        {
            db.Tenants.Add(tenant);
            var conn = new StorageConnection(
                connectionId, tenantId, "Drive Target", StorageProviderTypes.GoogleDrive,
                StorageOwnership.TenantManaged, "tenant", new Dictionary<string, string>(), isDefault: false, DateTimeOffset.UtcNow);
            conn.AttachSecret($"sc-{connectionId:N}-123", ["refreshToken"], DateTimeOffset.UtcNow);
            db.StorageConnections.Add(conn);
            await db.SaveChangesAsync();
        }

        // Seed secret in ISecretStore
        var secretStore = world.Factory.Services.GetRequiredService<ISecretStore>();
        await secretStore.SetAsync(new SecretReference($"sc-{connectionId:N}-123"), new SecretValue("{\"refreshToken\":\"test_refresh\"}"), CancellationToken.None);

        var resolver = world.Factory.Services.GetRequiredService<IBackupStorageResolver>();
        var provider = resolver.Resolve(tenantId, StorageProviderTypes.GoogleDrive, connectionId.ToString("D"));
        Assert.NotNull(provider);
        Assert.Equal(StorageTopology.BackendRelayed, provider!.Topology);

        var key = new StorageObjectKey($"{tenantId.Value:N}/job1/run1/attempt1/backup.bak");
        var content = RandomNumberGenerator.GetBytes(64 * 1024);
        var checksum = ContentChecksum.Sha256(SHA256.HashData(content));

        // Put
        using (var s = new MemoryStream(content))
        {
            var meta = await provider.PutAsync(key, s, new StoragePutOptions(StorageOverwritePolicy.Fail, checksum, content.Length), null, CancellationToken.None);
            Assert.Equal(content.Length, meta.SizeBytes);
        }

        // Exists
        Assert.True(await provider.ExistsAsync(key, CancellationToken.None));

        // Verify
        var verify = await provider.VerifyAsync(key, new StorageVerificationExpectation(content.Length, checksum), CancellationToken.None);
        Assert.Equal(StorageVerificationStatus.Verified, verify.Status);

        // Download / Read
        await using (var stream = await provider.OpenReadAsync(key, CancellationToken.None))
        {
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            Assert.Equal(content, ms.ToArray());
        }

        // A retry after external removal must replace the stale catalog file ID, not just its checksum.
        fakeHandler.SimulateExternalDelete();
        using (var s = new MemoryStream(content))
        {
            await provider.PutAsync(key, s, new StoragePutOptions(StorageOverwritePolicy.Fail, checksum, content.Length), null, CancellationToken.None);
        }
        Assert.True(await provider.ExistsAsync(key, CancellationToken.None));
        Assert.Equal(StorageVerificationStatus.Verified,
            (await provider.VerifyAsync(key, new StorageVerificationExpectation(content.Length, checksum), CancellationToken.None)).Status);

        // Retention Delete
        var deleted = await provider.DeleteAsync(key, CancellationToken.None);
        Assert.True(deleted);

        // Verify absence
        Assert.False(await provider.ExistsAsync(key, CancellationToken.None));
    }

    [Fact]
    public async Task GoogleDrive_CatalogSave_ReplacesStaleDriveIdentity()
    {
        var tenant = TenantId.New();
        var connectionId = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(fixture.ConnectionString).Options;
        var catalog = new EfGoogleDriveFileCatalog(options, TimeProvider.System);
        var storageKey = $"{tenant.Value:N}/job1/run1/attempt1/backup.bak";
        var initial = new GoogleDriveFileRecord("old-file", "old-folder", "old.bak", 4, ProbeChecksum.Hex, "old-md5");
        var replacement = new GoogleDriveFileRecord("new-file", "new-folder", "new.bak", 8, ProbeChecksum.Hex, "new-md5");

        await catalog.SaveAsync(tenant.Value, connectionId, storageKey, initial, CancellationToken.None);
        await catalog.SaveAsync(tenant.Value, connectionId, storageKey, replacement, CancellationToken.None);

        Assert.Equal(replacement, await catalog.FindAsync(tenant.Value, connectionId, storageKey, CancellationToken.None));
        await using var db = fixture.CreatePlatformContext();
        var row = Assert.Single(await db.GoogleDriveFileMetadata.AsNoTracking()
            .Where(m => m.TenantId == tenant && m.ConnectionId == connectionId && m.StorageKey == storageKey).ToListAsync());
        Assert.Equal("new-file", row.FileId);
        Assert.Equal("new-folder", row.ParentFolderId);
        Assert.Equal("new.bak", row.FileName);
    }

    private sealed class FakeGoogleDriveServerHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _files = [];
        private readonly Dictionary<string, (string Name, long TotalSize, MemoryStream Stream, Dictionary<string, string> Properties)> _sessions = [];
        private readonly Dictionary<string, string> _fileNames = [];
        private readonly Dictionary<string, Dictionary<string, string>> _fileProperties = [];
        private int _sessionSeq;

        public void SimulateExternalDelete() => _files.Clear();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri?.ToString() ?? "";

            // OAuth Token
            if (uri.Contains("oauth2.googleapis.com/token"))
            {
                var resp = new
                {
                    access_token = "fake_access_token_123",
                    refresh_token = "fake_refresh_token_abc",
                    expires_in = 3600
                };
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(resp) });
            }

            // User Info
            if (uri.Contains("/drive/v3/about"))
            {
                var resp = new
                {
                    user = new
                    {
                        emailAddress = "testuser@gmail.com",
                        displayName = "Test User"
                    }
                };
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(resp) });
            }

            // OAuth Revoke
            if (uri.Contains("oauth2.googleapis.com/revoke"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }

            // Folder search or create
            if (uri.Contains("/drive/v3/files") && request.Method == HttpMethod.Post && !uri.Contains("uploadType=resumable"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { id = "folder-mock-id" })
                });
            }

            if (uri.Contains("/drive/v3/files?q=") && request.Method == HttpMethod.Get)
            {
                if (Uri.UnescapeDataString(request.RequestUri!.Query).Contains("mimeType"))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { files = Array.Empty<object>() }) });
                var found = _files.Select(kv => new
                {
                    id = kv.Key,
                    name = _fileNames[kv.Key],
                    size = (long)kv.Value.Length,
                    md5Checksum = "fake_md5",
                    trashed = false,
                    modifiedTime = DateTimeOffset.UtcNow,
                    appProperties = _fileProperties[kv.Key]
                }).ToList();

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { files = found })
                });
            }

            // Initiate Resumable Upload
            if (uri.Contains("upload/drive/v3/files?uploadType=resumable") && request.Method == HttpMethod.Post)
            {
                var sid = $"sess-{++_sessionSeq}";
                var total = long.Parse(request.Headers.GetValues("X-Upload-Content-Length").First());
                using var metadata = JsonDocument.Parse(request.Content!.ReadAsStringAsync(cancellationToken).Result);
                _sessions[sid] = (metadata.RootElement.GetProperty("name").GetString()!, total, new MemoryStream(),
                    metadata.RootElement.GetProperty("appProperties").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!));

                var response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Headers.Location = new Uri($"https://fake-gdrive-upload/{sid}");
                return Task.FromResult(response);
            }

            // Put Chunk
            if (uri.StartsWith("https://fake-gdrive-upload/sess-") && request.Method == HttpMethod.Put)
            {
                var sid = uri.Split('/').Last();
                if (!_sessions.TryGetValue(sid, out var session))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                }

                var chunkBytes = request.Content?.ReadAsByteArrayAsync(cancellationToken).Result ?? [];
                session.Stream.Write(chunkBytes);

                if (session.Stream.Length >= session.TotalSize)
                {
                    var fileId = $"fid-{sid}";
                    _files[fileId] = session.Stream.ToArray();
                    _fileNames[fileId] = session.Name;
                    _fileProperties[fileId] = session.Properties;
                    var fileResp = new
                    {
                        id = fileId,
                        name = session.Name,
                        size = session.TotalSize,
                        md5Checksum = "fake_md5",
                        trashed = false,
                        modifiedTime = DateTimeOffset.UtcNow
                    };
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(fileResp)
                    });
                }

                var incomplete = new HttpResponseMessage((HttpStatusCode)308);
                incomplete.Headers.Add("Range", $"bytes=0-{session.Stream.Length - 1}");
                return Task.FromResult(incomplete);
            }

            // Read / Download
            if (uri.Contains("?alt=media") && request.Method == HttpMethod.Get)
            {
                var fid = uri.Split('?')[0].Split('/').Last();
                if (_files.TryGetValue(fid, out var data))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) });
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            // Get metadata by ID
            if (uri.Contains("/drive/v3/files/") && !uri.Contains("?alt=media") && request.Method == HttpMethod.Get)
            {
                var fid = uri.Split('?')[0].Split('/').Last();
                if (_files.TryGetValue(fid, out var data))
                {
                    var item = new
                    {
                        id = fid,
                        name = "probe",
                        size = (long)data.Length,
                        md5Checksum = "fake_md5",
                        trashed = false,
                        modifiedTime = DateTimeOffset.UtcNow
                    };
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(item) });
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            // Delete
            if (uri.Contains("/drive/v3/files/") && request.Method == HttpMethod.Delete)
            {
                var fid = uri.Split('/').Last();
                if (_files.Remove(fid))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
