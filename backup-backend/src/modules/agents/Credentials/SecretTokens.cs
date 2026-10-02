using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace BackupPlatform.Agents.Credentials;

/// <summary>Distinct hash domains so a stored value for one secret kind can never validate as another.</summary>
internal enum SecretPurpose
{
    EnrollmentToken = 1,
    AgentCredential = 2
}

/// <summary>
/// Generation and verification of 256-bit random secrets using only built-in primitives.
/// </summary>
/// <remarks>
/// Secrets are 32 bytes from the OS CSPRNG, so brute force and rainbow tables are infeasible and a slow
/// password KDF would only add per-request CPU cost without adding strength. The stored value is
/// SHA-256(domain label || secret). A keyed hash was rejected: it adds a pepper whose loss would lock every
/// agent out, while a database-only leak already reveals nothing usable. Comparison is constant time.
/// </remarks>
internal static class SecretTokens
{
    public const int HashLength = 32;
    private const int SecretByteLength = 32;
    private static readonly byte[] EnrollmentLabel = "backup-platform/agents/enrollment-token/v1\0"u8.ToArray();
    private static readonly byte[] CredentialLabel = "backup-platform/agents/credential/v1\0"u8.ToArray();
    private static readonly byte[] DummyHash = Hash(SecretPurpose.AgentCredential, NewSecret());

    public static string NewSecret()
    {
        Span<byte> bytes = stackalloc byte[SecretByteLength];
        RandomNumberGenerator.Fill(bytes);
        var secret = Base64Url.EncodeToString(bytes);
        CryptographicOperations.ZeroMemory(bytes);
        return secret;
    }

    public static byte[] Hash(SecretPurpose purpose, string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        var label = purpose switch
        {
            SecretPurpose.EnrollmentToken => EnrollmentLabel,
            SecretPurpose.AgentCredential => CredentialLabel,
            _ => throw new ArgumentOutOfRangeException(nameof(purpose))
        };
        var secretBytes = Encoding.UTF8.GetBytes(secret);
        var input = new byte[label.Length + secretBytes.Length];
        label.CopyTo(input, 0);
        secretBytes.CopyTo(input, label.Length);
        var hash = SHA256.HashData(input);
        CryptographicOperations.ZeroMemory(input);
        CryptographicOperations.ZeroMemory(secretBytes);
        return hash;
    }

    public static bool Verify(SecretPurpose purpose, ReadOnlySpan<byte> expectedHash, string presentedSecret) =>
        CryptographicOperations.FixedTimeEquals(expectedHash, Hash(purpose, presentedSecret));

    /// <summary>Spends comparable work when no record matched, so unknown identifiers are not distinguishable by timing.</summary>
    public static void BurnVerification(SecretPurpose purpose, string presentedSecret) =>
        _ = Verify(purpose, DummyHash, presentedSecret);

    public static void RequireValidHash(byte[] hash)
    {
        ArgumentNullException.ThrowIfNull(hash);
        if (hash.Length != HashLength)
        {
            throw new ArgumentException("A secret hash must be 32 bytes.", nameof(hash));
        }
    }
}
