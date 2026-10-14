namespace BackupPlatform.StorageProviders.Abstractions.Transfer;

/// <summary>The upload policy cannot be followed: it could not be obtained, or the lease it depends on is gone. An <see cref="IOException"/> so a paced stream fails like any broken source.</summary>
public sealed class UploadPolicyException(string message, bool transient = false, Exception? innerException = null) : IOException(message, innerException)
{
    /// <summary>Asking again may succeed (network, backend restart); false for a refusal.</summary>
    public bool Transient { get; } = transient;
}

/// <summary>What the policy owner currently allows one upload. <see cref="HoldsSlice"/> means a share of a tenant limit is leased and must be renewed within <see cref="ValidFor"/>.</summary>
public sealed record UploadPacingPolicy(long? BytesPerSecond, bool HoldsSlice, long? MinimumBytesPerSecond, TimeSpan RefreshAfter, TimeSpan ValidFor);

/// <summary>Where the policy comes from: the backend's database (called in-process by the relay) or the backend's API (called by the agent).</summary>
public interface IUploadPolicySource
{
    /// <summary>Reads the policy and renews this upload's slice; with <paramref name="release"/> gives the slice back.</summary>
    /// <exception cref="UploadPolicyException">The policy could not be obtained.</exception>
    Task<UploadPacingPolicy> FetchAsync(bool release, CancellationToken cancellationToken);
}

/// <summary>
/// Paces ONE upload to the policy of its <see cref="IUploadPolicySource"/> and keeps the pace current by asking again every few seconds, so a limit change
/// reaches a running transfer. Whoever reads the bytes uses it: the agent for uploads only it can see (AgentDirect), the backend relay for the rest, so a
/// byte is never paced twice.
/// <list type="bullet">
/// <item>Fail-closed start: no policy, no pacer. It also waits for a free slice before it hands out a stream, so no request sits open without a byte flowing.</item>
/// <item>A slice is a lease. If it cannot be renewed in time the transfer is held, never run on, because the slice may already belong to another upload;
/// a refusal fails the transfer at its next read.</item>
/// <item>The renewal loop is owned by the pacer and ends with it: no background task outlives the upload.</item>
/// </list>
/// </summary>
public sealed class UploadPacer : IAsyncDisposable
{
    private const int StartAttempts = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(5);

    private readonly IUploadPolicySource _source;
    private readonly TimeProvider _time;
    private readonly TransferRateLimiter _limiter;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _renewal;
    private UploadPacingPolicy _policy;
    private DateTimeOffset _validUntil;
    private bool _sliceTaken;

    private UploadPacer(IUploadPolicySource source, TimeProvider time, UploadPacingPolicy policy)
    {
        _source = source;
        _time = time;
        _limiter = new TransferRateLimiter(time: time);
        _policy = policy;
        Apply(policy);
        _renewal = RenewAsync(_stop.Token);
    }

    /// <summary>The lowest rate the transfer can legitimately be held to under the current policy; null when unlimited. Widens deadlines by size / rate.</summary>
    public long? MinimumBytesPerSecond => Volatile.Read(ref _policy).MinimumBytesPerSecond;

    /// <exception cref="UploadPolicyException">The policy could not be obtained.</exception>
    public static async Task<UploadPacer> BeginAsync(IUploadPolicySource source, TimeProvider time, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        var failures = 0;
        var registered = false;
        try
        {
            while (true)
            {
                TimeSpan delay;
                try
                {
                    var policy = await source.FetchAsync(release: false, cancellationToken).ConfigureAwait(false);
                    registered |= policy.HoldsSlice;
                    // Registered as waiting, the upload is given a slice at the next renewal.
                    if (policy.BytesPerSecond != 0) return new UploadPacer(source, time, policy);
                    delay = policy.RefreshAfter;
                }
                catch (UploadPolicyException exception) when (exception.Transient && ++failures < StartAttempts)
                {
                    delay = RetryDelay * failures;
                }
                await Task.Delay(delay, time, cancellationToken).ConfigureAwait(false);
            }
        }
        catch when (registered)
        {
            await ReleaseAsync(source).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>The stream to upload from: reads of <paramref name="source"/> are paced; length and position pass through.</summary>
    public Stream Wrap(Stream source) => new ThrottledStream(source, _limiter);

    private void Apply(UploadPacingPolicy policy)
    {
        Volatile.Write(ref _policy, policy);
        _sliceTaken |= policy.HoldsSlice;
        _validUntil = _time.GetUtcNow() + policy.ValidFor;
        _limiter.SetRate(policy.BytesPerSecond);
    }

    private async Task RenewAsync(CancellationToken cancellationToken)
    {
        try
        {
            var delay = _policy.RefreshAfter;
            while (true)
            {
                await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
                try
                {
                    Apply(await _source.FetchAsync(release: false, cancellationToken).ConfigureAwait(false));
                    delay = _policy.RefreshAfter;
                }
                catch (UploadPolicyException exception) when (exception.Transient)
                {
                    delay = RetryDelay;
                    // The slice was only promised until _validUntil; past it the slice belongs to someone else.
                    if (_policy.HoldsSlice && _time.GetUtcNow() >= _validUntil) _limiter.SetRate(0);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            // The lease is gone or the credential refused: the transfer must stop at its next read, not run on under a policy it can no longer follow.
            _limiter.Fail(exception as UploadPolicyException ?? new UploadPolicyException("The upload limits can no longer be followed.", innerException: exception));
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _renewal.ConfigureAwait(false);
        _stop.Dispose();
        if (_sliceTaken) await ReleaseAsync(_source).ConfigureAwait(false);
    }

    // Best effort: the slice expires by itself within seconds if this fails.
    private static async Task ReleaseAsync(IUploadPolicySource source)
    {
        using var timeout = new CancellationTokenSource(ReleaseTimeout);
        try { await source.FetchAsync(release: true, timeout.Token).ConfigureAwait(false); }
        catch (Exception exception) when (exception is UploadPolicyException or OperationCanceledException) { }
    }
}
