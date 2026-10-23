using System.Net;
using BackupPlatform.Agent.Core.Backup;
using BackupPlatform.Agent.Core.Tasks;
using BackupPlatform.Agent.Transport;
using BackupPlatform.AgentTests.Support;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Contracts.Restores;
using BackupPlatform.StorageProviders.Abstractions;

namespace BackupPlatform.AgentTests;

/// <summary>The agent's side of multi-destination runs: the request it builds from the snapshot, the result it reports, and the destination it names on the relay.</summary>
public sealed class MultiDestinationTaskTests
{
    private static readonly Guid Central = Guid.NewGuid();
    private static readonly Guid Sftp = Guid.NewGuid();
    private static readonly Guid Drive = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private static BackupTaskSnapshot Snapshot(params BackupDestinationContract[] destinations) => new()
    {
        TenantId = Guid.NewGuid(), JobId = Guid.NewGuid(), RunId = Guid.NewGuid(), AttemptId = Guid.NewGuid(), AttemptNumber = 1, LeaseToken = Guid.NewGuid(),
        LeaseExpiresAt = Now.AddMinutes(2),
        Configuration = new BackupExecutionConfiguration
        {
            Database = "Sales", Host = "sql.example.test", Port = 1433, IntegratedSecurity = true,
            Destinations = destinations, RequiredVerifiedReplicaCount = 2
        }
    };

    private static readonly BackupDestinationContract[] ThreeDestinations =
    [
        new() { StorageProvider = "central", StorageConnectionId = Central, StorageExecutionLocation = "Backend", IsPrimary = true },
        new() { StorageProvider = "sftp", StorageConnectionId = Sftp, StorageExecutionLocation = "Backend" },
        new() { StorageProvider = "google-drive", StorageConnectionId = Drive, StorageExecutionLocation = "Backend" }
    ];

    private sealed class RecordingFactory : ITaskStorageProviderFactory
    {
        public List<BackupDestinationContract> Destinations { get; } = [];
        public IStorageProvider CreateBackup(BackupTaskSnapshot task, BackupDestinationContract destination)
        {
            Destinations.Add(destination);
            return new NullStorage(destination.StorageProvider);
        }
        public IStorageProvider CreateRestore(RestoreTaskSnapshot task) => throw new NotSupportedException();
        public IStorageProvider CreateScoped(DirectStorageScope scope) => throw new NotSupportedException();
    }

