using BackupPlatform.Agent.Core.Backup;
using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.StorageProviders.Abstractions;
using BackupPlatform.StorageProviders.Abstractions.Transfer;

namespace BackupPlatform.Agent.Core.Tasks;

/// <summary>
/// Gets the upload policy of ONE attempt from the backend's API. An AgentDirect destination is paced here by an <see cref="UploadPacer"/> (the backend
/// never sees those bytes); a relayed destination is paced by the backend, so the agent only reads the policy once to know how much to widen its deadline.
/// </summary>
public sealed class UploadBandwidthGovernor(IBackupTaskClient client, IAgentCredentialStore identities, Guid attemptId, Guid leaseToken, TimeProvider time)
    : IUploadBandwidth
{
    public async Task<IUploadBandwidthSession> BeginAsync(StorageTopology destination, CancellationToken cancellationToken)
    {
        if (destination == StorageTopology.AgentDirect)
            return new PacedSession(await UploadPacer.BeginAsync(new UploadSource(this, Guid.NewGuid()), time, cancellationToken).ConfigureAwait(false));
        var policy = await FetchAsync(UploadPolicyRequestMode.Observe, Guid.Empty, cancellationToken).ConfigureAwait(false);
        return new ObservedSession(policy.MinimumBytesPerSecond);
    }

    /// <summary>One paced upload: it holds its own slice of the tenant limit, whatever else the attempt uploads.</summary>
    private sealed class UploadSource(UploadBandwidthGovernor owner, Guid uploadId) : IUploadPolicySource
    {
        public Task<UploadPacingPolicy> FetchAsync(bool release, CancellationToken cancellationToken) =>
            owner.FetchAsync(release ? UploadPolicyRequestMode.Release : UploadPolicyRequestMode.Renew, uploadId, cancellationToken);
    }

    private enum UploadPolicyRequestMode { Renew, Release, Observe }

    private async Task<UploadPacingPolicy> FetchAsync(UploadPolicyRequestMode mode, Guid uploadId, CancellationToken cancellationToken)
    {
        var identity = await identities.LoadAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new UploadPolicyException("The local identity is unavailable, so the upload limits cannot be obtained.");
        var request = new UploadPolicyRequest { LeaseToken = leaseToken, Release = mode == UploadPolicyRequestMode.Release, Observe = mode == UploadPolicyRequestMode.Observe, UploadId = uploadId };
        var result = await client.GetUploadPolicyAsync(identity, attemptId, request, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
            throw new UploadPolicyException("The upload limits could not be obtained from the backend.",
                result.Failure.Kind is AgentBackendFailureKind.Transient or AgentBackendFailureKind.RateLimited);
        var policy = result.Value;
        return new UploadPacingPolicy(policy.EffectiveBytesPerSecond, policy.TenantShareBytesPerSecond is not null, policy.MinimumBytesPerSecond,
            TimeSpan.FromSeconds(Math.Max(1, policy.RefreshAfterSeconds)), TimeSpan.FromSeconds(Math.Max(1, policy.ValidForSeconds)));
    }

    private sealed class PacedSession(UploadPacer pacer) : IUploadBandwidthSession
    {
        public Stream Wrap(Stream source) => pacer.Wrap(source);
        public TimeSpan DeadlineAllowance(long sizeBytes) => UploadPolicyResponse.DeadlineAllowance(sizeBytes, pacer.MinimumBytesPerSecond);
        public ValueTask DisposeAsync() => pacer.DisposeAsync();
    }

    private sealed class ObservedSession(long? minimumBytesPerSecond) : IUploadBandwidthSession
    {
        public Stream Wrap(Stream source) => source;
        public TimeSpan DeadlineAllowance(long sizeBytes) => UploadPolicyResponse.DeadlineAllowance(sizeBytes, minimumBytesPerSecond);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
