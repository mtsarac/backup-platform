using BackupPlatform.Application.Agents;
using BackupPlatform.Domain.Agents;
using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Agents.Credentials;

/// <summary>An agent credential together with the agent state required to decide whether it may authenticate.</summary>
public sealed record AgentCredentialRecord(AgentCredential Credential, AgentStatus AgentStatus, int AgentCredentialGeneration, TenantId AgentTenantId);

public sealed record RotationBeginCommit(RotationBeginOutcome Outcome, NewAgentCredential? Issued, Guid? ReplacedPendingCredentialId);

public sealed record RotationConfirmCommit(RotationConfirmOutcome Outcome, int CredentialGeneration, Guid? SupersededCredentialId);

public sealed record AgentRevocationCommit(bool Changed, int RevokedCredentialCount);

/// <summary>Agent registry and credential persistence. Rotation and revocation are serialized per agent.</summary>
public interface IAgentRegistryStore
{
    Task<AgentCredentialRecord?> FindCredentialAsync(Guid credentialId, CancellationToken cancellationToken);

    /// <summary>
    /// Serialized per agent. Revokes any pending credential and issues a replacement, leaving the active credential valid.
    /// </summary>
    Task<RotationBeginCommit> BeginRotationAsync(
        AgentIdentity caller, DateTimeOffset now, TimeSpan pendingLifetime, CancellationToken cancellationToken);

    /// <summary>Serialized per agent. Promotes the caller's pending credential and revokes the previous active one.</summary>
    Task<RotationConfirmCommit> ConfirmRotationAsync(AgentIdentity caller, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Null when the agent does not exist in the tenant. Terminal; revokes every non-revoked credential.</summary>
    Task<AgentRevocationCommit?> RevokeAgentAsync(TenantId tenantId, AgentId agentId, UserId? actor, DateTimeOffset now, CancellationToken cancellationToken);
}
