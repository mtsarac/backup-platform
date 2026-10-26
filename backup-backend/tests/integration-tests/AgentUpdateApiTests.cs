using System.Net;
using System.Security.Cryptography;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Infrastructure.Agents;
using BackupPlatform.Encryption;

namespace BackupPlatform.IntegrationTests;

public sealed class AgentUpdateApiTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task Only_trusted_signed_releases_are_published_immutably_and_survive_store_restart()
    {
        var root = Path.Combine(Path.GetTempPath(), "bp-update-test-" + Guid.NewGuid().ToString("N"));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            await using var world = await AgentWorld.CreateAsync(fixture, configuration: new Dictionary<string, string?>
            {
                ["AgentUpdates:PackagesDirectory"] = root,
                ["AgentUpdates:TrustedKeys:release-key"] = key.ExportSubjectPublicKeyInfoPem()
            });
            var administrator = await world.SignInAsync(await world.SeedUserAsync(platformAdmin: true));
            var bytes = "release package bytes"u8.ToArray();
            var payload = new AgentUpdateManifestPayload
            {
                Version = "2.0.0", MinimumSupportedVersion = "1.0.0", Platform = "linux-x64", PackageSize = bytes.Length,
                PackageSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), PublishedAt = world.Clock.GetUtcNow(),
                DownloadUrl = "/api/agent/updates/packages/linux-x64/2.0.0"
            };
            var forged = AgentUpdateManifestSigner.Sign(payload, attacker, "release-key");
            using var rejected = await administrator.SendAsync(HttpMethod.Post, "/api/platform/agent-updates", new { manifest = forged, packageBase64 = Convert.ToBase64String(bytes) });
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            var manifest = AgentUpdateManifestSigner.Sign(payload, key, "release-key");
            using var accepted = await administrator.SendAsync(HttpMethod.Post, "/api/platform/agent-updates", new { manifest, packageBase64 = Convert.ToBase64String(bytes) });
            Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
            var restored = await new FileAgentUpdateManifestStore(root).GetPackageAsync("linux-x64", "2.0.0");
            Assert.Equal(manifest, restored!.Value.Manifest);
            Assert.Equal(bytes, restored.Value.PackageBytes);
            var changed = AgentUpdateManifestSigner.Sign(payload with { PublishedAt = payload.PublishedAt.AddSeconds(1) }, key, "release-key");
            using var conflict = await administrator.SendAsync(HttpMethod.Post, "/api/platform/agent-updates", new { manifest = changed, packageBase64 = Convert.ToBase64String(bytes) });
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
