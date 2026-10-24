using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Contracts.Backups;

namespace BackupPlatform.Agent.Core.Tasks;

public sealed partial class ReplicaRepairTaskWorker
{
    private static readonly TimeSpan TransportRetry = TimeSpan.FromSeconds(2);

    private async Task<bool> AuthorizeAsync(RepairRun run, CancellationToken cancellationToken)
    {
        var task = run.Snapshot;
        var response = await SendAsync(task, identity => _client.RenewAsync(identity, task.OperationId,
            new ReplicaRepairLeaseRequest { LeaseToken = task.LeaseToken }, cancellationToken), cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return false;
        }
        if (response.Value.CancellationRequested)
        {
            run.RequestCancellation();
        }
        return run.Lease.Renew(response.Value.LeaseExpiresAt);
    }

    private async Task RenewLoopAsync(RepairRun run, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var remaining = run.Lease.ExpiresAt - _time.GetUtcNow();
                var delay = BackupTaskLease.RenewalDelay(remaining);
                await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
                if (!await AuthorizeAsync(run, cancellationToken).ConfigureAwait(false))
                {
                    await run.Lease.LoseAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task CompleteAsync(ReplicaRepairTaskSnapshot task, ReplicaRepairCompletionRequest completion, CancellationToken cancellationToken) =>
        await SendAsync(task, identity => _client.CompleteAsync(identity, task.OperationId, completion, cancellationToken), cancellationToken).ConfigureAwait(false);

    /// <summary>Repeats a call while the backend is unreachable or rate limiting; any other answer, including a refusal, is final.</summary>
    private async Task<AgentBackendResult<T>> SendAsync<T>(ReplicaRepairTaskSnapshot task,
        Func<AgentLocalIdentity, Task<AgentBackendResult<T>>> call, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await BackupTaskLease.CallAsync(_identities, task.AgentId, call, cancellationToken).ConfigureAwait(false);
            if (result.IsSuccess || !BackupTaskLease.IsRetryable(result.Failure))
            {
                return result;
            }
            await Task.Delay(result.Failure.RetryAfter is { } delay && delay > TransportRetry ? delay : TransportRetry, _time, cancellationToken).ConfigureAwait(false);
        }
    }

}
