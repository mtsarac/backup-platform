using BackupPlatform.Agent.Core.Backup;
using BackupPlatform.Agent.Core.Restore;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.StorageProviders.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackupPlatform.UnitTests.RestorePipeline;

/// <summary>
/// A real staging directory on the file system plus a fake restore provider and an in-memory readable storage, wired into the real restore
/// pipeline. The replica is a few megabytes so that the copy runs through several blocks.
/// </summary>
internal sealed class RestorePipelineHarness : IDisposable
{
    public const string Database = "SalesRestored";
    public static readonly byte[] BackupBytes = Enumerable.Range(0, 2 * 1024 * 1024 + 4321).Select(i => (byte)(i * 31 % 251)).ToArray();

    private readonly string _root = Path.Combine(Path.GetTempPath(), "bp_test_restore_" + Guid.NewGuid().ToString("N")[..10]);

    public RestorePipelineHarness(bool withProvider = true, bool withStorage = true, StagingUsageRegistry? usage = null)
    {
        Directory.CreateDirectory(_root);
        StagingDirectory = Path.Combine(_root, "staging");
        Directory.CreateDirectory(StagingDirectory);
        Staging = StagingArea.SameHost(StagingDirectory);
        Usage = usage ?? new StagingUsageRegistry();
        Tenant = Guid.NewGuid();
        Key = new StorageObjectKey(
            $"backups/{Tenant:N}/{Guid.NewGuid():N}/{Guid.NewGuid():N}/{Guid.NewGuid():N}/{ManagedBackupLayout.StagedFileName("Sales", DateTimeOffset.UtcNow.AddDays(-1), Guid.NewGuid())}");
        Provider = new FakeRestoreProvider(this);
        Storage = new ReadableStorage();
        Storage.Objects[Key.Value] = BackupBytes;
        Pipeline = new DatabaseRestorePipeline(
            withProvider ? [Provider] : [], withStorage ? [Storage] : [], NullLogger<DatabaseRestorePipeline>.Instance, staging: Usage);
    }

    public string StagingDirectory { get; }
    public StagingArea Staging { get; }
    public StagingUsageRegistry Usage { get; }
    public Guid Tenant { get; }
    public StorageObjectKey Key { get; }
    public FakeRestoreProvider Provider { get; }
    public ReadableStorage Storage { get; }
    public DatabaseRestorePipeline Pipeline { get; }
    public List<RestoreProgress> Progress { get; } = [];

    public string[] StagedFiles => Directory.Exists(StagingDirectory)
        ? Directory.GetFileSystemEntries(StagingDirectory).Select(path => Path.GetFileName(path)!).Order().ToArray()
        : [];

    public static ContentChecksum ChecksumOf(byte[] bytes) => ContentChecksum.Sha256(System.Security.Cryptography.SHA256.HashData(bytes));

    public DatabaseRestorePipelineRequest Request(
        DatabaseRestoreMode mode = DatabaseRestoreMode.NewDatabase,
        bool terminateActiveConnections = false,
        bool verifyChecksum = true,
        bool verifyBackup = true,
        StorageProviderId? storage = null,
        StorageObjectKey? key = null,
        Guid? tenant = null,
        long? size = null,
        ContentChecksum? checksum = null,
        StagingArea? staging = null) =>
        new(
            DatabaseEngineId.MsSql,
            new DatabaseConnectionSettings(new DatabaseEndpoint("sql.test")),
            new PasswordDatabaseCredential("sa", new DatabaseSecret("SECRET-PASSWORD-123")),
            Database,
            mode,
            staging ?? Staging,
            storage ?? StorageProviderId.Local,
            key ?? Key,
            tenant ?? Tenant,
            size ?? BackupBytes.Length,
            checksum ?? ChecksumOf(BackupBytes),
            terminateActiveConnections,
            verifyChecksum,
            verifyBackup);

    public Task<DatabaseRestorePipelineResult> RunAsync(DatabaseRestorePipelineRequest? request = null, CancellationToken cancellationToken = default) =>
        Pipeline.RunRestoreAsync(request ?? Request(), new ListProgress(Progress), cancellationToken);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    public static BackupPreCheckReport Report(params BackupPreCheckItem[] items) => new(items);

    public static BackupPreCheckItem Failed(string reason, BackupFailureCause cause = BackupFailureCause.Unclassified, string checkId = "target.check") =>
        new(checkId, BackupPreCheckStatus.Failed, reason, "A fixed sentence about " + reason + ".", cause);

    public static RestoreBackupInspection Inspection(bool verified = true) => new(
        new RestoreBackupHeader("Sales", RestoreBackupKind.Database, new Version(16, 0, 1000), "SOURCE", 160, "Latin1_General_CI_AS",
            DateTime.UtcNow.AddHours(-2), DateTime.UtcNow.AddHours(-2).AddMinutes(1), 1, 123456, true, true, true, false, Guid.NewGuid()),
        [new RestoreBackupFile("Sales", "/source/Sales.mdf", RestoreFileKind.Data, "PRIMARY", 8192), new RestoreBackupFile("Sales_log", "/source/Sales.ldf", RestoreFileKind.Log, null, 4096)],
        verified, DateTimeOffset.UtcNow.AddSeconds(-5), DateTimeOffset.UtcNow.AddSeconds(-4));

