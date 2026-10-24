using System.Threading.Channels;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.StorageProviders.Abstractions;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.Agent.Core.Tasks;

/// <summary>One independent deletion at a time; the backup worker retains its own single execution slot.</summary>
public sealed partial class DeleteReplicaTaskWorker : IAgentTaskSink
{
    private readonly Channel<DeleteReplicaTaskSnapshot> _queue = Channel.CreateBounded<DeleteReplicaTaskSnapshot>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        AllowSynchronousContinuations = false
    });
    private readonly object _gate = new();
    private readonly Dictionary<Claim, DateTimeOffset> _seen = [];
    private readonly IDeleteReplicaTaskClient _client;
    private readonly IAgentCredentialStore _identities;
    private readonly IDeleteReplicaProviderResolver _providers;
    private readonly ITaskStorageProviderFactory? _external;
    private readonly TimeProvider _time;
    private readonly ILogger<DeleteReplicaTaskWorker> _logger;
    private Claim? _active;
    private int _started;

    public DeleteReplicaTaskWorker(IDeleteReplicaTaskClient client, IAgentCredentialStore identities,
        IDeleteReplicaProviderResolver providers, TimeProvider timeProvider, ILogger<DeleteReplicaTaskWorker> logger,
        ITaskStorageProviderFactory? external = null)
    {
        _client = client;
        _identities = identities;
        _providers = providers;
        _external = external;
        _time = timeProvider;
        _logger = logger;
    }

    public bool TryPublish(AgentTaskEnvelope envelope)
    {
        var now = _time.GetUtcNow();
        if (envelope is not { Type: "delete-replica", DeleteReplica: { Version: 1 } task }
            || !Guid.TryParse(envelope.Id, out var id) || id != task.ReplicaId
            || task.ReplicaId == Guid.Empty || task.TenantId == Guid.Empty || task.AgentId == Guid.Empty
            || task.JobId == Guid.Empty || task.RunId == Guid.Empty || task.AttemptId == Guid.Empty
            || task.OperationId == Guid.Empty || task.LeaseToken == Guid.Empty || task.LeaseExpiresAt <= now)
        {
            return false;
        }
        var claim = Claim.From(task);
        lock (_gate)
        {
            foreach (var pair in _seen)
            {
                if (pair.Value <= now && pair.Key != _active)
                {
                    _seen.Remove(pair.Key);
                }
            }
            if (_seen.ContainsKey(claim) || !_queue.Writer.TryWrite(task))
            {
                return false;
            }
            _seen.Add(claim, task.LeaseExpiresAt);
            return true;
        }
    }

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("The deletion worker can only run once.");
        }
        try
        {
            await foreach (var task in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                lock (_gate)
                {
                    _active = Claim.From(task);
                }
                try
                {
                    await ExecuteAsync(task, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Shutdown or loss of the claim: a fresh backend claim can verify an already-deleted object.
                }
                catch (Exception exception)
                {
                    _logger.LogWarning("Replica deletion {ReplicaId} stopped ({ExceptionType}).", task.ReplicaId, exception.GetType().Name);
                }
                finally
                {
                    lock (_gate)
                    {
                        _active = null;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task ExecuteAsync(DeleteReplicaTaskSnapshot task, CancellationToken stoppingToken)
    {
        using var lease = new BackupTaskLease(task.LeaseExpiresAt, _time);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, lease.Token);
        if (!await AuthorizeAsync(task, lease, lifetime.Token).ConfigureAwait(false))
        {
            return;
        }
        using var maintenance = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var renewal = RenewLoopAsync(task, lease, maintenance.Token);
        try
        {
            var completion = await DeleteAsync(task, lifetime.Token).ConfigureAwait(false);
            await CompleteAsync(task, completion, lifetime.Token).ConfigureAwait(false);
        }
        finally
        {
            await maintenance.CancelAsync().ConfigureAwait(false);
            await renewal.ConfigureAwait(false);
            lock (_gate)
            {
                _seen[Claim.From(task)] = lease.ExpiresAt;
            }
        }
    }

    private async Task<DeleteReplicaCompletionRequest> DeleteAsync(DeleteReplicaTaskSnapshot task, CancellationToken cancellationToken)
    {
        try
        {
            if (!TryResolveKey(task, out var key))
            {
                return Completion(task, DeleteReplicaFailureCodes.InvalidLocation);
            }
            var resolution = StorageProviderKinds.AgentExecuted(task.StorageProvider) ? ResolveExternal(task) : _providers.Resolve(task.StorageProvider, task.StorageTarget);
            if (resolution.Provider is not { } provider)
            {
                return Completion(task, resolution.FailureCode ?? DeleteReplicaFailureCodes.InvalidConfiguration);
            }
            if (!provider.Capabilities.Supports(StorageCapabilityIds.Delete)
                || !provider.Capabilities.Supports(StorageCapabilityIds.VerifyAbsence))
            {
                return Completion(task, DeleteReplicaFailureCodes.DeleteNotSupported);
            }
            cancellationToken.ThrowIfCancellationRequested();
            await provider.DeleteAsync(key!, cancellationToken).ConfigureAwait(false);
            return Completion(task, await provider.ExistsAsync(key!, cancellationToken).ConfigureAwait(false)
                ? DeleteReplicaFailureCodes.DeleteNotVerified : null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (StorageException exception)
        {
            return Completion(task, exception.Kind switch
            {
                StorageFailureKind.InvalidConfiguration => DeleteReplicaFailureCodes.InvalidConfiguration,
                StorageFailureKind.AccessDenied => DeleteReplicaFailureCodes.AccessDenied,
                StorageFailureKind.NotSupported => DeleteReplicaFailureCodes.DeleteNotSupported,
                StorageFailureKind.Io => DeleteReplicaFailureCodes.IoError,
                _ => DeleteReplicaFailureCodes.Unexpected
            });
        }
        catch (Exception)
        {
            return Completion(task, DeleteReplicaFailureCodes.Unexpected);
        }
    }

    /// <summary>An external replica is removed through the connection's own provider, opened with access the backend releases only to this live lease.</summary>
    private DeleteReplicaProviderResolution ResolveExternal(DeleteReplicaTaskSnapshot task) =>
        _external is not null && Guid.TryParse(task.StorageTarget, out var connectionId) && connectionId != Guid.Empty
            ? new(_external.CreateScoped(new DirectStorageScope($"api/agent/replica-deletions/{task.ReplicaId:D}/storage/access",
                task.LeaseToken, task.TenantId, connectionId, task.StorageProvider)), null)
            : new(null, DeleteReplicaFailureCodes.InvalidConfiguration);

    private static bool TryResolveKey(DeleteReplicaTaskSnapshot task, out StorageObjectKey? key)
    {
        return StorageObjectKey.TryCreate(task.StorageKey, out key)
            && ManagedBackupLayout.TryParseStorageKey(task.StorageKey, out var managed)
            && managed is not null && (!managed.IsPartial || StorageProviderKinds.AgentExecuted(task.StorageProvider))
            && managed.TenantId == task.TenantId && managed.JobId == task.JobId
            && managed.RunId == task.RunId && managed.AttemptId == task.AttemptId
            && string.Equals(managed.Prefix, task.StoragePrefix, StringComparison.Ordinal);
    }

    private static DeleteReplicaCompletionRequest Completion(DeleteReplicaTaskSnapshot task, string? failure) => new()
    {
        OperationId = task.OperationId,
        LeaseToken = task.LeaseToken,
        AbsenceVerified = failure is null,
        FailureCode = failure
    };

    private readonly record struct Claim(Guid LeaseToken)
    {
        public static Claim From(DeleteReplicaTaskSnapshot task) => new(task.LeaseToken);
    }
}
