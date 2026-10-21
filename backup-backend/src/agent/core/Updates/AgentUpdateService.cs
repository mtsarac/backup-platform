using System.Security.Cryptography;
using BackupPlatform.Contracts.Agents;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.Agent.Core.Updates;

public interface IAgentUpdateService
{
    Task<AgentUpdateResult> CheckAndVerifyUpdateAsync(
        Version currentVersion,
        string platform,
        AgentUpdateManifest manifest,
        byte[] packageBytes,
        byte[] trustedPublicKeySpki,
        CancellationToken cancellationToken = default);
}

public sealed record AgentUpdateResult
{
    public required bool ShouldApply { get; init; }
    public required AgentUpdateEligibility Eligibility { get; init; }
    public string? Error { get; init; }
}

public sealed class AgentUpdateService(ILogger<AgentUpdateService> logger) : IAgentUpdateService
{
    public Task<AgentUpdateResult> CheckAndVerifyUpdateAsync(
        Version currentVersion,
        string platform,
        AgentUpdateManifest manifest,
        byte[] packageBytes,
        byte[] trustedPublicKeySpki,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(packageBytes);
        ArgumentNullException.ThrowIfNull(trustedPublicKeySpki);

        if (!string.Equals(manifest.Payload.Platform, platform, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Update manifest platform mismatch: Expected={Expected} Found={Found}", platform, manifest.Payload.Platform);
            return Task.FromResult(new AgentUpdateResult
            {
                ShouldApply = false,
                Eligibility = AgentUpdateEligibility.BelowMinimumSupported,
                Error = "platform_mismatch"
            });
        }

        if (!Version.TryParse(manifest.Payload.Version, out var targetVersion) ||
            !Version.TryParse(manifest.Payload.MinimumSupportedVersion, out var minSupportedVersion))
        {
            return Task.FromResult(new AgentUpdateResult
            {
                ShouldApply = false,
                Eligibility = AgentUpdateEligibility.BelowMinimumSupported,
                Error = "invalid_version_format"
            });
        }

        if (!AgentUpdatePolicy.IsEligible(currentVersion, targetVersion, minSupportedVersion, out var eligibility))
        {
            logger.LogInformation("Agent update not eligible: Current={Current} Target={Target} Min={Min} Reason={Reason}",
                currentVersion, targetVersion, minSupportedVersion, eligibility);

            return Task.FromResult(new AgentUpdateResult
            {
                ShouldApply = false,
                Eligibility = eligibility,
                Error = null
            });
        }

        // Verify cryptographic signature
        if (!AgentUpdateManifestVerifier.Verify(manifest, trustedPublicKeySpki))
        {
            logger.LogError("Agent update manifest signature verification failed for KeyId={KeyId}", manifest.KeyId);
            return Task.FromResult(new AgentUpdateResult
            {
                ShouldApply = false,
                Eligibility = eligibility,
                Error = "signature_verification_failed"
            });
        }

        // Verify package sha256
        var packageHash = Convert.ToHexStringLower(SHA256.HashData(packageBytes));
        if (!string.Equals(packageHash, manifest.Payload.PackageSha256, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogError("Agent update package hash mismatch: Expected={Expected} Actual={Actual}",
                manifest.Payload.PackageSha256, packageHash);

            return Task.FromResult(new AgentUpdateResult
            {
                ShouldApply = false,
                Eligibility = eligibility,
                Error = "package_hash_mismatch"
            });
        }

        // Verify package size
        if (packageBytes.LongLength != manifest.Payload.PackageSize)
        {
            logger.LogError("Agent update package size mismatch: Expected={Expected} Actual={Actual}",
                manifest.Payload.PackageSize, packageBytes.LongLength);

            return Task.FromResult(new AgentUpdateResult
            {
                ShouldApply = false,
                Eligibility = eligibility,
                Error = "package_size_mismatch"
            });
        }

        logger.LogInformation("Agent update verified successfully for Target={Target}", targetVersion);
        return Task.FromResult(new AgentUpdateResult
        {
            ShouldApply = true,
            Eligibility = AgentUpdateEligibility.Eligible,
            Error = null
        });
    }
}
