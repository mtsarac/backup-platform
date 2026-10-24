using System.Security.Cryptography;

namespace BackupPlatform.StorageProviders.Abstractions;

/// <summary>Hashes exactly the bytes a provider reads; <see cref="Position"/> stays zero when the provider never read the stream.</summary>
public sealed class Sha256VerifyingStream(Stream inner) : Stream
{
    private readonly IncrementalHash _hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long _position;

    public string GetHashHex() => Convert.ToHexStringLower(_hasher.GetHashAndReset());

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);
        Append(buffer.AsSpan(offset, read));
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Append(buffer.Span[..read]);
        return read;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var read = await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        Append(buffer.AsSpan(offset, read));
        return read;
    }

    public override void Flush() => inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private void Append(ReadOnlySpan<byte> data)
    {
        if (data.Length > 0)
        {
            _hasher.AppendData(data);
            _position += data.Length;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hasher.Dispose();
        }
        base.Dispose(disposing);
    }
}
