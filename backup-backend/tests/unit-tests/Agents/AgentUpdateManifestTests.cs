using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Encryption;
using Xunit;

namespace BackupPlatform.UnitTests.Agents;

public sealed class AgentUpdateManifestTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    [Fact]
    public void Manifest_SignAndVerify_SuccessWithValidEcdsaSignature()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKeySpki = ecdsa.ExportSubjectPublicKeyInfo();

        var packageBytes = "fake binary package content"u8.ToArray();
        var packageSha256 = Convert.ToHexStringLower(SHA256.HashData(packageBytes));

        var manifestData = new AgentUpdateManifestPayload
        {
            Version = "2.0.0",
            Platform = "linux",
            MinimumSupportedVersion = "1.0.0",
            PackageSha256 = packageSha256,
            PackageSize = packageBytes.Length,
            DownloadUrl = "/api/agent/packages/linux/2.0.0",
            PublishedAt = DateTimeOffset.UtcNow
        };

        var signedManifest = AgentUpdateManifestSigner.Sign(manifestData, ecdsa, "key-2026-v1");

        Assert.NotNull(signedManifest);
        Assert.Equal("key-2026-v1", signedManifest.KeyId);
        Assert.NotEmpty(signedManifest.SignatureBase64);

        var isValid = AgentUpdateManifestVerifier.Verify(signedManifest, publicKeySpki);
        Assert.True(isValid);
    }

    [Fact]
    public void Manifest_Verify_FailsWhenPayloadTampered()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKeySpki = ecdsa.ExportSubjectPublicKeyInfo();

        var manifestData = new AgentUpdateManifestPayload
        {
            Version = "2.0.0",
            Platform = "linux",
            MinimumSupportedVersion = "1.0.0",
            PackageSha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            PackageSize = 1000,
            DownloadUrl = "/api/agent/packages/linux/2.0.0",
            PublishedAt = DateTimeOffset.UtcNow
        };

        var signedManifest = AgentUpdateManifestSigner.Sign(manifestData, ecdsa, "key-2026-v1");

        // Tamper with package hash
        var tamperedPayload = signedManifest.Payload with { PackageSha256 = "0000000000000000000000000000000000000000000000000000000000000000" };
        var tamperedManifest = signedManifest with { Payload = tamperedPayload };

        var isValid = AgentUpdateManifestVerifier.Verify(tamperedManifest, publicKeySpki);
        Assert.False(isValid);
    }

    [Fact]
    public void UpdateEligibility_DeterminesIfUpdateNeededOrRefused()
    {
        var currentVersion = new Version(1, 2, 0);
        var targetVersion = new Version(2, 0, 0);
        var minSupported = new Version(1, 0, 0);

        Assert.True(AgentUpdatePolicy.IsEligible(currentVersion, targetVersion, minSupported, out var reason));
        Assert.Equal(AgentUpdateEligibility.Eligible, reason);

        // Already on target or newer
        Assert.False(AgentUpdatePolicy.IsEligible(new Version(2, 0, 0), targetVersion, minSupported, out reason));
        Assert.Equal(AgentUpdateEligibility.AlreadyUpToDate, reason);

        Assert.False(AgentUpdatePolicy.IsEligible(new Version(2, 1, 0), targetVersion, minSupported, out reason));
        Assert.Equal(AgentUpdateEligibility.CurrentIsNewer, reason);

        // Below minimum supported version for update
        Assert.False(AgentUpdatePolicy.IsEligible(new Version(0, 9, 0), targetVersion, minSupported, out reason));
        Assert.Equal(AgentUpdateEligibility.BelowMinimumSupported, reason);
    }
}
