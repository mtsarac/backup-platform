using System.Collections.Concurrent;
using System.Security.Cryptography;
using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Agent.Core.Tasks;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Contracts.Downloads;
using BackupPlatform.StorageProviders.Abstractions;
using BackupPlatform.StorageProviders.Local;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace BackupPlatform.AgentTests.Support;

internal sealed class DownloadTaskHarness : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private Task? _run;
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "download-agent-test-" + Guid.NewGuid().ToString("N"));
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 8, 0, 0, 0, TimeSpan.Zero));
    public FakeCredentialStore Identities { get; } = new(TestIdentity.Create());
    public FakeDownloadClient Client { get; }
    public DownloadTaskWorker Worker { get; }

    public DownloadTaskHarness()
    {
        Directory.CreateDirectory(Root);
        Client = new FakeDownloadClient(Time);
        Worker = new DownloadTaskWorker(Client, Identities, [new LocalStorageProvider(new LocalStorageOptions(Root))],
            new AgentSoftwareInfo("1.0.0", "linux", null, null), Time, NullLogger<DownloadTaskWorker>.Instance);
    }

    public AgentTaskEnvelope Envelope(byte[] bytes, string key = "tenant/artifact.bak")
    {
        var session = Guid.NewGuid();
        return new AgentTaskEnvelope
        {
            Id = session.ToString("D"), Type = DownloadTaskTypes.BackupDownload,
            Download = new DownloadTaskSnapshot
            {
                SessionId = session, TenantId = Guid.NewGuid(), ArtifactId = Guid.NewGuid(), ReplicaId = Guid.NewGuid(),
                LeaseToken = Guid.NewGuid(), LeaseExpiresAt = Time.GetUtcNow().AddSeconds(60),
                StorageProvider = "local", StorageTarget = "local", StorageKey = key,
                SizeBytes = bytes.LongLength, Checksum = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()
            }
        };
    }

    public async Task PutAsync(AgentTaskEnvelope envelope, byte[] bytes)
    {
        var provider = new LocalStorageProvider(new LocalStorageOptions(Root));
        await using var source = new MemoryStream(bytes, writable: false);
        await provider.PutAsync(new StorageObjectKey(envelope.Download!.StorageKey), source, new StoragePutOptions(StorageOverwritePolicy.Fail), null, default);
    }

    public void Start() => _run = Worker.RunAsync(_stop.Token);

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_run is not null) await _run.WaitAsync(TimeSpan.FromSeconds(5));
        _stop.Dispose();
        Directory.Delete(Root, recursive: true);
    }
}

internal sealed class FakeDownloadClient(TimeProvider time) : IDownloadTaskClient
{
    public ConcurrentQueue<BackupTaskStartRequest> Starts { get; } = new();
    public ConcurrentQueue<BackupTaskLeaseRequest> Renewals { get; } = new();
    public ConcurrentQueue<DownloadTaskCompletionRequest> Completions { get; } = new();
    public ConcurrentQueue<DownloadSignalRequest> Signals { get; } = new();
    public Func<CancellationToken, Task<AgentBackendResult<DownloadSignal>>>? OnSignal { get; set; }
    public Func<AgentBackendResult<BackupTaskLeaseResponse>>? OnRenew { get; set; }
    public Func<AgentBackendResult<BackupTaskLeaseResponse>>? OnStart { get; set; }

    public Task<AgentBackendResult<BackupTaskLeaseResponse>> StartAsync(AgentLocalIdentity identity, Guid sessionId, BackupTaskStartRequest request, CancellationToken cancellationToken)
    {
        Starts.Enqueue(request);
        return Task.FromResult(OnStart?.Invoke() ?? Lease());
    }

    public Task<AgentBackendResult<BackupTaskLeaseResponse>> RenewAsync(AgentLocalIdentity identity, Guid sessionId, BackupTaskLeaseRequest request, CancellationToken cancellationToken)
    {
        Renewals.Enqueue(request);
        return Task.FromResult(OnRenew?.Invoke() ?? Lease());
    }

    public Task<AgentBackendResult<DownloadSignal>> SignalAsync(AgentLocalIdentity identity, Guid sessionId, DownloadSignalRequest request, CancellationToken cancellationToken)
    {
        Signals.Enqueue(request);
        return OnSignal?.Invoke(cancellationToken) ?? Task.FromResult(AgentBackendResult<DownloadSignal>.Success(new DownloadSignal(null)));
    }

    public Task<AgentBackendResult<BackupPlatform.Contracts.Restores.ArtifactKeyUnwrapResponse>> KeyAsync(AgentLocalIdentity identity, Guid sessionId, DownloadSignalRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(Failures.Fail<BackupPlatform.Contracts.Restores.ArtifactKeyUnwrapResponse>(AgentBackendFailureKind.Rejected));

    public Task<AgentBackendResult<bool>> CompleteAsync(AgentLocalIdentity identity, Guid sessionId, DownloadTaskCompletionRequest request, CancellationToken cancellationToken)
    {
        Completions.Enqueue(request);
        return Task.FromResult(AgentBackendResult<bool>.Success(true));
    }

    private AgentBackendResult<BackupTaskLeaseResponse> Lease() => AgentBackendResult<BackupTaskLeaseResponse>.Success(new BackupTaskLeaseResponse
    {
        LeaseExpiresAt = time.GetUtcNow().AddSeconds(60)
    });
}
