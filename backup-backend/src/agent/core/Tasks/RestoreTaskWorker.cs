using System.Threading.Channels;
using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Agent.Core.Restore;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Contracts.Restores;
using BackupPlatform.Agent.Core.Updates;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.Agent.Core.Tasks;

/// <summary>
/// Runs claimed restore attempts with the same discipline as <see cref="BackupTaskWorker"/>: one active attempt, one waiting claim, a lease
/// renewed every ten seconds, progress coalesced to one report every two seconds, and a completion that is retried until the backend
/// answers. The backend never runs a restore in parallel with a backup of the same agent, so the two workers do not compete for the
/// database server; each keeps its own slot so a stuck one cannot starve the other's bookkeeping.
/// What differs is the outcome: a restore changes a database, so every terminal message states what the target was left as, and a
/// cancelled restore still completes (as Cancelled, with that state) instead of vanishing. Only a lost or expired lease sends nothing: the
/// backend then decides the outcome itself.
/// </summary>
public sealed partial class RestoreTaskWorker : IAgentTaskSink
{
    private readonly Channel<AgentTaskEnvelope> _queue = Channel.CreateBounded<AgentTaskEnvelope>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        AllowSynchronousContinuations = false
    });
    private readonly object _gate = new();
    private readonly Dictionary<Guid, DateTimeOffset> _seen = [];
    private readonly IRestoreTaskClient _client;
    private readonly IAgentCredentialStore _identities;
    private readonly IDatabaseRestorePipeline _pipeline;
    private readonly RestoreTaskRequestFactory _requests;
    private readonly AgentSoftwareInfo _software;
    private readonly TimeProvider _time;
    private readonly ILogger<RestoreTaskWorker> _logger;
    private Guid? _activeAttempt;
    private int _started;
    private readonly AgentUpdateGate? _updates;

    public RestoreTaskWorker(IRestoreTaskClient client, IAgentCredentialStore identities, IDatabaseRestorePipeline pipeline,
        RestoreTaskRequestFactory requests, AgentSoftwareInfo software, TimeProvider timeProvider, ILogger<RestoreTaskWorker> logger, AgentUpdateGate? updates = null)
    {
        _client = client;
        _identities = identities;
        _pipeline = pipeline;
        _requests = requests;
        _software = software;
        _time = timeProvider;
        _logger = logger;
        _updates = updates;
    }

    public bool TryPublish(AgentTaskEnvelope task)
    {
        var now = _time.GetUtcNow();
        if (task is not { Restore: { Version: 1 } restore }
            || !HasMatchingPurpose(task.Type, restore.Purpose)
            || !Guid.TryParse(task.Id, out var id) || id != restore.AttemptId
            || restore.AttemptId == Guid.Empty || restore.OperationId == Guid.Empty || restore.TenantId == Guid.Empty
            || restore.LeaseToken == Guid.Empty || restore.AttemptNumber < 1
            || (restore.Purpose != "Discovery" && restore.Source is null) || restore.Target is null || restore.LeaseExpiresAt <= now)
        {
            return false;
        }
        lock (_gate)
        {
            foreach (var pair in _seen)
            {
                if (pair.Value <= now && pair.Key != _activeAttempt)
                {
                    _seen.Remove(pair.Key);
                }
            }
            if (_seen.ContainsKey(id) || !TryEnqueue(task))
            {
                return false;
            }
            _seen.Add(id, restore.LeaseExpiresAt);
            return true;
        }
    }

    private static bool HasMatchingPurpose(string type, string purpose) => (type, purpose) is
        (RestoreTaskTypes.DatabaseRestore, "Execution")
        or (RestoreTaskTypes.DatabaseRestorePrecheck, "Precheck")
        or (RestoreTaskTypes.DatabaseRestoreDiscovery, "Discovery")
        or (RestoreTaskTypes.DatabaseRestoreVerification, "Verification");

    private bool TryEnqueue(AgentTaskEnvelope task) =>
        _updates?.TryQueue(() => Enqueue(task)) ?? Enqueue(task);

    private bool Enqueue(AgentTaskEnvelope task) =>
        _time is IAgentLoopObserver observer ? observer.Signal("restore worker", () => _queue.Writer.TryWrite(task)) : _queue.Writer.TryWrite(task);

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("The task worker can only run once.");
        }
        _time.StartOperation("restore worker");
        _time.EnterOperation("restore worker");
        try
        {
            while (await _time.ObserveAsync("restore worker", _queue.Reader.WaitToReadAsync(stoppingToken).AsTask()).ConfigureAwait(false))
            {
                if (!_queue.Reader.TryRead(out var envelope))
                {
                    continue;
                }
                var task = envelope.Restore!;
                lock (_gate)
                {
                    _activeAttempt = task.AttemptId;
                }
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
                    // Type only: provider/credential diagnostics must not leak into task logs.
                    _logger.LogWarning("Restore attempt {AttemptId} stopped ({ExceptionType}).", task.AttemptId, exception.GetType().Name);
                }
                finally
                {
                    lock (_gate)
                    {
                        _activeAttempt = null;
                    }
                    _updates?.Complete();
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _time.EndOperation("restore worker");
        }
    }

    private async Task ExecuteAsync(RestoreTaskSnapshot task, CancellationToken stoppingToken)
    {
        if (task.LeaseExpiresAt <= _time.GetUtcNow())
        {
            return;
        }
        using var lease = new BackupTaskLease(task.LeaseExpiresAt, _time);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, lease.Token);
        var started = await CallAsync(identity => _client.StartAsync(identity, task.AttemptId, new BackupTaskStartRequest
        {
            LeaseToken = task.LeaseToken,
            SoftwareVersion = _software.Version,
            ProtocolVersion = AgentProtocol.Current.Version.ToString(),
            Platform = _software.Platform
        }, lifetime.Token), lifetime.Token).ConfigureAwait(false);
        if (!started.IsSuccess || !lease.Renew(started.Value.LeaseExpiresAt))
        {
            return;
        }
        // A cancellation the user asked for is not a lost lease: it stops the restore through the pipeline, which then reports what the target was left as.
        using var requestedCancel = new CancellationTokenSource();
        if (task.CancellationRequested || started.Value.CancellationRequested)
        {
            requestedCancel.Cancel();
        }
        using var pipeline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, requestedCancel.Token);
        using var maintenance = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        using var progressLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var progress = new TaskProgress();
        var renewal = RenewLoopAsync(task, lease, requestedCancel, maintenance.Token);
        var reporting = ReportLoopAsync(task, progress, progressLifetime.Token);
        try
        {
            var completion = await ExecutePipelineAsync(task, progress, pipeline.Token).ConfigureAwait(false);
            await progressLifetime.CancelAsync().ConfigureAwait(false);
            await reporting.ConfigureAwait(false);
            if (!lifetime.IsCancellationRequested)
            {
                await CompleteAsync(task.AttemptId, completion, lifetime.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            await progressLifetime.CancelAsync().ConfigureAwait(false);
            await maintenance.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(renewal, reporting).ConfigureAwait(false);
            lock (_gate)
            {
                _seen[task.AttemptId] = lease.ExpiresAt;
            }
        }
    }

    /// <summary>
    /// Never throws: whatever happens becomes a terminal message. The request is built first, and nothing has touched the target until the
    /// pipeline is called, so a refused snapshot is <see cref="DatabaseRestoreTargetState.NotModified"/>; a pipeline that breaks its contract
    /// (it returns results, it does not throw) leaves the target <see cref="DatabaseRestoreTargetState.Unknown"/>.
    /// </summary>
    private async Task<RestoreTaskCompletionRequest> ExecutePipelineAsync(RestoreTaskSnapshot task, TaskProgress progress, CancellationToken cancellationToken)
    {
        DatabaseRestorePipelineRequest? request = null;
        RestoreDiscoveryRequest? discovery = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (task.Purpose == "Discovery")
            {
                discovery = await _requests.CreateDiscoveryAsync(task, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                request = await _requests.CreateAsync(task, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return RestoreTaskResultMapper.Failure(task.LeaseToken, RestoreAgentFailureCodes.Cancelled, DatabaseRestoreTargetState.NotModified, nothingCreated: task.Purpose == "Verification");
        }
        catch (RestoreTaskRequestException exception)
        {
            return RestoreTaskResultMapper.Failure(task.LeaseToken, exception.FailureCode, DatabaseRestoreTargetState.NotModified, nothingCreated: task.Purpose == "Verification");
        }
        catch (Exception)
        {
            return RestoreTaskResultMapper.Failure(task.LeaseToken, RestoreAgentFailureCodes.InternalError, DatabaseRestoreTargetState.NotModified, nothingCreated: task.Purpose == "Verification");
        }

        try
        {
            var result = discovery is not null
                ? await _pipeline.RunDiscoveryAsync(discovery, progress, cancellationToken).ConfigureAwait(false)
                : await _pipeline.RunRestoreAsync(request!, progress, cancellationToken).ConfigureAwait(false);
            return RestoreTaskResultMapper.Map(task, result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return RestoreTaskResultMapper.Failure(task.LeaseToken, RestoreAgentFailureCodes.Cancelled, StateAfterPipelineException(task));
        }
        catch (Exception)
        {
            return RestoreTaskResultMapper.Failure(task.LeaseToken, RestoreAgentFailureCodes.InternalError, StateAfterPipelineException(task));
        }
    }

    private static DatabaseRestoreTargetState StateAfterPipelineException(RestoreTaskSnapshot task) =>
        task.Purpose == "Execution" ? DatabaseRestoreTargetState.Unknown : DatabaseRestoreTargetState.NotModified;
}
