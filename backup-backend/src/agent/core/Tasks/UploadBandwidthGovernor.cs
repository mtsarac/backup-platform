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
        if (destination != StorageTopology.AgentDirect)
        {
            var observed = await FetchAsync(UploadPolicyRequestMode.Observe, Guid.Empty, cancellationToken).ConfigureAwait(false);
            if (observed.PauseRequested) throw new TransferPausedException("TimeRule");
            return new ObservedSession(observed.MinimumBytesPerSecond);
        }
        var pause = new CancellationTokenSource();
        try
        {
            using var start = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, pause.Token);
            var pacer = await UploadPacer.BeginAsync(new UploadSource(this, Guid.NewGuid(), pause), time, start.Token).ConfigureAwait(false);
            return new PacedSession(pacer, pause);
        }
        catch (OperationCanceledException) when (pause.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            pause.Dispose();
            throw new TransferPausedException("TimeRule");
        }
        catch
        {
            pause.Dispose();
            throw;
        }
    }

    /// <summary>One paced upload: it holds its own slice of the tenant limit, whatever else the attempt uploads.</summary>
    private sealed class UploadSource(UploadBandwidthGovernor owner, Guid uploadId, CancellationTokenSource pause) : IUploadPolicySource
    {
        public async Task<UploadPacingPolicy> FetchAsync(bool release, CancellationToken cancellationToken)
        {
            var policy = await owner.FetchAsync(release ? UploadPolicyRequestMode.Release : UploadPolicyRequestMode.Renew, uploadId, cancellationToken).ConfigureAwait(false);
            if (!release && policy.PauseRequested) pause.Cancel();
            return new UploadPacingPolicy(policy.EffectiveBytesPerSecond, !policy.PauseRequested && policy.TenantShareBytesPerSecond is not null,
                policy.MinimumBytesPerSecond, TimeSpan.FromSeconds(Math.Max(1, policy.RefreshAfterSeconds)), TimeSpan.FromSeconds(Math.Max(1, policy.ValidForSeconds)));
        }
    }

    private enum UploadPolicyRequestMode { Renew, Release, Observe }

    private async Task<UploadPolicyResponse> FetchAsync(UploadPolicyRequestMode mode, Guid uploadId, CancellationToken cancellationToken)
    {
        var identity = await identities.LoadAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new UploadPolicyException("The local identity is unavailable, so the upload limits cannot be obtained.");
        var request = new UploadPolicyRequest { LeaseToken = leaseToken, Release = mode == UploadPolicyRequestMode.Release, Observe = mode == UploadPolicyRequestMode.Observe, UploadId = uploadId };
        var result = await client.GetUploadPolicyAsync(identity, attemptId, request, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
            throw new UploadPolicyException("The upload limits could not be obtained from the backend.",
                result.Failure.Kind is AgentBackendFailureKind.Transient or AgentBackendFailureKind.RateLimited);
        return result.Value;
    }

    private sealed class PacedSession(UploadPacer pacer, CancellationTokenSource pause) : IUploadBandwidthSession
    {
        public CancellationToken TimeRulePauseToken => pause.Token;
        public Stream Wrap(Stream source) => pacer.Wrap(source);
        public TimeSpan DeadlineAllowance(long sizeBytes) => UploadPolicyResponse.DeadlineAllowance(sizeBytes, pacer.MinimumBytesPerSecond);
        public async ValueTask DisposeAsync()
        {
            await pacer.DisposeAsync().ConfigureAwait(false);
            pause.Dispose();
        }
    }

    private sealed class ObservedSession(long? minimumBytesPerSecond) : IUploadBandwidthSession
    {
        public Stream Wrap(Stream source) => source;
        public TimeSpan DeadlineAllowance(long sizeBytes) => UploadPolicyResponse.DeadlineAllowance(sizeBytes, minimumBytesPerSecond);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
