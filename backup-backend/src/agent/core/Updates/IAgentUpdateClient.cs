using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Contracts.Agents;

namespace BackupPlatform.Agent.Core.Updates;

public interface IAgentUpdateClient
{
    Task<AgentBackendResult<AgentUpdateManifest>> GetManifestAsync(AgentLocalIdentity identity, string platform, CancellationToken cancellationToken);
    Task<AgentBackendResult<bool>> DownloadAsync(AgentLocalIdentity identity, AgentUpdateManifest manifest, string destination,
        IReadOnlyDictionary<string, string> trustedKeys, CancellationToken cancellationToken);
}
