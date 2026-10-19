using System.Security.Cryptography;
using BackupPlatform.Application.Backups;
using BackupPlatform.Encryption;

namespace BackupPlatform.Infrastructure.Backups;

/// <summary>
/// Turns a PEM into a recovery recipient. The one door for both the platform recovery key (configuration) and a tenant recovery key (API): only an RSA PUBLIC key of at
/// least <see cref="MinimumBits"/> bits passes, and a PEM carrying a private key is refused outright rather than parsed, so a pasted private key is never stored or echoed.
/// </summary>
public static class RecoveryPublicKeyParser
{
    public const int MinimumBits = 4096;

    /// <exception cref="InvalidRecoveryKeyException">Not a usable RSA public key.</exception>
    public static (byte[] Spki, string KeyId) Parse(string? pem)
    {
        if (string.IsNullOrWhiteSpace(pem) || pem.Length > 8192) throw new InvalidRecoveryKeyException("not_a_public_key");
        if (pem.Contains("PRIVATE", StringComparison.OrdinalIgnoreCase)) throw new InvalidRecoveryKeyException("private_key_rejected");
        if (!PemEncoding.TryFind(pem, out var fields)) throw new InvalidRecoveryKeyException("not_a_public_key");
        var label = pem[fields.Label];
        if (label is not ("PUBLIC KEY" or "RSA PUBLIC KEY") || pem[fields.Location].Length != pem.Trim().Length) throw new InvalidRecoveryKeyException("not_a_public_key");
        byte[] der;
        try { der = Convert.FromBase64String(pem[fields.Base64Data]); }
        catch (FormatException) { throw new InvalidRecoveryKeyException("not_a_public_key"); }
        using var rsa = RSA.Create();
        try
        {
            if (label == "PUBLIC KEY") rsa.ImportSubjectPublicKeyInfo(der, out _);
            else rsa.ImportRSAPublicKey(der, out _);
        }
        catch (CryptographicException)
        {
            throw new InvalidRecoveryKeyException(label == "PUBLIC KEY" && IsOtherAlgorithm(der) ? "not_rsa" : "not_a_public_key");
        }
        if (rsa.KeySize < MinimumBits) throw new InvalidRecoveryKeyException("too_weak");
        var spki = rsa.ExportSubjectPublicKeyInfo();
        return (spki, BpeKeyId.FromSpki(spki));
    }

    private static bool IsOtherAlgorithm(byte[] spki)
    {
        using var ec = ECDsa.Create();
        try { ec.ImportSubjectPublicKeyInfo(spki, out _); return true; }
        catch (CryptographicException) { return false; }
    }
}
