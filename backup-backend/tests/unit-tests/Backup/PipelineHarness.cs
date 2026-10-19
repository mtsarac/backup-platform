using BackupPlatform.Agent.Core.Backup;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.StorageProviders.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackupPlatform.UnitTests.Backup;

/// <summary>A staging directory on the real file system plus a fake engine provider and an in-memory storage, wired into the real pipeline.</summary>
internal sealed class PipelineHarness : IDisposable
{
    public const string Database = "Sales";
    public static readonly byte[] BackupBytes = Enumerable.Range(0, 3 * 1024 * 1024 + 17).Select(i => (byte)(i * 31 % 251)).ToArray();

    private readonly string _root = Path.Combine(Path.GetTempPath(), "bp_test_pipeline_" + Guid.NewGuid().ToString("N")[..10]);

    public PipelineHarness(IArtifactPreparer? preparer = null, StagingUsageRegistry? usage = null)
    {
        Directory.CreateDirectory(_root);
        StagingDirectory = Path.Combine(_root, "staging");
        Directory.CreateDirectory(StagingDirectory);
        Staging = StagingArea.SameHost(StagingDirectory);
        Provider = new FakeBackupProvider(this);
        Storage = new InMemoryStorage(this);
        Pipeline = new DatabaseBackupPipeline(
            [Provider], [Storage], preparer ?? new PassThroughArtifactPreparer(), NullLogger<DatabaseBackupPipeline>.Instance, staging: usage);
    }

    public string StagingDirectory { get; }
    public StagingArea Staging { get; }
    public FakeBackupProvider Provider { get; }
    public InMemoryStorage Storage { get; }
    public DatabaseBackupPipeline Pipeline { get; }
    public List<BackupProgress> Progress { get; } = [];

    private readonly List<IDisposable> _held = [];

    /// <summary>Keeps a resource (for example an open file handle) alive until the harness is disposed.</summary>
    public void Hold(IDisposable resource) => _held.Add(resource);

    public string[] StagedFiles => Directory.GetFileSystemEntries(StagingDirectory).Select(Path.GetFileName).Order().ToArray()!;

    public DatabaseBackupPipelineRequest Request(
        FullBackupOptions? options = null,
        StorageOverwritePolicy overwrite = StorageOverwritePolicy.Fail,
        StagingFailurePolicy onFailure = StagingFailurePolicy.Delete,
        StorageProviderId? storage = null) =>
        new(
            DatabaseEngineId.MsSql,
            new DatabaseConnectionSettings(new DatabaseEndpoint("sql.test")),
            new PasswordDatabaseCredential("sa", new DatabaseSecret("SECRET-PASSWORD-123")),
            Database,
            Staging,
            storage ?? StorageProviderId.Local,
            new StorageObjectKey("tenant-a/mssql/sales"),
            options,
            overwrite,
            stagingOnFailure: onFailure);

    public Task<DatabaseBackupPipelineResult> RunAsync(DatabaseBackupPipelineRequest? request = null, CancellationToken cancellationToken = default) =>
        Pipeline.RunFullBackupAsync(request ?? Request(), new ListProgress(Progress), cancellationToken);

