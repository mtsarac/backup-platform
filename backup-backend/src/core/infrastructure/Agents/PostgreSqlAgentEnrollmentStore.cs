using BackupPlatform.Agents.Credentials;
using BackupPlatform.Agents.Enrollment;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Agents;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BackupPlatform.Infrastructure.Agents;

/// <summary>
/// Enrollment persistence under explicit platform operations; every query still names its tenant.
/// Single use rests on the database: consumption is an optimistic (PostgreSQL xmin) update committed in the same
/// transaction as the agent and credential, so concurrent consumers cannot both commit.
/// </summary>
public sealed class PostgreSqlAgentEnrollmentStore(DbContextOptions<PlatformDbContext> options) : IAgentEnrollmentStore
{
    private const int MaximumAttempts = 4;
    internal const string InstallationIndex = "ux_agents_tenant_installation_live";

    public async Task<EnrollmentPolicyState?> GetPolicyAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        if (!await context.Tenants.AnyAsync(tenant => tenant.Id == tenantId, cancellationToken))
        {
            return null;
        }
        var policy = await context.TenantAgentEnrollmentPolicies.AsNoTracking()
            .SingleOrDefaultAsync(row => row.TenantId == tenantId, cancellationToken);
        return new EnrollmentPolicyState(tenantId, policy?.IsEnabled ?? false, policy?.ChangedAt);
    }

    public async Task<EnrollmentPolicyChanged?> SetPolicyAsync(
        TenantId tenantId, bool isEnabled, UserId actor, AuditActorKind actorKind, DateTimeOffset now, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var context = PlatformDbContext.CreateForPlatformOperations(options);
            if (!await context.Tenants.AnyAsync(tenant => tenant.Id == tenantId, cancellationToken))
            {
                return null;
            }
            var policy = await context.TenantAgentEnrollmentPolicies.SingleOrDefaultAsync(row => row.TenantId == tenantId, cancellationToken);
            var previous = policy?.IsEnabled ?? false;
            if (policy is null)
            {
                if (isEnabled)
                {
                    context.TenantAgentEnrollmentPolicies.Add(new TenantAgentEnrollmentPolicy(tenantId, true, actor, now));
                }
            }
            else
            {
                policy.Set(isEnabled, actor, now);
            }
            if (previous != isEnabled)
            {
                context.AuditEvents.Add(AuditEvent.Record(now, actor, actorKind, AuditActions.EnrollmentPolicyChanged, tenantId,
                    "tenant", tenantId.Value.ToString("D"), new Dictionary<string, object?> { ["previousEnabled"] = previous, ["enabled"] = isEnabled }));
            }
            try
            {
                await context.SaveChangesAsync(cancellationToken);
                return new EnrollmentPolicyChanged(tenantId, previous, isEnabled, actor, now);
            }
            catch (DbUpdateException) when (attempt < MaximumAttempts)
            {
                // A concurrent first write or update won; re-read and re-apply the requested value.
            }
        }
    }

    public async Task AddTokenAsync(EnrollmentToken token, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        context.EnrollmentTokens.Add(token);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<EnrollmentToken>> ListActiveTokensAsync(TenantId tenantId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        return await context.EnrollmentTokens.AsNoTracking()
            .Where(token => token.TenantId == tenantId && token.ConsumedAt == null && token.RevokedAt == null && token.ExpiresAt > now)
            .OrderByDescending(token => token.CreatedAt).ThenBy(token => token.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> RevokeTokenAsync(TenantId tenantId, Guid tokenId, UserId actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var context = PlatformDbContext.CreateForPlatformOperations(options);
            var token = await context.EnrollmentTokens.SingleOrDefaultAsync(
                row => row.Id == tokenId && row.TenantId == tenantId, cancellationToken);
            if (token is null || token.ConsumedAt is not null || token.RevokedAt is not null)
            {
                return false;
            }
            token.Revoke(actor, now);
            try
            {
                await context.SaveChangesAsync(cancellationToken);
                return true;
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaximumAttempts)
            {
                // The token changed concurrently (for example it was just consumed); re-evaluate against the new state.
            }
        }
    }

    public async Task<EnrollmentToken?> FindTokenAsync(Guid tokenId, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        return await context.EnrollmentTokens.AsNoTracking().SingleOrDefaultAsync(token => token.Id == tokenId, cancellationToken);
    }

    public async Task<EnrollmentCommit> EnrollAsync(
        Guid tokenId, TenantId tenantId, Agent agent, AgentCredential credential, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var token = await context.EnrollmentTokens.SingleOrDefaultAsync(
            row => row.Id == tokenId && row.TenantId == tenantId, cancellationToken);
        if (token is null || token.GetState(now) != EnrollmentTokenState.Active)
        {
            return EnrollmentCommit.TokenUnavailable;
        }
        token.Consume(agent.Id, now);
        context.Agents.Add(agent);
        context.AgentCredentials.Add(credential);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return EnrollmentCommit.Enrolled;
        }
        catch (DbUpdateConcurrencyException)
        {
            return EnrollmentCommit.TokenUnavailable;
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: InstallationIndex
        })
        {
            return EnrollmentCommit.InstallationConflict;
        }
    }
}
