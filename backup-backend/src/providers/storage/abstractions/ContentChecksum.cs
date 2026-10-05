using System.Security.Cryptography;

namespace BackupPlatform.StorageProviders.Abstractions;

public enum ChecksumAlgorithm
{
    Sha256 = 1
}

/// <summary>An algorithm plus its digest, normalised to lowercase hexadecimal so equal digests always compare equal.</summary>
public sealed record ContentChecksum
{
    public const int Sha256HexLength = 64;

    public ContentChecksum(ChecksumAlgorithm algorithm, string hex)
    {
        if (algorithm != ChecksumAlgorithm.Sha256)
        {
            throw new ArgumentOutOfRangeException(nameof(algorithm), algorithm, "Unsupported checksum algorithm.");
        }

        ArgumentNullException.ThrowIfNull(hex);
        if (hex.Length != Sha256HexLength || !hex.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("A SHA-256 digest is 64 hexadecimal characters.", nameof(hex));
        }

        Algorithm = algorithm;
        Hex = hex.ToLowerInvariant();
    }

    public ChecksumAlgorithm Algorithm { get; }
    public string Hex { get; }

    public static ContentChecksum Sha256(ReadOnlySpan<byte> digest) =>
        digest.Length == SHA256.HashSizeInBytes
            ? new(ChecksumAlgorithm.Sha256, Convert.ToHexString(digest))
            : throw new ArgumentException("A SHA-256 digest is 32 bytes.", nameof(digest));

    public override string ToString() => $"sha256:{Hex}";
}

/// <summary>Streaming hashing: the content is read in fixed-size blocks and never held in memory as a whole.</summary>
public static class ContentHasher
{
    private const int BufferSize = 1024 * 1024;

    /// <summary>
    /// Reads <paramref name="content"/> to its end and returns the SHA-256 of what was read. <paramref name="progress"/> receives the
    /// cumulative byte count after every block. Cancellation is honoured between blocks.
    /// </summary>
    public static async Task<ContentChecksum> ComputeSha256Async(Stream content, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        long total = 0;
        int read;
        while ((read = await content.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            total += read;
            progress?.Report(total);
        }

        return ContentChecksum.Sha256(hash.GetHashAndReset());
    }
}
