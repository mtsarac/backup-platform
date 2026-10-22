using System.Security.Cryptography;
using BackupPlatform.Contracts.Agents;

namespace BackupPlatform.Encryption;

public static class AgentUpdateManifestSigner
{
    public static AgentUpdateManifest Sign(AgentUpdateManifestPayload payload, ECDsa privateKey, string keyId)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(privateKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);

        var data = payload.ComputeCanonicalBytes();
        var signature = privateKey.SignData(data, HashAlgorithmName.SHA256);

        return new AgentUpdateManifest
        {
            Payload = payload,
            KeyId = keyId,
            SignatureBase64 = Convert.ToBase64String(signature)
        };
    }
}

public static class AgentUpdateManifestVerifier
{
    public static bool Verify(AgentUpdateManifest manifest, byte[] publicKeySpki)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(publicKeySpki);

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(publicKeySpki, out _);

            var data = manifest.Payload.ComputeCanonicalBytes();
            var signature = Convert.FromBase64String(manifest.SignatureBase64);

            return ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256);
        }
        catch
        {
            return false;
        }
    }
}
