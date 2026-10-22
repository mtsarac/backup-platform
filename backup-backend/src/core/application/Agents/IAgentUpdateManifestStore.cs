using BackupPlatform.Contracts.Agents;

namespace BackupPlatform.Application.Agents;

public interface IAgentUpdateManifestStore
{
    Task SaveManifestAsync(AgentUpdateManifest manifest, byte[] packageBytes, CancellationToken cancellationToken = default);
    Task<AgentUpdateManifest?> GetLatestManifestAsync(string platform, CancellationToken cancellationToken = default);
    Task<(AgentUpdateManifest Manifest, byte[] PackageBytes)?> GetPackageAsync(string platform, string version, CancellationToken cancellationToken = default);
}

public sealed class AgentUpdateConflictException() : InvalidOperationException("A different immutable release already uses this platform and version.");
