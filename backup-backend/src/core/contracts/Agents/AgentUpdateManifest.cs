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
