using System.Security.Cryptography;
using BackupPlatform.Infrastructure.Persistence;

namespace BackupPlatform.Api.Security;

/// <summary>Runtime signing key plus retained public keys for verifying records after key rotation.</summary>
public sealed class AuditSealKeys
{
    public AuditSealKeys(IConfiguration configuration)
    {
        var keys = configuration.GetSection("Audit:VerificationKeys").Get<Dictionary<string, string>>() ?? new(StringComparer.Ordinal);
        var path = configuration["Audit:SigningKeyPemPath"];
        if (!string.IsNullOrWhiteSpace(path))
        {
            if (!Path.IsPathFullyQualified(path)) throw new InvalidOperationException("Audit signing key path must be absolute.");
            var keyId = configuration["Audit:SigningKeyId"];
            if (string.IsNullOrWhiteSpace(keyId) || keyId.Length > 128) throw new InvalidOperationException("Audit signing key ID is required.");
            var pem = File.ReadAllText(path);
            using var key = ECDsa.Create();
            key.ImportFromPem(pem);
            if (key.KeySize != 256) throw new InvalidOperationException("Audit signing requires an ECDSA P-256 key.");
            Span<byte> signature = stackalloc byte[64];
            if (!key.TrySignData(ReadOnlySpan<byte>.Empty, signature, HashAlgorithmName.SHA256, out _))
                throw new InvalidOperationException("The audit signing key has no private key.");
            var publicKey = key.ExportSubjectPublicKeyInfoPem();
            if (keys.TryGetValue(keyId, out var existing) && existing != publicKey) throw new InvalidOperationException("An audit key ID cannot identify two keys.");
            keys[keyId] = publicKey;
            Interceptor = new AuditSigningInterceptor(keyId, pem);
        }
        foreach (var pem in keys.Values)
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(pem);
        }
        PublicKeys = keys.AsReadOnly();
    }

    public AuditSigningInterceptor? Interceptor { get; }
    public IReadOnlyDictionary<string, string> PublicKeys { get; }
}
