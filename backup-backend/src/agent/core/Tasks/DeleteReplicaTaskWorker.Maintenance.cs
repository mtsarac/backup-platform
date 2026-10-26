using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Contracts.Backups;

namespace BackupPlatform.Agent.Core.Tasks;

public sealed partial class DeleteReplicaTaskWorker
{
    private static readonly TimeSpan TransportRetry = TimeSpan.FromSeconds(2);

    private async Task<bool> AuthorizeAsync(DeleteReplicaTaskSnapshot task, BackupTaskLease lease, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await RenewAsync(task, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccess)
            {
                return lease.Renew(response.Value.LeaseExpiresAt);
            }
            if (!BackupTaskLease.IsRetryable(response.Failure))
            {
                return false;
            }
            await DelayRetryAsync(response.Failure, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RenewLoopAsync(DeleteReplicaTaskSnapshot task, BackupTaskLease lease, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var remaining = lease.ExpiresAt - _time.GetUtcNow();
                var delay = BackupTaskLease.RenewalDelay(remaining);
                await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
                if (!await AuthorizeAsync(task, lease, cancellationToken).ConfigureAwait(false))
                {
                    await lease.LoseAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private Task<AgentBackendResult<DeleteReplicaLeaseResponse>> RenewAsync(DeleteReplicaTaskSnapshot task, CancellationToken cancellationToken) =>
        BackupTaskLease.CallAsync(_identities, task.AgentId, identity => _client.RenewAsync(identity, task.ReplicaId, new DeleteReplicaLeaseRequest
        {
            OperationId = task.OperationId,
            LeaseToken = task.LeaseToken
        }, cancellationToken), cancellationToken);

    private async Task CompleteAsync(DeleteReplicaTaskSnapshot task, DeleteReplicaCompletionRequest completion, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await BackupTaskLease.CallAsync(_identities, task.AgentId, identity => _client.CompleteAsync(identity, task.ReplicaId, completion, cancellationToken), cancellationToken).ConfigureAwait(false);
            if (response.IsSuccess || !BackupTaskLease.IsRetryable(response.Failure))
            {
                return;
            }
            await DelayRetryAsync(response.Failure, cancellationToken).ConfigureAwait(false);
        }
    }


    private Task DelayRetryAsync(AgentBackendFailure failure, CancellationToken cancellationToken) =>
        Task.Delay(failure.RetryAfter is { } delay && delay > TransportRetry ? delay : TransportRetry, _time, cancellationToken);
}
