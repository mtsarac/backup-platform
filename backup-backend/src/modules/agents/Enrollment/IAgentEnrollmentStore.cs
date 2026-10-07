using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Agents;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Agents.Credentials;

namespace BackupPlatform.Agents.Enrollment;

public enum EnrollmentCommit
{
    Enrolled = 1,
    TokenUnavailable = 2,
    InstallationConflict = 3
}

/// <summary>Persistence for tenant enrollment policy, enrollment tokens and the atomic enrollment commit.</summary>
public interface IAgentEnrollmentStore
{
    /// <summary>Null when the tenant does not exist.</summary>
    Task<EnrollmentPolicyState?> GetPolicyAsync(TenantId tenantId, CancellationToken cancellationToken);

    /// <summary>Null when the tenant does not exist. Otherwise reports the previous and resulting value; an actual change is audited in the same transaction.</summary>
    Task<EnrollmentPolicyChanged?> SetPolicyAsync(
        TenantId tenantId, bool isEnabled, UserId actor, AuditActorKind actorKind, DateTimeOffset now, CancellationToken cancellationToken);

    Task AddTokenAsync(EnrollmentToken token, CancellationToken cancellationToken);

    Task<IReadOnlyList<EnrollmentToken>> ListActiveTokensAsync(TenantId tenantId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>False when the token is unknown to this tenant or is no longer revocable (consumed or already revoked).</summary>
    Task<bool> RevokeTokenAsync(TenantId tenantId, Guid tokenId, UserId actor, DateTimeOffset now, CancellationToken cancellationToken);

    Task<EnrollmentToken?> FindTokenAsync(Guid tokenId, CancellationToken cancellationToken);

    Task<EnrollmentToken?> FindTenantTokenAsync(TenantId tenantId, Guid tokenId, CancellationToken cancellationToken);

    /// <summary>
    /// Atomically consumes the token and persists the agent and its first credential, or does none of it.
    /// Of any number of concurrent calls for one token, at most one returns <see cref="EnrollmentCommit.Enrolled"/>.
    /// </summary>
    Task<EnrollmentCommit> EnrollAsync(
        Guid tokenId, TenantId tenantId, Agent agent, AgentCredential credential, DateTimeOffset now, CancellationToken cancellationToken);
}
