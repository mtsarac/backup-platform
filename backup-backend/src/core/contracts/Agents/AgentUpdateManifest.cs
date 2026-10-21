using System.Security.Cryptography;
using System.Text;

namespace BackupPlatform.Contracts.Agents;

/// <summary>
/// Cryptographically signed release metadata specifying version, package digest, minimum supported version, and signature for agent fleet self-update.
/// </summary>
public sealed record AgentUpdateManifest
{
    public required AgentUpdateManifestPayload Payload { get; init; }
    public required string KeyId { get; init; }
    public required string SignatureBase64 { get; init; }
}

public sealed record AgentUpdateManifestPayload
{
    public required string Version { get; init; }
    public required string Platform { get; init; }
    public required string MinimumSupportedVersion { get; init; }
    public required string PackageSha256 { get; init; }
    public required long PackageSize { get; init; }
    public required string DownloadUrl { get; init; }
    public required DateTimeOffset PublishedAt { get; init; }

    public byte[] ComputeCanonicalBytes()
    {
        // Deterministic string byte encoding for signature signing and verification
        var canonical = $"{Platform}:{Version}:{MinimumSupportedVersion}:{PackageSha256.ToLowerInvariant()}:{PackageSize}:{DownloadUrl}:{PublishedAt.ToUnixTimeSeconds()}";
        return Encoding.UTF8.GetBytes(canonical);
    }
}

public enum AgentUpdateEligibility
{
    Eligible,
    AlreadyUpToDate,
    CurrentIsNewer,
    BelowMinimumSupported
}

public static class AgentUpdatePolicy
{
    public static bool IsEligible(Version current, Version target, Version minSupported, out AgentUpdateEligibility reason)
    {
        if (current < minSupported)
        {
            reason = AgentUpdateEligibility.BelowMinimumSupported;
            return false;
        }

        if (current >= target)
        {
            reason = current == target ? AgentUpdateEligibility.AlreadyUpToDate : AgentUpdateEligibility.CurrentIsNewer;
            return false;
        }

        reason = AgentUpdateEligibility.Eligible;
        return true;
    }
}

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
