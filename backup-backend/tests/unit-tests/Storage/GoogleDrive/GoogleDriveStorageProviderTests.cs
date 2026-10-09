using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BackupPlatform.StorageProviders.Abstractions;
using BackupPlatform.StorageProviders.GoogleDrive;
using Xunit;

namespace BackupPlatform.UnitTests.Storage.GoogleDrive;

public sealed class GoogleDriveStorageProviderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _connectionId = Guid.NewGuid();

    public GoogleDriveStorageProviderTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("gdrive-test-").FullName;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public async Task PutAsync_UploadsInChunksAndCompletes_WithVerification()
    {
        var fakeHandler = new FakeGoogleDriveHandler();
        var client = new HttpClient(fakeHandler);
        var options = new GoogleDriveStorageOptions(
            "client_id", "client_secret", _tenantId, _connectionId,
            folderId: "folder-123", stagingDirectory: _tempDir, chunkSizeBytes: 256 * 1024);

        var provider = new GoogleDriveStorageProvider(
            client, options, _ => ValueTask.FromResult(new GoogleDriveCredential("refresh_token")));

        var content = RandomNumberGenerator.GetBytes(512 * 1024); // 2 chunks of 256 KiB
        var checksum = ContentChecksum.Sha256(SHA256.HashData(content));
        var key = new StorageObjectKey($"{_tenantId:N}/job1/run1/attempt1/backup.bak");

        using var contentStream = new MemoryStream(content);
        var progressValues = new List<long>();
        var progress = new Progress<long>(p => progressValues.Add(p));

        var meta = await provider.PutAsync(
            key, contentStream,
            new StoragePutOptions(StorageOverwritePolicy.Fail, checksum, content.Length),
            progress, CancellationToken.None);

        Assert.Equal(content.Length, meta.SizeBytes);
        Assert.Equal(checksum, meta.Checksum);
        Assert.True(await provider.ExistsAsync(key, CancellationToken.None));

        // Verify read back
        var verify = await provider.VerifyAsync(
            key, new StorageVerificationExpectation(content.Length, checksum), CancellationToken.None);
        Assert.Equal(StorageVerificationStatus.Verified, verify.Status);
        Assert.Equal(StorageVerificationMethod.ReadBack, verify.Method);

        // Open read
        await using var readStream = await provider.OpenReadAsync(key, CancellationToken.None);
        using var ms = new MemoryStream();
        await readStream.CopyToAsync(ms);
        Assert.Equal(content, ms.ToArray());

        // Delete
        var deleted = await provider.DeleteAsync(key, CancellationToken.None);
        Assert.True(deleted);
        Assert.False(await provider.ExistsAsync(key, CancellationToken.None));
    }

    [Fact]
    public async Task PutAsync_RecoversFromExpiredUploadSession()
    {
        var fakeHandler = new FakeGoogleDriveHandler { ExpireFirstSession = true };
        var client = new HttpClient(fakeHandler);
        var options = new GoogleDriveStorageOptions(
            "client_id", "client_secret", _tenantId, _connectionId,
            folderId: "folder-123", stagingDirectory: _tempDir, chunkSizeBytes: 256 * 1024);

        var provider = new GoogleDriveStorageProvider(
            client, options, _ => ValueTask.FromResult(new GoogleDriveCredential("refresh_token")));

        var content = RandomNumberGenerator.GetBytes(512 * 1024);
        var checksum = ContentChecksum.Sha256(SHA256.HashData(content));
        var key = new StorageObjectKey($"{_tenantId:N}/job1/run1/attempt1/recovery.bak");

        using var contentStream = new MemoryStream(content);
        var meta = await provider.PutAsync(
            key, contentStream,
            new StoragePutOptions(StorageOverwritePolicy.Fail, checksum, content.Length),
            null, CancellationToken.None);

        Assert.Equal(content.Length, meta.SizeBytes);
        Assert.True(await provider.ExistsAsync(key, CancellationToken.None));
    }

    [Fact]
    public async Task PutAsync_DetectsExistingMatchingFile_Idempotent()
    {
        var fakeHandler = new FakeGoogleDriveHandler();
        var client = new HttpClient(fakeHandler);
        var options = new GoogleDriveStorageOptions(
            "client_id", "client_secret", _tenantId, _connectionId,
            folderId: "folder-123", stagingDirectory: _tempDir, chunkSizeBytes: 256 * 1024);

        var provider = new GoogleDriveStorageProvider(
            client, options, _ => ValueTask.FromResult(new GoogleDriveCredential("refresh_token")));

        var content = RandomNumberGenerator.GetBytes(256 * 1024);
        var checksum = ContentChecksum.Sha256(SHA256.HashData(content));
        var key = new StorageObjectKey($"{_tenantId:N}/job1/run1/attempt1/idempotent.bak");

        using (var s1 = new MemoryStream(content))
        {
            await provider.PutAsync(key, s1, new StoragePutOptions(StorageOverwritePolicy.Fail, checksum, content.Length), null, CancellationToken.None);
        }

        // A fresh provider has no in-memory catalog or resumable session, as after a process restart.
        var restarted = new GoogleDriveStorageProvider(
            client, options, _ => ValueTask.FromResult(new GoogleDriveCredential("refresh_token")));
        using (var s2 = new MemoryStream(content))
        {
            var meta2 = await restarted.PutAsync(key, s2, new StoragePutOptions(StorageOverwritePolicy.Fail, checksum, content.Length), null, CancellationToken.None);
            Assert.Equal(content.Length, meta2.SizeBytes);
        }
        Assert.Equal(1, fakeHandler.SessionCount);
    }

    [Fact]
    public async Task PutAsync_ThrowsAlreadyExists_WhenExistingFileDiffers()
    {
        var fakeHandler = new FakeGoogleDriveHandler();
        var client = new HttpClient(fakeHandler);
        var options = new GoogleDriveStorageOptions(
            "client_id", "client_secret", _tenantId, _connectionId,
            folderId: "folder-123", stagingDirectory: _tempDir, chunkSizeBytes: 256 * 1024);

        var provider = new GoogleDriveStorageProvider(
            client, options, _ => ValueTask.FromResult(new GoogleDriveCredential("refresh_token")));

        var content1 = RandomNumberGenerator.GetBytes(256 * 1024);
        var checksum1 = ContentChecksum.Sha256(SHA256.HashData(content1));
        var key = new StorageObjectKey($"{_tenantId:N}/job1/run1/attempt1/conflict.bak");

        using (var s1 = new MemoryStream(content1))
        {
            await provider.PutAsync(key, s1, new StoragePutOptions(StorageOverwritePolicy.Fail, checksum1, content1.Length), null, CancellationToken.None);
        }

        var content2 = RandomNumberGenerator.GetBytes(512 * 1024);
        var checksum2 = ContentChecksum.Sha256(SHA256.HashData(content2));

        using (var s2 = new MemoryStream(content2))
        {
            var ex = await Assert.ThrowsAsync<StorageException>(() =>
                provider.PutAsync(key, s2, new StoragePutOptions(StorageOverwritePolicy.Fail, checksum2, content2.Length), null, CancellationToken.None));
            Assert.Equal(StorageFailureKind.AlreadyExists, ex.Kind);
        }
    }


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PutAsync_ReplacePreservesExistingBytesOnFailure(bool invalidChecksum)
    {
        var handler = new FakeGoogleDriveHandler();
        using var client = new HttpClient(handler);
        var provider = new GoogleDriveStorageProvider(client,
            new GoogleDriveStorageOptions("client_id", "client_secret", _tenantId, _connectionId,
                folderId: "folder-123", stagingDirectory: _tempDir),
            _ => ValueTask.FromResult(new GoogleDriveCredential("refresh_token")));
        var key = new StorageObjectKey($"{_tenantId:N}/job1/run1/attempt1/replace.bak");
        byte[] original = [1, 2, 3, 4];
        byte[] replacement = [4, 3, 2, 1];
        var originalChecksum = ContentChecksum.Sha256(SHA256.HashData(original));
        using var firstStream = new MemoryStream(original);
        await provider.PutAsync(key, firstStream, new StoragePutOptions(StorageOverwritePolicy.Fail, originalChecksum, original.Length), null, CancellationToken.None);
        handler.DenyUploads = !invalidChecksum;
        var expectedChecksum = invalidChecksum ? originalChecksum : ContentChecksum.Sha256(SHA256.HashData(replacement));

        using var secondStream = new MemoryStream(replacement);
        var failure = await Assert.ThrowsAsync<StorageException>(() => provider.PutAsync(key, secondStream,
            new StoragePutOptions(StorageOverwritePolicy.Replace, expectedChecksum, replacement.Length), null, CancellationToken.None));

        Assert.Equal(invalidChecksum ? StorageFailureKind.IntegrityMismatch : StorageFailureKind.AccessDenied, failure.Kind);
        Assert.Equal(StorageVerificationStatus.Verified,
            (await provider.VerifyAsync(key, new StorageVerificationExpectation(original.Length, originalChecksum), CancellationToken.None)).Status);
        Assert.Empty(Directory.EnumerateFiles(_tempDir));
    }

    [Fact]
    public async Task PutAsync_ReplaceUpdatesExistingDriveFileWithoutDeletingIt()
    {
        using var client = new HttpClient(new FakeGoogleDriveHandler());
        var catalog = new InMemoryGoogleDriveFileCatalog();
        var provider = new GoogleDriveStorageProvider(client,
            new GoogleDriveStorageOptions("client_id", "client_secret", _tenantId, _connectionId,
                folderId: "folder-123", stagingDirectory: _tempDir),
            _ => ValueTask.FromResult(new GoogleDriveCredential("refresh_token")), catalog);
        var key = new StorageObjectKey($"{_tenantId:N}/job1/run1/attempt1/replace.bak");
        byte[] original = [1, 2, 3, 4];
        byte[] replacement = [4, 3, 2, 1, 0];
        using var firstStream = new MemoryStream(original);
        await provider.PutAsync(key, firstStream,
            new StoragePutOptions(StorageOverwritePolicy.Fail, ContentChecksum.Sha256(SHA256.HashData(original)), original.Length), null, CancellationToken.None);
        var originalRecord = await catalog.FindAsync(_tenantId, _connectionId, key.Value, CancellationToken.None);
        var replacementChecksum = ContentChecksum.Sha256(SHA256.HashData(replacement));

        using var secondStream = new MemoryStream(replacement);
        await provider.PutAsync(key, secondStream,
            new StoragePutOptions(StorageOverwritePolicy.Replace, replacementChecksum, replacement.Length), null, CancellationToken.None);

        Assert.Equal(originalRecord!.FileId, (await catalog.FindAsync(_tenantId, _connectionId, key.Value, CancellationToken.None))!.FileId);
        Assert.Equal(StorageVerificationStatus.Verified,
            (await provider.VerifyAsync(key, new StorageVerificationExpectation(replacement.Length, replacementChecksum), CancellationToken.None)).Status);
    }

    [Fact]
    public async Task PutAsync_AcceptsProductionShapedLongKey_WithinDriveAppPropertiesLimit()
    {
        var client = new HttpClient(new FakeGoogleDriveHandler());
        var options = new GoogleDriveStorageOptions(
            "client_id", "client_secret", _tenantId, _connectionId,
            folderId: "folder-123", stagingDirectory: _tempDir, chunkSizeBytes: 256 * 1024);
        var provider = new GoogleDriveStorageProvider(client, options, _ => ValueTask.FromResult(new GoogleDriveCredential("refresh_token")));

        var content = RandomNumberGenerator.GetBytes(64 * 1024);
        var checksum = ContentChecksum.Sha256(SHA256.HashData(content));
        var key = new StorageObjectKey($"{_tenantId:N}/{Guid.NewGuid():N}/{Guid.NewGuid():N}/{Guid.NewGuid():N}/bp_google_drive_acceptance_20261009T135816Z_c4fa5013_FULL.bak");

        using var stream = new MemoryStream(content);
        var meta = await provider.PutAsync(key, stream, new StoragePutOptions(StorageOverwritePolicy.Fail, checksum, content.Length), null, CancellationToken.None);

        Assert.Equal(content.Length, meta.SizeBytes);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameNamedFiles_AreIsolatedByStorageKeyAndConnection(bool differentConnection)
    {
        var handler = new FakeGoogleDriveHandler();
        using var client = new HttpClient(handler);
        var first = new GoogleDriveStorageProvider(client,
            new GoogleDriveStorageOptions("client_id", "client_secret", _tenantId, _connectionId,
                folderId: "folder-123", stagingDirectory: _tempDir),
            _ => ValueTask.FromResult(new GoogleDriveCredential("refresh_token")));
        var second = new GoogleDriveStorageProvider(client,
            new GoogleDriveStorageOptions("client_id", "client_secret", _tenantId, differentConnection ? Guid.NewGuid() : _connectionId,
                folderId: "folder-123", stagingDirectory: _tempDir),
            _ => ValueTask.FromResult(new GoogleDriveCredential("refresh_token")));
        var firstKey = new StorageObjectKey($"{_tenantId:N}/job1/run1/attempt1/backup.bak");
        var secondKey = differentConnection ? firstKey : new StorageObjectKey($"{_tenantId:N}/job1/run2/attempt2/backup.bak");
        byte[] firstContent = [1, 2, 3, 4];
        byte[] secondContent = [4, 3, 2, 1];
        var firstChecksum = ContentChecksum.Sha256(SHA256.HashData(firstContent));
        var secondChecksum = ContentChecksum.Sha256(SHA256.HashData(secondContent));

        using var firstStream = new MemoryStream(firstContent);
        await first.PutAsync(firstKey, firstStream, new StoragePutOptions(StorageOverwritePolicy.Fail, firstChecksum, firstContent.Length), null, CancellationToken.None);

        Assert.Null(await second.GetMetadataAsync(secondKey, CancellationToken.None));
        Assert.False(await second.DeleteAsync(secondKey, CancellationToken.None));
        Assert.True(await first.ExistsAsync(firstKey, CancellationToken.None));

        using var secondStream = new MemoryStream(secondContent);
        await second.PutAsync(secondKey, secondStream, new StoragePutOptions(StorageOverwritePolicy.Fail, secondChecksum, secondContent.Length), null, CancellationToken.None);
        Assert.Equal(StorageVerificationStatus.Verified,
            (await second.VerifyAsync(secondKey, new StorageVerificationExpectation(secondContent.Length, secondChecksum), CancellationToken.None)).Status);
        Assert.True(await second.DeleteAsync(secondKey, CancellationToken.None));
        Assert.Equal(StorageVerificationStatus.Verified,
            (await first.VerifyAsync(firstKey, new StorageVerificationExpectation(firstContent.Length, firstChecksum), CancellationToken.None)).Status);
    }

    [Fact]
    public async Task PutAsync_StopsReadingWhenStreamExceedsExpectedLength()
    {
        var handler = new FakeGoogleDriveHandler();
        using var client = new HttpClient(handler);
        var provider = new GoogleDriveStorageProvider(client,
            new GoogleDriveStorageOptions("client_id", "client_secret", _tenantId, _connectionId,
                folderId: "folder-123", stagingDirectory: _tempDir),
            _ => ValueTask.FromResult(new GoogleDriveCredential("refresh_token")));
        using var stream = new MemoryStream(new byte[128 * 1024]);
        var checksum = ContentChecksum.Sha256(SHA256.HashData([0]));

        var failure = await Assert.ThrowsAsync<StorageException>(() => provider.PutAsync(
            new StorageObjectKey($"{_tenantId:N}/job1/run1/attempt1/oversized.bak"), stream,
            new StoragePutOptions(StorageOverwritePolicy.Fail, checksum, 1), null, CancellationToken.None));

        Assert.Equal(StorageFailureKind.IntegrityMismatch, failure.Kind);
        Assert.True(stream.Position < stream.Length);
        Assert.Equal(0, handler.SessionCount);
        Assert.Empty(Directory.EnumerateFiles(_tempDir));
    }

    [Fact]
    public async Task PutAsync_RejectsLengthThatOverflowsStagingCapacityAddition()
    {
        using var client = new HttpClient(new FakeGoogleDriveHandler());
        var provider = new GoogleDriveStorageProvider(client,
            new GoogleDriveStorageOptions("client_id", "client_secret", _tenantId, _connectionId,
                folderId: "folder-123", stagingDirectory: _tempDir),
            _ => ValueTask.FromResult(new GoogleDriveCredential("refresh_token")));
        using var stream = new MemoryStream([0]);
        var checksum = ContentChecksum.Sha256(SHA256.HashData([0]));

        var failure = await Assert.ThrowsAsync<StorageException>(() => provider.PutAsync(
            new StorageObjectKey($"{_tenantId:N}/job1/run1/attempt1/oversized.bak"), stream,
            new StoragePutOptions(StorageOverwritePolicy.Fail, checksum, long.MaxValue), null, CancellationToken.None));

        Assert.Equal(StorageFailureKind.InsufficientSpace, failure.Kind);
        Assert.Equal(0, stream.Position);
        Assert.Empty(Directory.EnumerateFiles(_tempDir));
    }

    [Fact]
    public async Task PutAsync_StagesBackupWithOwnerOnlyUnixPermissions()
    {
        if (OperatingSystem.IsWindows()) return;
        var observedStaging = false;
        var handler = new FakeGoogleDriveHandler
        {
            OnSessionInitiated = () =>
            {
                if (OperatingSystem.IsWindows()) return;
                var stagedFile = Assert.Single(Directory.EnumerateFiles(_tempDir));
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(stagedFile));
                observedStaging = true;
            }
        };
        using var client = new HttpClient(handler);
        var provider = new GoogleDriveStorageProvider(client,
            new GoogleDriveStorageOptions("client_id", "client_secret", _tenantId, _connectionId,
                folderId: "folder-123", stagingDirectory: _tempDir),
            _ => ValueTask.FromResult(new GoogleDriveCredential("refresh_token")));
        byte[] content = [1, 2, 3, 4];
        using var stream = new MemoryStream(content);
        await provider.PutAsync(new StorageObjectKey($"{_tenantId:N}/job1/run1/attempt1/private.bak"), stream,
            new StoragePutOptions(StorageOverwritePolicy.Fail, ContentChecksum.Sha256(SHA256.HashData(content)), content.Length), null, CancellationToken.None);

        Assert.True(observedStaging);
        Assert.Empty(Directory.EnumerateFiles(_tempDir));
    }

    [Fact]
    public async Task GetMetadataAsync_DetectsDrift_WhenFileDeletedInDrive()
    {
        var fakeHandler = new FakeGoogleDriveHandler();
        var client = new HttpClient(fakeHandler);
        var options = new GoogleDriveStorageOptions(
            "client_id", "client_secret", _tenantId, _connectionId,
            folderId: "folder-123", stagingDirectory: _tempDir, chunkSizeBytes: 256 * 1024);

        var provider = new GoogleDriveStorageProvider(
            client, options, _ => ValueTask.FromResult(new GoogleDriveCredential("refresh_token")));

        var content = RandomNumberGenerator.GetBytes(256 * 1024);
        var checksum = ContentChecksum.Sha256(SHA256.HashData(content));
        var key = new StorageObjectKey($"{_tenantId:N}/job1/run1/attempt1/drift.bak");

        using (var s = new MemoryStream(content))
        {
            await provider.PutAsync(key, s, new StoragePutOptions(StorageOverwritePolicy.Fail, checksum, content.Length), null, CancellationToken.None);
        }

        Assert.True(await provider.ExistsAsync(key, CancellationToken.None));

        // Simulate external deletion in Google Drive
        fakeHandler.SimulateExternalDelete();

        // Drift detected: provider reports null / false
        Assert.Null(await provider.GetMetadataAsync(key, CancellationToken.None));
        Assert.False(await provider.ExistsAsync(key, CancellationToken.None));
    }

    [Fact]
    public async Task DeleteAsync_ThrowsAuthenticationFailed_WhenTokenRevoked()
    {
        var fakeHandler = new FakeGoogleDriveHandler { RevokeTokens = true };
        var client = new HttpClient(fakeHandler);
        var options = new GoogleDriveStorageOptions(
            "client_id", "client_secret", _tenantId, _connectionId,
            folderId: "folder-123", stagingDirectory: _tempDir, chunkSizeBytes: 256 * 1024);

        var provider = new GoogleDriveStorageProvider(
            client, options, _ => ValueTask.FromResult(new GoogleDriveCredential("revoked_token")));

        var key = new StorageObjectKey($"{_tenantId:N}/job1/run1/attempt1/revoked.bak");

        var ex = await Assert.ThrowsAsync<StorageException>(() =>
            provider.DeleteAsync(key, CancellationToken.None));

        Assert.Equal(StorageFailureKind.AuthenticationFailed, ex.Kind);
    }

    [Fact]
    public async Task TokenManager_RotatesRefreshToken_CallsCallback()
    {
        var fakeHandler = new FakeGoogleDriveHandler { EmitNewRefreshToken = "new_rotated_refresh_token" };
        var client = new HttpClient(fakeHandler);
        var options = new GoogleDriveStorageOptions(
            "client_id", "client_secret", _tenantId, _connectionId,
            folderId: "folder-123", stagingDirectory: _tempDir);

        string? rotatedToken = null;
        var tokenManager = new GoogleDriveTokenManager(
            client, options,
            _ => ValueTask.FromResult(new GoogleDriveCredential("initial_refresh_token")),
            (newToken, _) => { rotatedToken = newToken; return Task.CompletedTask; });

        var token = await tokenManager.GetAccessTokenAsync(CancellationToken.None);
        Assert.Equal("fake_access_token", token);
        Assert.Equal("new_rotated_refresh_token", rotatedToken);
    }

    private sealed class FakeGoogleDriveHandler : HttpMessageHandler
    {
        public bool ExpireFirstSession { get; set; }
        public bool RevokeTokens { get; set; }
        public string? EmitNewRefreshToken { get; set; }
        public Action? OnSessionInitiated { get; set; }
        public bool DenyUploads { get; set; }

        private readonly Dictionary<string, byte[]> _files = [];
        private readonly Dictionary<string, (string FileName, long TotalSize, MemoryStream Stream, Dictionary<string, string> Properties, string? ExistingFileId)> _sessions = [];
        private readonly Dictionary<string, string> _fileNames = [];
        private readonly Dictionary<string, Dictionary<string, string>> _fileProperties = [];
        private int _sessionCounter;
        public int SessionCount => _sessionCounter;
        // Real Drive only returns id/name/mimeType from files.create unless the session asked for more via ?fields=.
        private bool _sizeRequested;

        public void SimulateExternalDelete()
        {
            _files.Clear();
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri?.ToString() ?? "";

            // 1. Token endpoint
            if (uri.Contains("oauth2.googleapis.com/token"))
            {
                if (RevokeTokens)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent("{\"error\":\"invalid_grant\",\"error_description\":\"Token has been expired or revoked.\"}", Encoding.UTF8, "application/json")
                    });
                }

                var resp = new
                {
                    access_token = "fake_access_token",
                    refresh_token = EmitNewRefreshToken,
                    expires_in = 3600,
                    token_type = "Bearer"
                };
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(resp)
                });
            }

            // 2. Resumable upload session initiation
            if (uri.Contains("upload/drive/v3/files") && uri.Contains("uploadType=resumable")
                && (request.Method == HttpMethod.Post || request.Method == HttpMethod.Patch))
            {
                OnSessionInitiated?.Invoke();
                // Real Drive limit: each appProperties key+value is at most 124 bytes.
                using var metadata = JsonDocument.Parse(request.Content!.ReadAsStringAsync(cancellationToken).Result);
                var props = metadata.RootElement.GetProperty("appProperties");
                if (props.EnumerateObject().Any(p => Encoding.UTF8.GetByteCount(p.Name) + Encoding.UTF8.GetByteCount(p.Value.GetString()!) > 124))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
                }
                _sizeRequested = request.RequestUri!.Query.Contains("fields=") && request.RequestUri.Query.Contains("size");
                var sessionId = $"session-{++_sessionCounter}";
                var totalLength = long.Parse(request.Headers.GetValues("X-Upload-Content-Length").First());
                _sessions[sessionId] = (metadata.RootElement.GetProperty("name").GetString()!, totalLength, new MemoryStream(),
                    props.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!),
                    request.Method == HttpMethod.Patch ? request.RequestUri!.AbsolutePath.Split('/').Last() : null);

                var response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Headers.Location = new Uri($"https://fake-upload.googleapis.com/{sessionId}");
                return Task.FromResult(response);
            }

            // 3. Resumable upload chunk PUT
            if (uri.StartsWith("https://fake-upload.googleapis.com/session-") && request.Method == HttpMethod.Put)
            {
                if (DenyUploads) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
                var sessionId = uri.Split('/').Last();
                if (ExpireFirstSession && sessionId == "session-1")
                {
                    ExpireFirstSession = false; // expire once
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                }

                if (!_sessions.TryGetValue(sessionId, out var session))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                }

                var contentRange = request.Content?.Headers.GetValues("Content-Range").FirstOrDefault() ?? "";

                // Query offset check (Content-Length: 0)
                if (request.Content?.Headers.ContentLength == 0)
                {
                    var response = new HttpResponseMessage((HttpStatusCode)308);
                    response.Headers.Add("Range", $"bytes=0-{Math.Max(0, session.Stream.Length - 1)}");
                    return Task.FromResult(response);
                }

                var chunkBytes = request.Content?.ReadAsByteArrayAsync(cancellationToken).Result ?? [];
                session.Stream.Write(chunkBytes);

                if (session.Stream.Length >= session.TotalSize)
                {
                    var fileId = session.ExistingFileId ?? $"file-{sessionId}";
                    _files[fileId] = session.Stream.ToArray();
                    _fileNames[fileId] = session.FileName;
                    _fileProperties[fileId] = session.Properties;
                    var fileResp = new
                    {
                        id = fileId,
                        name = session.FileName,
                        size = _sizeRequested ? session.TotalSize : (long?)null,
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

            // 4. Drive v3 search files
            if (uri.Contains("/drive/v3/files?q=") && request.Method == HttpMethod.Get)
            {
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

            // 5. Drive v3 get file by ID
            if (uri.Contains("/drive/v3/files/") && !uri.Contains("?alt=media") && request.Method == HttpMethod.Get)
            {
                var fileId = uri.Split('?')[0].Split('/').Last();
                if (_files.TryGetValue(fileId, out var data))
                {
                    var item = new
                    {
                        id = fileId,
                        name = _fileNames[fileId],
                        size = (long)data.Length,
                        md5Checksum = "fake_md5",
                        trashed = false,
                        modifiedTime = DateTimeOffset.UtcNow,
                        appProperties = _fileProperties[fileId]
                    };
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(item)
                    });
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            // 6. Drive v3 download (alt=media)
            if (uri.Contains("?alt=media") && request.Method == HttpMethod.Get)
            {
                var fileId = uri.Split('?')[0].Split('/').Last();
                if (_files.TryGetValue(fileId, out var data))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(data)
                    });
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            // 7. Drive v3 delete
            if (uri.Contains("/drive/v3/files/") && request.Method == HttpMethod.Delete)
            {
                var fileId = uri.Split('/').Last();
                if (_files.Remove(fileId))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            // 8. Folder search or create
            if (uri.Contains("/drive/v3/files") && request.Method == HttpMethod.Post)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { id = "folder-123" })
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
