using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Contracts.Agents;

namespace BackupPlatform.Agent.Core.Tasks;

/// <summary>Asks the backend for the database login behind a reference minted for this agent. The answer is held in memory for one operation only.</summary>
public interface IDatabaseCredentialClient
{
    Task<AgentBackendResult<DatabaseCredentialResponse>> ResolveAsync(AgentLocalIdentity identity, string reference, CancellationToken cancellationToken);
}