    public static RestorePlan Plan(IEnumerable<BackupWarning>? warnings = null) => new(
        [
            new RestoreFileMapping("Sales", RestoreFileKind.Data, "/source/Sales.mdf", new DatabaseServerPath("/target/SalesRestored.mdf"), 8192, "PRIMARY"),
            new RestoreFileMapping("Sales_log", RestoreFileKind.Log, "/source/Sales.ldf", new DatabaseServerPath("/target/SalesRestored_log.ldf"), 4096)
        ],
        new RestoreTargetInfo(
            new DatabaseInstanceInfo(DatabaseEngineId.MsSql, "TARGET", null, "16.0.4000", "Developer Edition", "Linux"),
            new DatabaseServerPath("/target"), new DatabaseServerPath("/target"), false, null),
        warnings);

    public static DatabaseRestoreResult Succeeded(RestorePlan plan, IEnumerable<BackupWarning>? warnings = null) => new(
        DatabaseRestoreStatus.Succeeded, null, DatabaseRestoreTargetState.Online, "ONLINE",
        DateTimeOffset.UtcNow.AddSeconds(-3), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(-2), DateTimeOffset.UtcNow.AddSeconds(-1),
        plan.Mappings, warnings ?? []);

    public static DatabaseRestoreResult NotSucceeded(
        RestorePlan plan,
        RestoreFailure failure,
        DatabaseRestoreTargetState state,
        string? databaseState = "RESTORING",
        IEnumerable<BackupWarning>? warnings = null) => new(
        failure.Kind == RestoreFailureKind.Cancelled ? DatabaseRestoreStatus.Cancelled : DatabaseRestoreStatus.Failed, failure, state, databaseState,
        DateTimeOffset.UtcNow.AddSeconds(-3), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(-2), null, plan.Mappings, warnings ?? []);

    private sealed class ListProgress(List<RestoreProgress> sink) : IProgress<RestoreProgress>
    {
        public void Report(RestoreProgress value)
        {
            lock (sink)
            {
                sink.Add(value);
            }
        }
    }
}

internal sealed class FakeRestoreProvider(RestorePipelineHarness harness) : IDatabaseRestoreProvider
{
    private readonly RestorePlan _plan = RestorePipelineHarness.Plan();

    public DatabaseEngineId Engine => DatabaseEngineId.MsSql;

    public BackupPreCheckReport PreCheck { get; set; } = new([new BackupPreCheckItem("server.reachable", BackupPreCheckStatus.Passed, "ok", "The server is reachable.")]);

    public Func<RestoreInspectionRequest, CancellationToken, Task<RestoreInspectionResult>>? Inspect { get; set; }
    public Func<RestorePlanRequest, CancellationToken, Task<RestorePlanResult>>? PlanAsync { get; set; }
    public Func<DatabaseRestoreRequest, IProgress<RestoreProgress>?, CancellationToken, Task<DatabaseRestoreResult>>? Execute { get; set; }
    public Func<RestorePreCheckRequest, CancellationToken, Task<BackupPreCheckReport>>? PreCheckAsync { get; set; }

    public RestorePlan DefaultPlan => _plan;
    public List<RestorePreCheckRequest> PreChecks { get; } = [];
    public List<RestoreInspectionRequest> Inspections { get; } = [];
    public List<RestorePlanRequest> Plans { get; } = [];
    public List<DatabaseRestoreRequest> Executions { get; } = [];
    public DatabaseCredential? LastCredential { get; private set; }

    /// <summary>What the staging directory held when the engine was asked to inspect the file: the staged copy, byte for byte.</summary>
    public (string Name, byte[] Bytes, bool Held)[] StagedAtInspect { get; private set; } = [];

    public Task<BackupPreCheckReport> PreCheckRestoreAsync(DatabaseConnectionSettings connection, DatabaseCredential credential, RestorePreCheckRequest request, CancellationToken cancellationToken)
    {
        PreChecks.Add(request);
        LastCredential = credential;
        return PreCheckAsync is { } custom ? custom(request, cancellationToken) : Task.FromResult(PreCheck);
    }

    public async Task<RestoreInspectionResult> InspectBackupAsync(DatabaseConnectionSettings connection, DatabaseCredential credential, RestoreInspectionRequest request, CancellationToken cancellationToken)
    {
        Inspections.Add(request);
        StagedAtInspect = Directory.GetFiles(harness.StagingDirectory)
            .Select(path => (Path.GetFileName(path), File.ReadAllBytes(path), harness.Usage.IsHeld(Path.GetFileName(path))))
            .ToArray();
        if (Inspect is { } custom)
        {
            return await custom(request, cancellationToken).ConfigureAwait(false);
        }

        return new RestoreInspectionResult(RestorePipelineHarness.Inspection(request.RunVerifyOnly), null);
    }

