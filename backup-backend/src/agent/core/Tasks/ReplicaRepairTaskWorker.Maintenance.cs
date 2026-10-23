using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Contracts.Backups;

namespace BackupPlatform.Agent.Core.Tasks;

public sealed partial class ReplicaRepairTaskWorker
{
    private static readonly TimeSpan RenewalInterval = TimeSpan.FromSeconds(10);
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
                var delay = TimeSpan.FromTicks(Math.Clamp(remaining.Ticks / 3, TimeSpan.TicksPerMillisecond, RenewalInterval.Ticks));
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
            var result = await CallAsync(task, call, cancellationToken).ConfigureAwait(false);
            if (result.IsSuccess || !IsRetryable(result.Failure))
            {
                return result;
            }
            await Task.Delay(result.Failure.RetryAfter is { } delay && delay > TransportRetry ? delay : TransportRetry, _time, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<AgentBackendResult<T>> CallAsync<T>(ReplicaRepairTaskSnapshot task,
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
            return AgentBackendResult<T>.Fail(new AgentBackendFailure(AgentBackendFailureKind.Transient, "The repair backend could not be reached."));
        }
    }

    private static bool IsRetryable(AgentBackendFailure failure) => failure.Kind is AgentBackendFailureKind.Transient or AgentBackendFailureKind.RateLimited;
}
