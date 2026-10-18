using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace BackupPlatform.Encryption;

/// <summary>One recipient of an artifact as recorded in its header: <see cref="WrappedDek"/> is the DEK sealed to the key named by <see cref="KeyId"/>.</summary>
public sealed record BpeRecipientEntry(BpeRecipientType Type, string KeyId, int KeyVersion, byte[] WrappedDek);

public static class BpeDecryptor
{
    /// <summary>
    /// Reads the recipient list from the header of a <c>bpe</c> v2 stream, leaving the stream after the header. The list is NOT authenticated yet
    /// (that needs the DEK); use it to find which key to ask for, never to trust the artifact.
    /// </summary>
    /// <exception cref="BpeFormatException">The preamble or header is malformed.</exception>
    public static async Task<IReadOnlyList<BpeRecipientEntry>> ReadRecipientsAsync(Stream input, CancellationToken cancellationToken = default) =>
        (await ReadHeaderAsync(input, cancellationToken).ConfigureAwait(false)).Recipients;

    /// <summary>
    /// Decrypts a <c>bpe</c> v2 artifact. Every chunk is authenticated before its plaintext is written, but truncation or trailing damage is only
    /// proven at the end: callers MUST discard <paramref name="output"/> when this throws and MUST NOT treat partial output as a valid restore.
    /// </summary>
    /// <exception cref="BpeFormatException">The input is malformed, tampered with, truncated, or no recipient matches <paramref name="privateKey"/>.</exception>
    public static Task DecryptAsync(Stream input, Stream output, RSA privateKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(privateKey);
        var keyId = BpeFormat.KeyId(privateKey);
        return DecryptAsync(input, output, (recipients, _) =>
        {
            foreach (var recipient in recipients.Where(recipient => recipient.KeyId == keyId))
            {
                try
                {
                    var dek = privateKey.Decrypt(recipient.WrappedDek, RSAEncryptionPadding.OaepSHA256);
                    if (dek.Length == BpeFormat.DekLength)
                    {
                        return Task.FromResult(dek);
                    }
                }
                catch (CryptographicException)
                {
                    throw new BpeFormatException("The wrapped key is invalid.");
                }
            }

            throw new BpeFormatException("No recipient matches the supplied private key.");
        }, cancellationToken);
    }

    /// <summary>
    /// Like the RSA overload, but the DEK comes from <paramref name="unwrapDek"/>, which receives the (unauthenticated) recipient list. The returned
    /// array is zeroed once decryption ends. Exceptions from the callback propagate unchanged so a key-service failure stays distinguishable from a
    /// tampered artifact; a wrong DEK fails the recipient-list MAC with <see cref="BpeFormatException"/>.
    /// </summary>
    public static async Task DecryptAsync(
        Stream input, Stream output, Func<IReadOnlyList<BpeRecipientEntry>, CancellationToken, Task<byte[]>> unwrapDek, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(unwrapDek);
        var header = await ReadHeaderAsync(input, cancellationToken).ConfigureAwait(false);
        var dek = await unwrapDek(header.Recipients, cancellationToken).ConfigureAwait(false);
        try
        {
            if (dek.Length != BpeFormat.DekLength)
            {
                throw new BpeFormatException("The unwrapped key has an invalid length.");
            }

            if (!CryptographicOperations.FixedTimeEquals(header.Mac, BpeFormat.ComputeMac(dek, header.CoreHash, header.RecipientsJson)))
            {
                throw new BpeFormatException("Recipient list failed authentication.");
            }

            await ReadChunksAsync(input, output, dek, header.CoreHash, header.Core, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    private sealed record Header(BpeFormat.Core Core, byte[] CoreHash, byte[] RecipientsJson, byte[] Mac, IReadOnlyList<BpeRecipientEntry> Recipients);

    private static async Task<Header> ReadHeaderAsync(Stream input, CancellationToken cancellationToken)
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
        return new Header(core, SHA256.HashData(coreBytes), recipientsJson, header[^BpeFormat.MacLength..], ParseRecipients(recipientsJson));
    }

    private static List<BpeRecipientEntry> ParseRecipients(byte[] recipientsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(recipientsJson);
            return [.. document.RootElement.EnumerateArray().Select(entry => new BpeRecipientEntry(
                BpeFormat.ParseTypeName(entry.GetProperty("type").GetString()),
                entry.GetProperty("key_id").GetString()!,
                entry.GetProperty("key_version").GetInt32(),
                Convert.FromBase64String(entry.GetProperty("wrapped_dek").GetString()!)))];
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException)
        {
            throw new BpeFormatException("Recipient list is malformed.");
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
}
