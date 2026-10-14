using BackupPlatform.StorageProviders.Abstractions.Transfer;
using Microsoft.Extensions.Time.Testing;

namespace BackupPlatform.UnitTests.Transfer;

public sealed class TransferRateLimiterTests
{
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(10);

    [Fact]
    public async Task Unlimited_grants_everything_at_once_and_a_rate_change_applies_to_the_next_call()
    {
        var limiter = new TransferRateLimiter(null, new FakeTimeProvider());
        Assert.Equal(1 << 20, await limiter.AcquireAsync(1 << 20, default));

        limiter.SetRate(100_000);
        Assert.Equal(TransferRateLimiter.Chunk(100_000), await limiter.AcquireAsync(1 << 20, default));
    }

    // The ceiling is the contract: at no instant may more bytes have left than rate x elapsed plus the bucket,
    // and the transfer must also not run far below the ceiling.
    [Theory]
    [InlineData(16_000L)]       // lowest allowed limit
    [InlineData(1_250_000L)]    // 10 Mbit/s
    [InlineData(100_000_000L)]  // faster than a Task.Delay tick can pace one slice at a time
    public async Task Bytes_granted_never_exceed_rate_times_elapsed_plus_one_bucket_and_track_the_rate(long rate)
    {
        var time = new FakeTimeProvider();
        var limiter = new TransferRateLimiter(rate, time);
        var granted = new Counter();
        var pump = Task.Run(() => Consume(limiter, granted));

        var window = TimeSpan.FromSeconds(20);
        var bucket = TransferRateLimiter.Capacity(rate);
        for (var elapsed = TimeSpan.Zero; elapsed < window; elapsed += Step)
        {
            time.Advance(Step);
            await Settle();
            Assert.True(granted.Value <= rate * (elapsed + Step).TotalSeconds + bucket + 1, $"{granted.Value} bytes after {elapsed + Step}");
        }

        Assert.True(granted.Value >= rate * window.TotalSeconds * 0.9, $"only {granted.Value} bytes in {window}");
        granted.Stop = true;
        limiter.SetRate(null);
        await pump;
    }

    [Fact]
    public async Task Streams_sharing_one_limiter_share_its_ceiling()
    {
        const long rate = 200_000;
        var time = new FakeTimeProvider();
        var limiter = new TransferRateLimiter(rate, time);
        var first = new Counter();
        var second = new Counter();
        var pumps = new[] { Task.Run(() => Consume(limiter, first)), Task.Run(() => Consume(limiter, second)) };

        for (var elapsed = TimeSpan.Zero; elapsed < TimeSpan.FromSeconds(10); elapsed += Step)
        {
            time.Advance(Step);
            await Settle();
            Assert.True(first.Value + second.Value <= rate * (elapsed + Step).TotalSeconds + TransferRateLimiter.Capacity(rate) + 1);
        }

        Assert.True(first.Value > 0 && second.Value > 0, "both uploads make progress");
        first.Stop = second.Stop = true;
        limiter.SetRate(null);
        await Task.WhenAll(pumps);
    }

    [Fact]
    public async Task A_held_transfer_resumes_when_the_rate_is_raised_and_cancels_cleanly()
    {
        var time = new FakeTimeProvider();
        var limiter = new TransferRateLimiter(0, time);
        var waiting = limiter.AcquireAsync(1000, default).AsTask();
        time.Advance(TimeSpan.FromMinutes(5));
        await Settle();
        Assert.False(waiting.IsCompleted, "a held transfer does not move however long it waits");

        limiter.SetRate(1_000_000);
        Assert.Equal(1000, await waiting.WaitAsync(TimeSpan.FromSeconds(10)));

        limiter.SetRate(0);
        using var cancel = new CancellationTokenSource();
        var cancelled = limiter.AcquireAsync(1000, cancel.Token).AsTask();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
    }

    [Fact]
    public async Task A_rate_cut_applies_to_a_transfer_that_is_already_waiting()
    {
        var time = new FakeTimeProvider();
        var limiter = new TransferRateLimiter(16_000, time);
        var first = new Counter();
        var pump = Task.Run(() => Consume(limiter, first));
        time.Advance(TimeSpan.FromSeconds(1));
        await Settle();

        limiter.SetRate(0);
        await Settle();
        var before = first.Value;
        time.Advance(TimeSpan.FromMinutes(1));
        await Settle();
        Assert.Equal(before, first.Value);

        first.Stop = true;
        limiter.SetRate(null);
        await pump;
    }

    [Fact]
    public async Task Failing_the_limiter_wakes_held_and_future_callers_with_the_exception()
    {
        var limiter = new TransferRateLimiter(0, new FakeTimeProvider());
        var waiting = limiter.AcquireAsync(1000, default).AsTask();
        var failure = new IOException("lease lost");

        limiter.Fail(failure);

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => waiting));
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => limiter.AcquireAsync(1, default).AsTask()));
    }

    [Fact]
    public async Task Refunded_bytes_do_not_count_against_the_ceiling()
    {
        var time = new FakeTimeProvider();
        var limiter = new TransferRateLimiter(100_000, time);
        var all = await limiter.AcquireAsync(1 << 20, default);
        limiter.Refund(all);

        Assert.Equal(all, await limiter.AcquireAsync(1 << 20, default));
    }

    private sealed class Counter
    {
        public long Value;
        public volatile bool Stop;
    }

    private static async Task Consume(TransferRateLimiter limiter, Counter counter)
    {
        try
        {
            while (!counter.Stop) Interlocked.Add(ref counter.Value, await limiter.AcquireAsync(TransferRateLimiter.MaxChunkBytes, default));
        }
        catch (OperationCanceledException) { }
    }

    // Lets the continuations that a time advance released run to the next wait.
    private static async Task Settle()
    {
        for (var i = 0; i < 20; i++) await Task.Yield();
        await Task.Delay(1);
    }
}