    public Task<RestorePlanResult> PlanRestoreAsync(DatabaseConnectionSettings connection, DatabaseCredential credential, RestorePlanRequest request, CancellationToken cancellationToken)
    {
        Plans.Add(request);
        return PlanAsync is { } custom
            ? custom(request, cancellationToken)
            : Task.FromResult(new RestorePlanResult(new BackupPreCheckReport([new BackupPreCheckItem("plan.version", BackupPreCheckStatus.Passed, "ok", "The target version can restore this backup.")]), _plan));
    }

    public Task<DatabaseRestoreResult> ExecuteRestoreAsync(DatabaseConnectionSettings connection, DatabaseCredential credential, DatabaseRestoreRequest request, IProgress<RestoreProgress>? progress, CancellationToken cancellationToken)
    {
        Executions.Add(request);
        if (Execute is { } custom)
        {
            return custom(request, progress, cancellationToken);
        }

        progress?.Report(new RestoreProgress(RestoreExecutionStage.PreparingTarget, null, null, null, DateTimeOffset.UtcNow));
        progress?.Report(new RestoreProgress(RestoreExecutionStage.Restoring, 40, null, null, DateTimeOffset.UtcNow));
        progress?.Report(new RestoreProgress(RestoreExecutionStage.Finalizing, null, null, null, DateTimeOffset.UtcNow));
        return Task.FromResult(RestorePipelineHarness.Succeeded(request.Plan));
    }
}

internal sealed class ReadableStorage : IStorageProvider
{
    public StorageProviderId Id => StorageProviderId.Local;
    public StorageTopology Topology => StorageTopology.AgentDirect;
    public StorageCapabilities Capabilities { get; set; } = new([StorageCapabilityIds.Read, StorageCapabilityIds.Delete]);

    public Dictionary<string, byte[]> Objects { get; } = [];

    /// <summary>Replaces the answer of <see cref="GetMetadataAsync"/> (a null result means "no such object").</summary>
    public Func<StorageObjectKey, Task<StorageObjectMetadata?>>? OnGetMetadata { get; set; }

    /// <summary>Replaces the stream <see cref="OpenReadAsync"/> hands out.</summary>
    public Func<StorageObjectKey, CancellationToken, Task<Stream>>? OnOpenRead { get; set; }

    public int OpenReads { get; private set; }
    public int MetadataReads { get; private set; }

    public Task<StorageObjectMetadata?> GetMetadataAsync(StorageObjectKey key, CancellationToken cancellationToken)
    {
        MetadataReads++;
        if (OnGetMetadata is { } custom)
        {
            return custom(key);
        }

        return Task.FromResult(Objects.TryGetValue(key.Value, out var bytes)
            ? new StorageObjectMetadata(new StorageLocation(Id, key), bytes.Length, null, DateTimeOffset.UtcNow)
            : null);
    }

    public Task<Stream> OpenReadAsync(StorageObjectKey key, CancellationToken cancellationToken)
    {
        OpenReads++;
        if (OnOpenRead is { } custom)
        {
            return custom(key, cancellationToken);
        }

        return Objects.TryGetValue(key.Value, out var bytes)
            ? Task.FromResult<Stream>(new MemoryStream(bytes, writable: false))
            : Task.FromException<Stream>(new StorageException(StorageFailureKind.NotFound, "The stored object does not exist."));
    }

    public Task<StorageObjectMetadata> PutAsync(StorageObjectKey key, Stream content, StoragePutOptions options, IProgress<long>? progress, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<bool> ExistsAsync(StorageObjectKey key, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<StorageVerificationResult> VerifyAsync(StorageObjectKey key, StorageVerificationExpectation expected, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<bool> DeleteAsync(StorageObjectKey key, CancellationToken cancellationToken) => throw new NotSupportedException();

    public IAsyncEnumerable<StorageEntry> EnumerateAsync(StorageObjectKey? prefix, DateTimeOffset modifiedBefore, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

/// <summary>A stream that serves its first <paramref name="firstBlock"/> bytes, then blocks until cancelled: a replica read that stalls mid-copy.</summary>
internal sealed class StallingStream(byte[] content, int firstBlock, TaskCompletionSource stalled) : Stream
{
    private int _position;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => content.Length;
    public override long Position { get => _position; set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_position >= firstBlock)
        {
            stalled.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }

        var count = Math.Min(Math.Min(buffer.Length, firstBlock - _position), content.Length - _position);
        content.AsMemory(_position, count).CopyTo(buffer);
        _position += count;
        return count;
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>A stream that serves a prefix and then fails like a broken disk or network share.</summary>
internal sealed class BrokenStream(byte[] content, int goodBytes, Exception failure) : Stream
{
    private int _position;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => content.Length;
    public override long Position { get => _position; set => throw new NotSupportedException(); }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_position >= goodBytes)
        {
            throw failure;
        }

        var count = Math.Min(Math.Min(buffer.Length, goodBytes - _position), content.Length - _position);
        content.AsMemory(_position, count).CopyTo(buffer);
        _position += count;
        return ValueTask.FromResult(count);
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
