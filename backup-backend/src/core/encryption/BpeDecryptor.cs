using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace BackupPlatform.Encryption;

public static class BpeDecryptor
{
    /// <summary>
    /// Decrypts a <c>bpe</c> v2 artifact. Every chunk is authenticated before its plaintext is written, but truncation or trailing damage is only
    /// proven at the end: callers MUST discard <paramref name="output"/> when this throws and MUST NOT treat partial output as a valid restore.
    /// </summary>
    /// <exception cref="BpeFormatException">The input is malformed, tampered with, truncated, or no recipient matches <paramref name="privateKey"/>.</exception>
    public static async Task DecryptAsync(Stream input, Stream output, RSA privateKey, CancellationToken cancellationToken = default)
    {
        var preamble = new byte[12];
        if (await BpeEncryptor.ReadFullAsync(input, preamble, cancellationToken).ConfigureAwait(false) != preamble.Length
            || !preamble.AsSpan(0, 6).SequenceEqual(BpeFormat.Magic))
        {
            throw new BpeFormatException("Not a bpe artifact.");
        }

        if (BinaryPrimitives.ReadUInt16BigEndian(preamble.AsSpan(6)) != BpeFormat.Version)
        {
            throw new BpeFormatException("Unsupported bpe version.");
        }

        var headerLength = BinaryPrimitives.ReadUInt32BigEndian(preamble.AsSpan(8));
        if (headerLength is < BpeFormat.CoreLength + 4 + BpeFormat.MacLength or > BpeFormat.MaxHeaderLength)
        {
            throw new BpeFormatException("Invalid header length.");
        }

        var header = new byte[headerLength];
        if (await BpeEncryptor.ReadFullAsync(input, header, cancellationToken).ConfigureAwait(false) != header.Length)
        {
            throw new BpeFormatException("Truncated header.");
        }

        var coreBytes = header[..BpeFormat.CoreLength];
        var core = BpeFormat.DecodeCore(coreBytes);
        var recipientsLength = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(BpeFormat.CoreLength));
        if (recipientsLength != headerLength - BpeFormat.CoreLength - 4 - BpeFormat.MacLength)
        {
            throw new BpeFormatException("Invalid recipients length.");
        }

        var recipientsJson = header[(BpeFormat.CoreLength + 4)..^BpeFormat.MacLength];
        var mac = header[^BpeFormat.MacLength..];
        var coreHash = SHA256.HashData(coreBytes);
        var dek = UnwrapDek(recipientsJson, privateKey);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(mac, BpeFormat.ComputeMac(dek, coreHash, recipientsJson)))
            {
                throw new BpeFormatException("Recipient list failed authentication.");
            }

            await ReadChunksAsync(input, output, dek, coreHash, core, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    private static async Task ReadChunksAsync(
        Stream input, Stream output, byte[] dek, byte[] coreHash, BpeFormat.Core core, CancellationToken cancellationToken)
    {
        var sealedSize = core.ChunkSize + BpeFormat.TagLength;
        using var aes = new AesGcm(dek, BpeFormat.TagLength);
        var current = new byte[sealedSize];
        var next = new byte[sealedSize];
        var plain = new byte[core.ChunkSize];
        var currentLength = await BpeEncryptor.ReadFullAsync(input, current, cancellationToken).ConfigureAwait(false);
        long total = 0;
        for (ulong counter = 0; ; counter++)
        {
            if (currentLength < BpeFormat.TagLength)
            {
                throw new BpeFormatException("Truncated artifact: missing final chunk.");
            }

            var nextLength = currentLength == sealedSize
                ? await BpeEncryptor.ReadFullAsync(input, next, cancellationToken).ConfigureAwait(false)
                : 0;
            var isFinal = nextLength == 0;
            var plainLength = currentLength - BpeFormat.TagLength;
            try
            {
                aes.Decrypt(BpeFormat.ChunkNonce(core.NoncePrefix, counter), current.AsSpan(0, plainLength),
                    current.AsSpan(plainLength, BpeFormat.TagLength), plain.AsSpan(0, plainLength),
                    BpeFormat.ChunkAad(coreHash, counter, isFinal));
            }
            catch (CryptographicException)
            {
                throw new BpeFormatException($"Chunk {counter} failed authentication (corrupt, reordered, or truncated).");
            }

            await output.WriteAsync(plain.AsMemory(0, plainLength), cancellationToken).ConfigureAwait(false);
            total += plainLength;
            if (isFinal)
            {
                break;
            }

            (current, next) = (next, current);
            currentLength = nextLength;
        }

        if (total != core.SourceSize)
        {
            throw new BpeFormatException("Decrypted size does not match the declared source size.");
        }
    }

    private static byte[] UnwrapDek(byte[] recipientsJson, RSA privateKey)
    {
        var keyId = BpeFormat.KeyId(privateKey);
        try
        {
            using var document = JsonDocument.Parse(recipientsJson);
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (entry.GetProperty("key_id").GetString() != keyId)
                {
                    continue;
                }

                var dek = privateKey.Decrypt(Convert.FromBase64String(entry.GetProperty("wrapped_dek").GetString()!), RSAEncryptionPadding.OaepSHA256);
                if (dek.Length == BpeFormat.DekLength)
                {
                    return dek;
                }
            }
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or CryptographicException)
        {
            throw new BpeFormatException("Recipient list is malformed or the wrapped key is invalid.");
        }

        throw new BpeFormatException("No recipient matches the supplied private key.");
    }
}
