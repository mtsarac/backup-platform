using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace BackupPlatform.Encryption;

/// <summary>A public wrap key the DEK is sealed to (RSA-OAEP-SHA256). Private halves never reach the encrypting side.</summary>
public sealed record BpeRecipient(BpeRecipientType Type, int KeyVersion, RSA PublicKey);

/// <summary>Fixed inputs for deterministic test vectors; production callers leave every member null so each is random.</summary>
public sealed record BpeEncryptOptions(int ChunkSize = BpeFormat.DefaultChunkSize, byte[]? Dek = null, byte[]? ArtifactId = null, byte[]? NoncePrefix = null);

public static class BpeEncryptor
{
    private const int MinRsaBits = 4096;

    /// <summary>Streams <paramref name="plaintext"/> into a <c>bpe</c> v2 artifact on <paramref name="output"/> with memory bounded by two chunks.</summary>
    /// <param name="sourceSize">Exact plaintext length; it is part of the authenticated core, so a mismatch fails the call.</param>
    public static async Task EncryptAsync(
        Stream plaintext, Stream output, long sourceSize, IReadOnlyList<BpeRecipient> recipients,
        BpeEncryptOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new BpeEncryptOptions();
        ArgumentNullException.ThrowIfNull(recipients);
        if (recipients.Count == 0)
        {
            throw new ArgumentException("At least one recipient is required.", nameof(recipients));
        }

        if (options.ChunkSize is <= 0 or > BpeFormat.MaxChunkSize || sourceSize < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Invalid chunk size or source size.");
        }

        var dek = options.Dek ?? RandomNumberGenerator.GetBytes(BpeFormat.DekLength);
        if (dek.Length != BpeFormat.DekLength)
        {
            throw new ArgumentException("DEK must be 32 bytes.", nameof(options));
        }

        try
        {
            var core = BpeFormat.EncodeCore(new BpeFormat.Core(
                options.ArtifactId ?? RandomNumberGenerator.GetBytes(16), options.ChunkSize,
                options.NoncePrefix ?? RandomNumberGenerator.GetBytes(BpeFormat.NoncePrefixLength), sourceSize));
            var coreHash = SHA256.HashData(core);
            var recipientsJson = WrapRecipients(dek, recipients);
            var header = BpeFormat.BuildHeader(core, recipientsJson, BpeFormat.ComputeMac(dek, coreHash, recipientsJson));

            var preamble = new byte[12];
            BpeFormat.Magic.CopyTo(preamble);
            BinaryPrimitives.WriteUInt16BigEndian(preamble.AsSpan(6), BpeFormat.Version);
            BinaryPrimitives.WriteUInt32BigEndian(preamble.AsSpan(8), (uint)header.Length);
            await output.WriteAsync(preamble, cancellationToken).ConfigureAwait(false);
            await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);

            await WriteChunksAsync(plaintext, output, dek, coreHash, core, options.ChunkSize, sourceSize, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    private static async Task WriteChunksAsync(
        Stream plaintext, Stream output, byte[] dek, byte[] coreHash, byte[] core, int chunkSize, long sourceSize, CancellationToken cancellationToken)
    {
        var noncePrefix = core.AsSpan(21, BpeFormat.NoncePrefixLength).ToArray();
        using var aes = new AesGcm(dek, BpeFormat.TagLength);
        var current = new byte[chunkSize];
        var next = new byte[chunkSize];
        var sealedChunk = new byte[chunkSize + BpeFormat.TagLength];
        var currentLength = await ReadFullAsync(plaintext, current, cancellationToken).ConfigureAwait(false);
        long total = 0;
        for (ulong counter = 0; ; counter++)
        {
            var nextLength = currentLength == chunkSize
                ? await ReadFullAsync(plaintext, next, cancellationToken).ConfigureAwait(false)
                : 0;
            var isFinal = nextLength == 0;
            aes.Encrypt(BpeFormat.ChunkNonce(noncePrefix, counter), current.AsSpan(0, currentLength),
                sealedChunk.AsSpan(0, currentLength), sealedChunk.AsSpan(currentLength, BpeFormat.TagLength),
                BpeFormat.ChunkAad(coreHash, counter, isFinal));
            await output.WriteAsync(sealedChunk.AsMemory(0, currentLength + BpeFormat.TagLength), cancellationToken).ConfigureAwait(false);
            total += currentLength;
            if (isFinal)
            {
                break;
            }

            (current, next) = (next, current);
            currentLength = nextLength;
        }

        if (total != sourceSize)
        {
            throw new InvalidOperationException($"Plaintext was {total} bytes but {sourceSize} were declared.");
        }
    }

    private static byte[] WrapRecipients(byte[] dek, IReadOnlyList<BpeRecipient> recipients)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartArray();
            foreach (var recipient in recipients)
            {
                if (recipient.PublicKey.KeySize < MinRsaBits)
                {
                    throw new ArgumentException($"Recipient RSA keys must be at least {MinRsaBits} bits.");
                }

                json.WriteStartObject();
                json.WriteString("type", BpeFormat.TypeName(recipient.Type));
                json.WriteString("key_id", BpeFormat.KeyId(recipient.PublicKey));
                json.WriteNumber("key_version", recipient.KeyVersion);
                json.WriteString("wrapped_dek", Convert.ToBase64String(recipient.PublicKey.Encrypt(dek, RSAEncryptionPadding.OaepSHA256)));
                json.WriteEndObject();
            }

            json.WriteEndArray();
        }

        return stream.ToArray();
    }

    internal static async Task<int> ReadFullAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        return read;
    }
}
