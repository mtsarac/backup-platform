using BackupPlatform.Agent.Core.Backup;
using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.StorageProviders.Abstractions.Transfer;

namespace BackupPlatform.Agent.Core.Tasks;

/// <summary>
/// The agent's enforcement of the backend's upload policy for ONE attempt. The backend decides (job, agent and tenant ceilings, and this upload's
/// slice of the tenant ceiling); this applies the lowest of them to a <see cref="TransferRateLimiter"/> and keeps it current by asking again every few
/// seconds, so a limit change reaches a running upload without restarting it.
/// <list type="bullet">
/// <item>Fail-closed start: no policy, no upload.</item>
/// <item>A slice of the tenant limit is a lease. If it cannot be renewed in time (backend unreachable) the transfer is held, never run on, because the
/// backend has already handed the slice to others; a refusal (lease lost) fails the transfer at its next read.</item>
/// <item>The poll loop is owned by the session and ends with it: no background task outlives the upload.</item>
/// </list>
/// </summary>
public sealed class UploadBandwidthGovernor(IBackupTaskClient client, IAgentCredentialStore identities, Guid attemptId, Guid leaseToken, TimeProvider time) : IUploadBandwidth
{
    private const int StartAttempts = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(5);

    public async Task<IUploadBandwidthSession> BeginAsync(CancellationToken cancellationToken)
    {
        var failures = 0;
        var registered = false;
        try
        {
            while (true)
            {
                TimeSpan delay;
                try
                {
                    var policy = await FetchAsync(release: false, cancellationToken).ConfigureAwait(false);
                    registered |= policy.TenantShareBytesPerSecond is not null;
                    // A transfer must not begin while it holds no slice: its request would sit open without a byte flowing, which the relay's
                    // minimum-data-rate check (and every proxy in front of it) ends. Registered as waiting, it is given a slice at the next renewal.
                    if (policy.EffectiveBytesPerSecond != 0) return new Session(this, policy);
                    delay = TimeSpan.FromSeconds(Math.Max(1, policy.RefreshAfterSeconds));
                }
                catch (UploadBandwidthUnavailableException exception) when (exception.InnerException is TransientPolicyFailure && ++failures < StartAttempts)
                {
                    delay = RetryDelay * failures;
                }
                await Task.Delay(delay, time, cancellationToken).ConfigureAwait(false);
            }
        }
        catch when (registered)
        {
            await ReleaseAsync().ConfigureAwait(false);
            throw;
        }
    }

    // Best effort: the slice expires by itself within seconds if this fails.
    private async Task ReleaseAsync()
    {
        using var timeout = new CancellationTokenSource(ReleaseTimeout);
        try { await FetchAsync(release: true, timeout.Token).ConfigureAwait(false); }
        catch (Exception exception) when (exception is UploadBandwidthUnavailableException or OperationCanceledException) { }
    }

    private TimeProvider Clock => time;

    private async Task<UploadPolicyResponse> FetchAsync(bool release, CancellationToken cancellationToken)
    {
        var identity = await identities.LoadAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new UploadBandwidthUnavailableException("The local identity is unavailable, so the upload limits cannot be obtained.");
        var result = await client.GetUploadPolicyAsync(identity, attemptId, new UploadPolicyRequest { LeaseToken = leaseToken, Release = release }, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess) return result.Value;
        var transient = result.Failure.Kind is AgentBackendFailureKind.Transient or AgentBackendFailureKind.RateLimited;
        throw new UploadBandwidthUnavailableException("The upload limits could not be obtained from the backend.", transient ? new TransientPolicyFailure(result.Failure.Description) : null);
    }

    private sealed class TransientPolicyFailure(string description) : Exception(description);

    private sealed class Session : IUploadBandwidthSession
    {
        private readonly UploadBandwidthGovernor _owner;
        private readonly TransferRateLimiter _limiter;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _poll;
        private UploadPolicyResponse _policy;
        private DateTimeOffset _validUntil;
        private bool _sliceTaken;

        public Session(UploadBandwidthGovernor owner, UploadPolicyResponse policy)
        {
            _owner = owner;
            _limiter = new TransferRateLimiter(time: owner.Clock);
            _policy = policy;
            Apply(policy);
            _poll = PollAsync(_stop.Token);
        }

        public Stream Wrap(Stream source) => new ThrottledStream(source, _limiter);

        public TimeSpan DeadlineAllowance(long sizeBytes) => UploadPolicyResponse.DeadlineAllowance(sizeBytes, Volatile.Read(ref _policy).MinimumBytesPerSecond);

        private void Apply(UploadPolicyResponse policy)
        {
            Volatile.Write(ref _policy, policy);
            _sliceTaken |= policy.TenantShareBytesPerSecond is not null;
            _validUntil = _owner.Clock.GetUtcNow() + TimeSpan.FromSeconds(Math.Max(1, policy.ValidForSeconds));
            _limiter.SetRate(policy.EffectiveBytesPerSecond);
        }

        private async Task PollAsync(CancellationToken cancellationToken)
        {
            try
            {
                var delay = TimeSpan.FromSeconds(Math.Max(1, _policy.RefreshAfterSeconds));
                while (true)
                {
                    await Task.Delay(delay, _owner.Clock, cancellationToken).ConfigureAwait(false);
                    try
                    {
                        Apply(await _owner.FetchAsync(release: false, cancellationToken).ConfigureAwait(false));
                        delay = TimeSpan.FromSeconds(Math.Max(1, _policy.RefreshAfterSeconds));
                    }
                    catch (UploadBandwidthUnavailableException exception) when (exception.InnerException is TransientPolicyFailure)
                    {
                        delay = RetryDelay;
                        // The backend only promised the slice until _validUntil; past it the slice belongs to someone else.
                        if (_policy.TenantShareBytesPerSecond is not null && _owner.Clock.GetUtcNow() >= _validUntil) _limiter.SetRate(0);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                // The lease is gone or the credential refused: the transfer must stop at its next read, not run on under a policy it can no longer follow.
                _limiter.Fail(exception as UploadBandwidthUnavailableException ?? new UploadBandwidthUnavailableException("The upload limits can no longer be followed.", exception));
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync().ConfigureAwait(false);
            await _poll.ConfigureAwait(false);
            _stop.Dispose();
            if (_sliceTaken) await _owner.ReleaseAsync().ConfigureAwait(false);
        }
    }
}
