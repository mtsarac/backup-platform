using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using BackupPlatform.Encryption;

namespace BackupPlatform.IntegrationTests;

public sealed class PlatformEncryptionStatusApiTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private const string Route = "/api/platform/encryption/status";

    [Fact]
    public async Task PlatformAdminSeesWhetherARecoveryKeyIsConfiguredWithoutAnyKeyMaterial()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var admin = await world.SignInAsync(await world.SeedUserAsync(platformAdmin: true));
        using var response = await admin.GetAsync(Route);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var recovery = AgentWorld.ReadJson(response).GetProperty("platformRecovery");
        Assert.False(recovery.GetProperty("configured").GetBoolean());
        Assert.Equal(JsonValueKind.Null, recovery.GetProperty("keyId").ValueKind);

        using var rsa = RSA.Create(4096);
        var pem = rsa.ExportSubjectPublicKeyInfoPem();
        await using var configured = await AgentWorld.CreateAsync(fixture, configuration: new Dictionary<string, string?> { ["Encryption:PlatformRecoveryPublicKeyPem"] = pem });
        var second = await configured.SignInAsync(await configured.SeedUserAsync(platformAdmin: true));
        using var withKey = await second.GetAsync(Route);
        var body = AgentWorld.ReadJson(withKey);
        Assert.True(body.GetProperty("platformRecovery").GetProperty("configured").GetBoolean());
        Assert.Equal(BpeKeyId.FromSpki(rsa.ExportSubjectPublicKeyInfo()), body.GetProperty("platformRecovery").GetProperty("keyId").GetString());
        Assert.DoesNotContain("PUBLIC KEY", await withKey.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OtherRolesAndAnonymousCallersAreRefused()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var user = await world.SignInAsync(await world.SeedUserAsync());
        using var forbidden = await user.GetAsync(Route);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using var anonymous = await world.Anonymous.GetAsync(Route);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }
}
