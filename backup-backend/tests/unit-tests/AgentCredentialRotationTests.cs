using BackupPlatform.Agents.Credentials;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Domain.Agents;
using BackupPlatform.Domain.Identity;

namespace BackupPlatform.UnitTests;

// BackupPlatform.Agent is also a namespace (Agent.Core); the alias keeps the domain type addressable.
using Agent = BackupPlatform.Domain.Agents.Agent;

public sealed class AgentCredentialRotationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CredentialsVerifyOnlyTheirOwnSecretAndNeverRevealIt()
    {
        var (_, first) = Enroll();
        var other = AgentCredential.IssueInitial(AgentId.New(), TenantId.New(), Now);
        Assert.True(first.Credential.VerifySecret(first.Secret));
        Assert.False(first.Credential.VerifySecret(other.Secret));
        Assert.DoesNotContain(first.Secret, first.ToString(), StringComparison.Ordinal);
        Assert.Equal(AgentSecretReference.SecretLength, first.Secret.Length);
        Assert.Equal(AgentCredentialStatus.Active, first.Credential.Status);
        Assert.Equal(1, first.Credential.Generation);
    }

    [Fact]
    public void ConfirmedRotationPromotesPendingAndSupersedesTheOldCredential()
    {
        var (agent, active) = Enroll();
        var pending = AgentCredential.IssuePending(agent.Id, agent.TenantId, 2, Now, TimeSpan.FromHours(1));
        var credentials = new[] { active.Credential, pending.Credential };
        var decision = AgentCredentialRotation.EvaluateConfirm(agent, credentials, pending.Credential.Id, Now.AddMinutes(1));
        Assert.Equal(RotationConfirmOutcome.Activate, decision.Outcome);
        Assert.Same(pending.Credential, decision.Pending);
        Assert.Same(active.Credential, decision.Superseded);
    }

    [Fact]
    public void OldCredentialCannotConfirmWhileAPendingOneExistsButKeepsWorkingUntilThen()
    {
        var (agent, active) = Enroll();
        var pending = AgentCredential.IssuePending(agent.Id, agent.TenantId, 2, Now, TimeSpan.FromHours(1));
        var credentials = new[] { active.Credential, pending.Credential };
        var decision = AgentCredentialRotation.EvaluateConfirm(agent, credentials, active.Credential.Id, Now.AddMinutes(1));
        Assert.Equal(RotationConfirmOutcome.PendingRotationExists, decision.Outcome);
        Assert.Equal(AgentCredentialStatus.Active, active.Credential.Status);
    }

    [Fact]
    public void ExpiredPendingCredentialCannotBeConfirmedAndDoesNotBlockAnIdempotentConfirm()
    {
        var (agent, active) = Enroll();
        var pending = AgentCredential.IssuePending(agent.Id, agent.TenantId, 2, Now, TimeSpan.FromHours(1));
        var credentials = new[] { active.Credential, pending.Credential };
        var late = Now.AddHours(1);
        Assert.Equal(RotationConfirmOutcome.NotAuthorized, AgentCredentialRotation.EvaluateConfirm(agent, credentials, pending.Credential.Id, late).Outcome);
        Assert.Equal(RotationConfirmOutcome.AlreadyConfirmed, AgentCredentialRotation.EvaluateConfirm(agent, credentials, active.Credential.Id, late).Outcome);
        Assert.Throws<InvalidOperationException>(() => pending.Credential.Activate(late));
    }

    [Fact]
    public void ConfirmingAnAlreadyActiveCredentialIsIdempotent()
    {
        var (agent, active) = Enroll();
        var decision = AgentCredentialRotation.EvaluateConfirm(agent, [active.Credential], active.Credential.Id, Now);
        Assert.Equal(RotationConfirmOutcome.AlreadyConfirmed, decision.Outcome);
    }

    [Fact]
    public void BeginRotationReplacesAnInterruptedPendingCredentialInsteadOfStackingThem()
    {
        var (agent, active) = Enroll();
        var abandoned = AgentCredential.IssuePending(agent.Id, agent.TenantId, 2, Now, TimeSpan.FromHours(1));
        var decision = AgentCredentialRotation.EvaluateBegin(agent, [active.Credential, abandoned.Credential], active.Credential.Id);
        Assert.Equal(RotationBeginOutcome.Ready, decision.Outcome);
        Assert.Same(abandoned.Credential, decision.ReplacedPending);
        Assert.Equal(2, decision.NextGeneration);
    }

    [Fact]
    public void PendingCredentialCannotStartAnotherRotation()
    {
        var (agent, active) = Enroll();
        var pending = AgentCredential.IssuePending(agent.Id, agent.TenantId, 2, Now, TimeSpan.FromHours(1));
        var decision = AgentCredentialRotation.EvaluateBegin(agent, [active.Credential, pending.Credential], pending.Credential.Id);
        Assert.Equal(RotationBeginOutcome.NotAuthorized, decision.Outcome);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RevokedOrDisabledAgentsCannotRotateOrConfirm(bool revoke)
    {
        var (agent, active) = Enroll();
        var pending = AgentCredential.IssuePending(agent.Id, agent.TenantId, 2, Now, TimeSpan.FromHours(1));
        if (revoke)
        {
            agent.Revoke();
        }
        else
        {
            agent.Disable();
        }
        Assert.Equal(RotationBeginOutcome.AgentNotActive, AgentCredentialRotation.EvaluateBegin(agent, [active.Credential], active.Credential.Id).Outcome);
        Assert.Equal(RotationConfirmOutcome.AgentNotActive,
            AgentCredentialRotation.EvaluateConfirm(agent, [active.Credential, pending.Credential], pending.Credential.Id, Now).Outcome);
    }

    [Fact]
    public void CredentialRevocationIsTerminal()
    {
        var (_, active) = Enroll();
        active.Credential.Revoke(AgentCredentialRevocationReason.Superseded, Now);
        Assert.Equal(AgentCredentialStatus.Revoked, active.Credential.Status);
        Assert.Throws<InvalidOperationException>(() => active.Credential.Revoke(AgentCredentialRevocationReason.AgentRevoked, Now));
        Assert.Throws<InvalidOperationException>(() => active.Credential.Activate(Now));
    }

    private static (Agent Agent, NewAgentCredential Credential) Enroll()
    {
        var agent = new Agent(AgentId.New(), TenantId.New(),
            new AgentSoftware(AgentPlatform.Linux, new Version(1, 0), new AgentProtocolVersion(1, 0)), "host");
        agent.RecordEnrollment();
        return (agent, AgentCredential.IssueInitial(agent.Id, agent.TenantId, Now));
    }
}
