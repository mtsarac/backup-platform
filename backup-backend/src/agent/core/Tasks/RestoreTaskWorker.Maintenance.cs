using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Contracts.Restores;

namespace BackupPlatform.Agent.Core.Tasks;

public sealed partial class RestoreTaskWorker
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CompletionRetry = TimeSpan.FromSeconds(2);

    private async Task RenewLoopAsync(RestoreTaskSnapshot task, BackupTaskLease lease, CancellationTokenSource requestedCancel, CancellationToken cancellationToken)
    {
        _time.StartOperation("restore renewal");
        _time.EnterOperation("restore renewal");
        try
        {
            while (true)
            {
                await _time.ObserveAsync("restore renewal", Task.Delay(BackupTaskLease.RenewalInterval, _time, cancellationToken)).ConfigureAwait(false);
                var response = await BackupTaskLease.CallAsync(_identities, null, identity => _client.RenewAsync(identity, task.AttemptId,
                    new BackupTaskLeaseRequest { LeaseToken = task.LeaseToken }, cancellationToken), cancellationToken).ConfigureAwait(false);
                if (response.IsSuccess)
                {
                    if (!lease.Renew(response.Value.LeaseExpiresAt))
                    {
                        return;
                    }
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
            _time.EndOperation("restore renewal");
        }
    }

    private async Task ReportLoopAsync(RestoreTaskSnapshot task, TaskProgress progress, CancellationToken cancellationToken)
    {
        _time.StartOperation("restore progress");
        _time.EnterOperation("restore progress");
        try
        {
            while (true)
            {
                await _time.ObserveAsync("restore progress", Task.Delay(ProgressInterval, _time, cancellationToken)).ConfigureAwait(false);
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
            _time.EndOperation("restore progress");
        }
    }

    /// <summary>Retries a transient failure until the backend answers or the lease is gone: a finished restore's outcome must reach the backend, and the backend ignores a repeat that agrees with what it recorded.</summary>
    private async Task CompleteAsync(Guid attemptId, RestoreTaskCompletionRequest completion, CancellationToken cancellationToken)
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
            await _time.ObserveAsync("restore worker", Task.Delay(delay, _time, cancellationToken)).ConfigureAwait(false);
        }
    }

    private sealed class TaskProgress : IProgress<RestoreProgress>
    {
        private RestoreProgress? _latest;
        public void Report(RestoreProgress value) => Interlocked.Exchange(ref _latest, value);
        public RestoreProgress? Take() => Interlocked.Exchange(ref _latest, null);
    }
}
