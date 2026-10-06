using System.Collections.Concurrent;
using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Agent.Core.Tasks;
using BackupPlatform.Agent.Host;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.StorageProviders.Abstractions;
using BackupPlatform.StorageProviders.Local;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace BackupPlatform.AgentTests.Support;

internal sealed class DeleteReplicaHarness : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private Task? _running;
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "replica-delete-" + Guid.NewGuid().ToString("N"));
    public DeletionTestClock Time { get; } = new();
    public FakeCredentialStore Store { get; } = new(TestIdentity.Create());
    public DeletionClient Client { get; }
    public LocalStorageProvider Local { get; }
    public RecordingDeletionProvider Storage { get; }
    public DeleteReplicaTaskWorker Worker { get; }

    public DeleteReplicaHarness()
    {
        Directory.CreateDirectory(Root);
        Local = new LocalStorageProvider(new LocalStorageOptions(Root));
        Storage = new RecordingDeletionProvider(Local);
        Client = new DeletionClient(Time);
        Worker = new DeleteReplicaTaskWorker(Client, Store, new RecordingDeletionResolver(Local, Storage), Time, NullLogger<DeleteReplicaTaskWorker>.Instance);
    }

    public AgentTaskEnvelope Envelope(string prefix = "backups")
    {
        var task = new DeleteReplicaTaskSnapshot
        {
            ReplicaId = Guid.NewGuid(), TenantId = Guid.NewGuid(), AgentId = TestIdentity.AgentId,
            OperationId = Guid.NewGuid(), LeaseToken = Guid.NewGuid(), LeaseExpiresAt = Time.GetUtcNow().AddSeconds(120),
            JobId = Guid.NewGuid(), RunId = Guid.NewGuid(), AttemptId = Guid.NewGuid(),
            StorageProvider = "local", StorageTarget = "local", StoragePrefix = prefix
        };
        var scope = ManagedBackupLayout.AttemptScope(task.TenantId, task.JobId, task.RunId, task.AttemptId);
        var name = ManagedBackupLayout.StagedFileName("Sales", Time.GetUtcNow(), Guid.NewGuid());
        task = task with { StorageKey = (prefix.Length == 0 ? scope : $"{prefix}/{scope}") + "/" + name };
        return new AgentTaskEnvelope { Type = "delete-replica", Id = task.ReplicaId.ToString(), DeleteReplica = task };
    }

    public async Task PutAsync(DeleteReplicaTaskSnapshot task)
    {
        using var content = new MemoryStream([1, 2, 3, 4]);
        await Local.PutAsync(new StorageObjectKey(task.StorageKey), content, new StoragePutOptions(StorageOverwritePolicy.Fail), null, default);
    }

    public string ObjectPath(DeleteReplicaTaskSnapshot task) => Path.Combine(Root, task.StorageKey.Replace('/', Path.DirectorySeparatorChar));
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
        Directory.Delete(Root, true);
    }
}
internal sealed class DeletionTestClock : FakeTimeProvider
{
    private int _timersCreated;

    public DeletionTestClock() : base(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)) { }

    public int TimersCreated => Volatile.Read(ref _timersCreated);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = base.CreateTimer(callback, state, dueTime, period);
        Interlocked.Increment(ref _timersCreated);
        return timer;
    }
}


internal sealed class DeletionClient(TimeProvider time) : IDeleteReplicaTaskClient
{
    public ConcurrentQueue<DeleteReplicaLeaseRequest> Renewals { get; } = new();
    public ConcurrentQueue<DeleteReplicaCompletionRequest> Completions { get; } = new();
    public Func<DeleteReplicaLeaseRequest, CancellationToken, Task<AgentBackendResult<DeleteReplicaLeaseResponse>>>? OnRenew { get; set; }
    public Func<DeleteReplicaCompletionRequest, CancellationToken, Task<AgentBackendResult<bool>>>? OnComplete { get; set; }

    public Task<AgentBackendResult<DeleteReplicaLeaseResponse>> RenewAsync(AgentLocalIdentity identity, Guid replicaId, DeleteReplicaLeaseRequest request, CancellationToken cancellationToken)
    {
        Renewals.Enqueue(request);
        return OnRenew?.Invoke(request, cancellationToken) ?? Task.FromResult(AgentBackendResult<DeleteReplicaLeaseResponse>.Success(
            new DeleteReplicaLeaseResponse { LeaseExpiresAt = time.GetUtcNow().AddSeconds(120) }));
    }

    public Task<AgentBackendResult<bool>> CompleteAsync(AgentLocalIdentity identity, Guid replicaId, DeleteReplicaCompletionRequest request, CancellationToken cancellationToken)
    {
        Completions.Enqueue(request);
        return OnComplete?.Invoke(request, cancellationToken) ?? Task.FromResult(AgentBackendResult<bool>.Success(true));
    }
}

internal sealed class RecordingDeletionResolver(LocalStorageProvider local, RecordingDeletionProvider recording) : IDeleteReplicaProviderResolver
{
    private readonly DeleteReplicaProviderResolver _resolver = new([local]);

    public DeleteReplicaProviderResolution Resolve(string provider, string target)
    {
        var result = _resolver.Resolve(provider, target);
        return result.Provider is null ? result : result with { Provider = recording };
    }
}

/// <summary>Instrumentation only: storage outcomes always come from the real filesystem provider.</summary>
internal sealed class RecordingDeletionProvider(IStorageProvider inner) : IStorageProvider
{
    public ConcurrentQueue<string> Calls { get; } = new();
    public Func<CancellationToken, Task>? BeforeDelete { get; set; }
    public StorageProviderId Id => inner.Id;
    public StorageTopology Topology => inner.Topology;
    public StorageCapabilities Capabilities => inner.Capabilities;

    public async Task<bool> DeleteAsync(StorageObjectKey key, CancellationToken cancellationToken)
    {
        Calls.Enqueue("delete");
        if (BeforeDelete is { } before)
        {
            await before(cancellationToken);
        }
        return await inner.DeleteAsync(key, cancellationToken);
    }

    public Task<bool> ExistsAsync(StorageObjectKey key, CancellationToken cancellationToken)
    {
        Calls.Enqueue("exists");
        return inner.ExistsAsync(key, cancellationToken);
    }

    public Task<StorageObjectMetadata> PutAsync(StorageObjectKey key, Stream content, StoragePutOptions options, IProgress<long>? progress, CancellationToken cancellationToken) =>
        inner.PutAsync(key, content, options, progress, cancellationToken);
    public Task<StorageObjectMetadata?> GetMetadataAsync(StorageObjectKey key, CancellationToken cancellationToken) => inner.GetMetadataAsync(key, cancellationToken);
    public Task<StorageVerificationResult> VerifyAsync(StorageObjectKey key, StorageVerificationExpectation expected, CancellationToken cancellationToken) => inner.VerifyAsync(key, expected, cancellationToken);
    public IAsyncEnumerable<StorageEntry> EnumerateAsync(StorageObjectKey? prefix, DateTimeOffset modifiedBefore, CancellationToken cancellationToken) => inner.EnumerateAsync(prefix, modifiedBefore, cancellationToken);
}
