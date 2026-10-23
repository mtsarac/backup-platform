using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.Contracts.Backups;

namespace BackupPlatform.Agent.Core.Tasks;

public sealed partial class BackupTaskWorker
{
    private static readonly TimeSpan RenewalInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CompletionRetry = TimeSpan.FromSeconds(2);

    private async Task RenewLoopAsync(BackupTaskSnapshot task, BackupTaskLease lease, CancellationTokenSource requestedCancel, CancellationTokenSource requestedPause, CancellationTokenSource timeRulePause, CancellationToken cancellationToken)
    {
        _time.StartOperation("backup renewal");
        _time.EnterOperation("backup renewal");
        try
        {
            while (true)
            {
                await _time.ObserveAsync("backup renewal", Task.Delay(RenewalInterval, _time, cancellationToken)).ConfigureAwait(false);
                var response = await CallAsync(identity => _client.RenewAsync(identity, task.AttemptId,
                    new BackupTaskLeaseRequest { LeaseToken = task.LeaseToken }, cancellationToken), cancellationToken).ConfigureAwait(false);
                if (response.IsSuccess)
                {
                    if (!lease.Renew(response.Value.LeaseExpiresAt))
                    {
                        return;
                    }
                    if (response.Value.PauseRequested) await requestedPause.CancelAsync().ConfigureAwait(false);
                    if (response.Value.TimeRulePauseRequested) await timeRulePause.CancelAsync().ConfigureAwait(false);
                    if (response.Value.CancellationRequested)
                    {
                        await requestedCancel.CancelAsync().ConfigureAwait(false);
                    }
                }
                else if (response.Failure.Kind is not (AgentBackendFailureKind.Transient or AgentBackendFailureKind.RateLimited))
                {
                    await lease.LoseAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            _time.EndOperation("backup renewal");
        }
    }

    private async Task ReportLoopAsync(BackupTaskSnapshot task, TaskProgress progress, CancellationToken cancellationToken)
    {
        _time.StartOperation("backup progress");
        _time.EnterOperation("backup progress");
        try
        {
            while (true)
            {
                await _time.ObserveAsync("backup progress", Task.Delay(ProgressInterval, _time, cancellationToken)).ConfigureAwait(false);
                if (progress.Take() is not { } current)
                {
                    continue;
                }
                await CallAsync(identity => _client.ReportProgressAsync(identity, task.AttemptId, new BackupTaskProgressRequest
                {
                    LeaseToken = task.LeaseToken,
                    Stage = current.Stage.ToString(),
                    Percent = current.Percent
                }, cancellationToken), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            _time.EndOperation("backup progress");
        }
    }

    private async Task CompleteAsync(Guid attemptId, BackupTaskCompletionRequest completion, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await CallAsync(identity => _client.CompleteAsync(identity, attemptId, completion, cancellationToken), cancellationToken).ConfigureAwait(false);
            if (response.IsSuccess || response.Failure.Kind is not (AgentBackendFailureKind.Transient or AgentBackendFailureKind.RateLimited))
            {
                return;
            }
            var delay = response.Failure.RetryAfter is { } requested && requested > CompletionRetry ? requested : CompletionRetry;
            await _time.ObserveAsync("backup worker", Task.Delay(delay, _time, cancellationToken)).ConfigureAwait(false);
        }
    }

    private async Task<AgentBackendResult<T>> CallAsync<T>(Func<AgentLocalIdentity, Task<AgentBackendResult<T>>> call, CancellationToken cancellationToken)
    {
        try
        {
            var identity = await _identities.LoadAsync(cancellationToken).ConfigureAwait(false);
            return identity is null
                ? AgentBackendResult<T>.Fail(new AgentBackendFailure(AgentBackendFailureKind.Unauthorized, "The local identity is unavailable."))
                : await call(identity).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return AgentBackendResult<T>.Fail(new AgentBackendFailure(AgentBackendFailureKind.Transient, "The task backend could not be reached."));
        }
    }

    private sealed class TaskProgress : IProgress<BackupProgress>
    {
        private BackupProgress? _latest;
        public void Report(BackupProgress value) => Interlocked.Exchange(ref _latest, value);
        public BackupProgress? Take() => Interlocked.Exchange(ref _latest, null);
    }
}

internal sealed class BackupTaskLease : IDisposable
{
    private readonly CancellationTokenSource _expiry;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private DateTimeOffset _expiresAt;

    public BackupTaskLease(DateTimeOffset expiresAt, TimeProvider time)
    {
        _time = time;
        _expiresAt = expiresAt;
        var remaining = expiresAt - time.GetUtcNow();
        _expiry = new CancellationTokenSource(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, time);
    }

    public CancellationToken Token => _expiry.Token;

    public DateTimeOffset ExpiresAt
    {
        get
        {
            lock (_gate)
            {
                return _expiresAt;
            }
        }
    }

    public bool Renew(DateTimeOffset expiresAt)
    {
        lock (_gate)
        {
            var remaining = expiresAt - _time.GetUtcNow();
            if (_expiry.IsCancellationRequested || remaining <= TimeSpan.Zero)
            {
                _expiry.Cancel();
                return false;
            }
            _expiresAt = expiresAt;
            _expiry.CancelAfter(remaining);
            return true;
        }
    }

    public Task LoseAsync() => _expiry.CancelAsync();
    public void Dispose() => _expiry.Dispose();
}
