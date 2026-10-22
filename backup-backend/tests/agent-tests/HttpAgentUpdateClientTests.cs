using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using BackupPlatform.Agent.Transport;
using BackupPlatform.AgentTests.Support;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Encryption;

namespace BackupPlatform.AgentTests;

public sealed class HttpAgentUpdateClientTests
{
    [Fact]
    public async Task Manifest_and_streaming_package_requests_use_agent_authentication_and_protocol()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var bytes = new byte[200_000];
        RandomNumberGenerator.Fill(bytes);
        var manifest = Sign(key, bytes, "https://backup.example/api/agent/updates/packages/linux-x64/2.0.0");
        using var handler = new FakeHttpHandler();
        handler.Respond = (request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("manifest")
            ? FakeHttpHandler.Json(HttpStatusCode.OK, JsonSerializer.Serialize(manifest, JsonSerializerOptions.Web))
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(bytes)) });
        using var http = new HttpClient(handler) { MaxResponseContentBufferSize = 1 };
        var client = new HttpAgentUpdateClient(http, new AgentBackendEndpoint(new Uri("https://backup.example/")), TimeProvider.System);
        var path = Path.Combine(Path.GetTempPath(), "update-download-" + Guid.NewGuid().ToString("N"));
        try
        {
            var metadata = await client.GetManifestAsync(TestIdentity.Create(), "linux-x64", default);
            Assert.True(metadata.IsSuccess);
            var result = await client.DownloadAsync(TestIdentity.Create(), metadata.Value, path,
                new Dictionary<string, string> { ["release"] = key.ExportSubjectPublicKeyInfoPem() }, default);
            Assert.True(result.IsSuccess);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
            Assert.Equal(2, handler.Requests.Count);
            Assert.All(handler.Requests, request =>
            {
                Assert.Equal($"BackupAgent {TestIdentity.CredentialId:N}.{TestIdentity.Secret}", request.Headers["Authorization"]);
                Assert.Equal(AgentProtocol.Current.Version.ToString(), request.Headers[AgentProtocolHeader.Name]);
                Assert.DoesNotContain(TestIdentity.Secret, request.Uri.ToString(), StringComparison.Ordinal);
            });
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Signed_cross_origin_url_and_unpinned_signature_never_send_credentials_or_download()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var handler = new FakeHttpHandler();
        using var http = new HttpClient(handler);
        var client = new HttpAgentUpdateClient(http, new AgentBackendEndpoint(new Uri("https://backup.example/")), TimeProvider.System);
        var keys = new Dictionary<string, string> { ["release"] = key.ExportSubjectPublicKeyInfoPem() };
        var manifest = Sign(key, [1], "https://attacker.example/package");
        await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadAsync(TestIdentity.Create(), manifest, "unused", keys, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadAsync(TestIdentity.Create(), manifest with { KeyId = "unknown" }, "unused", keys, default));
        Assert.Empty(handler.Requests);
    }

    private static AgentUpdateManifest Sign(ECDsa key, byte[] bytes, string url) => AgentUpdateManifestSigner.Sign(new AgentUpdateManifestPayload
    {
        Version = "2.0.0", MinimumSupportedVersion = "1.0.0", Platform = "linux-x64", PackageSize = bytes.Length,
        PackageSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), DownloadUrl = url, PublishedAt = DateTimeOffset.UnixEpoch
    }, key, "release");
}
