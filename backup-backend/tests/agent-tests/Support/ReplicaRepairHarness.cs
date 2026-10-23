using System.Collections.Concurrent;
using System.Security.Cryptography;
using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Agent.Core.Tasks;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Contracts.Restores;
using BackupPlatform.StorageProviders.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackupPlatform.AgentTests.Support;

internal sealed class ReplicaRepairHarness : IAsyncDisposable
{
    public const string Source1Key = "backups/t1/a_FULL.bak";
    public const string Source2Key = "backups/t2/b_FULL.bak";
    public static readonly byte[] Content = Enumerable.Range(0, 4096).Select(i => (byte)(i * 31 + 7)).ToArray();
    public static readonly string Checksum = Convert.ToHexStringLower(SHA256.HashData(Content));

    private readonly CancellationTokenSource _stop = new();
    private Task? _running;
    public ConcurrentQueue<string> Events { get; } = new();
    public DeletionTestClock Time { get; } = new();
    public FakeCredentialStore Store { get; } = new(TestIdentity.Create());
    public RepairClient Client { get; }
    public RepairStorage Storage { get; } = new();
    public MemoryRepairProvider Source1 { get; }
    public MemoryRepairProvider Source2 { get; }
    public MemoryRepairProvider Target { get; }
    public Guid Source1Connection { get; } = Guid.NewGuid();
    public Guid Source2Connection { get; } = Guid.NewGuid();
    public Guid TargetConnection { get; } = Guid.NewGuid();
    public ReplicaRepairTaskWorker Worker { get; }

    public ReplicaRepairHarness(string targetProvider = "sftp")
    {
        Client = new RepairClient(Time, Events);
        Source1 = new MemoryRepairProvider("sftp", "source1", Events);
        Source2 = new MemoryRepairProvider("sftp", "source2", Events);
        Target = new MemoryRepairProvider(targetProvider, "target", Events);
        Source1.Seed(Source1Key, Content);
        Source2.Seed(Source2Key, Content);
        Storage.Register(Source1Connection, Source1);
        Storage.Register(Source2Connection, Source2);
        Storage.Register(TargetConnection, Target);
        Worker = new ReplicaRepairTaskWorker(Client, Store, Storage, Time, NullLogger<ReplicaRepairTaskWorker>.Instance);
    }

    public AgentTaskEnvelope Envelope(string targetProvider = "sftp") => Wrap(Snapshot(targetProvider));

    public static AgentTaskEnvelope Wrap(ReplicaRepairTaskSnapshot task) =>
        new() { Type = ReplicaLifecycleProtocol.RepairTaskType, Id = task.OperationId.ToString(), ReplicaRepair = task };

    public ReplicaRepairTaskSnapshot Snapshot(string targetProvider = "sftp") => new()
    {
        OperationId = Guid.NewGuid(), TenantId = Guid.NewGuid(), AgentId = TestIdentity.AgentId, ArtifactId = Guid.NewGuid(),
        LeaseToken = Guid.NewGuid(), LeaseExpiresAt = Time.GetUtcNow().AddSeconds(120),
        SizeBytes = Content.Length, Checksum = Checksum,
        Target = new ReplicaRepairTargetContract { StorageProvider = targetProvider, StorageConnectionId = TargetConnection },
        Sources =
        [
            new ReplicaRepairSourceContract { ReplicaId = Guid.NewGuid(), StorageProvider = "sftp", StorageConnectionId = Source1Connection, StorageKey = Source1Key },
            new ReplicaRepairSourceContract { ReplicaId = Guid.NewGuid(), StorageProvider = "sftp", StorageConnectionId = Source2Connection, StorageKey = Source2Key }
        ]
    };

    /// <summary>The only name the backend accepts for this lease: the lease-specific repair file beside the source object.</summary>
    public static string ExpectedTargetKey(ReplicaRepairTaskSnapshot task, ReplicaRepairSourceContract source) =>
        source.StorageKey[..source.StorageKey.LastIndexOf('/')] + "/" + ManagedBackupLayout.RepairFileName(task.OperationId, task.LeaseToken);

    public void Start() => _running = Worker.RunAsync(_stop.Token);

