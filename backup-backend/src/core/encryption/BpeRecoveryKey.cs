using System.Security.Cryptography;

namespace BackupPlatform.Encryption;

/// <summary>Result of a recovery key ceremony. The private key exists only as <see cref="Shares"/>; no single piece of it is returned or kept.</summary>
/// <param name="PublicKeyPem">The only part that goes onto the platform (Encryption:PlatformRecoveryPublicKeyPem).</param>
/// <param name="KeyId">Matches the <c>key_id</c> written into every artifact header for this key.</param>
public sealed record BpeRecoveryCeremony(string PublicKeyPem, string KeyId, int Threshold, IReadOnlyList<byte[]> Shares);

/// <summary>Creation and offline reconstruction of the platform Recovery Key (RK) as Shamir shares of its PKCS#8 private key.</summary>
public static class BpeRecoveryKey
{
    public const int DefaultBits = 4096;
    public const int DefaultThreshold = 3;
    public const int DefaultShares = 5;
    private const int MinBits = 4096;

    /// <summary>Generates an RSA keypair and splits the private key; also proves that a different subset of shares reconstructs the same key before returning.</summary>
    public static BpeRecoveryCeremony Generate(int bits = DefaultBits, int threshold = DefaultThreshold, int total = DefaultShares)
    {
        if (bits < MinBits)
        {
            throw new ArgumentOutOfRangeException(nameof(bits), $"Recovery keys must be at least {MinBits} bits.");
        }

        using var rsa = RSA.Create(bits);
        var pkcs8 = rsa.ExportPkcs8PrivateKey();
        try
        {
            var shares = ShamirSecretSharing.Split(pkcs8, threshold, total);
            var keyId = BpeKeyId.FromSpki(rsa.ExportSubjectPublicKeyInfo());
            using (var check = FromShares(shares.TakeLast(threshold).ToArray()))
            {
                if (BpeKeyId.FromSpki(check.ExportSubjectPublicKeyInfo()) != keyId)
                {
                    throw new BpeShareException("Self-check failed: the shares do not reconstruct the generated key.");
                }
            }

            return new BpeRecoveryCeremony(rsa.ExportSubjectPublicKeyInfoPem(), keyId, threshold, shares);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }
    }

    /// <summary>Reconstructs the Recovery Key private key from shares. Dispose the result as soon as the artifact is decrypted.</summary>
    /// <exception cref="BpeShareException">The shares are unusable, or do not form an RSA private key.</exception>
    public static RSA FromShares(IReadOnlyList<byte[]> shares)
    {
        var pkcs8 = ShamirSecretSharing.Combine(shares);
        try
        {
            var rsa = RSA.Create();
            try
            {
                rsa.ImportPkcs8PrivateKey(pkcs8, out _);
                return rsa;
            }
            catch (CryptographicException)
            {
                rsa.Dispose();
                throw new BpeShareException("The reconstructed secret is not an RSA private key.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }
    }

    /// <summary>Reads share files written by the ceremony tool (one <see cref="ShamirSecretSharing.ToText"/> line each) and reconstructs the key.</summary>
    public static async Task<RSA> FromShareFilesAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        var shares = new List<byte[]>();
        foreach (var path in paths)
        {
            shares.Add(ShamirSecretSharing.FromText(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)));
        }

        return FromShares(shares);
    }
}
