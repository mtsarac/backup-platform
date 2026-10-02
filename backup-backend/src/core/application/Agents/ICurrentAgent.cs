using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Application.Agents;

/// <summary>An agent identity established by the agent authentication scheme; never a user and never a tenant membership.</summary>
public sealed record AgentIdentity(AgentId AgentId, TenantId TenantId, Guid CredentialId, int CredentialGeneration);

/// <summary>Identity of the calling agent, independent of HTTP. Unauthenticated callers have a null identity.</summary>
public interface ICurrentAgent
{
    AgentIdentity? Identity { get; }
}