    private sealed class NullStorage(string id) : IStorageProvider
    {
        public StorageProviderId Id { get; } = new(id);
        public StorageTopology Topology => StorageTopology.BackendRelayed;
        public StorageCapabilities Capabilities { get; } = new([]);
        public Task<StorageObjectMetadata> PutAsync(StorageObjectKey key, Stream content, StoragePutOptions options, IProgress<long>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> ExistsAsync(StorageObjectKey key, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StorageObjectMetadata?> GetMetadataAsync(StorageObjectKey key, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StorageVerificationResult> VerifyAsync(StorageObjectKey key, StorageVerificationExpectation expected, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Stream> OpenReadAsync(StorageObjectKey key, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(StorageObjectKey key, CancellationToken cancellationToken) => throw new NotSupportedException();
        public IAsyncEnumerable<StorageEntry> EnumerateAsync(StorageObjectKey? prefix, DateTimeOffset modifiedBefore, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static BackupTaskRequestFactory Factory(RecordingFactory? storage = null) =>
        new(new TaskCredentials(), [StagingArea.SameHost(Path.GetTempPath())], storage ?? new RecordingFactory());

    [Fact]
    public async Task The_request_carries_every_snapshot_destination_with_a_relay_provider_per_backend_relayed_one()
    {
        var storage = new RecordingFactory();

        var request = await Factory(storage).CreateAsync(Snapshot(ThreeDestinations) with { MaxArtifactBytes = 123 }, default);

        Assert.Equal(3, request.Destinations.Count);
        Assert.Equal(2, request.RequiredVerifiedReplicas);
        Assert.Equal(123L, request.MaxArtifactBytes);
        Assert.Equal([Central, Sftp, Drive], request.Destinations.Select(d => d.ConnectionId!.Value));
        // Central is written by the agent's own provider; only sftp and Google Drive go through the relay, each bound to ITS destination.
        Assert.Null(request.Destinations[0].StorageOverride);
        Assert.Equal(["sftp", "google-drive"], storage.Destinations.Select(d => d.StorageProvider));
        Assert.Equal([Sftp, Drive], storage.Destinations.Select(d => d.StorageConnectionId!.Value));
        Assert.Single(request.Destinations, d => d.IsPrimary);
    }

    [Fact]
    public async Task A_negative_hard_quota_bound_is_refused_as_invalid_task_input()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Factory().CreateAsync(Snapshot(ThreeDestinations) with { MaxArtifactBytes = -1 }, default).AsTask());
    }

    [Fact]
    public async Task A_snapshot_without_destinations_is_refused_not_read_through_legacy_fields()
    {
        var snapshot = Snapshot(ThreeDestinations[0]);
        snapshot = snapshot with { Configuration = snapshot.Configuration with { Destinations = [], RequiredVerifiedReplicaCount = 1 } };

        await Assert.ThrowsAsync<ArgumentException>(async () => await Factory().CreateAsync(snapshot, default));
    }

    public static TheoryData<string> Unsupported => new()
    {
        "unknown-provider", "relayed-on-agent", "relayed-without-connection", "missing-location", "required-too-high", "no-primary", "duplicate"
    };

    [Theory]
    [MemberData(nameof(Unsupported))]
    public async Task An_unsupported_destination_combination_is_refused_not_skipped(string kind)
    {
        var destinations = kind switch
        {
            "unknown-provider" => [ThreeDestinations[0], new BackupDestinationContract { StorageProvider = "unknown-custom-storage", StorageConnectionId = Guid.NewGuid(), StorageExecutionLocation = "Backend" }],
            "relayed-on-agent" => [ThreeDestinations[0], ThreeDestinations[1] with { StorageExecutionLocation = "Agent" }],
            "relayed-without-connection" => [ThreeDestinations[0], ThreeDestinations[1] with { StorageConnectionId = null }],
            "missing-location" => [ThreeDestinations[0], ThreeDestinations[1] with { StorageExecutionLocation = null }],
            "no-primary" => [ThreeDestinations[0] with { IsPrimary = false }, ThreeDestinations[1]],
            "duplicate" => [ThreeDestinations[0], ThreeDestinations[0] with { IsPrimary = false }],
            _ => new[] { ThreeDestinations[0], ThreeDestinations[1] }
        };
        var snapshot = Snapshot(destinations);
        snapshot = snapshot with { Configuration = snapshot.Configuration with { RequiredVerifiedReplicaCount = kind == "required-too-high" ? 3 : 1 } };

        await Assert.ThrowsAsync<ArgumentException>(async () => await Factory().CreateAsync(snapshot, default));
    }

    // ---- result mapping -----------------------------------------------------------------------------------------------------------------------

    private static readonly ContentChecksum SourceChecksum = new(ChecksumAlgorithm.Sha256, new string('a', 64));
    private static readonly ContentChecksum ArtifactChecksum = new(ChecksumAlgorithm.Sha256, new string('b', 64));

    private static BackupDestinationResult Verified(BackupPipelineDestination destination, StorageObjectKey key) => new(destination, BackupDestinationState.Verified,
        new BackupReplicaInfo(new StorageLocation(destination.Storage, key), 256, ArtifactChecksum, StorageVerificationStatus.Verified, StorageVerificationMethod.ReadBack, Now),
        null, true, key, false, Now.AddSeconds(-1), Now);

    private static BackupDestinationResult Failed(BackupPipelineDestination destination, BackupFailure failure, bool uploaded = false, StorageObjectKey? key = null, bool mayRemain = false) =>
        new(destination, BackupDestinationState.Failed, null, failure, uploaded, key, mayRemain, Now.AddSeconds(-1), Now);

    private static DatabaseBackupPipelineResult Result(DatabaseBackupStatus status, BackupFailure? failure, params BackupDestinationResult[] destinations) => new()
    {
        Status = status, FinalStage = status == DatabaseBackupStatus.Succeeded ? BackupExecutionStage.Completed : BackupExecutionStage.Failed, Failure = failure,
        StartedAt = Now.AddSeconds(-5), FinishedAt = Now, BackupStartedAt = Now.AddSeconds(-4), BackupFinishedAt = Now.AddSeconds(-2),
        SourceFile = new BackupSourceFile("source.bak", 512, SourceChecksum),
        Artifact = new BackupArtifactInfo("source.bak", 256, ArtifactChecksum, true, []),
        Options = new BackupOptionsOutcome(FullBackupOptions.None, new AppliedFullBackupOptions(true, true, false)),
        Verification = new BackupVerification(BackupVerificationStatus.Passed, "RESTORE VERIFYONLY", Now.AddSeconds(-3), Now.AddSeconds(-2)),
        Destinations = destinations
    };

    private static BackupPipelineDestination[] Targets() =>
        [.. ThreeDestinations.Select(d => new BackupPipelineDestination(new StorageProviderId(d.StorageProvider), null, d.StorageConnectionId, d.IsPrimary))];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_partial_success_publishes_only_verified_replicas_and_reports_every_destination(bool cancelled)
    {
        var targets = Targets();
        var key = new StorageObjectKey("t/j/r/a/Sales_20261010T120000Z_aaaaaaaa_FULL.bak");
        var failure = cancelled
            ? new BackupFailure(BackupFailureKind.Cancelled, BackupExecutionStage.Uploading, BackupPipelineReasons.Cancelled, "cancelled")
            : new BackupFailure(BackupFailureKind.StorageWriteFailed, BackupExecutionStage.Uploading, BackupPipelineReasons.DestinationTimeout, "slow", BackupFailureCause.Timeout);
        var remaining = Failed(targets[2], failure) with { State = cancelled ? BackupDestinationState.Cancelled : BackupDestinationState.Failed };
        var result = Result(DatabaseBackupStatus.Succeeded, null, Verified(targets[0], key), Verified(targets[1], key), remaining);

        var completion = BackupTaskResultMapper.Map(Snapshot(ThreeDestinations), result);

        Assert.Equal("Succeeded", completion.Status);
        var replicas = completion.Artifact!.Replicas;
        Assert.Equal(["central", "sftp"], replicas.Select(r => r.StorageProvider));
        Assert.Equal([Central, Sftp], replicas.Select(r => r.StorageConnectionId!.Value));
        Assert.Equal(Sftp.ToString("D"), replicas[1].StorageTarget);
        Assert.Equal("central", replicas[0].StorageTarget);
        Assert.Equal(3, completion.Destinations.Count);
        Assert.Equal(["Verified", "Verified", cancelled ? "Cancelled" : "Failed"], completion.Destinations.Select(d => d.State));
        var failed = completion.Destinations[2];
        Assert.Equal(cancelled ? "Cancelled" : "OperationTimedOut", failed.FailureCode);
        Assert.Equal(cancelled ? "NotAttempted" : "Failed", failed.Upload);
        Assert.Equal("NotRun", failed.Verification);
        Assert.Null(failed.Key);
    }

    [Fact]
    public void When_every_destination_failed_the_failure_still_names_each_destination()
    {
        var targets = Targets();
        var failure = new BackupFailure(BackupFailureKind.StorageWriteFailed, BackupExecutionStage.Uploading, "storage-io-error", "io", BackupFailureCause.StorageUnavailable);
        var key = new StorageObjectKey("t/j/r/a/Sales_20261010T120000Z_aaaaaaaa_FULL.bak");
        var verification = new BackupFailure(BackupFailureKind.ReplicaVerificationFailed, BackupExecutionStage.VerifyingReplica, BackupPipelineReasons.ReplicaChecksumMismatch, "bad");
        var result = Result(DatabaseBackupStatus.Failed, failure, Failed(targets[0], failure), Failed(targets[1], verification, uploaded: true, key, mayRemain: true), Failed(targets[2], failure));

        var completion = BackupTaskResultMapper.Map(Snapshot(ThreeDestinations), result);

        Assert.Equal("Failed", completion.Status);
        Assert.Equal("StorageUnavailable", completion.FailureCode);
        Assert.Equal(256, completion.Artifact!.SizeBytes);
        Assert.Empty(completion.Artifact.Replicas);
        Assert.All(completion.Destinations, d => Assert.Equal("Failed", d.State));
        var mismatch = completion.Destinations[1];
        Assert.Equal("Succeeded", mismatch.Upload);
        Assert.Equal("Failed", mismatch.Verification);
        Assert.True(mismatch.ObjectMayRemain);
        Assert.Equal(key.Value, mismatch.Key);
        Assert.Equal("ReplicaVerificationFailed", mismatch.FailureCode);
    }

    [Fact]
    public void Cancellation_during_verification_retains_orphan_metadata_without_publishing_a_replica()
    {
        var targets = Targets();
        var key = new StorageObjectKey("t/j/r/a/Sales_20261010T120000Z_aaaaaaaa_FULL.bak");
        var cancellation = new BackupFailure(BackupFailureKind.Cancelled, BackupExecutionStage.VerifyingReplica, BackupPipelineReasons.Cancelled, "cancelled");
        var uploaded = Failed(targets[0], cancellation, uploaded: true, key, mayRemain: true) with { State = BackupDestinationState.Cancelled };
        var result = Result(DatabaseBackupStatus.Cancelled, cancellation, uploaded,
            Failed(targets[1], cancellation) with { State = BackupDestinationState.Cancelled },
            Failed(targets[2], cancellation) with { State = BackupDestinationState.Cancelled });

        var completion = BackupTaskResultMapper.Map(Snapshot(ThreeDestinations), result);

        Assert.Equal("Cancelled", completion.Status);
        Assert.Equal("Cancelled", completion.FailureCode);
        Assert.Equal(256, completion.Artifact!.SizeBytes);
        Assert.Empty(completion.Artifact.Replicas);
        Assert.All(completion.Destinations, destination => Assert.Equal("Cancelled", destination.State));
        Assert.True(completion.Destinations[0].ObjectMayRemain);
        Assert.Equal(key.Value, completion.Destinations[0].Key);
        Assert.Equal("Succeeded", completion.Destinations[0].Upload);
        Assert.Equal("Failed", completion.Destinations[0].Verification);
        Assert.All(completion.Destinations.Skip(1), destination => Assert.False(destination.ObjectMayRemain));
    }

    [Fact]
    public void A_destination_claiming_verified_with_a_replica_that_does_not_match_the_artifact_is_reported_failed()
    {
        var targets = Targets();
        var key = new StorageObjectKey("t/j/r/a/Sales_20261010T120000Z_aaaaaaaa_FULL.bak");
        var forged = Verified(targets[1], key) with
        {
            Replica = new BackupReplicaInfo(new StorageLocation(targets[1].Storage, key), 255, ArtifactChecksum, StorageVerificationStatus.Verified, StorageVerificationMethod.ReadBack, Now)
        };

        var completion = BackupTaskResultMapper.Map(Snapshot(ThreeDestinations), Result(DatabaseBackupStatus.Succeeded, null, Verified(targets[0], key), forged));

        Assert.Equal("Succeeded", completion.Status);
        Assert.Equal(["central"], completion.Artifact!.Replicas.Select(r => r.StorageProvider));
        Assert.Equal("Failed", completion.Destinations[1].State);
        Assert.Equal("ReplicaVerificationFailed", completion.Destinations[1].FailureCode);
    }

    [Fact]
    public void A_run_with_no_sound_replica_is_never_reported_as_succeeded()
    {
        var targets = Targets();
        var key = new StorageObjectKey("t/j/r/a/Sales_20261010T120000Z_aaaaaaaa_FULL.bak");
        var forged = Verified(targets[0], key) with { Replica = null };

        var completion = BackupTaskResultMapper.Map(Snapshot(ThreeDestinations), Result(DatabaseBackupStatus.Succeeded, null, forged));

        Assert.Equal("Failed", completion.Status);
        Assert.Equal("InternalError", completion.FailureCode);
    }

    // ---- relay ---------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_relay_names_the_destination_of_each_provider_on_every_call()
    {
        using var handler = new FakeHttpHandler();
        handler.Respond = (_, _) => Task.FromResult(FakeHttpHandler.Json(HttpStatusCode.OK, """{"exists":true,"sizeBytes":3,"verificationStatus":"Verified","verificationMethod":"ReadBack"}"""));
        using var http = new HttpClient(handler);
        var factory = new HttpTaskStorageProviderFactory(http, new AgentBackendEndpoint(new Uri("https://backup.example.test/")), new FakeCredentialStore(TestIdentity.Create()));
        var task = Snapshot(ThreeDestinations);

        var sftp = factory.CreateBackup(task, ThreeDestinations[1]);
        var drive = factory.CreateBackup(task, ThreeDestinations[2]);
        var key = new StorageObjectKey("t/j/r/a/Sales_20261010T120000Z_aaaaaaaa_FULL.bak");
        await sftp.VerifyAsync(key, new StorageVerificationExpectation(3, ArtifactChecksum), default);
        await drive.GetMetadataAsync(key, default);

        var requests = handler.Requests;
        Assert.Equal(Sftp.ToString("D"), requests[0].Headers[TaskStorageProtocol.DestinationHeader]);
        Assert.Equal(Drive.ToString("D"), requests[1].Headers[TaskStorageProtocol.DestinationHeader]);
        Assert.All(requests, r => Assert.Equal(task.LeaseToken.ToString("D"), r.Headers[TaskStorageProtocol.LeaseHeader]));
    }

    [Fact]
    public async Task A_relay_that_refuses_the_destination_surfaces_as_a_storage_failure_of_that_destination()
    {
        using var handler = new FakeHttpHandler();
        handler.Respond = (_, _) => Task.FromResult(FakeHttpHandler.Json(HttpStatusCode.Conflict, """{"error":"destination_not_authorized"}"""));
        using var http = new HttpClient(handler);
        var factory = new HttpTaskStorageProviderFactory(http, new AgentBackendEndpoint(new Uri("https://backup.example.test/")), new FakeCredentialStore(TestIdentity.Create()));
        var provider = factory.CreateBackup(Snapshot(ThreeDestinations), ThreeDestinations[1]);

        var failure = await Assert.ThrowsAsync<StorageException>(() => provider.GetMetadataAsync(new StorageObjectKey("a/b"), default));

        Assert.Equal(StorageFailureKind.AccessDenied, failure.Kind);
    }

    [Fact]
    public async Task Direct_destination_creates_agent_direct_provider_and_fails_closed_if_remote_base_directory_missing()
    {
        using var handler = new FakeHttpHandler();
        handler.Respond = (_, _) => Task.FromResult(FakeHttpHandler.Json(HttpStatusCode.OK, """
            {
                "providerType": "sftp",
                "host": "sftp.test",
                "port": 22,
                "username": "user",
                "authenticationType": "password",
                "hostKeyFingerprint": "SHA256:abc",
                "password": "pass"
            }
            """));
        using var http = new HttpClient(handler);
        var factory = new HttpTaskStorageProviderFactory(http, new AgentBackendEndpoint(new Uri("https://backup.example.test/")), new FakeCredentialStore(TestIdentity.Create()));
        var directDestination = new BackupDestinationContract
        {
            StorageProvider = "sftp",
            StorageConnectionId = Sftp,
            Direct = true,
            StorageExecutionLocation = "Agent"
        };
        var provider = factory.CreateBackup(Snapshot([directDestination]), directDestination);

        Assert.Equal(StorageTopology.AgentDirect, provider.Topology);
        var failure = await Assert.ThrowsAsync<StorageException>(() => provider.GetMetadataAsync(new StorageObjectKey("a/b"), default));
        Assert.Equal(StorageFailureKind.InvalidConfiguration, failure.Kind);
        Assert.Contains("remote base directory", failure.Message);
    }

    [Fact]
    public async Task Direct_google_drive_caches_token_across_operations()
    {
        var fetchCount = 0;
        using var handler = new FakeHttpHandler();
        handler.Respond = (req, _) =>
        {
            if (req.RequestUri?.AbsolutePath.EndsWith("/storage/access", StringComparison.Ordinal) == true)
            {
                Interlocked.Increment(ref fetchCount);
                return Task.FromResult(FakeHttpHandler.Json(HttpStatusCode.OK, $$"""
                    {
                        "providerType": "google-drive",
                        "accessToken": "cached-token-123",
                        "expiresAt": "{{DateTimeOffset.UtcNow.AddMinutes(50):O}}",
                        "folderId": "root-folder-id"
                    }
                    """));
            }
            if (req.RequestUri?.AbsolutePath.EndsWith("/storage/catalog", StringComparison.Ordinal) == true)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
            return Task.FromResult(FakeHttpHandler.Json(HttpStatusCode.OK, """{"files":[]}"""));
        };
        using var http = new HttpClient(handler);
        var factory = new HttpTaskStorageProviderFactory(http, new AgentBackendEndpoint(new Uri("https://backup.example.test/")), new FakeCredentialStore(TestIdentity.Create()));
        var directDestination = new BackupDestinationContract
        {
            StorageProvider = "google-drive",
            StorageConnectionId = Drive,
            Direct = true,
            StorageExecutionLocation = "Agent"
        };
        var provider = factory.CreateBackup(Snapshot([directDestination]), directDestination);

        Assert.Equal(StorageTopology.AgentDirect, provider.Topology);
        var exists1 = await provider.ExistsAsync(new StorageObjectKey("a/b"), default);
        var exists2 = await provider.ExistsAsync(new StorageObjectKey("a/b"), default);

        Assert.Equal(1, fetchCount);
        Assert.False(exists1);
        Assert.False(exists2);
    }

    [Fact]
    public async Task Direct_google_drive_refetches_token_when_expired()
    {
        var fetchCount = 0;
        using var handler = new FakeHttpHandler();
        handler.Respond = (req, _) =>
        {
            if (req.RequestUri?.AbsolutePath.EndsWith("/storage/access", StringComparison.Ordinal) == true)
            {
                Interlocked.Increment(ref fetchCount);
                return Task.FromResult(FakeHttpHandler.Json(HttpStatusCode.OK, $$"""
                    {
                        "providerType": "google-drive",
                        "accessToken": "expired-token-123",
                        "expiresAt": "{{DateTimeOffset.UtcNow.AddMinutes(-5):O}}",
                        "folderId": "root-folder-id"
                    }
                    """));
            }
            if (req.RequestUri?.AbsolutePath.EndsWith("/storage/catalog", StringComparison.Ordinal) == true)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
            return Task.FromResult(FakeHttpHandler.Json(HttpStatusCode.OK, """{"files":[]}"""));
        };
        using var http = new HttpClient(handler);
        var factory = new HttpTaskStorageProviderFactory(http, new AgentBackendEndpoint(new Uri("https://backup.example.test/")), new FakeCredentialStore(TestIdentity.Create()));
        var directDestination = new BackupDestinationContract
        {
            StorageProvider = "google-drive",
            StorageConnectionId = Drive,
            Direct = true,
            StorageExecutionLocation = "Agent"
        };
        var provider = factory.CreateBackup(Snapshot([directDestination]), directDestination);

        await provider.ExistsAsync(new StorageObjectKey("a/b"), default);
        await provider.ExistsAsync(new StorageObjectKey("a/b"), default);

        Assert.True(fetchCount >= 2, $"Expected at least 2 fetches due to expiry, but got {fetchCount}");
    }

    [Fact]
    public async Task Direct_sftp_supports_private_key_authentication()
    {
        using var handler = new FakeHttpHandler();
        handler.Respond = (_, _) => Task.FromResult(FakeHttpHandler.Json(HttpStatusCode.OK, """
            {
                "providerType": "sftp",
                "host": "sftp.test",
                "port": 22,
                "username": "user",
                "authenticationType": "private-key",
                "remoteBaseDirectory": "/upload",
                "hostKeyFingerprint": "SHA256:abc",
                "privateKey": "-----BEGIN OPENSSH PRIVATE KEY-----\nMIIB...-----END OPENSSH PRIVATE KEY-----"
            }
            """));
        using var http = new HttpClient(handler);
        var factory = new HttpTaskStorageProviderFactory(http, new AgentBackendEndpoint(new Uri("https://backup.example.test/")), new FakeCredentialStore(TestIdentity.Create()));
        var directDestination = new BackupDestinationContract
        {
            StorageProvider = "sftp",
            StorageConnectionId = Sftp,
            Direct = true,
            StorageExecutionLocation = "Agent"
        };
        var provider = factory.CreateBackup(Snapshot([directDestination]), directDestination);

        Assert.Equal(StorageTopology.AgentDirect, provider.Topology);
    }
}
