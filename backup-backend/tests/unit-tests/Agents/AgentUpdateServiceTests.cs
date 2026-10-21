using System.Security.Cryptography;
using BackupPlatform.Agent.Core.Updates;
using BackupPlatform.Contracts.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BackupPlatform.UnitTests.Agents;

public sealed class AgentUpdateServiceTests
{
    private readonly AgentUpdateService _sut = new(NullLogger<AgentUpdateService>.Instance);

    [Fact]
    public async Task CheckAndVerifyUpdate_Succeeds_WhenAllChecksPass()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKeySpki = ecdsa.ExportSubjectPublicKeyInfo();

        var packageBytes = "valid package payload"u8.ToArray();
        var packageSha = Convert.ToHexStringLower(SHA256.HashData(packageBytes));

        var payload = new AgentUpdateManifestPayload
        {
            Version = "2.1.0",
            Platform = "linux",
            MinimumSupportedVersion = "1.0.0",
            PackageSha256 = packageSha,
            PackageSize = packageBytes.Length,
            DownloadUrl = "/api/agent/updates/packages/linux/2.1.0",
            PublishedAt = DateTimeOffset.UtcNow
        };

        var manifest = AgentUpdateManifestSigner.Sign(payload, ecdsa, "key-1");

        var result = await _sut.CheckAndVerifyUpdateAsync(
            new Version(1, 5, 0),
            "linux",
            manifest,
            packageBytes,
            publicKeySpki);

        Assert.True(result.ShouldApply);
        Assert.Equal(AgentUpdateEligibility.Eligible, result.Eligibility);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task CheckAndVerifyUpdate_Fails_WhenPackageHashMismatch()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKeySpki = ecdsa.ExportSubjectPublicKeyInfo();

        var packageBytes = "valid package payload"u8.ToArray();
        var corruptBytes = "tampered package payload"u8.ToArray();
        var packageSha = Convert.ToHexStringLower(SHA256.HashData(packageBytes));

        var payload = new AgentUpdateManifestPayload
        {
            Version = "2.1.0",
            Platform = "linux",
            MinimumSupportedVersion = "1.0.0",
            PackageSha256 = packageSha,
            PackageSize = packageBytes.Length,
            DownloadUrl = "/api/agent/updates/packages/linux/2.1.0",
            PublishedAt = DateTimeOffset.UtcNow
        };

        var manifest = AgentUpdateManifestSigner.Sign(payload, ecdsa, "key-1");

        var result = await _sut.CheckAndVerifyUpdateAsync(
            new Version(1, 5, 0),
            "linux",
            manifest,
            corruptBytes,
            publicKeySpki);

        Assert.False(result.ShouldApply);
        Assert.Equal("package_hash_mismatch", result.Error);
    }

    [Fact]
    public async Task CheckAndVerifyUpdate_Fails_WhenSignatureInvalid()
    {
        using var validKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var differentKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var differentPublicKeySpki = differentKey.ExportSubjectPublicKeyInfo();

        var packageBytes = "valid package payload"u8.ToArray();
        var packageSha = Convert.ToHexStringLower(SHA256.HashData(packageBytes));

        var payload = new AgentUpdateManifestPayload
        {
            Version = "2.1.0",
            Platform = "linux",
            MinimumSupportedVersion = "1.0.0",
            PackageSha256 = packageSha,
            PackageSize = packageBytes.Length,
            DownloadUrl = "/api/agent/updates/packages/linux/2.1.0",
            PublishedAt = DateTimeOffset.UtcNow
        };

        var manifest = AgentUpdateManifestSigner.Sign(payload, validKey, "key-1");

        var result = await _sut.CheckAndVerifyUpdateAsync(
            new Version(1, 5, 0),
            "linux",
            manifest,
            packageBytes,
            differentPublicKeySpki);

        Assert.False(result.ShouldApply);
        Assert.Equal("signature_verification_failed", result.Error);
    }

    [Fact]
    public async Task CheckAndVerifyUpdate_Refuses_WhenCurrentVersionBelowMinimumSupported()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKeySpki = ecdsa.ExportSubjectPublicKeyInfo();

        var packageBytes = "valid package payload"u8.ToArray();
        var packageSha = Convert.ToHexStringLower(SHA256.HashData(packageBytes));

        var payload = new AgentUpdateManifestPayload
        {
            Version = "2.1.0",
            Platform = "linux",
            MinimumSupportedVersion = "1.5.0",
            PackageSha256 = packageSha,
            PackageSize = packageBytes.Length,
            DownloadUrl = "/api/agent/updates/packages/linux/2.1.0",
            PublishedAt = DateTimeOffset.UtcNow
        };

        var manifest = AgentUpdateManifestSigner.Sign(payload, ecdsa, "key-1");

        // Current version is 1.2.0, below minimum 1.5.0
        var result = await _sut.CheckAndVerifyUpdateAsync(
            new Version(1, 2, 0),
            "linux",
            manifest,
            packageBytes,
            publicKeySpki);

        Assert.False(result.ShouldApply);
        Assert.Equal(AgentUpdateEligibility.BelowMinimumSupported, result.Eligibility);
        Assert.Null(result.Error);
    }
}
