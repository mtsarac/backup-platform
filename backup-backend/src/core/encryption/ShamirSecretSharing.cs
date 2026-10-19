using System.Buffers.Binary;
using System.Security.Cryptography;

namespace BackupPlatform.Encryption;

/// <summary>The shares are malformed, from different sets, duplicated, insufficient, or do not reconstruct the original secret.</summary>
public sealed class BpeShareException(string message) : Exception(message);

/// <summary>
/// Shamir k-of-n secret sharing over GF(256) (AES polynomial 0x11B), applied byte-wise. Shares are self-describing and authenticated against
/// accidental damage: a trailing checksum catches a mistyped or corrupted share, and a secret check embedded in every share proves that the
/// reconstruction is the original secret, so a forged, mixed-set or tampered share can never yield a silently wrong key.
/// </summary>
/// <remarks>
/// Share layout: "BPSH" | version u8 | threshold u8 | total u8 | index u8 (1..total) | set_id(16) | secret_check(16) | data_len u16 BE | data | checksum(8).
/// secret_check is SHA-256(secret)[0..16]; checksum is SHA-256 over every preceding share byte [0..8]. The checksum is NOT a MAC (anyone can
/// recompute it); integrity of the reconstruction comes from secret_check. The field arithmetic uses lookup tables, so it is not constant-time;
/// the ceremony and recovery tools run offline on custodian-controlled machines.
/// </remarks>
public static class ShamirSecretSharing
{
    public const int MaxShares = 255;
    public const int MaxSecretLength = ushort.MaxValue;
    public const string TextPrefix = "BPSHARE1:";

    private const byte ShareVersion = 1;
    private const int SetIdLength = 16;
    private const int CheckLength = 16;
    private const int ChecksumLength = 8;
    private const int HeaderLength = 4 + 1 + 1 + 1 + 1 + SetIdLength + CheckLength + 2;
    private static readonly byte[] ShareMagic = "BPSH"u8.ToArray();
    private static readonly byte[] Exp = new byte[510];
    private static readonly byte[] Log = new byte[256];

    static ShamirSecretSharing()
    {
        var x = 1;
        for (var i = 0; i < 255; i++)
        {
            Exp[i] = Exp[i + 255] = (byte)x;
            Log[x] = (byte)i;
            // Multiply by the generator 3 (= x + 1) modulo the AES polynomial.
            x ^= (x << 1) ^ ((x & 0x80) != 0 ? 0x11B : 0);
        }
    }

    /// <summary>Splits <paramref name="secret"/> into <paramref name="total"/> encoded shares, any <paramref name="threshold"/> of which reconstruct it.</summary>
    public static IReadOnlyList<byte[]> Split(ReadOnlySpan<byte> secret, int threshold, int total)
    {
        if (secret.Length is 0 or > MaxSecretLength)
        {
            throw new ArgumentOutOfRangeException(nameof(secret), $"Secret length must be 1..{MaxSecretLength} bytes.");
        }

        if (threshold < 2 || threshold > total || total > MaxShares)
        {
            throw new ArgumentOutOfRangeException(nameof(threshold), $"Require 2 <= threshold <= total <= {MaxShares}.");
        }

        var setId = RandomNumberGenerator.GetBytes(SetIdLength);
        var check = SecretCheck(secret);
        var coefficients = RandomNumberGenerator.GetBytes((threshold - 1) * secret.Length);
        var shares = new byte[total][];
        for (var s = 0; s < total; s++)
        {
            var share = new byte[HeaderLength + secret.Length + ChecksumLength];
            ShareMagic.CopyTo(share, 0);
            share[4] = ShareVersion;
            share[5] = (byte)threshold;
            share[6] = (byte)total;
            share[7] = (byte)(s + 1);
            setId.CopyTo(share, 8);
            check.CopyTo(share, 8 + SetIdLength);
            BinaryPrimitives.WriteUInt16BigEndian(share.AsSpan(HeaderLength - 2), (ushort)secret.Length);
            var x = (byte)(s + 1);
            for (var b = 0; b < secret.Length; b++)
            {
                // Horner evaluation of secret[b] + c1*x + c2*x^2 + ... at x.
                var y = coefficients[(threshold - 2) * secret.Length + b];
                for (var degree = threshold - 3; degree >= 0; degree--)
                {
                    y = (byte)(Mul(y, x) ^ coefficients[degree * secret.Length + b]);
                }

                share[HeaderLength + b] = (byte)(Mul(y, x) ^ secret[b]);
            }

            SHA256.HashData(share.AsSpan(0, share.Length - ChecksumLength))[..ChecksumLength].CopyTo(share.AsSpan(share.Length - ChecksumLength));
            shares[s] = share;
        }

        CryptographicOperations.ZeroMemory(coefficients);
        return shares;
    }

