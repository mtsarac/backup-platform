using BackupPlatform.Application.Backups;
using BackupPlatform.StorageProviders.Abstractions.Transfer;

namespace BackupPlatform.Api.Backups;

/// <summary>
/// The upload policy as the relay reads it: straight from the database, for the attempt whose body it is relaying. The relay paces what it reads, so a limit
/// holds whatever the agent does (a modified or old agent cannot skip it) and the bytes are paced exactly once.
/// </summary>
public sealed class RelayUploadPolicySource(UploadBandwidthService service, BackupLeaseCommand lease, TimeProvider time) : IUploadPolicySource
{
    // Every relayed request is its own upload with its own slice, however many the attempt (or a modified agent) runs at once.
    private readonly Guid _uploadId = Guid.NewGuid();

    public async Task<UploadPacingPolicy> FetchAsync(bool release, CancellationToken cancellationToken)
    {
        UploadPolicyResponseView? policy;
        try
        {
            var response = await service.PolicyAsync(lease with { Now = time.GetUtcNow(), UploadId = _uploadId }, release ? UploadSliceAction.Release : UploadSliceAction.Renew, cancellationToken);
            policy = response is null ? null : new(response.EffectiveBytesPerSecond, response.TenantShareBytesPerSecond is not null, response.MinimumBytesPerSecond,
                TimeSpan.FromSeconds(response.RefreshAfterSeconds), TimeSpan.FromSeconds(response.ValidForSeconds));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new UploadPolicyException("The upload limits could not be read.", transient: true, exception);
        }
        return policy is null
            ? throw new UploadPolicyException("The task lease no longer permits this upload.")
            : new UploadPacingPolicy(policy.Rate, policy.HoldsSlice, policy.Minimum, policy.Refresh, policy.ValidFor);
    }

    private sealed record UploadPolicyResponseView(long? Rate, bool HoldsSlice, long? Minimum, TimeSpan Refresh, TimeSpan ValidFor);
}
