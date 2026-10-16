using BackupPlatform.StorageProviders.Abstractions.Transfer;
using Microsoft.Extensions.Time.Testing;

namespace BackupPlatform.UnitTests.Transfer;

public sealed class UploadPacerTests
{
    private static readonly TimeSpan Refresh = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Valid = TimeSpan.FromSeconds(10);

    private static UploadPacingPolicy Slice(long rate) => new(rate, HoldsSlice: true, rate, Refresh, Valid);

    private static async Task SettleAsync(FakeTimeProvider time, TimeSpan advance)
    {
        time.Advance(advance);
        for (var i = 0; i < 20; i++) await Task.Yield();
        await Task.Delay(1);
    }

    // A timer registered after an Advance only fires on the next one, so keep advancing until the awaited event happens; the real-time limit only guards against a hang.
    private static async Task AdvanceUntilAsync(Task completed, FakeTimeProvider time, TimeSpan step)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!completed.IsCompleted)
        {
            guard.Token.ThrowIfCancellationRequested();
            time.Advance(step);
            await Task.Yield();
        }
    }

    // The policy owner gives a slice away Valid after it was last asked. A renewal that never answers must not keep the transfer running past that.
    [Fact]
    public async Task A_renewal_that_never_answers_holds_the_transfer_when_the_slice_runs_out()
    {
        var time = new FakeTimeProvider();
        var source = new Source(call => call == 1 ? Task.FromResult(Slice(500_000)) : new TaskCompletionSource<UploadPacingPolicy>().Task);
        await using var pacer = await UploadPacer.BeginAsync(source, time, default);
        await using var stream = pacer.Wrap(new MemoryStream(new byte[1 << 20]));
        Assert.Equal(TransferRateLimiter.Chunk(500_000), await stream.ReadAsync(new byte[1 << 20]).AsTask().WaitAsync(TimeSpan.FromSeconds(10)));

        await SettleAsync(time, Refresh);          // the renewal is sent and hangs
        Assert.Equal(TransferRateLimiter.Chunk(500_000), await stream.ReadAsync(new byte[1 << 20]).AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
        time.Advance(Valid);                       // past the promise: the hanging renewal is abandoned and the transfer held
        await AdvanceUntilAsync(source.ThirdCall, time, TimeSpan.FromSeconds(1)); // the next renewal starts only after the hold
        var held = stream.ReadAsync(new byte[1 << 20]).AsTask();
        await SettleAsync(time, TimeSpan.FromSeconds(30));

        Assert.False(held.IsCompleted, "the slice may belong to another upload by now");
    }

    private sealed class Source(Func<int, Task<UploadPacingPolicy>> answer) : IUploadPolicySource
    {
        private readonly TaskCompletionSource _third = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        /// <summary>Completes when the third (non-release) fetch is made.</summary>
        public Task ThirdCall => _third.Task;

        public Task<UploadPacingPolicy> FetchAsync(bool release, CancellationToken cancellationToken)
        {
            if (release) return Task.FromResult(new UploadPacingPolicy(null, false, null, Refresh, Valid));
            var call = Interlocked.Increment(ref _calls);
            if (call == 3) _third.TrySetResult();
            return answer(call);
        }
    }
}
