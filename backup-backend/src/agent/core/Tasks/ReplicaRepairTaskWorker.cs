using System.Threading.Channels;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.StorageProviders.Abstractions;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.Agent.Core.Tasks;

/// <summary>
/// Copies a verified SFTP/Google Drive replica to an external target on behalf of the backend. The backend holds no credential and relays no byte:
/// it only fences every step by the lease and keeps quota, source pin, orphan ledger and audit. One repair at a time.
/// </summary>
public sealed partial class ReplicaRepairTaskWorker : IAgentTaskSink
{
    private const long ProgressStep = 10L * 1024 * 1024;

    private readonly Channel<ReplicaRepairTaskSnapshot> _queue = Channel.CreateBounded<ReplicaRepairTaskSnapshot>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        AllowSynchronousContinuations = false
    });
    private readonly object _gate = new();
    private readonly Dictionary<Guid, DateTimeOffset> _seen = [];
    private readonly IReplicaRepairTaskClient _client;
    private readonly IAgentCredentialStore _identities;
    private readonly ITaskStorageProviderFactory _providers;
    private readonly TimeProvider _time;
    private readonly ILogger<ReplicaRepairTaskWorker> _logger;
    private Guid? _active;
    private int _started;

    public ReplicaRepairTaskWorker(IReplicaRepairTaskClient client, IAgentCredentialStore identities,
        ITaskStorageProviderFactory providers, TimeProvider timeProvider, ILogger<ReplicaRepairTaskWorker> logger)
    {
        _client = client;
        _identities = identities;
        _providers = providers;
        _time = timeProvider;
        _logger = logger;
    }

    public bool TryPublish(AgentTaskEnvelope envelope)
    {
        var now = _time.GetUtcNow();
        if (envelope is not { Type: ReplicaLifecycleProtocol.RepairTaskType, ReplicaRepair: { Version: 1 } task }
            || !Guid.TryParse(envelope.Id, out var id) || id != task.OperationId || !IsValid(task, now))
        {
            return false;
        }
        lock (_gate)
        {
            foreach (var pair in _seen)
            {
                if (pair.Value <= now && pair.Key != _active)
                {
                    _seen.Remove(pair.Key);
                }
            }
            if (_seen.ContainsKey(task.LeaseToken) || !_queue.Writer.TryWrite(task))
            {
                return false;
            }
            _seen.Add(task.LeaseToken, task.LeaseExpiresAt);
            return true;
        }
    }

    private static bool IsValid(ReplicaRepairTaskSnapshot task, DateTimeOffset now) =>
        task.OperationId != Guid.Empty && task.TenantId != Guid.Empty && task.AgentId != Guid.Empty
        && task.ArtifactId != Guid.Empty && task.LeaseToken != Guid.Empty && task.LeaseExpiresAt > now
        && task.SizeBytes > 0 && task.Checksum.Length == ContentChecksum.Sha256HexLength && task.Checksum.All(Uri.IsHexDigit)
        && task.Target.StorageConnectionId != Guid.Empty && StorageProviderKinds.AgentExecuted(task.Target.StorageProvider)
        && task.Sources is { Count: > 0 }
        && task.Sources.All(source => source.ReplicaId != Guid.Empty && source.StorageConnectionId != Guid.Empty
            && StorageProviderKinds.External(source.StorageProvider) && StorageObjectKey.TryCreate(source.StorageKey, out _));

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("The repair worker can only run once.");
        }
        try
        {
            await foreach (var task in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                lock (_gate)
                {
                    _active = task.LeaseToken;
                }
                try
                {
                    await ExecuteAsync(task, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Shutdown or lost lease: the backend recovers the expired lease and records any target object as an orphan.
                }
                catch (Exception exception)
                {
                    _logger.LogWarning("Replica repair {OperationId} stopped ({ExceptionType}).", task.OperationId, exception.GetType().Name);
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

    private async Task ExecuteAsync(ReplicaRepairTaskSnapshot task, CancellationToken stoppingToken)
    {
        using var lease = new BackupTaskLease(task.LeaseExpiresAt, _time);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, lease.Token);
        using var transfer = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var run = new RepairRun(task, lease, transfer);
        if (!await AuthorizeAsync(run, lifetime.Token).ConfigureAwait(false))
        {
            return;
        }
        using var maintenance = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var renewal = RenewLoopAsync(run, maintenance.Token);
        try
        {
            if (await RepairAsync(run, lifetime.Token).ConfigureAwait(false) is { } completion)
            {
                await CompleteAsync(task, completion, lifetime.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            await maintenance.CancelAsync().ConfigureAwait(false);
            await renewal.ConfigureAwait(false);
            lock (_gate)
            {
                _seen[task.LeaseToken] = lease.ExpiresAt;
            }
        }
    }

    /// <summary>Null when the lease was lost or the agent is stopping: the backend, not this agent, then settles the operation.</summary>
    private async Task<ReplicaRepairCompletionRequest?> RepairAsync(RepairRun run, CancellationToken lifetime)
    {
        var task = run.Snapshot;
        var transfer = run.Transfer.Token;
        IStorageProvider? target = null;
        string? targetKey = null;
        var putStarted = false;
        var putCompleted = false;
        try
        {
            target = _providers.CreateScoped(Scope(task, "target", task.Target.StorageConnectionId, task.Target.StorageProvider));
            var (source, stream) = await OpenSourceAsync(run, transfer).ConfigureAwait(false);
            if (stream is null || source is null)
            {
                return Failed(task, RepairFailure.SourceInaccessible, "No verified source replica could be opened.");
            }
            targetKey = TargetKey(task, source);
            await using var sourceStream = stream;
            await using var hashing = new Sha256VerifyingStream(sourceStream);
            var checksum = new ContentChecksum(ChecksumAlgorithm.Sha256, task.Checksum);
            var reporter = new ProgressReporter(this, task, lifetime);
            putStarted = true;
            await target.PutAsync(new StorageObjectKey(targetKey), hashing,
                new StoragePutOptions(StorageOverwritePolicy.Fail, checksum, task.SizeBytes), reporter, transfer).ConfigureAwait(false);
            putCompleted = true;
            await reporter.DrainAsync().ConfigureAwait(false);

            // A provider that found the target already published never reads the source; the read-back below then validates and adopts it.
            if (hashing.Position > 0
                && (hashing.Position != task.SizeBytes || !string.Equals(hashing.GetHashHex(), task.Checksum, StringComparison.OrdinalIgnoreCase)))
            {
                return Failed(task, RepairFailure.SourceChecksumMismatch, "The source replica failed SHA-256 verification.",
                    await CleanupAsync(run, target, targetKey, lifetime).ConfigureAwait(false));
            }
            var verified = await target.VerifyAsync(new StorageObjectKey(targetKey), new StorageVerificationExpectation(task.SizeBytes, checksum), transfer).ConfigureAwait(false);
            if (verified.Status != StorageVerificationStatus.Verified)
            {
                return Failed(task, RepairFailure.DestinationReadbackFailed, "The destination read-back verification failed.",
                    await CleanupAsync(run, target, targetKey, lifetime).ConfigureAwait(false));
            }
            TaskStorageCatalogRecord? driveRecord = null;
            if (task.Target.StorageProvider == "google-drive"
                && (target is not IScopedDriveRecordSource records
                    || (driveRecord = await records.FindRecordAsync(new StorageObjectKey(targetKey), transfer).ConfigureAwait(false)) is null))
            {
                return Failed(task, RepairFailure.InternalError, "The Google Drive object record is unavailable.",
                    await CleanupAsync(run, target, targetKey, lifetime).ConfigureAwait(false));
            }
            return new ReplicaRepairCompletionRequest
            {
                LeaseToken = task.LeaseToken,
                Outcome = ReplicaRepairOutcomes.Succeeded,
                VerificationMethod = verified.Method.ToString(),
                DriveRecord = driveRecord
            };
        }
        catch (OperationCanceledException)
        {
            if (lifetime.IsCancellationRequested)
            {
                return null;
            }
            var orphan = putStarted ? await CleanupAsync(run, target!, targetKey!, lifetime).ConfigureAwait(false) : null;
            return run.CancelRequested
                ? new ReplicaRepairCompletionRequest { LeaseToken = task.LeaseToken, Outcome = ReplicaRepairOutcomes.Cancelled, OrphanKey = orphan }
                : Failed(task, RepairFailure.OperationTimedOut, "The transfer deadline was exceeded.", orphan);
        }
        catch (StorageException exception)
        {
            if (exception.Kind == StorageFailureKind.AlreadyExists)
            {
                return new ReplicaRepairCompletionRequest { LeaseToken = task.LeaseToken, Outcome = ReplicaRepairOutcomes.TargetConflict };
            }
            string? orphan = null;
            var quarantine = false;
            if (putStarted)
            {
                var key = exception.RemainingObjectKey?.Value ?? targetKey!;
                if (!putCompleted && !exception.ObjectMayRemain)
                {
                    (orphan, quarantine) = (key, true);
                }
                else
                {
                    orphan = await CleanupAsync(run, target!, key, lifetime).ConfigureAwait(false);
                }
            }
            return Failed(task, exception.Kind switch
            {
                StorageFailureKind.InsufficientSpace => RepairFailure.QuotaExceeded,
                StorageFailureKind.Timeout => RepairFailure.OperationTimedOut,
                StorageFailureKind.NotFound or StorageFailureKind.AccessDenied or StorageFailureKind.Io => RepairFailure.StorageWriteFailed,
                _ => RepairFailure.StorageUnavailable
            }, $"The storage operation failed ({exception.Kind}).", orphan, quarantine);
        }
        catch (Exception exception)
        {
            var orphan = putStarted ? await CleanupAsync(run, target!, targetKey!, lifetime).ConfigureAwait(false) : null;
            return Failed(task, RepairFailure.InternalError, $"Internal error: {exception.GetType().Name}", orphan);
        }
    }

    /// <summary>Pins the first candidate the backend accepts and the provider can open; nothing is written to the target before that.</summary>
    private async Task<(ReplicaRepairSourceContract? Source, Stream? Stream)> OpenSourceAsync(RepairRun run, CancellationToken transfer)
    {
        var task = run.Snapshot;
        foreach (var candidate in task.Sources)
        {
            transfer.ThrowIfCancellationRequested();
            var pin = await SendAsync(task, identity => _client.PinAsync(identity, task.OperationId, new ReplicaRepairPinRequest
            {
                LeaseToken = task.LeaseToken,
                SourceReplicaId = candidate.ReplicaId,
                TargetKey = TargetKey(task, candidate)
            }, transfer), transfer).ConfigureAwait(false);
            if (!pin.IsSuccess)
            {
                continue;
            }
            try
            {
                var provider = _providers.CreateScoped(Scope(task, "source", candidate.StorageConnectionId, candidate.StorageProvider));
                return (candidate, await provider.OpenReadAsync(new StorageObjectKey(candidate.StorageKey), transfer).ConfigureAwait(false));
            }
            catch (StorageException exception)
            {
                _logger.LogWarning("Repair {OperationId} could not open source replica {ReplicaId} ({Kind}).", task.OperationId, candidate.ReplicaId, exception.Kind);
            }
        }
        return (null, null);
    }

    /// <summary>Deletes a partial target only when the backend allows it; the key to report as an orphan when that is not allowed or not confirmed.</summary>
    private async Task<string?> CleanupAsync(RepairRun run, IStorageProvider target, string key, CancellationToken cancellationToken)
    {
        var task = run.Snapshot;
        var check = await SendAsync(task, identity => _client.CleanupCheckAsync(identity, task.OperationId,
            new ReplicaRepairCleanupRequest { LeaseToken = task.LeaseToken, TargetKey = key }, cancellationToken), cancellationToken).ConfigureAwait(false);
        if (!check.IsSuccess || !check.Value.Allowed)
        {
            return key;
        }
        try
        {
            var objectKey = new StorageObjectKey(key);
            try
            {
                await target.DeleteAsync(objectKey, cancellationToken).ConfigureAwait(false);
            }
            catch (StorageException exception) when (exception.Kind == StorageFailureKind.NotFound)
            {
                // Delete is idempotent; only the independent absence check below releases the orphan accounting.
            }
            return await target.ExistsAsync(objectKey, cancellationToken).ConfigureAwait(false) ? key : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return key;
        }
    }

    /// <summary>The backend accepts only this lease-specific name beside the source object.</summary>
    private static string TargetKey(ReplicaRepairTaskSnapshot task, ReplicaRepairSourceContract source)
    {
        var name = ManagedBackupLayout.RepairFileName(task.OperationId, task.LeaseToken);
        var slash = source.StorageKey.LastIndexOf('/');
        return slash > 0 ? $"{source.StorageKey[..slash]}/{name}" : name;
    }

    private static DirectStorageScope Scope(ReplicaRepairTaskSnapshot task, string side, Guid connectionId, string provider) =>
        new($"api/agent/replica-repairs/{task.OperationId:D}/storage/{side}/access", task.LeaseToken, task.TenantId, connectionId, provider);

    private static ReplicaRepairCompletionRequest Failed(ReplicaRepairTaskSnapshot task, string code, string reason,
        string? orphanKey = null, bool quarantine = false) => new()
    {
        LeaseToken = task.LeaseToken,
        Outcome = ReplicaRepairOutcomes.Failed,
        FailureCode = code,
        Reason = reason,
        OrphanKey = orphanKey,
        OrphanQuarantine = quarantine
    };

    /// <summary>Mirrors the backend's ReplicaRepairFailureCodes; the backend maps any other value to InternalError.</summary>
    private static class RepairFailure
    {
        public const string SourceInaccessible = "SourceInaccessible";
        public const string SourceChecksumMismatch = "SourceChecksumMismatch";
        public const string DestinationReadbackFailed = "DestinationReadbackFailed";
        public const string StorageWriteFailed = "StorageWriteFailed";
        public const string StorageUnavailable = "StorageUnavailable";
        public const string QuotaExceeded = "QuotaExceeded";
        public const string OperationTimedOut = "OperationTimedOut";
        public const string InternalError = "InternalError";
    }

    private sealed class RepairRun(ReplicaRepairTaskSnapshot snapshot, BackupTaskLease lease, CancellationTokenSource transfer)
    {
        private int _cancelRequested;

        public ReplicaRepairTaskSnapshot Snapshot => snapshot;
        public BackupTaskLease Lease => lease;
        public CancellationTokenSource Transfer => transfer;
        public bool CancelRequested => Volatile.Read(ref _cancelRequested) != 0;

        public void RequestCancellation()
        {
            Volatile.Write(ref _cancelRequested, 1);
            transfer.Cancel();
        }
    }

    /// <summary>Reports at most one update in flight, no more often than every <see cref="ProgressStep"/>; a failed report never affects the transfer.</summary>
    private sealed class ProgressReporter(ReplicaRepairTaskWorker worker, ReplicaRepairTaskSnapshot task, CancellationToken cancellationToken) : IProgress<long>
    {
        private long _reported;
        private int _inFlight;
        private Task _last = Task.CompletedTask;

        public void Report(long bytes)
        {
            if (bytes - Volatile.Read(ref _reported) < ProgressStep || Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0)
            {
                return;
            }
            Volatile.Write(ref _reported, bytes);
            _last = SendAsync(bytes);
        }

        public Task DrainAsync() => _last;

        private async Task SendAsync(long bytes)
        {
            try
            {
                await worker.SendAsync(task, identity => worker._client.ProgressAsync(identity, task.OperationId,
                    new ReplicaRepairProgressRequest { LeaseToken = task.LeaseToken, TransferredBytes = bytes }, cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best effort: the lease renewal, not progress, decides whether the repair continues.
            }
            finally
            {
                Volatile.Write(ref _inFlight, 0);
            }
        }
    }
}