    public void Dispose()
    {
        foreach (var resource in _held)
        {
            resource.Dispose();
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    public static FullBackupResult SucceededResult(
        FullBackupRequest request,
        long size,
        AppliedFullBackupOptions? applied = null,
        IEnumerable<BackupWarning>? warnings = null, DatabaseEngineId? engine = null) =>
        new(
            FullBackupStatus.Succeeded,
            failure: null,
            Source(engine),
            new BackupOptionsOutcome(request.Options, applied ?? new AppliedFullBackupOptions(false, false, false)),
            DateTimeOffset.UtcNow.AddSeconds(-5),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddSeconds(-4),
            DateTimeOffset.UtcNow.AddSeconds(-2),
            new StagedBackupFile(request.Staging.ServerDirectory.Combine(request.StagedFileName), new AgentPath(Path.Combine(request.Staging.AgentDirectory.Value, request.StagedFileName)), request.StagedFileName, size),
            new BackupVerification(BackupVerificationStatus.Passed, "restore-verifyonly", DateTimeOffset.UtcNow.AddSeconds(-2), DateTimeOffset.UtcNow),
            warnings ?? [],
            StagingCleanup.NotNeeded);

    public static FullBackupResult FailedResult(
        FullBackupStatus status,
        BackupFailure failure,
        StagingCleanup? cleanup = null) =>
        new(
            status,
            failure,
            Source(),
            options: null,
            DateTimeOffset.UtcNow.AddSeconds(-1),
            DateTimeOffset.UtcNow,
            backupStartedAt: null,
            backupFinishedAt: null,
            stagedFile: null,
            BackupVerification.NotRun,
            [],
            cleanup ?? StagingCleanup.NotNeeded);

    public static DatabaseBackupSource Source(DatabaseEngineId? engine = null) =>
        new(
            new DatabaseInstanceInfo(engine ?? DatabaseEngineId.MsSql, "SRV", null, "17.0.5005.3", "Enterprise Developer Edition", "Linux"),
            Database,
            new Dictionary<string, string> { ["recovery-model"] = "Full" });

    private sealed class ListProgress(List<BackupProgress> sink) : IProgress<BackupProgress>
    {
        public void Report(BackupProgress value)
        {
            lock (sink)
            {
                sink.Add(value);
            }
        }
    }
}

internal sealed class FakeBackupProvider(PipelineHarness harness) : IDatabaseBackupProvider
{
    public DatabaseEngineId Engine { get; set; } = DatabaseEngineId.MsSql;

    public BackupPreCheckReport PreCheck { get; set; } = new([new BackupPreCheckItem("database.exists", BackupPreCheckStatus.Passed, "ok", "The database exists.")]);

    public Func<FullBackupRequest, IProgress<BackupProgress>?, CancellationToken, Task<FullBackupResult>>? Execute { get; set; }

    public List<FullBackupRequest> Requests { get; } = [];

    public FullBackupPreCheckRequest? PreCheckRequest { get; private set; }

    public Task<DatabaseInstanceDiscovery> DiscoverAsync(DatabaseConnectionSettings connection, DatabaseCredential credential, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<BackupPreCheckReport> PreCheckFullBackupAsync(
        DatabaseConnectionSettings connection,
        DatabaseCredential credential,
        FullBackupPreCheckRequest request,
        CancellationToken cancellationToken)
    {
        PreCheckRequest = request;
        return Task.FromResult(PreCheck);
    }

    public Task<FullBackupResult> ExecuteFullBackupAsync(
        DatabaseConnectionSettings connection,
        DatabaseCredential credential,
        FullBackupRequest request,
        IProgress<BackupProgress>? progress,
        CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (Execute is { } custom)
        {
            return custom(request, progress, cancellationToken);
        }

        return Task.FromResult(WriteBackup(request, progress));
    }

    /// <summary>What a real provider does on success: leaves a file in the staging directory and reports its own stages.</summary>
    public FullBackupResult WriteBackup(FullBackupRequest request, IProgress<BackupProgress>? progress, byte[]? content = null)
    {
        var bytes = content ?? PipelineHarness.BackupBytes;
        progress?.Report(new BackupProgress(BackupExecutionStage.Preparing, null, null, null, DateTimeOffset.UtcNow));
        progress?.Report(new BackupProgress(BackupExecutionStage.BackingUp, 50, null, null, DateTimeOffset.UtcNow));
        File.WriteAllBytes(Path.Combine(harness.StagingDirectory, request.StagedFileName), bytes);
        progress?.Report(new BackupProgress(BackupExecutionStage.Verifying, null, null, null, DateTimeOffset.UtcNow));
        return PipelineHarness.SucceededResult(request, bytes.Length, engine: Engine);
    }
}

internal sealed class InMemoryStorage(PipelineHarness harness) : IStorageProvider
{
    private readonly Dictionary<string, byte[]> _objects = [];

    public StorageProviderId Id => StorageProviderId.Local;
    public StorageTopology Topology => StorageTopology.AgentDirect;
    public StorageCapabilities Capabilities { get; set; } = new([StorageCapabilityIds.Delete]);

    public Func<StorageObjectKey, Stream, StoragePutOptions, CancellationToken, Task<StorageObjectMetadata>>? OnPut { get; set; }
    public Func<StorageObjectKey, StorageVerificationExpectation, StorageVerificationResult?>? OnVerify { get; set; }
    public Func<StorageObjectKey, Task>? OnDelete { get; set; }

    /// <summary>The staged files that existed at the moment the replica was verified.</summary>
    public string[]? StagedFilesDuringVerify { get; private set; }

    public IReadOnlyDictionary<string, byte[]> Objects => _objects;

    public async Task<StorageObjectMetadata> PutAsync(
        StorageObjectKey key, Stream content, StoragePutOptions options, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        if (OnPut is { } custom)
        {
            return await custom(key, content, options, cancellationToken);
        }

        if (_objects.ContainsKey(key.Value) && options.Overwrite == StorageOverwritePolicy.Fail)
        {
            throw new StorageException(StorageFailureKind.AlreadyExists, "An object with this key already exists.");
        }

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        var bytes = buffer.ToArray();
        progress?.Report(bytes.Length);
        _objects[key.Value] = bytes;
        return new StorageObjectMetadata(new StorageLocation(Id, key), bytes.Length, Checksum(bytes), DateTimeOffset.UtcNow);
    }

    public Task<bool> ExistsAsync(StorageObjectKey key, CancellationToken cancellationToken) => Task.FromResult(_objects.ContainsKey(key.Value));

    public Task<StorageObjectMetadata?> GetMetadataAsync(StorageObjectKey key, CancellationToken cancellationToken) =>
        Task.FromResult(_objects.TryGetValue(key.Value, out var bytes)
            ? new StorageObjectMetadata(new StorageLocation(Id, key), bytes.Length, null, DateTimeOffset.UtcNow)
            : null);

    public Task<StorageVerificationResult> VerifyAsync(StorageObjectKey key, StorageVerificationExpectation expected, CancellationToken cancellationToken)
    {
        StagedFilesDuringVerify = harness.StagedFiles;
        if (OnVerify?.Invoke(key, expected) is { } custom)
        {
            return Task.FromResult(custom);
        }

        var location = new StorageLocation(Id, key);
        if (!_objects.TryGetValue(key.Value, out var bytes))
        {
            return Task.FromResult(new StorageVerificationResult(location, StorageVerificationStatus.Missing, StorageVerificationMethod.ReadBack, null, null));
        }

        var checksum = Checksum(bytes);
        var status = bytes.Length != expected.SizeBytes ? StorageVerificationStatus.SizeMismatch
            : checksum != expected.Checksum ? StorageVerificationStatus.ChecksumMismatch
            : StorageVerificationStatus.Verified;
        return Task.FromResult(new StorageVerificationResult(location, status, StorageVerificationMethod.ReadBack, bytes.Length, checksum));
    }

    public async Task<bool> DeleteAsync(StorageObjectKey key, CancellationToken cancellationToken)
    {
        if (OnDelete is { } custom)
        {
            await custom(key);
        }

        return _objects.Remove(key.Value);
    }

    public async IAsyncEnumerable<StorageEntry> EnumerateAsync(
        StorageObjectKey? prefix,
        DateTimeOffset modifiedBefore,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        throw new StorageException(StorageFailureKind.NotSupported, "This storage cannot enumerate.");
        #pragma warning disable CS0162
        yield break;
        #pragma warning restore CS0162
    }

    public static ContentChecksum Checksum(byte[] bytes) => ContentChecksum.Sha256(System.Security.Cryptography.SHA256.HashData(bytes));
}
