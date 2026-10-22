using System.Text.Json;
using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Agent.Core.Updates;
using BackupPlatform.Contracts.Agents;

namespace BackupPlatform.Agent.Transport;

public sealed class HttpAgentUpdateClient(HttpClient http, AgentBackendEndpoint endpoint, TimeProvider time) : IAgentUpdateClient
{
    private readonly AgentHttpClient _client = new(http, endpoint, time);

    public Task<AgentBackendResult<AgentUpdateManifest>> GetManifestAsync(AgentLocalIdentity identity, string platform, CancellationToken cancellationToken)
    {
        if (platform is not ("linux-x64" or "linux-arm64" or "win-x64")) throw new ArgumentException("Unsupported update RID.", nameof(platform));
        return _client.ReadAsync(identity, "api/agent/updates/manifest?platform=" + platform, async (response, token) =>
        {
            if (response.Content.Headers.ContentLength is > 65536) throw new InvalidDataException("Update manifest exceeds its limit.");
            await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var metadata = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await source.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            {
                if (metadata.Length + count > 65536) throw new InvalidDataException("Update manifest exceeds its limit.");
                metadata.Write(buffer, 0, count);
            }
            var manifest = JsonSerializer.Deserialize<AgentUpdateManifest>(metadata.GetBuffer().AsSpan(0, (int)metadata.Length), JsonSerializerOptions.Web)
                ?? throw new InvalidDataException("Invalid update manifest.");
            return AgentBackendResult<AgentUpdateManifest>.Success(manifest);
        }, cancellationToken);
    }

    public Task<AgentBackendResult<bool>> DownloadAsync(AgentLocalIdentity identity, AgentUpdateManifest manifest, string destination,
        IReadOnlyDictionary<string, string> trustedKeys, CancellationToken cancellationToken)
    {
        AgentUpdateService.ValidateManifest(manifest, manifest.Payload.Platform, trustedKeys);
        var path = $"api/agent/updates/packages/{manifest.Payload.Platform}/{manifest.Payload.Version}";
        var expected = new Uri(endpoint.BaseUri, path);
        if (!Uri.TryCreate(endpoint.BaseUri, manifest.Payload.DownloadUrl, out var supplied) || supplied != expected)
            throw new InvalidDataException("The signed download URL is not the authenticated release endpoint.");
        return _client.ReadAsync(identity, path, async (response, token) =>
        {
            if (response.Content.Headers.ContentLength is { } length && length != manifest.Payload.PackageSize)
                throw new InvalidDataException("Update response length does not match its signed size.");
            await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await new AgentUpdateService().DownloadAsync(source, destination, manifest, manifest.Payload.Platform, trustedKeys, token).ConfigureAwait(false);
            return AgentBackendResult<bool>.Success(true);
        }, cancellationToken);
    }
}
