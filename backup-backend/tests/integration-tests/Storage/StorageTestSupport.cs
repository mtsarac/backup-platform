using System.Security.Cryptography;
using BackupPlatform.StorageProviders.Abstractions;

namespace BackupPlatform.IntegrationTests.Storage;

/// <summary>A readable stream that yields a deterministic byte pattern of a fixed length, then optionally runs a hook (to fail or stall) instead of reporting the end.</summary>
public sealed class PatternStream(long length, Func<CancellationToken, Task>? afterEnd = null) : Stream
{
    private long _position;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _position; set => throw new NotSupportedException(); }

    public static byte ByteAt(long position) => (byte)(position ^ (position >> 8) ^ (position >> 16) ^ 0x5A);

    public static byte[] Bytes(long length)
    {
        var data = new byte[length];
        new PatternStream(length).ReadExactly(data);
        return data;
    }

    public static ContentChecksum ChecksumOf(long length)
    {
        using var stream = new PatternStream(length);
        return ContentChecksum.Sha256(SHA256.HashData(stream));
    }

    public override int Read(Span<byte> buffer)
    {
        var count = (int)Math.Min(buffer.Length, length - _position);
        if (count == 0 && afterEnd is not null)
        {
            afterEnd(CancellationToken.None).GetAwaiter().GetResult();
        }

        for (var i = 0; i < count; i++)
        {
            buffer[i] = ByteAt(_position + i);
        }

        _position += count;
        return count;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_position >= length && afterEnd is not null)
        {
            await afterEnd(cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Read(buffer.Span);
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

public sealed class RecordingProgress : IProgress<long>
{
    private readonly List<long> _values = [];

    public IReadOnlyList<long> Values
    {
        get
        {
            lock (_values)
            {
                return [.. _values];
            }
        }
    }

    public void Report(long value)
    {
        lock (_values)
        {
            _values.Add(value);
        }
    }
}

/// <summary>
/// Runs the test only where the current account may create symbolic links (always on Linux/macOS; on Windows only with the privilege or
/// Developer Mode). The probe happens at discovery and the skip reason says so, so a missing capability is visible rather than a silent pass.
/// </summary>
public sealed class SymlinkFactAttribute : FactAttribute
{
    public SymlinkFactAttribute()
    {
        var probe = Path.Combine(Path.GetTempPath(), "bp_test_probe_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(probe);
            Directory.CreateSymbolicLink(Path.Combine(probe, "link"), probe);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Skip = "Creating symbolic links is not permitted for this account on this platform.";
        }
        finally
        {
            try
            {
                Directory.Delete(probe, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
