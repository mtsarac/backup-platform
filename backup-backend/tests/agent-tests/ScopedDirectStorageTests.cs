using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BackupPlatform.Agent.Core.Tasks;
using BackupPlatform.Agent.Transport;
using BackupPlatform.AgentTests.Support;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Contracts.Restores;
using BackupPlatform.StorageProviders.Abstractions;

namespace BackupPlatform.AgentTests;

public sealed class ScopedDirectStorageTests
{
    [Theory]
    [InlineData("source")]
    [InlineData("target")]
    public async Task Scoped_access_posts_to_the_exact_side_route_with_lease_and_identity_headers(string side)
    {
        using var fixture = new DriveFixture();
        var operation = Guid.NewGuid();
        var lease = Guid.NewGuid();
        var path = $"api/agent/replica-repairs/{operation:D}/storage/{side}/access";
        var provider = fixture.Factory.CreateScoped(new DirectStorageScope(path, lease, fixture.Tenant, fixture.Connection, "google-drive"));

        Assert.Equal(StorageTopology.AgentDirect, provider.Topology);
        Assert.False(await provider.ExistsAsync(fixture.Key, default));

        var access = Assert.Single(fixture.BackendRequests);
        Assert.Equal(HttpMethod.Post, access.Method);
        Assert.Equal("/" + path, access.Uri.AbsolutePath);
        Assert.Equal(lease.ToString("D"), access.Headers[TaskStorageProtocol.LeaseHeader]);
        Assert.Equal(AgentProtocol.Current.Version.ToString(), access.Headers[AgentProtocolHeader.Name]);
        Assert.Equal($"BackupAgent {TestIdentity.CredentialId:N}.{TestIdentity.Secret}", access.Headers["Authorization"]);
        Assert.False(access.Headers.ContainsKey(TaskStorageProtocol.DestinationHeader));
        Assert.False(access.Headers.ContainsKey(TaskStorageProtocol.RestoreHeader));
        Assert.Null(access.Body);
        Assert.Single(fixture.RemoteRequests);
    }

