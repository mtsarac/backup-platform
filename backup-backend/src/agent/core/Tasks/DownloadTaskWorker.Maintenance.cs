using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Contracts.Downloads;

namespace BackupPlatform.Agent.Core.Tasks;

public sealed partial class DownloadTaskWorker
{
    private static readonly TimeSpan CompletionRetry = TimeSpan.FromSeconds(2);

    private async Task RenewLoopAsync(DownloadTaskSnapshot task, BackupTaskLease lease, CancellationTokenSource requestedCancel, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(BackupTaskLease.RenewalInterval, time, cancellationToken).ConfigureAwait(false);
                var response = await BackupTaskLease.CallAsync(identities, null, identity => client.RenewAsync(identity, task.SessionId,
                    new BackupTaskLeaseRequest { LeaseToken = task.LeaseToken }, cancellationToken), cancellationToken).ConfigureAwait(false);
                if (response.IsSuccess)
                {
                    if (!lease.Renew(response.Value.LeaseExpiresAt)) return;
                    if (response.Value.CancellationRequested) await requestedCancel.CancelAsync().ConfigureAwait(false);
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
    }

    private async Task CompleteAsync(DownloadTaskSnapshot task, string? failure, CancellationToken cancellationToken)
    {
        var request = new DownloadTaskCompletionRequest
        {
            LeaseToken = task.LeaseToken,
            Status = failure is null ? "Completed" : failure == DownloadAgentFailureCodes.Cancelled ? "Cancelled" : "Failed",
            FailureCode = failure,
            TransferredBytes = failure is null ? task.SizeBytes : 0,
            Checksum = failure is null ? task.Decrypt ? task.SourceChecksum : task.Checksum : null
        };
        while (true)
        {
            var response = await BackupTaskLease.CallAsync(identities, null, identity => client.CompleteAsync(identity, task.SessionId, request, cancellationToken), cancellationToken).ConfigureAwait(false);
            if (response.IsSuccess || !BackupTaskLease.IsRetryable(response.Failure)) return;
            var delay = response.Failure.RetryAfter is { } retry && retry > CompletionRetry ? retry : CompletionRetry;
            await Task.Delay(delay, time, cancellationToken).ConfigureAwait(false);
        }
    }

}
