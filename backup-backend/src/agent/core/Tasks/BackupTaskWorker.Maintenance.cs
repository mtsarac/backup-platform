using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.Contracts.Backups;

namespace BackupPlatform.Agent.Core.Tasks;

public sealed partial class BackupTaskWorker
{
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
                await _time.ObserveAsync("backup renewal", Task.Delay(BackupTaskLease.RenewalInterval, _time, cancellationToken)).ConfigureAwait(false);
                var response = await BackupTaskLease.CallAsync(_identities, null, identity => _client.RenewAsync(identity, task.AttemptId,
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
                else if (!BackupTaskLease.IsRetryable(response.Failure))
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
                await BackupTaskLease.CallAsync(_identities, null, identity => _client.ReportProgressAsync(identity, task.AttemptId, new BackupTaskProgressRequest
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
            var response = await BackupTaskLease.CallAsync(_identities, null, identity => _client.CompleteAsync(identity, attemptId, completion, cancellationToken), cancellationToken).ConfigureAwait(false);
            if (response.IsSuccess || !BackupTaskLease.IsRetryable(response.Failure))
            {
                return;
            }
            var delay = response.Failure.RetryAfter is { } requested && requested > CompletionRetry ? requested : CompletionRetry;
            await _time.ObserveAsync("backup worker", Task.Delay(delay, _time, cancellationToken)).ConfigureAwait(false);
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

    /// <summary>How often a running task renews; a short remaining lease renews sooner (see <see cref="RenewalDelay"/>).</summary>
    public static readonly TimeSpan RenewalInterval = TimeSpan.FromSeconds(10);

    public CancellationToken Token => _expiry.Token;

    /// <summary>A third of what is left of the lease, never longer than <see cref="RenewalInterval"/> and never zero.</summary>
    public static TimeSpan RenewalDelay(TimeSpan remaining) =>
        TimeSpan.FromTicks(Math.Clamp(remaining.Ticks / 3, TimeSpan.TicksPerMillisecond, RenewalInterval.Ticks));

    public static bool IsRetryable(AgentBackendFailure failure) => failure.Kind is AgentBackendFailureKind.Transient or AgentBackendFailureKind.RateLimited;

    /// <summary>
    /// Calls the task backend as the local identity. <paramref name="agentId"/> pins the task to that identity (a task claimed by another agent is refused);
    /// an unreachable backend is a retryable <see cref="AgentBackendFailureKind.Transient"/>, never an exception.
    /// </summary>
    public static async Task<AgentBackendResult<T>> CallAsync<T>(IAgentCredentialStore identities, Guid? agentId,
        Func<AgentLocalIdentity, Task<AgentBackendResult<T>>> call, CancellationToken cancellationToken)
    {
        try
        {
            var identity = await identities.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (identity is null || agentId is { } expected && identity.AgentId != expected)
            {
                return AgentBackendResult<T>.Fail(new AgentBackendFailure(AgentBackendFailureKind.Unauthorized, "The task does not belong to the local identity."));
            }
            return await call(identity).ConfigureAwait(false);
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
