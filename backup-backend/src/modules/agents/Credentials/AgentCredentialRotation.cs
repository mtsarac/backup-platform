using BackupPlatform.Domain.Agents;

namespace BackupPlatform.Agents.Credentials;

public enum RotationBeginOutcome
{
    Ready = 1,
    NotAuthorized = 2,
    AgentNotActive = 3
}

public enum RotationConfirmOutcome
{
    Activate = 1,
    AlreadyConfirmed = 2,
    /// <summary>The active credential tried to confirm while a pending one exists; only the pending secret may confirm it.</summary>
    PendingRotationExists = 3,
    NotAuthorized = 4,
    AgentNotActive = 5
}

public sealed record RotationBeginDecision(RotationBeginOutcome Outcome, AgentCredential? ReplacedPending, int NextGeneration);

public sealed record RotationConfirmDecision(RotationConfirmOutcome Outcome, AgentCredential? Pending, AgentCredential? Superseded);

/// <summary>
/// Pure rotation decisions over already-loaded, serialized state; persistence applies and orders the resulting transitions.
/// </summary>
/// <remarks>
/// Lifecycle: Active(N) --begin--&gt; Active(N) + Pending(N+1) --confirm with the pending secret--&gt; Revoked(N) + Active(N+1).
/// The old credential keeps working until confirmation, so an agent that never persists the new secret is not locked out.
/// A new begin replaces any existing pending credential, so an interrupted rotation is recovered by starting again, and
/// at most one pending credential ever exists. Confirmation is idempotent for the credential that is already active.
/// </remarks>
public static class AgentCredentialRotation
{
    public static RotationBeginDecision EvaluateBegin(
        Agent agent, IReadOnlyCollection<AgentCredential> credentials, Guid presentedCredentialId)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(credentials);
        if (agent.Status != AgentStatus.Active)
        {
            return new RotationBeginDecision(RotationBeginOutcome.AgentNotActive, null, 0);
        }
        var presented = credentials.FirstOrDefault(credential => credential.Id == presentedCredentialId);
        if (presented is not { Status: AgentCredentialStatus.Active } || presented.Generation != agent.CredentialGeneration)
        {
            return new RotationBeginDecision(RotationBeginOutcome.NotAuthorized, null, 0);
        }
        var replaced = credentials.FirstOrDefault(credential => credential.Status == AgentCredentialStatus.Pending);
        return new RotationBeginDecision(RotationBeginOutcome.Ready, replaced, checked(agent.CredentialGeneration + 1));
    }

    public static RotationConfirmDecision EvaluateConfirm(
        Agent agent, IReadOnlyCollection<AgentCredential> credentials, Guid presentedCredentialId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(credentials);
        if (agent.Status != AgentStatus.Active)
        {
            return new RotationConfirmDecision(RotationConfirmOutcome.AgentNotActive, null, null);
        }
        var presented = credentials.FirstOrDefault(credential => credential.Id == presentedCredentialId);
        if (presented is null)
        {
            return new RotationConfirmDecision(RotationConfirmOutcome.NotAuthorized, null, null);
        }
        if (presented.Status == AgentCredentialStatus.Active)
        {
            if (presented.Generation != agent.CredentialGeneration)
            {
                return new RotationConfirmDecision(RotationConfirmOutcome.NotAuthorized, null, null);
            }
            var livePending = credentials.Any(credential =>
                credential.Status == AgentCredentialStatus.Pending && !credential.IsExpired(now));
            return new RotationConfirmDecision(
                livePending ? RotationConfirmOutcome.PendingRotationExists : RotationConfirmOutcome.AlreadyConfirmed, null, null);
        }
        if (presented.Status != AgentCredentialStatus.Pending
            || presented.IsExpired(now)
            || presented.Generation != checked(agent.CredentialGeneration + 1))
        {
            return new RotationConfirmDecision(RotationConfirmOutcome.NotAuthorized, null, null);
        }
        var superseded = credentials.FirstOrDefault(credential =>
            credential.Status == AgentCredentialStatus.Active && credential.Generation == agent.CredentialGeneration);
        return new RotationConfirmDecision(RotationConfirmOutcome.Activate, presented, superseded);
    }
}
