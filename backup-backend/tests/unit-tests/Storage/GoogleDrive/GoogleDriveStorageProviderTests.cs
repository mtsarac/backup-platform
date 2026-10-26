using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using BackupPlatform.StorageProviders.Abstractions;
using BackupPlatform.StorageProviders.GoogleDrive;
using Xunit;

namespace BackupPlatform.UnitTests.Storage.GoogleDrive;

public sealed class GoogleDriveStorageProviderTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("drive-provider-").FullName;
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _connection = Guid.NewGuid();
    private readonly StorageObjectKey _key = new("tenant/job/run/attempt/backup.bak");
    private static readonly byte[] Bytes = [1, 2, 3, 4];
    private static ContentChecksum Digest(byte[] bytes) => ContentChecksum.Sha256(SHA256.HashData(bytes));
    public void Dispose() => Directory.Delete(_directory, true);

    private GoogleDriveStorageOptions Options(string? folder = "folder-123", int attempts = 3, TimeSpan? budget = null) =>
        new("client", "secret", _tenant, _connection, folderId: folder, stagingDirectory: _directory,
            chunkSizeBytes: 256 * 1024, maxRetryAttempts: attempts, maxRetryElapsed: budget,
            retryBaseDelay: TimeSpan.Zero, maxRetryDelay: TimeSpan.Zero);
    private GoogleDriveStorageProvider Provider(FakeDrive handler, IGoogleDriveFileCatalog? catalog = null,
        GoogleDriveStorageOptions? options = null, Guid? connection = null) =>
        new(new HttpClient(handler), options ?? (connection is null ? Options() :
                new("client", "secret", _tenant, connection.Value, "folder-123")),
            _ => ValueTask.FromResult(new GoogleDriveCredential("refresh")), catalog,
            stagingStore: new TestStagingStore(_directory));
    private Task<StorageObjectMetadata> Put(GoogleDriveStorageProvider provider, byte[]? bytes = null,
        StorageOverwritePolicy policy = StorageOverwritePolicy.Fail, CancellationToken token = default)
    {
        bytes ??= Bytes;
        return provider.PutAsync(_key, new MemoryStream(bytes), new(policy, Digest(bytes), bytes.Length), null, token);
    }

    [Fact]
    public async Task Put_ReadsBackBeforePublishing_AndSupportsReadDelete()
    {
        var server = new FakeDrive();
        var catalog = new InMemoryGoogleDriveFileCatalog();
        var provider = Provider(server, catalog);
        var bytes = RandomNumberGenerator.GetBytes(512 * 1024);
        var result = await Put(provider, bytes);
        Assert.Equal(bytes.Length, result.SizeBytes);
        Assert.True(server.Readbacks > 0);
        await using var read = await provider.OpenReadAsync(_key, default);
        using var output = new MemoryStream();
        await read.CopyToAsync(output);
        Assert.Equal(bytes, output.ToArray());
        Assert.Equal(StorageVerificationStatus.Verified,
            (await provider.VerifyAsync(_key, new(bytes.Length, Digest(bytes)), default)).Status);
        Assert.True(await provider.DeleteAsync(_key, default));
        Assert.False(await provider.ExistsAsync(_key, default));
        Assert.Empty(Directory.EnumerateFiles(_directory));
    }

    [Fact]
    public async Task Put_FailsClosedWithoutStaging()
    {
        var server = new FakeDrive();
        var provider = new GoogleDriveStorageProvider(new HttpClient(server), Options(),
            _ => ValueTask.FromResult(new GoogleDriveCredential("refresh")));
        var failure = await Assert.ThrowsAsync<StorageException>(() => Put(provider));
        Assert.Equal(StorageFailureKind.InvalidConfiguration, failure.Kind);
        Assert.Equal(0, server.SessionsCreated);
    }

    [Fact]
    public async Task Put_RestartsExpiredSession_WithoutDuplicateObjects()
    {
        var server = new FakeDrive { Expirations = 1 };
        await Put(Provider(server));
        Assert.Equal(2, server.SessionsCreated);
        Assert.Equal(Bytes, Assert.Single(server.Files).Value.Bytes);
        Assert.Equal(1, server.IdsGenerated);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Put_LostCompletionResponse_RecoversWithoutDuplicateObjects(bool expireSession)
    {
        var server = new FakeDrive { LoseCompletionReply = true, ExpireCompletedSession = expireSession };
        var catalog = new InMemoryGoogleDriveFileCatalog();
        var result = await Put(Provider(server, catalog));
        Assert.Equal(Bytes.Length, result.SizeBytes);
        Assert.Equal(Bytes, Assert.Single(server.Files).Value.Bytes);
        Assert.Equal(1, server.IdsGenerated);
        Assert.Equal(1, server.SessionsCreated);
        server.RejectListing = true;
        await Put(Provider(server, catalog));
        Assert.Single(server.Files);
        Assert.Equal(1, server.SessionsCreated);
    }

    [Fact]
    public async Task Put_InterruptedInitiation_PersistsLocatorForRestartCleanup()
    {
        var server = new FakeDrive { LoseInitiationReply = true };
        var catalog = new InMemoryGoogleDriveFileCatalog();
        var reachedInitiation = false;
        server.BeforeInitiation = async token =>
        {
            reachedInitiation = true;
            var locator = await catalog.FindAsync(_tenant, _connection, _key.Value, token);
            Assert.NotNull(locator);
            Assert.Null(locator.Checksum);
        };
        var failure = await Assert.ThrowsAsync<StorageException>(() => Put(Provider(server, catalog)));
        Assert.True(reachedInitiation);
        Assert.True(failure.ObjectMayRemain);
        Assert.Equal(_key, failure.RemainingObjectKey);
        Assert.Single(server.Files);
        server.RejectReadback = true;
        server.RejectListing = true;
        var restarted = Provider(server, catalog);
        Assert.True(await restarted.DeleteAsync(_key, default));
        Assert.False(await restarted.DeleteAsync(_key, default));
        Assert.Empty(server.Files);
        Assert.Empty(Directory.EnumerateFiles(_directory));
    }

    [Fact]
    public async Task Put_CancelledAfterRemoteCompletion_CleansUpByPersistedIdentity()
    {
        using var cancellation = new CancellationTokenSource();
        var server = new FakeDrive { LoseCompletionReply = true, OnOffsetQuery = cancellation.Cancel };
        var catalog = new InMemoryGoogleDriveFileCatalog();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Put(Provider(server, catalog), token: cancellation.Token));
        var retained = Assert.IsType<StorageException>(exception.Data[nameof(StorageException)]);
        Assert.True(retained.ObjectMayRemain);
        Assert.Single(server.Files);
        server.RejectReadback = true;
        server.RejectListing = true;
        Assert.True(await Provider(server, catalog).DeleteAsync(retained.RemainingObjectKey!, default));
        Assert.Empty(server.Files);
        Assert.Empty(Directory.EnumerateFiles(_directory));
    }

    [Fact]
    public async Task Put_RepeatedSessionExpiryStops()
    {
        var server = new FakeDrive { Expirations = int.MaxValue };
        var failure = await Assert.ThrowsAsync<StorageException>(() => Put(Provider(server)));
        Assert.Equal(StorageFailureKind.NetworkUnavailable, failure.Kind);
        Assert.Equal(3, server.SessionsCreated);
        Assert.Empty(Directory.EnumerateFiles(_directory));
        Assert.Empty(server.Files);
        Assert.Equal(1, server.IdsGenerated);
    }

    [Theory]
    [InlineData(null, StorageFailureKind.NetworkUnavailable)]
    [InlineData("bytes=0-9223372036854775807", StorageFailureKind.Io)]
    [InlineData("bytes=1-2", StorageFailureKind.Io)]
    [InlineData("bytes=0--1", StorageFailureKind.Io)]
    [InlineData("bytes=0-4", StorageFailureKind.Io)]
    public async Task Put_NoProgressOrMalformed308Stops(string? range, StorageFailureKind expected)
    {
        var server = new FakeDrive { IncompleteWithoutProgress = true, Range = range };
        var failure = await Assert.ThrowsAsync<StorageException>(() => Put(Provider(server)));
        Assert.Equal(expected, failure.Kind);
        Assert.InRange(server.ChunkRequests, 1, 3);
    }

    [Fact]
    public async Task Put_ConfirmedOffsetAfterNetworkFailureResumes()
    {
        var server = new FakeDrive { LoseChunkReply = true };
        var bytes = RandomNumberGenerator.GetBytes(512 * 1024);
        await Put(Provider(server), bytes);
        Assert.Equal(1, server.OffsetQueries);
        Assert.Equal(bytes, Assert.Single(server.Files).Value.Bytes);
    }

    private Task<StorageObjectMetadata> PutCheckpointed(GoogleDriveStorageProvider provider, byte[] bytes, List<StorageTransferCheckpoint> recorded,
        StorageTransferCheckpoint? retained = null) =>
        provider.PutAsync(_key, new MemoryStream(bytes), new(StorageOverwritePolicy.Fail, Digest(bytes), bytes.Length)
        {
            Checkpoint = retained,
            RecordCheckpointAsync = (checkpoint, _) => { recorded.Add(checkpoint); return Task.CompletedTask; }
        }, null, default);

    [Fact]
    public async Task Resume_ContinuesRetainedSessionFromTheServerReportedOffset()
    {
        var server = new FakeDrive { LoseChunkReply = true };
        var bytes = RandomNumberGenerator.GetBytes(512 * 1024);
        var recorded = new List<StorageTransferCheckpoint>();
        var dropped = await Assert.ThrowsAsync<StorageException>(() => PutCheckpointed(Provider(server), bytes, recorded));
        Assert.Equal(StorageFailureKind.NetworkUnavailable, dropped.Kind);
        Assert.Equal(1, server.ChunkRequests);

        await PutCheckpointed(Provider(server), bytes, recorded, recorded[^1]);

        Assert.Equal(1, server.SessionsCreated);
        Assert.Equal(1, server.IdsGenerated);
        Assert.Equal(1, server.OffsetQueries);
        // One chunk was lost with its reply and one remained: nothing already stored was sent again.
        Assert.Equal(2, server.ChunkRequests);
        Assert.Equal(bytes, Assert.Single(server.Files).Value.Bytes);
        Assert.Equal(bytes.Length, recorded[^1].VerifiedByteOffset);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData((HttpStatusCode)429)]
    public async Task CheckpointedPut_LeavesRetryingAndPausingToTheCaller(HttpStatusCode status)
    {
        var server = new FakeDrive { ChunkStatus = status };
        var failure = await Assert.ThrowsAsync<StorageException>(() => PutCheckpointed(Provider(server), Bytes, []));
        Assert.Equal(StorageFailureKind.NetworkUnavailable, failure.Kind);
        Assert.Equal(1, server.ChunkRequests);
    }

    [Fact]
    public async Task Resume_AfterSessionExpiryStartsAFreshSessionAndCheckpointsIt()
    {
        var server = new FakeDrive { LoseChunkReply = true };
        var bytes = RandomNumberGenerator.GetBytes(512 * 1024);
        var recorded = new List<StorageTransferCheckpoint>();
        await Assert.ThrowsAsync<StorageException>(() => PutCheckpointed(Provider(server), bytes, recorded));
        server.Expirations = 1;

        await PutCheckpointed(Provider(server), bytes, recorded, recorded[^1]);

        Assert.Equal(2, server.SessionsCreated);
        Assert.Equal(1, server.IdsGenerated);
        Assert.Contains(recorded, checkpoint => checkpoint.VerifiedByteOffset == 0 && checkpoint.ProviderState!.Contains("session-2"));
        Assert.Equal(bytes, Assert.Single(server.Files).Value.Bytes);
    }

    [Theory]
    [InlineData("https://evil.example/upload/session-1")]
    [InlineData("http://upload.googleapis.com/session-1")]
    [InlineData("https://user@upload.googleapis.com/session-1")]
    public async Task Resume_RejectsARetainedSessionThatIsNotAGoogleUploadUri(string session)
    {
        var server = new FakeDrive();
        var state = JsonSerializer.Serialize(new { Session = session, FileId = "generated-1" });
        var failure = await Assert.ThrowsAsync<StorageException>(() =>
            PutCheckpointed(Provider(server), Bytes, [], new StorageTransferCheckpoint(0, state)));
        Assert.Equal(StorageFailureKind.InvalidConfiguration, failure.Kind);
        Assert.Equal(0, server.ChunkRequests);
        Assert.Equal(0, server.OffsetQueries);
    }

    [Fact]
    public async Task Resume_DetectsPrefixThatDoesNotMatchTheArtifactBeforePublishing()
    {
        var server = new FakeDrive { LoseChunkReply = true };
        var bytes = RandomNumberGenerator.GetBytes(512 * 1024);
        var recorded = new List<StorageTransferCheckpoint>();
        await Assert.ThrowsAsync<StorageException>(() => PutCheckpointed(Provider(server), bytes, recorded));
        server.CorruptNextUpload = true;

        var failure = await Assert.ThrowsAsync<StorageException>(() => PutCheckpointed(Provider(server), bytes, recorded, recorded[^1]));

        Assert.Equal(StorageFailureKind.IntegrityMismatch, failure.Kind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Put_QueryCancellationIsNotSwallowed(bool completion)
    {
        using var cancelled = new CancellationTokenSource();
        var server = new FakeDrive { LoseChunkReply = !completion, CompleteAs308 = completion,
            OnOffsetQuery = () => cancelled.Cancel() };
        var bytes = completion ? Bytes : RandomNumberGenerator.GetBytes(512 * 1024);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Put(Provider(server), bytes, token: cancelled.Token));
        Assert.Equal(1, server.OffsetQueries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Put_RetryAfterDeltaOrDateExceedingBudgetStops(bool date)
    {
        var server = new FakeDrive { ChunkStatus = (HttpStatusCode)429,
            RetryAfter = date ? new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddHours(1))
                : new RetryConditionHeaderValue(TimeSpan.FromHours(1)) };
        var failure = await Assert.ThrowsAsync<StorageException>(() => Put(Provider(server)));
        Assert.Equal(StorageFailureKind.NetworkUnavailable, failure.Kind);
        Assert.Equal(1, server.ChunkRequests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, null, StorageFailureKind.AuthenticationFailed)]
    [InlineData(HttpStatusCode.Forbidden, "insufficientFilePermissions", StorageFailureKind.AccessDenied)]
    [InlineData(HttpStatusCode.Forbidden, "storageQuotaExceeded", StorageFailureKind.InsufficientSpace)]
    [InlineData(HttpStatusCode.Forbidden, "rateLimitExceeded", StorageFailureKind.NetworkUnavailable)]
    [InlineData(HttpStatusCode.Forbidden, "userRateLimitExceeded", StorageFailureKind.NetworkUnavailable)]
    [InlineData(HttpStatusCode.Conflict, null, StorageFailureKind.AlreadyExists)]
    public async Task Put_ClassifiesFailuresWithoutRawResponse(HttpStatusCode status, string? reason, StorageFailureKind kind)
    {
        var server = new FakeDrive { ChunkStatus = status, FailureReason = reason };
        var failure = await Assert.ThrowsAsync<StorageException>(() => Put(Provider(server)));
        Assert.Equal(kind, failure.Kind);
        Assert.DoesNotContain("refresh", failure.Message);
        Assert.DoesNotContain("https://", failure.Message);
        Assert.DoesNotContain("secret-response", failure.Message);
    }

    [Fact]
    public async Task Put_CatalogSaveFailureRecoversVerifiedObjectAfterRestart()
    {
        var server = new FakeDrive();
        var catalog = new FailingCatalog();
        var failure = await Assert.ThrowsAsync<StorageException>(() => Put(Provider(server, catalog)));
        Assert.True(failure.ObjectMayRemain);
        Assert.Equal(_key, failure.RemainingObjectKey);
        Assert.Single(server.Files);
        Assert.Empty(Directory.EnumerateFiles(_directory));
        var restarted = Provider(server, catalog);
        await Put(restarted);
        Assert.Equal(1, server.SessionsCreated);
        Assert.NotNull(await catalog.FindAsync(_tenant, _connection, _key.Value, default));
    }

    [Fact]
    public async Task Put_RestartIdempotencyUsesVerifiedOwnedObject()
    {
        var server = new FakeDrive();
        await Put(Provider(server));
        await Put(Provider(server));
        Assert.Equal(1, server.SessionsCreated);
        Assert.True(server.Readbacks >= 2);
    }

    [Fact]
    public async Task Put_ReplacementCreatesNewObject_AndPreservesOldBytes()
    {
        var server = new FakeDrive();
        var catalog = new InMemoryGoogleDriveFileCatalog();
        var provider = Provider(server, catalog);
        await Put(provider);
        var original = (await catalog.FindAsync(_tenant, _connection, _key.Value, default))!;
        byte[] replacement = [4, 3, 2, 1, 0];
        await Put(provider, replacement, StorageOverwritePolicy.Replace);
        var updated = (await catalog.FindAsync(_tenant, _connection, _key.Value, default))!;
        Assert.NotEqual(original.FileId, updated.FileId);
        Assert.Equal(Bytes, server.Files[original.FileId].Bytes);
        Assert.Equal(replacement, server.Files[updated.FileId].Bytes);
        Assert.Equal(0, server.PatchRequests);
        var report = await provider.ReconcileAsync(default);
        Assert.Contains(report.Findings, f => f.Kind == "unreferenced_candidate" && f.FileId == original.FileId);
        Assert.Equal(2, server.Files.Count);
    }

    [Fact]
    public async Task Put_ReplacementReadbackMismatchKeepsGoodCatalogAndBytes()
    {
        var server = new FakeDrive();
        var catalog = new InMemoryGoogleDriveFileCatalog();
        var provider = Provider(server, catalog);
        await Put(provider);
        var original = (await catalog.FindAsync(_tenant, _connection, _key.Value, default))!;
        server.CorruptNextUpload = true;
        var failure = await Assert.ThrowsAsync<StorageException>(() => Put(provider, [5, 6, 7, 8], StorageOverwritePolicy.Replace));
        Assert.Equal(StorageFailureKind.IntegrityMismatch, failure.Kind);
        Assert.Equal(original.FileId, (await catalog.FindAsync(_tenant, _connection, _key.Value, default))!.FileId);
        Assert.Equal(StorageVerificationStatus.Verified,
            (await provider.VerifyAsync(_key, new(Bytes.Length, Digest(Bytes)), default)).Status);
        Assert.Equal(0, server.DeleteRequests);
    }

    [Fact]
    public async Task Put_ReplacementCatalogFailureRetriesMatchingNewCandidate()
    {
        var server = new FakeDrive();
        var catalog = new FailingCatalog { FailNextSave = false };
        await Put(Provider(server, catalog));
        catalog.FailNextSave = true;
        byte[] replacement = [7, 8, 9];
        await Assert.ThrowsAsync<IOException>(() => Put(Provider(server, catalog), replacement, StorageOverwritePolicy.Replace));
        await Put(Provider(server, catalog), replacement, StorageOverwritePolicy.Replace);
        Assert.Equal(2, server.SessionsCreated);
        Assert.Equal(replacement, server.Files[(await catalog.FindAsync(_tenant, _connection, _key.Value, default))!.FileId].Bytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CatalogOwnershipConflictBlocksReadAndDelete(bool delete)
    {
        var server = new FakeDrive();
        var catalog = new InMemoryGoogleDriveFileCatalog();
        var provider = Provider(server, catalog);
        await Put(provider);
        var file = Assert.Single(server.Files).Value;
        file.Properties["tenantId"] = Guid.NewGuid().ToString("N");
        var failure = await Assert.ThrowsAsync<StorageException>(async () =>
        {
            if (delete) await provider.DeleteAsync(_key, default);
            else await provider.OpenReadAsync(_key, default);
        });
        Assert.Equal(StorageFailureKind.AccessDenied, failure.Kind);
        Assert.Equal(0, server.DeleteRequests);
        Assert.Contains(catalog.Findings, f => f.Kind == "ownership_conflict");
    }

    [Fact]
    public async Task SameNameIsNotIdentity_ConnectionAndKeyIsolation()
    {
        var server = new FakeDrive();
        var first = Provider(server);
        await Put(first);
        var foreign = Provider(server, connection: Guid.NewGuid());
        Assert.Null(await foreign.GetMetadataAsync(_key, default));
        Assert.False(await foreign.DeleteAsync(_key, default));
        Assert.Null(await first.GetMetadataAsync(new("tenant/job/run2/attempt/backup.bak"), default));
        Assert.Single(server.Files);
    }

    [Fact]
    public async Task Reconcile_PaginatesDetectsDriftAndOrphans_WithoutMutation()
    {
        var server = new FakeDrive { PageSize = 1 };
        var catalog = new InMemoryGoogleDriveFileCatalog();
        var provider = Provider(server, catalog);
        await Put(provider);
        var original = (await catalog.FindAsync(_tenant, _connection, _key.Value, default))!;
        await Put(provider, [2, 3, 4], StorageOverwritePolicy.Replace);
        var published = (await catalog.FindAsync(_tenant, _connection, _key.Value, default))!;
        server.Files.Remove(published.FileId);
        server.Duplicate(original.FileId);
        var report = await provider.ReconcileAsync(default);
        Assert.True(report.DryRun);
        Assert.Equal(2, report.DriveCount);
        Assert.Contains(report.Findings, f => f.Kind == "missing" && f.FileId == published.FileId);
        Assert.Equal(2, report.Findings.Count(f => f.Kind == "duplicate"));
        Assert.Equal(2, report.Findings.Count(f => f.Kind == "unreferenced_candidate"));
        Assert.NotNull(await catalog.FindAsync(_tenant, _connection, _key.Value, default));
        Assert.Contains(catalog.Findings, f => f.Kind == "missing");
        Assert.Equal(0, server.DeleteRequests);
    }

    [Fact]
    public async Task ConcurrentFolderCreationConvergesWithoutDeletingFolders()
    {
        var server = new FakeDrive { GateRootSearch = true };
        var first = Provider(server, options: Options(folder: null));
        var second = Provider(server, options: Options(folder: null));
        await Task.WhenAll(Put(first), Put(second));
        Assert.True(server.Folders.Count >= 3);
        Assert.Equal(0, server.DeleteRequests);
        Assert.All(server.Files.Values, f => Assert.True(f.Properties.ContainsKey("storageKeyHash")));
        await Put(Provider(server, options: Options(folder: null)));
        Assert.Equal(2, server.SessionsCreated);
    }

    [Fact]
    public async Task Put_ReadbackMismatchRetainsCleanupLocator_WithoutPublishingVerifiedChecksum()
    {
        var server = new FakeDrive { CorruptNextUpload = true };
        var catalog = new InMemoryGoogleDriveFileCatalog();
        var provider = Provider(server, catalog);
        var failure = await Assert.ThrowsAsync<StorageException>(() => Put(provider));
        Assert.Equal(StorageFailureKind.IntegrityMismatch, failure.Kind);
        Assert.True(failure.ObjectMayRemain);
        Assert.Equal(_key, failure.RemainingObjectKey);
        var retained = await catalog.FindAsync(_tenant, _connection, _key.Value, default);
        Assert.NotNull(retained);
        Assert.Null(retained.Checksum);
        var readbacks = server.Readbacks;
        Assert.True(await provider.DeleteAsync(failure.RemainingObjectKey!, default));
        Assert.Equal(readbacks, server.Readbacks);
        Assert.False(await provider.DeleteAsync(_key, default));
        Assert.Empty(server.Files);
    }

    [Fact]
    public async Task Delete_UncatalogedCorruptOwnedObject_DoesNotRequireVerification()
    {
        var server = new FakeDrive { CorruptNextUpload = true };
        await Assert.ThrowsAsync<StorageException>(() => Put(Provider(server)));
        var restarted = Provider(server);
        var readbacks = server.Readbacks;
        Assert.True(await restarted.DeleteAsync(_key, default));
        Assert.Equal(readbacks, server.Readbacks);
        Assert.Empty(server.Files);
    }

    [Fact]
    public async Task Put_CancelledReadback_PreservesCancellationAndCleanupLocator()
    {
        using var cancellation = new CancellationTokenSource();
        var server = new FakeDrive { OnReadback = cancellation.Cancel };
        var catalog = new InMemoryGoogleDriveFileCatalog();
        var provider = Provider(server, catalog);
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Put(provider, token: cancellation.Token));
        var retained = Assert.IsType<StorageException>(exception.Data[nameof(StorageException)]);
        Assert.True(retained.ObjectMayRemain);
        Assert.Equal(_key, retained.RemainingObjectKey);
        Assert.Null((await catalog.FindAsync(_tenant, _connection, _key.Value, default))!.Checksum);
        Assert.True(await provider.DeleteAsync(retained.RemainingObjectKey!, default));
        Assert.Empty(server.Files);
    }

    [Fact]
    public async Task Put_ChoosesDeterministicVerifiedCandidate_WithoutDeletingDuplicates()
    {
        var server = new FakeDrive();
        await Put(Provider(server));
        var existing = Assert.Single(server.Files);
        server.Files.Add("000-corrupt", existing.Value with { Bytes = [0, 0, 0, 0] });
        server.Duplicate(existing.Key);
        var catalog = new InMemoryGoogleDriveFileCatalog();
        await Put(Provider(server, catalog));
        Assert.Equal(existing.Key, (await catalog.FindAsync(_tenant, _connection, _key.Value, default))!.FileId);
        Assert.Equal(1, server.SessionsCreated);
        Assert.Equal(3, server.Files.Count);
    }

    [Fact]
    public async Task GetMetadata_AmbiguousUnpublishedGenerationsFailsClosed()
    {
        var server = new FakeDrive();
        var provider = Provider(server);
        await Put(provider);
        await Put(provider, [8, 9, 0], StorageOverwritePolicy.Replace);
        var failure = await Assert.ThrowsAsync<StorageException>(() => Provider(server).GetMetadataAsync(_key, default));
        Assert.Equal(StorageFailureKind.AlreadyExists, failure.Kind);
        Assert.Equal(0, server.DeleteRequests);
    }

    [Fact]
    public async Task Put_OverallDeadlineBoundsHungChunk()
    {
        var server = new FakeDrive { BeforeChunk = token => Task.Delay(Timeout.InfiniteTimeSpan, token) };
        var options = new GoogleDriveStorageOptions("client", "secret", _tenant, _connection, "folder-123",
            uploadTimeout: TimeSpan.FromMilliseconds(50));
        var failure = await Assert.ThrowsAsync<StorageException>(() => Put(Provider(server, options: options)));
        Assert.Equal(StorageFailureKind.Timeout, failure.Kind);
        Assert.Empty(Directory.EnumerateFiles(_directory));
    }

    [Fact]
    public async Task Put_RequestTimeoutUsesBoundedRetryBudget()
    {
        var server = new FakeDrive { BeforeChunk = token => Task.Delay(Timeout.InfiniteTimeSpan, token) };
        var options = new GoogleDriveStorageOptions("client", "secret", _tenant, _connection, "folder-123",
            maxRetryAttempts: 2, requestTimeout: TimeSpan.FromMilliseconds(10),
            retryBaseDelay: TimeSpan.Zero, maxRetryDelay: TimeSpan.Zero);
        var failure = await Assert.ThrowsAsync<StorageException>(() => Put(Provider(server, options: options)));
        Assert.Equal(StorageFailureKind.NetworkUnavailable, failure.Kind);
        Assert.Equal(1, server.OffsetQueries);
    }

    [Fact]
    public async Task Put_LeaseLossCancelsWithoutPublication()
    {
        using var lease = new CancellationTokenSource();
        var server = new FakeDrive { BeforeChunk = token => { lease.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; } };
        var catalog = new InMemoryGoogleDriveFileCatalog();
        var provider = new GoogleDriveStorageProvider(new HttpClient(server), Options(),
            _ => ValueTask.FromResult(new GoogleDriveCredential("refresh")), catalog,
            stagingStore: new TestStagingStore(_directory, lease.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Put(provider));
        var locator = await catalog.FindAsync(_tenant, _connection, _key.Value, default);
        Assert.NotNull(locator);
        Assert.Null(locator.Checksum);
        var restarted = Provider(server, catalog);
        server.RejectReadback = true;
        server.RejectListing = true;
        Assert.False(await restarted.DeleteAsync(_key, default));
        Assert.Empty(server.Files);
        Assert.Empty(Directory.EnumerateFiles(_directory));
    }

    [Fact]
    public async Task Delete_RetainsKnownIdentityUntilDurableRetentionCompletion()
    {
        var server = new FakeDrive();
        var catalog = new InMemoryGoogleDriveFileCatalog();
        await Put(Provider(server, catalog));
        var restarted = Provider(server, catalog);
        server.RejectReadback = true;
        server.RejectListing = true;
        Assert.True(await restarted.DeleteAsync(_key, default));
        Assert.False(await restarted.DeleteAsync(_key, default));
        Assert.False(await restarted.ExistsAsync(_key, default));
        Assert.False(await Provider(server, catalog).ExistsAsync(_key, default));
        Assert.Empty(server.Files);
    }

    [Fact]
    public async Task Put_UnexpectedCompletionIdentity_DoesNotPublishAndRemainsDeletable()
    {
        var server = new FakeDrive { WrongCompletionId = true };
        var catalog = new InMemoryGoogleDriveFileCatalog();
        var failure = await Assert.ThrowsAsync<StorageException>(() => Put(Provider(server, catalog)));
        Assert.Equal(StorageFailureKind.IntegrityMismatch, failure.Kind);
        Assert.True(failure.ObjectMayRemain);
        Assert.Equal(_key, failure.RemainingObjectKey);
        Assert.Null((await catalog.FindAsync(_tenant, _connection, _key.Value, default))!.Checksum);
        Assert.Single(server.Files);
        server.RejectListing = true;
        server.RejectReadback = true;
        Assert.True(await Provider(server, catalog).DeleteAsync(_key, default));
        Assert.Empty(server.Files);
    }

    [Fact]
    public async Task Put_IdentityAllocationFailure_DoesNotCreateRemoteObject()
    {
        var server = new FakeDrive { GenerateIdStatus = HttpStatusCode.Unauthorized };
        var failure = await Assert.ThrowsAsync<StorageException>(() => Put(Provider(server)));
        Assert.Equal(StorageFailureKind.AuthenticationFailed, failure.Kind);
        Assert.Equal(0, server.SessionsCreated);
        Assert.Empty(server.Files);
        Assert.Empty(Directory.EnumerateFiles(_directory));
    }

    [Fact]
    public async Task Put_InitialCatalogSaveFailure_PreventsRemoteCreation()
    {
        var server = new FakeDrive();
        var catalog = new FailingCatalog { FailUnverifiedSave = true };
        await Assert.ThrowsAsync<IOException>(() => Put(Provider(server, catalog)));
        Assert.Equal(0, server.SessionsCreated);
        Assert.Empty(server.Files);
        Assert.Empty(Directory.EnumerateFiles(_directory));
    }
    [Fact]
    public async Task Put_DirectAccessTokenSource_SucceedsWithoutClientSecret()
    {
        var server = new FakeDrive();
        var options = new GoogleDriveStorageOptions(_tenant, _connection, stagingDirectory: _directory, uploadTimeout: TimeSpan.FromSeconds(5));
        var tokenSourceCalled = false;
        var provider = GoogleDriveStorageProvider.CreateDirect(
            new HttpClient(server),
            options,
            _ => { tokenSourceCalled = true; return ValueTask.FromResult("direct-token-xyz"); },
            stagingStore: new TestStagingStore(_directory));

        var meta = await Put(provider);
        Assert.True(tokenSourceCalled);
        Assert.Single(server.Files);
        Assert.Equal(Bytes.Length, meta.SizeBytes);
    }
    [Fact]
    public void GoogleDriveStorageOptions_ThrowsWhenTenantIdEmpty()
    {
        Assert.Throws<ArgumentException>(() => new GoogleDriveStorageOptions(Guid.Empty, _connection));
        Assert.Throws<ArgumentException>(() => new GoogleDriveStorageOptions("client", "secret", Guid.Empty, _connection));
    }

    [Fact]
    public void GoogleDriveTokenManager_ThrowsWhenClientSecretsMissing()
    {
        var options = new GoogleDriveStorageOptions(_tenant, _connection);
        Assert.Throws<ArgumentException>(() => new GoogleDriveTokenManager(new HttpClient(), options, _ => ValueTask.FromResult(new GoogleDriveCredential("refresh"))));
    }



    private sealed class FailingCatalog : IGoogleDriveFileCatalog
    {
        private readonly InMemoryGoogleDriveFileCatalog _inner = new();
        public bool FailNextSave { get; set; } = true;
        public bool FailUnverifiedSave { get; set; }
        public Task<GoogleDriveFileRecord?> FindAsync(Guid tenant, Guid connection, string key, CancellationToken token) => _inner.FindAsync(tenant, connection, key, token);
        public Task SaveAsync(Guid tenant, Guid connection, string key, GoogleDriveFileRecord record, CancellationToken token)
        {
            if (FailUnverifiedSave && record.Checksum is null) throw new IOException("Injected initial catalog failure.");
            if (FailNextSave && record.Checksum is not null) { FailNextSave = false; throw new IOException("Injected catalog failure."); }
            return _inner.SaveAsync(tenant, connection, key, record, token);
        }
        public Task DeleteAsync(Guid tenant, Guid connection, string key, string expectedFileId, CancellationToken token) => _inner.DeleteAsync(tenant, connection, key, expectedFileId, token);
        public Task<IReadOnlyList<GoogleDriveCatalogEntry>> ListAsync(Guid tenant, Guid connection, CancellationToken token) => _inner.ListAsync(tenant, connection, token);
        public Task ReportFindingAsync(Guid tenant, Guid connection, GoogleDriveReconciliationFinding finding, CancellationToken token) => _inner.ReportFindingAsync(tenant, connection, finding, token);
    }

    private sealed class TestStagingStore(string directory, CancellationToken leaseToken = default) : IGoogleDriveStagingStore
    {
        public async Task<GoogleDriveStagedFile> StageAsync(Stream content, long expectedLength, ContentChecksum checksum, CancellationToken token)
        {
            var path = Path.Combine(directory, Guid.NewGuid().ToString("N"));
            try
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                {
                    var buffer = new byte[65536];
                    long length = 0;
                    int count;
                    while ((count = await content.ReadAsync(buffer, token)) > 0)
                    {
                        if (count > expectedLength - length) throw new StorageException(StorageFailureKind.IntegrityMismatch, "Unexpected length.");
                        hash.AppendData(buffer, 0, count);
                        await output.WriteAsync(buffer.AsMemory(0, count), token);
                        length += count;
                    }
                    if (length != expectedLength || !ContentChecksum.Sha256(hash.GetHashAndReset()).Equals(checksum))
                        throw new StorageException(StorageFailureKind.IntegrityMismatch, "Unexpected staged content.");
                }
                var linked = CancellationTokenSource.CreateLinkedTokenSource(token, leaseToken);
                return new(path, linked.Token, () => { linked.Dispose(); File.Delete(path); return ValueTask.CompletedTask; });
            }
            catch { File.Delete(path); throw; }
        }
    }

    private sealed class FakeDrive : HttpMessageHandler
    {
        public sealed record FileData(string Name, byte[] Bytes, Dictionary<string, string> Properties, string Parent);
        private sealed record Session(string FileId, string Name, long Length, MemoryStream Bytes, Dictionary<string, string> Properties, string Parent);
        public Dictionary<string, FileData> Files { get; } = new();
        public Dictionary<string, (string Name, Dictionary<string, string> Properties)> Folders { get; } = new();
        private readonly Dictionary<string, Session> _sessions = new();
        private readonly object _gate = new();
        private readonly TaskCompletionSource _rootGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _rootSearches;
        public int IdsGenerated { get; private set; }
        public bool LoseInitiationReply { get; set; }
        public int SessionsCreated { get; private set; }
        public int ChunkRequests { get; private set; }
        public int OffsetQueries { get; private set; }
        public int DeleteRequests { get; private set; }
        public int PatchRequests { get; private set; }
        public int Readbacks { get; private set; }
        public int Expirations { get; set; }
        public bool IncompleteWithoutProgress { get; set; }
        public string? Range { get; set; }
        public bool LoseChunkReply { get; set; }
        public bool LoseCompletionReply { get; set; }
        public bool ExpireCompletedSession { get; set; }
        public bool WrongCompletionId { get; set; }
        public HttpStatusCode? GenerateIdStatus { get; set; }
        public bool RejectReadback { get; set; }
        public bool RejectListing { get; set; }
        public bool CompleteAs308 { get; set; }
        public bool CorruptNextUpload { get; set; }
        public bool GateRootSearch { get; set; }
        public int PageSize { get; set; } = 1000;
        public HttpStatusCode? ChunkStatus { get; set; }
        public string? FailureReason { get; set; }
        public RetryConditionHeaderValue? RetryAfter { get; set; }
        public Action? OnOffsetQuery { get; set; }
        public Action? OnReadback { get; set; }
        public Func<CancellationToken, Task>? BeforeChunk { get; set; }
        public Func<CancellationToken, Task>? BeforeInitiation { get; set; }
        public void Duplicate(string id) => Files.Add(id + "-duplicate", Files[id]);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var uri = request.RequestUri!;
            if (uri.Host == "oauth2.googleapis.com") return Json(new { access_token = "access", expires_in = 3600 });
            var metadata = request.Method == HttpMethod.Post && request.Content is not null
                ? await request.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token) : default;
            var payload = request.Method == HttpMethod.Put && request.Content is not null
                ? await request.Content.ReadAsByteArrayAsync(token) : [];
            if (uri.Host == "upload.googleapis.com" && payload.Length > 0 && BeforeChunk is not null)
                await BeforeChunk(token);
            if (uri.AbsolutePath.StartsWith("/upload/") && BeforeInitiation is not null)
                await BeforeInitiation(token);
            if (uri.Query.Contains("backupFolder") && uri.Query.Contains("root") && GateRootSearch)
            {
                if (Interlocked.Increment(ref _rootSearches) <= 2)
                {
                    if (_rootSearches == 2) _rootGate.TrySetResult();
                    await _rootGate.Task.WaitAsync(token);
                    return Json(new { files = Array.Empty<object>() });
                }
            }
            lock (_gate)
            {
                if (request.Method == HttpMethod.Patch) PatchRequests++;
                if (request.Method == HttpMethod.Get && uri.AbsolutePath == "/drive/v3/files/generateIds")
                {
                    if (GenerateIdStatus is { } status) return new(status);
                    var parameters = uri.Query.TrimStart('?').Split('&')
                        .Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
                    if (parameters["count"] != "1" || parameters["space"] != "drive" || parameters["type"] != "files")
                        throw new InvalidOperationException("Unexpected generated-ID request.");
                    return Json(new { ids = new[] { "generated-" + ++IdsGenerated } });
                }
                if (uri.AbsolutePath.StartsWith("/upload/"))
                {
                    var fileId = metadata.GetProperty("id").GetString()!;
                    if (Files.ContainsKey(fileId)) return new(HttpStatusCode.Conflict);
                    var id = "session-" + ++SessionsCreated;
                    _sessions[id] = new(fileId, metadata.GetProperty("name").GetString()!, long.Parse(request.Headers.GetValues("X-Upload-Content-Length").Single()),
                        new MemoryStream(), Properties(metadata), metadata.GetProperty("parents")[0].GetString()!);
                    if (LoseInitiationReply)
                    {
                        Files.Add(fileId, new(metadata.GetProperty("name").GetString()!, [], Properties(metadata),
                            metadata.GetProperty("parents")[0].GetString()!));
                        throw new HttpRequestException("Injected lost initiation response.");
                    }
                    var response = Json(new { });
                    response.Headers.Location = new("https://upload.googleapis.com/" + id);
                    return response;
                }
                if (uri.Host == "upload.googleapis.com")
                {
                    var id = uri.AbsolutePath.TrimStart('/');
                    var session = _sessions[id];
                    if (payload.Length == 0)
                    {
                        OffsetQueries++;
                        OnOffsetQuery?.Invoke();
                        token.ThrowIfCancellationRequested();
                        if (ExpireCompletedSession && Files.ContainsKey(session.FileId)) return new(HttpStatusCode.NotFound);
                        if (session.Bytes.Length == session.Length) return FileJson(session.FileId, Files[session.FileId]);
                        return Incomplete(session.Bytes.Length == 0 ? null : "bytes=0-" + (session.Bytes.Length - 1));
                    }
                    ChunkRequests++;
                    if (Expirations > 0) { Expirations--; return new(HttpStatusCode.NotFound); }
                    if (ChunkStatus is { } status)
                    {
                        var response = new HttpResponseMessage(status) { Content = JsonContent.Create(new { error = new { errors = new[] { new { reason = FailureReason } }, message = "secret-response" } }) };
                        response.Headers.RetryAfter = RetryAfter;
                        return response;
                    }
                    if (IncompleteWithoutProgress) return Incomplete(Range);
                    var start = request.Content!.Headers.ContentRange!.From!.Value;
                    session.Bytes.Position = start;
                    session.Bytes.Write(payload);
                    if (session.Bytes.Length == session.Length)
                    {
                        var bytes = session.Bytes.ToArray();
                        if (CorruptNextUpload) { bytes[0] ^= 1; CorruptNextUpload = false; }
                        var file = new FileData(session.Name, bytes, session.Properties, session.Parent);
                        if (!Files.TryAdd(session.FileId, file)) return new(HttpStatusCode.Conflict);
                        if (LoseCompletionReply) { LoseCompletionReply = false; throw new HttpRequestException("Injected lost completion response."); }
                        if (CompleteAs308) return Incomplete("bytes=0-" + (session.Length - 1));
                        return FileJson(WrongCompletionId ? "unexpected-completion-id" : session.FileId, file);
                    }
                    if (LoseChunkReply) { LoseChunkReply = false; throw new HttpRequestException("Injected lost response."); }
                    return Incomplete("bytes=0-" + (session.Bytes.Length - 1));
                }
                if (request.Method == HttpMethod.Get && uri.Query.StartsWith("?q="))
                {
                    if (RejectListing) throw new InvalidOperationException("Listing unavailable.");
                    var parameters = uri.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
                    var query = parameters["q"];
                    IEnumerable<object> matches;
                    if (query.StartsWith("mimeType ="))
                        matches = Folders.Where(f => query.Contains("value='" + f.Value.Properties["backupFolder"] + "'"))
                            .OrderBy(f => f.Key).Select(f => (object)new { id = f.Key, name = f.Value.Name, appProperties = f.Value.Properties });
                    else matches = Files.Where(f => f.Value.Properties.All(p => !query.Contains("key='" + p.Key + "'")
                                || query.Contains("key='" + p.Key + "' and value='" + p.Value + "'")))
                            .OrderBy(f => f.Key).Select(f => FileObject(f.Key, f.Value));
                    var all = matches.ToArray();
                    var offset = parameters.TryGetValue("pageToken", out var page) ? int.Parse(page) : 0;
                    return Json(new { files = all.Skip(offset).Take(PageSize).ToArray(), nextPageToken = offset + PageSize < all.Length ? (offset + PageSize).ToString() : null });
                }
                if (request.Method == HttpMethod.Post && uri.AbsolutePath == "/drive/v3/files")
                {
                    var id = "folder-" + (Folders.Count + 1);
                    Folders.Add(id, (metadata.GetProperty("name").GetString()!, Properties(metadata)));
                    return Json(new { id });
                }
                var targetFileId = uri.AbsolutePath.Split('/')[^1];
                if (request.Method == HttpMethod.Delete)
                {
                    DeleteRequests++;
                    return new(Files.Remove(targetFileId) ? HttpStatusCode.NoContent : HttpStatusCode.NotFound);
                }
                if (request.Method == HttpMethod.Get && Files.TryGetValue(targetFileId, out var data))
                {
                    if (uri.Query == "?alt=media")
                    {
                        if (RejectReadback) throw new InvalidOperationException("Media readback unavailable.");
                        Readbacks++;
                        OnReadback?.Invoke();
                        token.ThrowIfCancellationRequested();
                        return new(HttpStatusCode.OK) { Content = new ByteArrayContent(data.Bytes) };
                    }
                    return FileJson(targetFileId, data);
                }
                if (request.Method == HttpMethod.Get) return new(HttpStatusCode.NotFound);
                throw new InvalidOperationException("Unexpected fake Drive request.");
            }
        }
        private static Dictionary<string, string> Properties(JsonElement metadata) => metadata.GetProperty("appProperties").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
        private static object FileObject(string id, FileData file) => new { id, name = file.Name, size = file.Bytes.LongLength,
            trashed = false, modifiedTime = DateTimeOffset.UtcNow, appProperties = file.Properties, parents = new[] { file.Parent } };
        private static HttpResponseMessage FileJson(string id, FileData file) => Json(FileObject(id, file));
        private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
        private static HttpResponseMessage Incomplete(string? range)
        {
            var response = new HttpResponseMessage((HttpStatusCode)308);
            if (range is not null) response.Headers.TryAddWithoutValidation("Range", range);
            return response;
        }
    }
}
