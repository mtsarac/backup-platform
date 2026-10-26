using System.Text.Json;
using System.Security.Cryptography;
using BackupPlatform.Application.Agents;
using BackupPlatform.Contracts.Agents;

namespace BackupPlatform.Infrastructure.Agents;

/// <summary>Immutable release directories on the operator-configured shared volume; atomic rename publishes a complete release.</summary>
public sealed class FileAgentUpdateManifestStore(string directory) : IAgentUpdateManifestStore
{
    private string Root => Path.IsPathFullyQualified(directory) ? directory : throw new InvalidOperationException("AgentUpdates:PackagesDirectory must be absolute.");

    private string ReleasePath(string platform, string version)
    {
        if (platform is not ("linux-x64" or "linux-arm64" or "win-x64") || !Version.TryParse(version, out var parsed) || parsed.ToString() != version)
            throw new ArgumentException("Invalid release platform or version.");
        return Path.Combine(Root, platform, version);
    }

    public async Task SaveManifestAsync(AgentUpdateManifest manifest, byte[] packageBytes, CancellationToken cancellationToken = default)
    {
        var target = ReleasePath(manifest.Payload.Platform, manifest.Payload.Version);
        if (packageBytes.LongLength != manifest.Payload.PackageSize || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(packageBytes)), manifest.Payload.PackageSha256, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Release package does not match its manifest.");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var staging = target + ".partial-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(staging, "package"), packageBytes, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(staging, "manifest.json"), JsonSerializer.Serialize(manifest), cancellationToken);
            try { Directory.Move(staging, target); }
            catch (IOException) when (Directory.Exists(target))
            {
                var existing = await GetPackageAsync(manifest.Payload.Platform, manifest.Payload.Version, cancellationToken);
                if (existing is null || existing.Value.Manifest != manifest || !existing.Value.PackageBytes.AsSpan().SequenceEqual(packageBytes))
                    throw new AgentUpdateConflictException();
            }
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    public async Task<AgentUpdateManifest?> GetLatestManifestAsync(string platform, CancellationToken cancellationToken = default)
    {
        _ = ReleasePath(platform, "0.0");
        var path = Path.Combine(Root, platform);
        if (!Directory.Exists(path)) return null;
        foreach (var version in Directory.EnumerateDirectories(path).Select(Path.GetFileName)
                     .Where(name => Version.TryParse(name, out _)).OrderByDescending(name => Version.Parse(name!)))
        {
            var manifestPath = Path.Combine(path, version!, "manifest.json");
            if (!File.Exists(manifestPath)) continue;
            return JsonSerializer.Deserialize<AgentUpdateManifest>(await File.ReadAllTextAsync(manifestPath, cancellationToken))
                ?? throw new InvalidDataException("Invalid persisted agent update manifest.");
        }
        return null;
    }

    public async Task<(AgentUpdateManifest Manifest, byte[] PackageBytes)?> GetPackageAsync(string platform, string version, CancellationToken cancellationToken = default)
    {
        var path = ReleasePath(platform, version);
        if (!Directory.Exists(path)) return null;
        var manifest = JsonSerializer.Deserialize<AgentUpdateManifest>(await File.ReadAllTextAsync(Path.Combine(path, "manifest.json"), cancellationToken))
            ?? throw new InvalidDataException("Invalid persisted agent update manifest.");
        return (manifest, await File.ReadAllBytesAsync(Path.Combine(path, "package"), cancellationToken));
    }
}
