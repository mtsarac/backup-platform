using System.Threading.Channels;
using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Contracts.Downloads;
using BackupPlatform.StorageProviders.Abstractions;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.Agent.Core.Tasks;

/// <summary>Reads only configured provider objects claimed by authenticated polling. A verified open handle is streamed outbound; the backend verifies its own copy before allowing a browser download.</summary>
public sealed partial class DownloadTaskWorker(
    IDownloadTaskClient client, IAgentCredentialStore identities, IEnumerable<IStorageProvider> providers,
    AgentSoftwareInfo software, TimeProvider time, ILogger<DownloadTaskWorker> logger, LocalVolumes? volumes = null) : IAgentTaskSink
{
    private readonly Channel<DownloadTaskSnapshot> _queue = Channel.CreateBounded<DownloadTaskSnapshot>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait,
        AllowSynchronousContinuations = false
    });
    private readonly IReadOnlyDictionary<StorageProviderId, IStorageProvider> _providers = providers.ToDictionary(provider => provider.Id);
    private readonly Dictionary<Guid, DateTimeOffset> _seen = [];
    private readonly object _gate = new();
    private Guid? _activeSession;
    private int _started;

    public bool TryPublish(AgentTaskEnvelope envelope)
    {
        var now = time.GetUtcNow();
        if (envelope is not { Type: DownloadTaskTypes.BackupDownload, Download: { Version: 1 } task }
            || !Guid.TryParse(envelope.Id, out var id) || id != task.SessionId
            || task.SessionId == Guid.Empty || task.TenantId == Guid.Empty || task.ArtifactId == Guid.Empty
            || task.ReplicaId == Guid.Empty || task.LeaseToken == Guid.Empty || task.LeaseExpiresAt <= now)
        {
            return false;
        }
        lock (_gate)
        {
            foreach (var entry in _seen)
            {
                if (entry.Value <= now && entry.Key != _activeSession) _seen.Remove(entry.Key);
            }
            if (_seen.ContainsKey(id) || !_queue.Writer.TryWrite(task)) return false;
            _seen.Add(id, task.LeaseExpiresAt);
            return true;
        }
    }

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("The download worker can only run once.");
        try
        {
            await foreach (var task in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                lock (_gate) _activeSession = task.SessionId;
                try
                {
                    await ExecuteAsync(task, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    logger.LogWarning("Download session {SessionId} stopped ({ExceptionType}).", task.SessionId, exception.GetType().Name);
                }
                finally
                {
                    lock (_gate) _activeSession = null;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task ExecuteAsync(DownloadTaskSnapshot task, CancellationToken stoppingToken)
    {
        if (task.LeaseExpiresAt <= time.GetUtcNow()) return;
        using var lease = new BackupTaskLease(task.LeaseExpiresAt, time);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, lease.Token);
        var started = await CallAsync(identity => client.StartAsync(identity, task.SessionId, new BackupTaskStartRequest
        {
            LeaseToken = task.LeaseToken,
            SoftwareVersion = software.Version,
            ProtocolVersion = AgentProtocol.Current.Version.ToString(),
            Platform = software.Platform
        }, lifetime.Token), lifetime.Token).ConfigureAwait(false);
        if (!started.IsSuccess || !lease.Renew(started.Value.LeaseExpiresAt)) return;
        using var requestedCancel = new CancellationTokenSource();
        if (started.Value.CancellationRequested) requestedCancel.Cancel();
        using var transfer = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, requestedCancel.Token);
        using var maintenance = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var renewal = RenewLoopAsync(task, lease, requestedCancel, maintenance.Token);
        try
        {
            var failure = await TransferAsync(task, transfer.Token).ConfigureAwait(false);
            if (failure is not null && !lifetime.IsCancellationRequested)
            {
                await CompleteAsync(task, failure, lifetime.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            await maintenance.CancelAsync().ConfigureAwait(false);
            await renewal.ConfigureAwait(false);
            lock (_gate) _seen[task.SessionId] = lease.ExpiresAt;
        }
    }

    private async Task<string?> TransferAsync(DownloadTaskSnapshot task, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (provider, key, checksum) = ResolveSource(task);
            await using var content = await provider.OpenReadAsync(key, cancellationToken).ConfigureAwait(false);
            if (!content.CanRead || !content.CanSeek) return DownloadAgentFailureCodes.SourceInaccessible;
            if (content.Length != task.SizeBytes) return DownloadAgentFailureCodes.IntegrityMismatch;
            var actual = await ContentHasher.ComputeSha256Async(content, null, cancellationToken).ConfigureAwait(false);
            if (actual != checksum || content.Position != task.SizeBytes) return DownloadAgentFailureCodes.IntegrityMismatch;
            content.Position = 0;
            var response = await CallAsync(identity => client.UploadAsync(identity, task, content, cancellationToken), cancellationToken).ConfigureAwait(false);
            return response.IsSuccess ? null : DownloadAgentFailureCodes.AgentUnavailable;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return DownloadAgentFailureCodes.Cancelled;
        }
        catch (ArgumentException)
        {
            return DownloadAgentFailureCodes.InvalidConfiguration;
        }
        catch (StorageException exception)
        {
            return exception.Kind switch
            {
                StorageFailureKind.NotFound => DownloadAgentFailureCodes.SourceMissing,
                StorageFailureKind.IntegrityMismatch => DownloadAgentFailureCodes.IntegrityMismatch,
                StorageFailureKind.InvalidConfiguration => DownloadAgentFailureCodes.InvalidConfiguration,
                _ => DownloadAgentFailureCodes.SourceInaccessible
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return DownloadAgentFailureCodes.SourceInaccessible;
        }
        catch (Exception)
        {
            return DownloadAgentFailureCodes.InternalError;
        }
    }

    private (IStorageProvider Provider, StorageObjectKey Key, ContentChecksum Checksum) ResolveSource(DownloadTaskSnapshot task)
    {
        if (task.StorageProvider != "local" || task.ChecksumAlgorithm != "sha256" || task.SizeBytes <= 0
            || task.StorageTarget != "local" && !LocalVolumes.IsValidName(task.StorageTarget))
        {
            throw new ArgumentException("The download source is invalid.");
        }
        var providerId = new StorageProviderId(task.StorageProvider);
        var key = new StorageObjectKey(task.StorageKey);
        var checksum = new ContentChecksum(ChecksumAlgorithm.Sha256, task.Checksum);
        var found = task.StorageTarget == "local" ? _providers.GetValueOrDefault(providerId) : volumes?.Resolve(task.StorageTarget);
        if (found is not { } provider || provider.Topology != StorageTopology.AgentDirect
            || !provider.Capabilities.Supports(StorageCapabilityIds.Read))
        {
            throw new StorageException(StorageFailureKind.NotSupported, "The download source is inaccessible.");
        }
        return (provider, key, checksum);
    }
}
