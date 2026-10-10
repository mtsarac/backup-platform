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
            Destinations = destinations, RequiredVerifiedReplicaCount = 2,
            StorageProvider = destinations[0].StorageProvider, StorageConnectionId = destinations[0].StorageConnectionId, StorageExecutionLocation = destinations[0].StorageExecutionLocation
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

        var request = await Factory(storage).CreateAsync(Snapshot(ThreeDestinations), default);

        Assert.Equal(3, request.Destinations.Count);
        Assert.Equal(2, request.RequiredVerifiedReplicas);
        Assert.Equal([Central, Sftp, Drive], request.Destinations.Select(d => d.ConnectionId!.Value));
        // Central is written by the agent's own provider; only sftp and Google Drive go through the relay, each bound to ITS destination.
        Assert.Null(request.Destinations[0].StorageOverride);
        Assert.Equal(["sftp", "google-drive"], storage.Destinations.Select(d => d.StorageProvider));
        Assert.Equal([Sftp, Drive], storage.Destinations.Select(d => d.StorageConnectionId!.Value));
        Assert.Single(request.Destinations, d => d.IsPrimary);
    }

    [Fact]
    public async Task A_legacy_snapshot_without_destinations_still_runs_as_one_destination_through_the_legacy_fields()
    {
        var snapshot = Snapshot(ThreeDestinations[0]) with { };
        snapshot = snapshot with { Configuration = snapshot.Configuration with { Destinations = [], RequiredVerifiedReplicaCount = 1 } };

        var request = await Factory().CreateAsync(snapshot, default);

        Assert.Empty(request.Destinations);
        var only = Assert.Single(request.EffectiveDestinations);
        Assert.Equal("central", only.Storage.Value);
    }

    public static TheoryData<string> Unsupported => new()
    {
        "unknown-provider", "relayed-on-agent", "relayed-without-connection", "required-too-high", "no-primary", "duplicate"
    };

    [Theory]
    [MemberData(nameof(Unsupported))]
    public async Task An_unsupported_destination_combination_is_refused_not_skipped(string kind)
    {
        var destinations = kind switch
        {
            "unknown-provider" => [ThreeDestinations[0], new BackupDestinationContract { StorageProvider = "s3", StorageConnectionId = Guid.NewGuid(), StorageExecutionLocation = "Backend" }],
            "relayed-on-agent" => [ThreeDestinations[0], ThreeDestinations[1] with { StorageExecutionLocation = "Agent" }],
            "relayed-without-connection" => [ThreeDestinations[0], ThreeDestinations[1] with { StorageConnectionId = null }],
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

    [Fact]
    public void A_partial_success_publishes_only_verified_replicas_and_reports_every_destination()
    {
        var targets = Targets();
        var key = new StorageObjectKey("t/j/r/a/Sales_20261010T120000Z_aaaaaaaa_FULL.bak");
        var timeout = new BackupFailure(BackupFailureKind.StorageWriteFailed, BackupExecutionStage.Uploading, BackupPipelineReasons.DestinationTimeout, "slow", BackupFailureCause.Timeout);
        var result = Result(DatabaseBackupStatus.Succeeded, null, Verified(targets[0], key), Verified(targets[1], key), Failed(targets[2], timeout));

        var completion = BackupTaskResultMapper.Map(Snapshot(ThreeDestinations), result);

        Assert.Equal("Succeeded", completion.Status);
        var replicas = completion.Artifact!.Replicas;
        Assert.Equal(["central", "sftp"], replicas.Select(r => r.StorageProvider));
        Assert.Equal([Central, Sftp], replicas.Select(r => r.StorageConnectionId!.Value));
        Assert.Equal(Sftp.ToString("D"), replicas[1].StorageTarget);
        Assert.Equal("central", replicas[0].StorageTarget);
        Assert.Equal(3, completion.Destinations.Count);
        Assert.Equal(["Verified", "Verified", "Failed"], completion.Destinations.Select(d => d.State));
        var failed = completion.Destinations[2];
        Assert.Equal("OperationTimedOut", failed.FailureCode);
        Assert.Equal("Failed", failed.Upload);
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
        Assert.Null(completion.Artifact);
        Assert.All(completion.Destinations, d => Assert.Equal("Failed", d.State));
        var mismatch = completion.Destinations[1];
        Assert.Equal("Succeeded", mismatch.Upload);
        Assert.Equal("Failed", mismatch.Verification);
        Assert.True(mismatch.ObjectMayRemain);
        Assert.Equal(key.Value, mismatch.Key);
        Assert.Equal("ReplicaVerificationFailed", mismatch.FailureCode);
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
}
