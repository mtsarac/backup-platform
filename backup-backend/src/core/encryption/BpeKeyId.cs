using System.Security.Cryptography;

namespace BackupPlatform.Encryption;

/// <summary>The key id written into every <c>bpe</c> header, derived from a public key alone.</summary>
public static class BpeKeyId
{
    /// <summary>First 16 bytes of SHA-256 over the SubjectPublicKeyInfo DER, lowercase hex. Identical to what the encryptor writes.</summary>
    public static string FromSpki(ReadOnlySpan<byte> spki) => Convert.ToHexStringLower(SHA256.HashData(spki)[..16]);
}