    /// <summary>Reconstructs the secret from at least <c>threshold</c> distinct shares of one set. The caller owns, and should zero, the result.</summary>
    /// <exception cref="BpeShareException">A share is malformed or damaged, shares come from different sets, an index repeats, too few shares were given, or the reconstruction fails its secret check.</exception>
    public static byte[] Combine(IReadOnlyList<byte[]> shares)
    {
        ArgumentNullException.ThrowIfNull(shares);
        if (shares.Count == 0)
        {
            throw new BpeShareException("No shares were supplied.");
        }

        var parsed = shares.Select(Parse).ToArray();
        var first = parsed[0];
        if (parsed.Any(share => share.Threshold != first.Threshold || share.Total != first.Total
            || share.Data.Length != first.Data.Length || !share.SetId.AsSpan().SequenceEqual(first.SetId) || !share.Check.AsSpan().SequenceEqual(first.Check)))
        {
            throw new BpeShareException("The shares belong to different sets (mixed ceremonies or rotations).");
        }

        if (parsed.Select(share => share.Index).Distinct().Count() != parsed.Length)
        {
            throw new BpeShareException("The same share was supplied more than once.");
        }

        if (parsed.Length < first.Threshold)
        {
            throw new BpeShareException($"{first.Threshold} distinct shares are required but only {parsed.Length} were supplied.");
        }

        var used = parsed.Take(first.Threshold).ToArray();
        var weights = used.Select(share => LagrangeWeightAtZero(share.Index, used)).ToArray();
        var secret = new byte[first.Data.Length];
        for (var b = 0; b < secret.Length; b++)
        {
            byte value = 0;
            for (var i = 0; i < used.Length; i++)
            {
                value ^= Mul(weights[i], used[i].Data[b]);
            }

            secret[b] = value;
        }

        if (!CryptographicOperations.FixedTimeEquals(SecretCheck(secret), first.Check))
        {
            CryptographicOperations.ZeroMemory(secret);
            throw new BpeShareException("The shares do not reconstruct the original secret: at least one share is corrupted or forged.");
        }

        return secret;
    }

    /// <summary>Encodes a share as a single printable line suitable for a file or a paper copy.</summary>
    public static string ToText(byte[] share) => TextPrefix + Convert.ToBase64String(share);

    /// <summary>Decodes a share written by <see cref="ToText"/>; surrounding whitespace is ignored.</summary>
    public static byte[] FromText(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith(TextPrefix, StringComparison.Ordinal))
        {
            throw new BpeShareException("Not a bpe share (missing BPSHARE1: prefix).");
        }

        try
        {
            return Convert.FromBase64String(trimmed[TextPrefix.Length..]);
        }
        catch (FormatException)
        {
            throw new BpeShareException("The share text is not valid base64.");
        }
    }

    private sealed record ParsedShare(int Threshold, int Total, int Index, byte[] SetId, byte[] Check, byte[] Data);

    private static ParsedShare Parse(byte[] share)
    {
        if (share is null || share.Length < HeaderLength + 1 + ChecksumLength || !share.AsSpan(0, 4).SequenceEqual(ShareMagic))
        {
            throw new BpeShareException("Not a bpe share.");
        }

        if (share[4] != ShareVersion)
        {
            throw new BpeShareException($"Unsupported share version {share[4]}.");
        }

        var dataLength = BinaryPrimitives.ReadUInt16BigEndian(share.AsSpan(HeaderLength - 2));
        if (share.Length != HeaderLength + dataLength + ChecksumLength
            || !CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(share.AsSpan(0, share.Length - ChecksumLength))[..ChecksumLength], share.AsSpan(share.Length - ChecksumLength)))
        {
            throw new BpeShareException("The share is damaged (length or checksum mismatch).");
        }

        int threshold = share[5], total = share[6], index = share[7];
        if (threshold < 2 || threshold > total || index < 1 || index > total)
        {
            throw new BpeShareException("The share header is invalid.");
        }

        return new ParsedShare(threshold, total, index, share[8..(8 + SetIdLength)], share[(8 + SetIdLength)..(8 + SetIdLength + CheckLength)], share[HeaderLength..(HeaderLength + dataLength)]);
    }

    private static byte[] SecretCheck(ReadOnlySpan<byte> secret) => SHA256.HashData(secret)[..CheckLength];

    private static byte LagrangeWeightAtZero(int xi, ParsedShare[] used)
    {
        byte weight = 1;
        foreach (var other in used)
        {
            if (other.Index != xi)
            {
                weight = Mul(weight, Div((byte)other.Index, (byte)(xi ^ other.Index)));
            }
        }

        return weight;
    }

    private static byte Mul(byte a, byte b) => a == 0 || b == 0 ? (byte)0 : Exp[Log[a] + Log[b]];

    private static byte Div(byte a, byte b) => a == 0 ? (byte)0 : Exp[Log[a] + 255 - Log[b]];
}
