using BackupPlatform.Agents.Credentials;
using BackupPlatform.Application.Agents;
using BackupPlatform.Domain.Agents;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Infrastructure.Agents;

/// <summary>
/// Agent and credential persistence under explicit platform operations; every query names its tenant.
/// Rotation and revocation lock the agent row (SELECT ... FOR UPDATE) so concurrent transitions for one agent are
/// serialized and each decision is made against committed state. Partial unique indexes backstop the invariants.
/// </summary>
/// <remarks>
/// A credential change is saved in two steps inside one transaction (revoke the outgoing credential, then
/// add or activate the incoming one) so the unique indexes are never violated mid-batch.
/// </remarks>
public sealed class PostgreSqlAgentRegistryStore(DbContextOptions<PlatformDbContext> options) : IAgentRegistryStore
{
    public async Task<AgentCredentialRecord?> FindCredentialAsync(Guid credentialId, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var row = await (from credential in context.AgentCredentials.AsNoTracking()
                         join agent in context.Agents.AsNoTracking() on credential.AgentId equals agent.Id
                         where credential.Id == credentialId
                         select new { Credential = credential, agent.Status, agent.CredentialGeneration, agent.TenantId })
            .SingleOrDefaultAsync(cancellationToken);
        return row is null ? null : new AgentCredentialRecord(row.Credential, row.Status, row.CredentialGeneration, row.TenantId);
    }

    public async Task<RotationBeginCommit> BeginRotationAsync(
        AgentIdentity caller, DateTimeOffset now, TimeSpan pendingLifetime, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var state = await LoadLockedAsync(context, caller.TenantId, caller.AgentId, cancellationToken);
        if (state is null)
        {
            return new RotationBeginCommit(RotationBeginOutcome.NotAuthorized, null, null);
        }
        var decision = AgentCredentialRotation.EvaluateBegin(state.Agent, state.Credentials, caller.CredentialId);
        if (decision.Outcome != RotationBeginOutcome.Ready)
        {
            return new RotationBeginCommit(decision.Outcome, null, null);
        }
        if (decision.ReplacedPending is { } replaced)
        {
            replaced.Revoke(AgentCredentialRevocationReason.RotationReplaced, now);
            await context.SaveChangesAsync(cancellationToken);
        }
        var issued = AgentCredential.IssuePending(state.Agent.Id, state.Agent.TenantId, decision.NextGeneration, now, pendingLifetime);
        context.AgentCredentials.Add(issued.Credential);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new RotationBeginCommit(RotationBeginOutcome.Ready, issued, decision.ReplacedPending?.Id);
    }

    public async Task<RotationConfirmCommit> ConfirmRotationAsync(AgentIdentity caller, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var state = await LoadLockedAsync(context, caller.TenantId, caller.AgentId, cancellationToken);
        if (state is null)
        {
            return new RotationConfirmCommit(RotationConfirmOutcome.NotAuthorized, 0, null);
        }
        var decision = AgentCredentialRotation.EvaluateConfirm(state.Agent, state.Credentials, caller.CredentialId, now);
        switch (decision.Outcome)
        {
            case RotationConfirmOutcome.Activate:
                if (decision.Superseded is { } superseded)
                {
                    superseded.Revoke(AgentCredentialRevocationReason.Superseded, now);
                    await context.SaveChangesAsync(cancellationToken);
                }
                decision.Pending!.Activate(now);
                state.Agent.RecordCredentialRotation();
                await context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new RotationConfirmCommit(decision.Outcome, state.Agent.CredentialGeneration, decision.Superseded?.Id);
            case RotationConfirmOutcome.AlreadyConfirmed:
                return new RotationConfirmCommit(decision.Outcome, state.Agent.CredentialGeneration, null);
            default:
                return new RotationConfirmCommit(decision.Outcome, 0, null);
        }
    }

    public async Task<AgentRevocationCommit?> RevokeAgentAsync(TenantId tenantId, AgentId agentId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var state = await LoadLockedAsync(context, tenantId, agentId, cancellationToken);
        if (state is null)
        {
            return null;
        }
        if (state.Agent.Status == AgentStatus.Revoked)
        {
            return new AgentRevocationCommit(false, 0);
        }
        foreach (var credential in state.Credentials)
        {
            credential.Revoke(AgentCredentialRevocationReason.AgentRevoked, now);
        }
        state.Agent.Revoke();
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AgentRevocationCommit(true, state.Credentials.Count);
    }

    private static async Task<LockedAgent?> LoadLockedAsync(
        PlatformDbContext context, TenantId tenantId, AgentId agentId, CancellationToken cancellationToken)
    {
        var tenant = tenantId.Value;
        var agentValue = agentId.Value;
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT \"Id\" FROM agents WHERE \"Id\" = {agentValue} AND \"TenantId\" = {tenant} FOR UPDATE", cancellationToken);
        var agent = await context.Agents.SingleOrDefaultAsync(row => row.Id == agentId && row.TenantId == tenantId, cancellationToken);
        if (agent is null)
        {
            return null;
        }
        var credentials = await context.AgentCredentials
            .Where(row => row.AgentId == agentId && row.TenantId == tenantId && row.Status != AgentCredentialStatus.Revoked)
            .ToListAsync(cancellationToken);
        return new LockedAgent(agent, credentials);
    }

    private sealed record LockedAgent(Agent Agent, List<AgentCredential> Credentials);
}
