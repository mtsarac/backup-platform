using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Contracts.Downloads;

namespace BackupPlatform.Agent.Core.Tasks;

public sealed partial class DownloadTaskWorker
{
    private static readonly TimeSpan RenewalInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CompletionRetry = TimeSpan.FromSeconds(2);

    private async Task RenewLoopAsync(DownloadTaskSnapshot task, BackupTaskLease lease, CancellationTokenSource requestedCancel, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(RenewalInterval, time, cancellationToken).ConfigureAwait(false);
                var response = await CallAsync(identity => client.RenewAsync(identity, task.SessionId,
                    new BackupTaskLeaseRequest { LeaseToken = task.LeaseToken }, cancellationToken), cancellationToken).ConfigureAwait(false);
                if (response.IsSuccess)
                {
                    if (!lease.Renew(response.Value.LeaseExpiresAt)) return;
                    if (response.Value.CancellationRequested) await requestedCancel.CancelAsync().ConfigureAwait(false);
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
    }

    private async Task CompleteAsync(DownloadTaskSnapshot task, string failure, CancellationToken cancellationToken)
    {
        var request = new DownloadTaskCompletionRequest
        {
            LeaseToken = task.LeaseToken,
            Status = failure == DownloadAgentFailureCodes.Cancelled ? "Cancelled" : "Failed",
            FailureCode = failure
        };
        while (true)
        {
            var response = await CallAsync(identity => client.CompleteAsync(identity, task.SessionId, request, cancellationToken), cancellationToken).ConfigureAwait(false);
            if (response.IsSuccess || response.Failure.Kind is not (AgentBackendFailureKind.Transient or AgentBackendFailureKind.RateLimited)) return;
            var delay = response.Failure.RetryAfter is { } retry && retry > CompletionRetry ? retry : CompletionRetry;
            await Task.Delay(delay, time, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<AgentBackendResult<T>> CallAsync<T>(Func<AgentLocalIdentity, Task<AgentBackendResult<T>>> call, CancellationToken cancellationToken)
    {
        try
        {
            var identity = await identities.LoadAsync(cancellationToken).ConfigureAwait(false);
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
            return AgentBackendResult<T>.Fail(new AgentBackendFailure(AgentBackendFailureKind.Transient, "The download backend could not be reached."));
        }
    }
}
