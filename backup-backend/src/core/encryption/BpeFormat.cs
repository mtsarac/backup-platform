using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace BackupPlatform.Encryption;

/// <summary>
/// Constants and header codec of the <c>bpe</c> v2 artifact format (see docs/plan/E1_Artifact_Sifreleme_Plani.md section 4.1).
/// The format uses only standard primitives so an artifact can be decrypted without this library or a backend.
/// </summary>
/// <remarks>
/// Layout: magic(6) | version u16 BE | header_len u32 BE | header | chunk*.
/// Header: core(33) | recipients_len u32 BE | recipients JSON (UTF-8, MAC'd as stored) | recipients_mac(32).
/// Core: artifact_id(16) | alg u8 | chunk_size u32 BE | nonce_prefix(4) | source_size u64 BE.
/// </remarks>
internal static class BpeFormat
{
    public static ReadOnlySpan<byte> Magic => "BPENC\0"u8;
    public const ushort Version = 2;
    public const byte AlgAes256GcmStream = 1;
    public const int CoreLength = 33;
    public const int TagLength = 16;
    public const int DekLength = 32;
    public const int MacLength = 32;
    public const int NoncePrefixLength = 4;
    public const int DefaultChunkSize = 1 << 20;
    public const int MaxChunkSize = 64 << 20;
    public const int MaxHeaderLength = 1 << 20;
    private static readonly byte[] MacInfo = "bpe-recipients-mac"u8.ToArray();

    public static byte[] DeriveMacKey(byte[] dek) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, dek, 32, salt: [], info: MacInfo);

    public static byte[] ComputeMac(byte[] dek, ReadOnlySpan<byte> coreHash, ReadOnlySpan<byte> recipientsJson)
    {
        var message = new byte[coreHash.Length + recipientsJson.Length];
        coreHash.CopyTo(message);
        recipientsJson.CopyTo(message.AsSpan(coreHash.Length));
        return HMACSHA256.HashData(DeriveMacKey(dek), message);
    }

    public static byte[] EncodeCore(Core core)
    {
        var bytes = new byte[CoreLength];
        core.ArtifactId.CopyTo(bytes, 0);
        bytes[16] = AlgAes256GcmStream;
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(17), (uint)core.ChunkSize);
        core.NoncePrefix.CopyTo(bytes, 21);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(25), (ulong)core.SourceSize);
        return bytes;
    }

    public static Core DecodeCore(byte[] bytes)
    {
        if (bytes[16] != AlgAes256GcmStream)
        {
            throw new BpeFormatException($"Unsupported algorithm id {bytes[16]}.");
        }

        var chunkSize = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(17));
        if (chunkSize is 0 or > MaxChunkSize)
        {
            throw new BpeFormatException("Invalid chunk size.");
        }

        var sourceSize = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(25));
        if (sourceSize > long.MaxValue)
        {
            throw new BpeFormatException("Invalid source size.");
        }

        return new Core(bytes[..16], (int)chunkSize, bytes[21..25], (long)sourceSize);
    }

    public static byte[] BuildHeader(byte[] core, byte[] recipientsJson, byte[] mac)
    {
        var header = new byte[CoreLength + 4 + recipientsJson.Length + MacLength];
        core.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(CoreLength), (uint)recipientsJson.Length);
        recipientsJson.CopyTo(header, CoreLength + 4);
        mac.CopyTo(header, CoreLength + 4 + recipientsJson.Length);
        return header;
    }

    public static byte[] ChunkNonce(ReadOnlySpan<byte> prefix, ulong counter)
    {
        var nonce = new byte[12];
        prefix.CopyTo(nonce);
        BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), counter);
        return nonce;
    }

    public static byte[] ChunkAad(ReadOnlySpan<byte> coreHash, ulong counter, bool isFinal)
    {
        var aad = new byte[41];
        coreHash.CopyTo(aad);
        BinaryPrimitives.WriteUInt64BigEndian(aad.AsSpan(32), counter);
        aad[40] = isFinal ? (byte)1 : (byte)0;
        return aad;
    }

    /// <summary>Key id: first 16 bytes of SHA-256 over the SubjectPublicKeyInfo DER, lowercase hex.</summary>
    public static string KeyId(RSA key) =>
        Convert.ToHexStringLower(SHA256.HashData(key.ExportSubjectPublicKeyInfo())[..16]);

    public static string TypeName(BpeRecipientType type) => type switch
    {
        BpeRecipientType.TenantWrap => "tenant-wrap",
        BpeRecipientType.PlatformRecovery => "platform-recovery",
        BpeRecipientType.TenantRecovery => "tenant-recovery",
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    public static BpeRecipientType ParseTypeName(string? name) => name switch
    {
        "tenant-wrap" => BpeRecipientType.TenantWrap,
        "platform-recovery" => BpeRecipientType.PlatformRecovery,
        "tenant-recovery" => BpeRecipientType.TenantRecovery,
        _ => throw new BpeFormatException("Unknown recipient type.")
    };

    public static string Utf8(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    public sealed record Core(byte[] ArtifactId, int ChunkSize, byte[] NoncePrefix, long SourceSize);
}

public enum BpeRecipientType
{
    TenantWrap,
    PlatformRecovery,
    TenantRecovery
}

/// <summary>The input is not a valid <c>bpe</c> v2 artifact, or it was tampered with or truncated.</summary>
public sealed class BpeFormatException(string message) : Exception(message);
