using System.Runtime.ExceptionServices;

namespace BackupPlatform.StorageProviders.Abstractions.Transfer;

/// <summary>
/// A byte-rate ceiling shared by every stream that is given it: a token bucket that is refilled continuously, so bytes leave in small steady
/// slices rather than bursts followed by long stalls (reverse proxies and provider idle timeouts see a live connection). The rate can change while
/// transfers are waiting; <c>null</c> is unlimited and costs one volatile read per call, <c>0</c> holds every caller until the rate changes.
/// It knows nothing about storage, HTTP or the backup engine, so any upload path can use it.
/// </summary>
/// <remarks>
/// Over any window of length T the bytes granted never exceed <c>rate × T + <see cref="Capacity"/></c>: one slice (a tenth of a second of traffic,
/// at most <see cref="MaxChunkBytes"/>) plus 50 ms. A limiter that is shared by several streams limits their sum.
/// </remarks>
public sealed class TransferRateLimiter
{
    public const int MaxChunkBytes = 64 * 1024;
    private const int MinChunkBytes = 512;
    private const long Unlimited = -1;
    private static readonly TimeSpan MinimumWait = TimeSpan.FromMilliseconds(2);

    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private long _rate;
    private double _tokens;
    private long _stamp;
    private Exception? _fault;
    private TaskCompletionSource _changed = NewSignal();

    public TransferRateLimiter(long? bytesPerSecond = null, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _stamp = _time.GetTimestamp();
        Apply(bytesPerSecond);
    }

    /// <summary>True when a read needs no permission at all (no ceiling, nothing failed): the caller may take the bare-stream path.</summary>
    public bool IsUnlimited => Volatile.Read(ref _rate) == Unlimited && Volatile.Read(ref _fault) is null;

    /// <summary>The current ceiling: null unlimited, 0 held.</summary>
    public long? BytesPerSecond => Volatile.Read(ref _rate) is var rate and >= 0 ? rate : null;

    /// <summary>Changes the ceiling for every current and future caller; waiting callers re-evaluate at once.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The rate is negative.</exception>
    public void SetRate(long? bytesPerSecond)
    {
        if (bytesPerSecond < 0) throw new ArgumentOutOfRangeException(nameof(bytesPerSecond));
        TaskCompletionSource released;
        lock (_gate)
        {
            Refill();
            Apply(bytesPerSecond);
            released = _changed;
            _changed = NewSignal();
        }
        released.TrySetResult();
    }

    /// <summary>Makes every current and future <see cref="AcquireAsync"/> throw <paramref name="exception"/>; the transfer cannot continue under a policy it can no longer follow.</summary>
    public void Fail(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        TaskCompletionSource released;
        lock (_gate)
        {
            _fault ??= exception;
            released = _changed;
            _changed = NewSignal();
        }
        released.TrySetResult();
    }

    /// <summary>
    /// Waits until up to <paramref name="requested"/> bytes may be sent and returns how many (at least 1, at most <paramref name="requested"/>; at most
    /// <see cref="MaxChunkBytes"/> while a limit applies). Call <see cref="Refund"/> with whatever was not actually used.
    /// </summary>
    public ValueTask<int> AcquireAsync(int requested, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requested);
        if (IsUnlimited) return ValueTask.FromResult(requested);
        return AcquireLimitedAsync(requested, cancellationToken);
    }

    /// <summary>Returns bytes that were granted but not transferred (a short read or end of stream) so they do not count against the ceiling.</summary>
    public void Refund(int bytes)
    {
        if (bytes <= 0) return;
        lock (_gate)
        {
            if (_rate > 0) _tokens = Math.Min(Capacity(_rate), _tokens + bytes);
        }
    }

    private async ValueTask<int> AcquireLimitedAsync(int requested, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TimeSpan wait;
            Task changed;
            lock (_gate)
            {
                if (_fault is { } fault) ExceptionDispatchInfo.Capture(fault).Throw();
                if (_rate == Unlimited) return requested;
                changed = _changed.Task;
                if (_rate == 0)
                {
                    wait = Timeout.InfiniteTimeSpan;
                }
                else
                {
                    Refill();
                    var chunk = Math.Min(requested, Chunk(_rate));
                    if (_tokens >= chunk)
                    {
                        _tokens -= chunk;
                        return chunk;
                    }
                    // Never sleep for less than a timer can resolve: at high rates the surplus that accrues meanwhile is handed out by the next calls, without a wait.
                    wait = TimeSpan.FromSeconds(Math.Max((chunk - _tokens) / _rate, MinimumWait.TotalSeconds));
                }
            }
            if (wait == Timeout.InfiniteTimeSpan)
            {
                await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                try { await Task.WhenAny(Task.Delay(wait, _time, stop.Token), changed).ConfigureAwait(false); }
                finally { await stop.CancelAsync().ConfigureAwait(false); }
            }
        }
    }

    private void Apply(long? bytesPerSecond)
    {
        var rate = bytesPerSecond ?? Unlimited;
        Volatile.Write(ref _rate, rate);
        // A new ceiling starts with a full bucket: a bounded burst, and a limited transfer never starts with a stall.
        _tokens = rate > 0 ? Capacity(rate) : 0;
    }

    private void Refill()
    {
        var now = _time.GetTimestamp();
        // Raw ticks, not TimeSpan: a TimeSpan truncates to 100 ns, and a caller polling in a tight loop would lose that remainder on every call.
        var elapsedSeconds = (now - _stamp) / (double)_time.TimestampFrequency;
        _stamp = now;
        if (_rate > 0) _tokens = Math.Min(Capacity(_rate), _tokens + elapsedSeconds * _rate);
    }

    /// <summary>The largest single grant: a tenth of a second of traffic, within <see cref="MinChunkBytes"/> and <see cref="MaxChunkBytes"/>.</summary>
    public static int Chunk(long rate) => (int)Math.Clamp(rate / 10, MinChunkBytes, MaxChunkBytes);

    /// <summary>One slice plus 50 ms of traffic: the extra lets a coarse timer (Windows ticks at ~15 ms) oversleep without costing throughput.</summary>
    public static double Capacity(long rate) => Chunk(rate) + rate / 20.0;

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
