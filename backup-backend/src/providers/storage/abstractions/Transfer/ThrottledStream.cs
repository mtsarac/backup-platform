namespace BackupPlatform.StorageProviders.Abstractions.Transfer;

/// <summary>
/// Wraps a source stream so that reading it is paced by a <see cref="TransferRateLimiter"/>. Nothing is buffered or copied: a read asks the limiter
/// for permission first, reads that many bytes straight from the inner stream, and gives back what it did not get. Length, position and seeking pass
/// through, so a consumer that inspects them (an HTTP body that already declares its length) behaves as it does with the bare stream.
/// </summary>
public sealed class ThrottledStream(Stream inner, TransferRateLimiter limiter, bool leaveOpen = true) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }

    // Unlimited is the bare stream: no state machine, no permission, no slice.
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        limiter.IsUnlimited ? inner.ReadAsync(buffer, cancellationToken) : ReadPacedAsync(buffer, cancellationToken);

    private async ValueTask<int> ReadPacedAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (buffer.IsEmpty) return 0;
        var granted = await limiter.AcquireAsync(buffer.Length, cancellationToken).ConfigureAwait(false);
        var read = 0;
        try
        {
            read = await inner.ReadAsync(buffer[..granted], cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            limiter.Refund(granted - read);
        }
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty || limiter.IsUnlimited) return inner.Read(buffer);
        var granted = limiter.AcquireAsync(buffer.Length, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        var read = 0;
        try
        {
            read = inner.Read(buffer[..granted]);
        }
        finally
        {
            limiter.Refund(granted - read);
        }
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !leaveOpen) inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!leaveOpen) await inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
