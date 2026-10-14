using System.Security.Cryptography;
using BackupPlatform.StorageProviders.Abstractions.Transfer;
using Microsoft.Extensions.Time.Testing;

namespace BackupPlatform.UnitTests.Transfer;

public sealed class ThrottledStreamTests
{
    private static byte[] Content(int length) => Enumerable.Range(0, length).Select(i => (byte)(i * 31 % 251)).ToArray();

    [Theory]
    [InlineData(null)]
    [InlineData(5_000_000L)]
    public async Task Paced_content_is_byte_identical_to_the_source(long? rate)
    {
        // A real clock with a rate fast enough to finish quickly: the point is that pacing never alters, drops or reorders a byte.
        var source = Content(3 * 1024 * 1024 + 17);
        await using var stream = new ThrottledStream(new MemoryStream(source), new TransferRateLimiter(rate));

        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);

        Assert.Equal(SHA256.HashData(source), SHA256.HashData(copy.ToArray()));
    }

    [Fact]
    public async Task A_read_never_returns_more_than_one_slice_while_a_limit_applies()
    {
        var limiter = new TransferRateLimiter(10_000_000, new FakeTimeProvider());
        await using var stream = new ThrottledStream(new MemoryStream(Content(1 << 20)), limiter);

        var read = await stream.ReadAsync(new byte[1 << 20]);

        Assert.Equal(TransferRateLimiter.MaxChunkBytes, read);
    }

    [Fact]
    public async Task End_of_stream_gives_the_unread_permission_back()
    {
        var time = new FakeTimeProvider();
        var limiter = new TransferRateLimiter(100_000, time);
        await using var stream = new ThrottledStream(new MemoryStream(Content(10)), limiter);
        Assert.Equal(10, await stream.ReadAsync(new byte[1000]));
        Assert.Equal(0, await stream.ReadAsync(new byte[1000]));

        // Had the unused permission been kept, this would have to wait for the bucket to refill.
        Assert.Equal(TransferRateLimiter.Chunk(100_000), await limiter.AcquireAsync(1 << 20, default));
    }

    [Fact]
    public async Task A_failing_inner_read_gives_its_permission_back_and_the_failure_surfaces()
    {
        var limiter = new TransferRateLimiter(100_000, new FakeTimeProvider());
        await using var stream = new ThrottledStream(new FailingStream(), limiter);

        await Assert.ThrowsAsync<IOException>(async () => Assert.Equal(-1, await stream.ReadAsync(new byte[1000])));

        Assert.Equal(TransferRateLimiter.Chunk(100_000), await limiter.AcquireAsync(1 << 20, default));
    }

    [Fact]
    public void Length_position_and_seeking_pass_through_so_a_consumer_sees_the_bare_stream()
    {
        using var inner = new MemoryStream(Content(100));
        using var stream = new ThrottledStream(inner, new TransferRateLimiter(1_000_000));

        Assert.Equal(100, stream.Length);
        stream.Position = 40;
        Assert.Equal(40, inner.Position);
        Assert.True(stream.CanSeek);
        Assert.False(stream.CanWrite);
    }

    [Fact]
    public async Task The_inner_stream_stays_open_unless_ownership_is_handed_over()
    {
        var inner = new MemoryStream(Content(10));
        await new ThrottledStream(inner, new TransferRateLimiter()).DisposeAsync();
        Assert.True(inner.CanRead);

        await new ThrottledStream(inner, new TransferRateLimiter(), leaveOpen: false).DisposeAsync();
        Assert.False(inner.CanRead);
    }

    private sealed class FailingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("disk");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw new IOException("disk");
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
