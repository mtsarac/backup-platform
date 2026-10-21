using System.Security.Cryptography;
using BackupPlatform.Contracts.Agents;

namespace BackupPlatform.Core.Application.Agents;

public interface IAgentUpdateManifestStore
{
    Task SaveManifestAsync(AgentUpdateManifest manifest, byte[] packageBytes, CancellationToken cancellationToken = default);
    Task<AgentUpdateManifest?> GetLatestManifestAsync(string platform, CancellationToken cancellationToken = default);
    Task<(AgentUpdateManifest Manifest, byte[] PackageBytes)?> GetPackageAsync(string platform, string version, CancellationToken cancellationToken = default);
}

public sealed class InMemoryAgentUpdateManifestStore : IAgentUpdateManifestStore
{
    private readonly Dictionary<string, (AgentUpdateManifest Manifest, byte[] PackageBytes)> _packages = new(StringComparer.OrdinalIgnoreCase);

    public Task SaveManifestAsync(AgentUpdateManifest manifest, byte[] packageBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(packageBytes);

        var key = $"{manifest.Payload.Platform}:{manifest.Payload.Version}";
        _packages[key] = (manifest, packageBytes);
        return Task.CompletedTask;
    }

    public Task<AgentUpdateManifest?> GetLatestManifestAsync(string platform, CancellationToken cancellationToken = default)
    {
        var manifests = _packages.Values
            .Where(x => string.Equals(x.Manifest.Payload.Platform, platform, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Manifest)
            .OrderByDescending(x => Version.TryParse(x.Payload.Version, out var v) ? v : new Version(0, 0))
            .FirstOrDefault();

        return Task.FromResult<AgentUpdateManifest?>(manifests);
    }

    public Task<(AgentUpdateManifest Manifest, byte[] PackageBytes)?> GetPackageAsync(string platform, string version, CancellationToken cancellationToken = default)
    {
        var key = $"{platform}:{version}";
        if (_packages.TryGetValue(key, out var entry))
        {
            return Task.FromResult<(AgentUpdateManifest Manifest, byte[] PackageBytes)?>(entry);
        }

        return Task.FromResult<(AgentUpdateManifest Manifest, byte[] PackageBytes)?>(null);
    }
}
