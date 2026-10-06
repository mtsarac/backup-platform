using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Contracts.Backups;

namespace BackupPlatform.Agent.Core.Tasks;

public sealed partial class DeleteReplicaTaskWorker
{
    private static readonly TimeSpan RenewalInterval = TimeSpan.FromSeconds(10);
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
            if (!IsRetryable(response.Failure))
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
                var delay = TimeSpan.FromTicks(Math.Clamp(remaining.Ticks / 3, TimeSpan.TicksPerMillisecond, RenewalInterval.Ticks));
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
        CallAsync(task, identity => _client.RenewAsync(identity, task.ReplicaId, new DeleteReplicaLeaseRequest
        {
            OperationId = task.OperationId,
            LeaseToken = task.LeaseToken
        }, cancellationToken), cancellationToken);

    private async Task CompleteAsync(DeleteReplicaTaskSnapshot task, DeleteReplicaCompletionRequest completion, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await CallAsync(task, identity => _client.CompleteAsync(identity, task.ReplicaId, completion, cancellationToken), cancellationToken).ConfigureAwait(false);
            if (response.IsSuccess || !IsRetryable(response.Failure))
            {
                return;
            }
            await DelayRetryAsync(response.Failure, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<AgentBackendResult<T>> CallAsync<T>(DeleteReplicaTaskSnapshot task,
        Func<AgentLocalIdentity, Task<AgentBackendResult<T>>> call, CancellationToken cancellationToken)
    {
        try
        {
            var identity = await _identities.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (identity is null || identity.AgentId != task.AgentId)
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
            return AgentBackendResult<T>.Fail(new AgentBackendFailure(AgentBackendFailureKind.Transient, "The deletion backend could not be reached."));
        }
    }

    private static bool IsRetryable(AgentBackendFailure failure) => failure.Kind is AgentBackendFailureKind.Transient or AgentBackendFailureKind.RateLimited;

    private Task DelayRetryAsync(AgentBackendFailure failure, CancellationToken cancellationToken) =>
        Task.Delay(failure.RetryAfter is { } delay && delay > TransportRetry ? delay : TransportRetry, _time, cancellationToken);
}