    [Theory]
    [InlineData("sftp")]
    [InlineData("google-drive")]
    public async Task Refused_scoped_access_fails_before_any_remote_request(string providerType)
    {
        using var handler = new FakeHttpHandler
        {
            Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict))
        };
        using var http = new HttpClient(handler);
        var path = $"api/agent/replica-repairs/{Guid.NewGuid():D}/storage/source/access";
        var factory = new HttpTaskStorageProviderFactory(http, Endpoint(), new FakeCredentialStore(TestIdentity.Create()));
        var provider = factory.CreateScoped(new DirectStorageScope(path, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), providerType));

        var failure = await Assert.ThrowsAsync<StorageException>(() => provider.OpenReadAsync(new StorageObjectKey("backups/source_FULL.bak"), default));

        Assert.Equal(StorageFailureKind.AccessDenied, failure.Kind);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("backup.example.test", request.Uri.Host);
        Assert.Equal("/" + path, request.Uri.AbsolutePath);
    }

    [Fact]
    public async Task Scoped_drive_put_keeps_the_object_record_in_memory_without_backend_catalog_or_content_calls()
    {
        var staging = Path.Combine(Path.GetTempPath(), "repair-drive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            using var fixture = new DriveFixture(staging);
            var scope = new DirectStorageScope($"api/agent/replica-repairs/{Guid.NewGuid():D}/storage/target/access",
                Guid.NewGuid(), fixture.Tenant, fixture.Connection, "google-drive");
            var provider = fixture.Factory.CreateScoped(scope);
            var records = Assert.IsAssignableFrom<IScopedDriveRecordSource>(provider);
            Assert.Null(await records.FindRecordAsync(fixture.Key, default));
            using var source = new MemoryStream(DriveFixture.Payload, writable: false);

            await provider.PutAsync(fixture.Key, source,
                new StoragePutOptions(StorageOverwritePolicy.Fail, fixture.Checksum, DriveFixture.Payload.Length), null, default);
            var verified = await provider.VerifyAsync(fixture.Key, new StorageVerificationExpectation(DriveFixture.Payload.Length, fixture.Checksum), default);
            var record = await records.FindRecordAsync(fixture.Key, default);

            Assert.Equal(StorageVerificationStatus.Verified, verified.Status);
            Assert.Equal(StorageVerificationMethod.ReadBack, verified.Method);
            Assert.NotNull(record);
            Assert.Equal("drive-file-1", record.FileId);
            Assert.Equal("folder-1", record.ParentFolderId);
            Assert.Equal(fixture.Key.FileName, record.FileName);
            Assert.Equal(DriveFixture.Payload.Length, record.SizeBytes);
            Assert.Equal(fixture.Checksum.Hex, record.Checksum);
            Assert.Equal("drive-md5", record.Md5Checksum);
            Assert.Null(await records.FindRecordAsync(new StorageObjectKey("backups/other_FULL.bak"), default));
            var access = Assert.Single(fixture.BackendRequests);
            Assert.Equal("/" + scope.AccessPath, access.Uri.AbsolutePath);
            Assert.Equal(scope.LeaseToken.ToString("D"), access.Headers[TaskStorageProtocol.LeaseHeader]);
            var upload = Assert.Single(fixture.RemoteRequests, request => request.Method == HttpMethod.Put);
            Assert.Equal(Encoding.UTF8.GetString(DriveFixture.Payload), upload.Body);
            Assert.All(fixture.RemoteRequests.Where(request => request.Method != HttpMethod.Put),
                request => Assert.Equal("Bearer drive-access-token", request.Headers["Authorization"]));
            Assert.Empty(Directory.GetFiles(staging));
        }
        finally
        {
            Directory.Delete(staging, recursive: true);
        }
    }

    [Fact]
    public async Task Agent_direct_drive_restore_gets_only_access_and_catalog_from_backend_and_reads_bytes_from_drive()
    {
        using var fixture = new DriveFixture { Uploaded = true };
        var task = new RestoreTaskSnapshot
        {
            OperationId = Guid.NewGuid(), AttemptId = Guid.NewGuid(), TenantId = fixture.Tenant, LeaseToken = Guid.NewGuid(),
            Source = new RestoreTaskSource
            {
                Access = "AgentDirect", StorageProvider = "google-drive", StorageTarget = fixture.Connection.ToString("D"),
                StorageKey = fixture.Key.Value, SizeBytes = DriveFixture.Payload.Length, Checksum = fixture.Checksum.Hex
            },
            Target = new RestoreTaskTarget { Host = "database.test" }
        };
        var provider = fixture.Factory.CreateRestore(task);
        Assert.Equal(StorageTopology.AgentDirect, provider.Topology);
        await using var stream = await provider.OpenReadAsync(fixture.Key, default);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);

        Assert.Equal(DriveFixture.Payload, buffer.ToArray());
        Assert.Contains(fixture.BackendRequests, request => request.Uri.AbsolutePath == $"/api/agent/restore-tasks/{task.AttemptId:D}/storage/access");
        Assert.Contains(fixture.BackendRequests, request => request.Uri.AbsolutePath == $"/api/agent/restore-tasks/{task.AttemptId:D}/storage/catalog");
        Assert.All(fixture.BackendRequests, request =>
        {
            Assert.True(request.Uri.AbsolutePath.EndsWith("/storage/access", StringComparison.Ordinal)
                || request.Uri.AbsolutePath.EndsWith("/storage/catalog", StringComparison.Ordinal));
            Assert.Equal(task.LeaseToken.ToString("D"), request.Headers[TaskStorageProtocol.LeaseHeader]);
            Assert.Equal(task.OperationId.ToString("D"), request.Headers[TaskStorageProtocol.RestoreHeader]);
        });
        var media = Assert.Single(fixture.RemoteRequests, request => request.Uri.Query == "?alt=media");
        Assert.Equal("Bearer drive-access-token", media.Headers["Authorization"]);
    }

    private static AgentBackendEndpoint Endpoint() => new(new Uri("https://backup.example.test/"));

    private sealed class DriveFixture : IDisposable
    {
        public static readonly byte[] Payload = Encoding.UTF8.GetBytes("verified replica bytes");
        public Guid Tenant { get; } = Guid.NewGuid();
        public Guid Connection { get; } = Guid.NewGuid();
        public StorageObjectKey Key { get; } = new("backups/repair_FULL.bak");
        public ContentChecksum Checksum { get; } = ContentChecksum.Sha256(SHA256.HashData(Payload));
        public bool Uploaded { get; set; }
        public FakeHttpHandler Handler { get; } = new();
        private readonly HttpClient _http;
        public HttpTaskStorageProviderFactory Factory { get; }
        public RecordedRequest[] BackendRequests => Handler.Requests.Where(request => request.Uri.Host == "backup.example.test").ToArray();
        public RecordedRequest[] RemoteRequests => Handler.Requests.Where(request => request.Uri.Host != "backup.example.test").ToArray();

        public DriveFixture(string? staging = null)
        {
            Handler.Respond = RespondAsync;
            _http = new HttpClient(Handler);
            Factory = new HttpTaskStorageProviderFactory(_http, Endpoint(), new FakeCredentialStore(TestIdentity.Create()), staging);
        }

        private Task<HttpResponseMessage> RespondAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            HttpResponseMessage response;
            if (uri.Host == "backup.example.test")
            {
                if (uri.AbsolutePath.EndsWith("/storage/access", StringComparison.Ordinal)
                    || uri.AbsolutePath.EndsWith("/storage/source/access", StringComparison.Ordinal)
                    || uri.AbsolutePath.EndsWith("/storage/target/access", StringComparison.Ordinal))
                {
                    response = Json(new { providerType = "google-drive", accessToken = "drive-access-token", expiresAt = DateTimeOffset.UtcNow.AddHours(1), folderId = "folder-1" });
                }
                else if (uri.AbsolutePath.EndsWith("/storage/catalog", StringComparison.Ordinal))
                {
                    response = Json(new TaskStorageCatalogRecord("drive-file-1", "folder-1", Key.FileName, Payload.Length, Checksum.Hex, "drive-md5"));
                }
                else
                {
                    throw new InvalidOperationException("Unexpected backend route: " + uri.AbsolutePath);
                }
            }
            else
            {
                Assert.Equal("www.googleapis.com", uri.Host);
                if (uri.AbsolutePath == "/drive/v3/files/generateIds")
                {
                    response = Json(new { ids = new[] { "drive-file-1" } });
                }
                else if (request.Method == HttpMethod.Post && uri.AbsolutePath == "/upload/drive/v3/files")
                {
                    response = new HttpResponseMessage(HttpStatusCode.OK);
                    response.Headers.Location = new Uri("https://www.googleapis.com/upload-session");
                }
                else if (request.Method == HttpMethod.Put && uri.AbsolutePath == "/upload-session")
                {
                    Uploaded = true;
                    response = Json(FileMetadata());
                }
                else if (uri.AbsolutePath == "/drive/v3/files")
                {
                    response = Json(new { files = Uploaded ? new[] { FileMetadata() } : Array.Empty<object>() });
                }
                else if (uri.AbsolutePath == "/drive/v3/files/drive-file-1" && uri.Query == "?alt=media")
                {
                    response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) };
                }
                else if (uri.AbsolutePath == "/drive/v3/files/drive-file-1")
                {
                    response = Json(FileMetadata());
                }
                else
                {
                    throw new InvalidOperationException("Unexpected Drive route: " + uri.AbsolutePath);
                }
            }
            return Task.FromResult(response);
        }

        private object FileMetadata() => new
        {
            id = "drive-file-1", name = Key.FileName, size = Payload.Length, parents = new[] { "folder-1" }, md5Checksum = "drive-md5",
            appProperties = new
            {
                tenantId = Tenant.ToString("N"), connectionId = Connection.ToString("N"),
                storageKeyHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Key.Value))), sha256 = Checksum.Hex
            }
        };

        private static HttpResponseMessage Json<T>(T value) => FakeHttpHandler.Json(HttpStatusCode.OK, JsonSerializer.Serialize(value, JsonSerializerOptions.Web));

        public void Dispose() => _http.Dispose();
    }
}
