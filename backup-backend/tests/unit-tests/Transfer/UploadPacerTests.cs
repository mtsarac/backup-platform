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
        time.Advance(Valid);            // past the promise
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (pacer.CurrentBytesPerSecond != 0 && DateTime.UtcNow < deadline)
        {
            for (var i = 0; i < 20; i++) await Task.Yield();
            await Task.Delay(5);
        }
        Assert.Equal(0, pacer.CurrentBytesPerSecond);
        var held = stream.ReadAsync(new byte[1 << 20]).AsTask();
        await SettleAsync(time, TimeSpan.FromSeconds(30));

        Assert.False(held.IsCompleted, "the slice may belong to another upload by now");
    }

    private sealed class Source(Func<int, Task<UploadPacingPolicy>> answer) : IUploadPolicySource
    {
        private int _calls;
        public Task<UploadPacingPolicy> FetchAsync(bool release, CancellationToken cancellationToken) => release
            ? Task.FromResult(new UploadPacingPolicy(null, false, null, Refresh, Valid))
            : answer(Interlocked.Increment(ref _calls));
    }
}