    public async Task StopAsync()
    {
        await _stop.CancelAsync();
        if (_running is not null)
        {
            await _running.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _stop.Dispose();
    }
}

internal sealed class RepairClient(TimeProvider time, ConcurrentQueue<string> events) : IReplicaRepairTaskClient
{
    public ConcurrentQueue<ReplicaRepairLeaseRequest> Renewals { get; } = new();
    public ConcurrentQueue<ReplicaRepairPinRequest> Pins { get; } = new();
    public ConcurrentQueue<ReplicaRepairProgressRequest> Progress { get; } = new();
    public ConcurrentQueue<ReplicaRepairCleanupRequest> CleanupChecks { get; } = new();
    public ConcurrentQueue<ReplicaRepairCompletionRequest> Completions { get; } = new();
    public Func<ReplicaRepairLeaseRequest, CancellationToken, Task<AgentBackendResult<ReplicaRepairLeaseResponse>>>? OnRenew { get; set; }
    public Func<ReplicaRepairPinRequest, bool> AcceptPin { get; set; } = _ => true;
    public bool CleanupAllowed { get; set; } = true;

    public Task<AgentBackendResult<ReplicaRepairLeaseResponse>> RenewAsync(AgentLocalIdentity identity, Guid operationId,
        ReplicaRepairLeaseRequest request, CancellationToken cancellationToken)
    {
        events.Enqueue("renew");
        Renewals.Enqueue(request);
        return OnRenew?.Invoke(request, cancellationToken) ?? Task.FromResult(AgentBackendResult<ReplicaRepairLeaseResponse>.Success(
            new ReplicaRepairLeaseResponse { LeaseExpiresAt = time.GetUtcNow().AddSeconds(120) }));
    }

    public Task<AgentBackendResult<bool>> PinAsync(AgentLocalIdentity identity, Guid operationId,
        ReplicaRepairPinRequest request, CancellationToken cancellationToken)
    {
        events.Enqueue("pin");
        Pins.Enqueue(request);
        return Task.FromResult(AcceptPin(request) ? AgentBackendResult<bool>.Success(true)
            : AgentBackendResult<bool>.Fail(new AgentBackendFailure(AgentBackendFailureKind.Rejected, "pin refused") { StatusCode = 409 }));
    }

    public Task<AgentBackendResult<bool>> ProgressAsync(AgentLocalIdentity identity, Guid operationId,
        ReplicaRepairProgressRequest request, CancellationToken cancellationToken)
    {
        events.Enqueue("progress");
        Progress.Enqueue(request);
        return Task.FromResult(AgentBackendResult<bool>.Success(true));
    }

    public Task<AgentBackendResult<ReplicaRepairCleanupResponse>> CleanupCheckAsync(AgentLocalIdentity identity, Guid operationId,
        ReplicaRepairCleanupRequest request, CancellationToken cancellationToken)
    {
        events.Enqueue("cleanup-check");
        CleanupChecks.Enqueue(request);
        return Task.FromResult(AgentBackendResult<ReplicaRepairCleanupResponse>.Success(new ReplicaRepairCleanupResponse { Allowed = CleanupAllowed }));
    }

    public Task<AgentBackendResult<bool>> CompleteAsync(AgentLocalIdentity identity, Guid operationId,
        ReplicaRepairCompletionRequest request, CancellationToken cancellationToken)
    {
        events.Enqueue("complete");
        Completions.Enqueue(request);
        return Task.FromResult(AgentBackendResult<bool>.Success(true));
    }
}

/// <summary>Hands out the registered provider for a scope's connection and records every scope, as the real factory binds them to a lease.</summary>
internal sealed class RepairStorage : ITaskStorageProviderFactory
{
    private readonly Dictionary<Guid, IStorageProvider> _providers = [];
    public ConcurrentQueue<DirectStorageScope> Scopes { get; } = new();

    public void Register(Guid connectionId, IStorageProvider provider) => _providers[connectionId] = provider;

    public IStorageProvider CreateScoped(DirectStorageScope scope)
    {
        Scopes.Enqueue(scope);
        return _providers[scope.ConnectionId];
    }

    public IStorageProvider CreateBackup(BackupTaskSnapshot task, BackupDestinationContract destination) => throw new NotSupportedException();
    public IStorageProvider CreateRestore(RestoreTaskSnapshot task) => throw new NotSupportedException();
}

internal sealed class MemoryRepairProvider(string providerId, string name, ConcurrentQueue<string> events) : IStorageProvider, IScopedDriveRecordSource
{
    private readonly ConcurrentDictionary<string, byte[]> _objects = new();

