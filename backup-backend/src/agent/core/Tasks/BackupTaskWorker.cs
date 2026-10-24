using System.Threading.Channels;
using BackupPlatform.Agent.Core.Backup;
using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Agent.Core.Updates;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.Agent.Core.Tasks;

/// <summary>One active attempt, one waiting claim. Provider execution never occupies a connectivity loop.</summary>
public sealed partial class BackupTaskWorker : IAgentTaskSink
{
    private readonly Channel<AgentTaskEnvelope> _queue = Channel.CreateBounded<AgentTaskEnvelope>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        AllowSynchronousContinuations = false
    });
    private readonly object _gate = new();
    private readonly Dictionary<Guid, DateTimeOffset> _seen = [];
    private readonly IBackupTaskClient _client;
    private readonly IAgentCredentialStore _identities;
    private readonly IDatabaseBackupPipeline _pipeline;
    private readonly BackupTaskRequestFactory _requests;
    private readonly AgentSoftwareInfo _software;
    private readonly TimeProvider _time;
    private readonly ILogger<BackupTaskWorker> _logger;
    private Guid? _activeAttempt;
    private int _started;
    private readonly AgentUpdateGate? _updates;

    public BackupTaskWorker(IBackupTaskClient client, IAgentCredentialStore identities, IDatabaseBackupPipeline pipeline,
        BackupTaskRequestFactory requests, AgentSoftwareInfo software, TimeProvider timeProvider, ILogger<BackupTaskWorker> logger, AgentUpdateGate? updates = null)
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
        if (task is not { Type: "database-backup", Backup: { Version: 1, BackupType: "Full" } backup }
            || !Guid.TryParse(task.Id, out var id) || id != backup.AttemptId
            || backup.AttemptId == Guid.Empty || backup.RunId == Guid.Empty || backup.JobId == Guid.Empty
            || backup.TenantId == Guid.Empty || backup.LeaseToken == Guid.Empty || backup.AttemptNumber < 1
            || backup.Configuration is null || backup.LeaseExpiresAt <= now)
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
            _seen.Add(id, backup.LeaseExpiresAt);
            return true;
        }
    }

    private bool TryEnqueue(AgentTaskEnvelope task) =>
        _updates?.TryQueue(() => Enqueue(task)) ?? Enqueue(task);

    private bool Enqueue(AgentTaskEnvelope task) =>
        _time is IAgentLoopObserver observer ? ObserveEnqueue(observer, task) : _queue.Writer.TryWrite(task);

    private bool ObserveEnqueue(IAgentLoopObserver observer, AgentTaskEnvelope task) =>
        observer.Signal("backup worker", () => _queue.Writer.TryWrite(task));

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("The task worker can only run once.");
        }
        _time.StartOperation("backup worker");
        _time.EnterOperation("backup worker");
        try
        {
            while (await _time.ObserveAsync("backup worker", _queue.Reader.WaitToReadAsync(stoppingToken).AsTask()).ConfigureAwait(false))
            {
                if (!_queue.Reader.TryRead(out var envelope))
                {
                    continue;
                }
                var task = envelope.Backup!;
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
                    _logger.LogWarning("Backup attempt {AttemptId} stopped ({ExceptionType}).", task.AttemptId, exception.GetType().Name);
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
            _time.EndOperation("backup worker");
        }
    }

    private async Task ExecuteAsync(BackupTaskSnapshot task, CancellationToken stoppingToken)
    {
        if (task.LeaseExpiresAt <= _time.GetUtcNow())
        {
            return;
        }
        using var lease = new BackupTaskLease(task.LeaseExpiresAt, _time);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, lease.Token);
        var started = await BackupTaskLease.CallAsync(_identities, null, identity => _client.StartAsync(identity, task.AttemptId, new BackupTaskStartRequest
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
        using var requestedCancel = new CancellationTokenSource();
        using var requestedPause = new CancellationTokenSource();
        using var timeRulePause = new CancellationTokenSource();
        if (started.Value.TimeRulePauseRequested) timeRulePause.Cancel();
        if (started.Value.PauseRequested) requestedPause.Cancel();
        if (task.CancellationRequested || started.Value.CancellationRequested)
        {
            requestedCancel.Cancel();
        }
        using var provider = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, requestedCancel.Token);
        using var maintenance = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        using var progressLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var progress = new TaskProgress();
        var renewal = RenewLoopAsync(task, lease, requestedCancel, requestedPause, timeRulePause, maintenance.Token);
        var reporting = ReportLoopAsync(task, progress, progressLifetime.Token);
        try
        {
            var completion = await ExecutePipelineAsync(task, progress, requestedPause.Token, timeRulePause.Token, stoppingToken, lifetime.Token, provider.Token).ConfigureAwait(false);
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

    private async Task<BackupTaskCompletionRequest> ExecutePipelineAsync(BackupTaskSnapshot task, TaskProgress progress, CancellationToken pauseToken, CancellationToken timeRulePauseToken, CancellationToken shutdownToken, CancellationToken leaseToken, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = (await _requests.CreateAsync(task, cancellationToken).ConfigureAwait(false)) with
            {
                AttemptId = task.AttemptId, ResumeTransfer = task.ResumeTransfer, Checkpoints = task.Checkpoints, PauseToken = pauseToken,
                TimeRulePauseToken = timeRulePauseToken, ShutdownToken = shutdownToken,
                RecordCheckpointAsync = async (checkpoint, _) =>
                {
                    var response = await BackupTaskLease.CallAsync(_identities, null, identity => _client.ReportProgressAsync(identity, task.AttemptId,
                        new BackupTaskProgressRequest { LeaseToken = task.LeaseToken, Stage = "Uploading", Checkpoint = checkpoint }, leaseToken), leaseToken).ConfigureAwait(false);
                    if (!response.IsSuccess) throw new IOException("The transfer checkpoint could not be committed.");
                }
            };
            var result = await _pipeline.RunFullBackupAsync(request, progress, cancellationToken).ConfigureAwait(false);
            return BackupTaskResultMapper.Map(task, result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return BackupTaskResultMapper.Failure(task.LeaseToken, BackupTaskFailureCode.Cancelled);
        }
        catch (BackupTaskEncryptionException)
        {
            return BackupTaskResultMapper.Failure(task.LeaseToken, BackupTaskFailureCode.InvalidConfiguration);
        }
        catch (Exception)
        {
            return BackupTaskResultMapper.Failure(task.LeaseToken, BackupTaskFailureCode.InternalError);
        }
    }
}