    public StorageProviderId Id { get; } = new(providerId);
    public StorageTopology Topology => StorageTopology.AgentDirect;
    public StorageCapabilities Capabilities { get; } = new([StorageCapabilityIds.Read, StorageCapabilityIds.Delete]);
    public Func<StorageException>? PutFailure { get; set; }
    public Func<StorageException>? OpenFailure { get; set; }
    /// <summary>Runs after the bytes are stored and before the put returns, like a transfer that is still in flight.</summary>
    public Func<CancellationToken, Task>? DuringPut { get; set; }
    public long[] ReportedProgress { get; set; } = [];
    public bool CorruptOnPut { get; set; }
    public bool DeleteLeavesObject { get; set; }
    public TaskStorageCatalogRecord? DriveRecord { get; set; }

    public void Seed(string key, byte[] content) => _objects[key] = content;
    public bool Has(string key) => _objects.ContainsKey(key);
    public byte[]? Get(string key) => _objects.GetValueOrDefault(key);

    public async Task<StorageObjectMetadata> PutAsync(StorageObjectKey key, Stream content, StoragePutOptions options, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        events.Enqueue($"put:{name}");
        if (PutFailure is { } failure)
        {
            throw failure();
        }
        if (_objects.ContainsKey(key.Value))
        {
            throw new StorageException(StorageFailureKind.AlreadyExists, "The object exists.");
        }
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        var bytes = buffer.ToArray();
        if (CorruptOnPut)
        {
            bytes[0] ^= 0xFF;
        }
        _objects[key.Value] = bytes;
        foreach (var reported in ReportedProgress)
        {
            progress?.Report(reported);
        }
        if (DuringPut is { } hook)
        {
            await hook(cancellationToken);
        }
        return new StorageObjectMetadata(new StorageLocation(Id, key), bytes.Length, null, DateTimeOffset.UnixEpoch);
    }

    public Task<Stream> OpenReadAsync(StorageObjectKey key, CancellationToken cancellationToken)
    {
        events.Enqueue($"read:{name}");
        if (OpenFailure is { } failure)
        {
            return Task.FromException<Stream>(failure());
        }
        return _objects.TryGetValue(key.Value, out var bytes)
            ? Task.FromResult<Stream>(new MemoryStream(bytes, writable: false))
            : Task.FromException<Stream>(new StorageException(StorageFailureKind.NotFound, "The object does not exist."));
    }

    public Task<StorageVerificationResult> VerifyAsync(StorageObjectKey key, StorageVerificationExpectation expected, CancellationToken cancellationToken)
    {
        var location = new StorageLocation(Id, key);
        if (!_objects.TryGetValue(key.Value, out var bytes))
        {
            return Task.FromResult(new StorageVerificationResult(location, StorageVerificationStatus.Missing, StorageVerificationMethod.ReadBack, null, null));
        }
        var actual = ContentChecksum.Sha256(SHA256.HashData(bytes));
        var status = bytes.Length != expected.SizeBytes ? StorageVerificationStatus.SizeMismatch
            : actual.Equals(expected.Checksum) ? StorageVerificationStatus.Verified : StorageVerificationStatus.ChecksumMismatch;
        return Task.FromResult(new StorageVerificationResult(location, status, StorageVerificationMethod.ReadBack, bytes.Length, actual));
    }

    public Task<bool> DeleteAsync(StorageObjectKey key, CancellationToken cancellationToken)
    {
        events.Enqueue($"delete:{name}");
        return Task.FromResult(DeleteLeavesObject || _objects.TryRemove(key.Value, out _));
    }

    public Task<TaskStorageCatalogRecord?> FindRecordAsync(StorageObjectKey key, CancellationToken cancellationToken) =>
        Task.FromResult(_objects.ContainsKey(key.Value) ? DriveRecord : null);

    public Task<bool> ExistsAsync(StorageObjectKey key, CancellationToken cancellationToken) => Task.FromResult(_objects.ContainsKey(key.Value));
    public Task<StorageObjectMetadata?> GetMetadataAsync(StorageObjectKey key, CancellationToken cancellationToken) => throw new NotSupportedException();
    public IAsyncEnumerable<StorageEntry> EnumerateAsync(StorageObjectKey? prefix, DateTimeOffset modifiedBefore, CancellationToken cancellationToken) => throw new NotSupportedException();
}
